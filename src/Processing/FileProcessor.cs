// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using ABChapterize.Audio;
using ABChapterize.Cli;
using ABChapterize.Concurrency;
using ABChapterize.Detection;
using ABChapterize.Gpu;
using ABChapterize.Transcription;
using ABChapterize.Ui;
using ABChapterize.Vad;
using static ABChapterize.Processing.RunStatistics;

namespace ABChapterize.Processing;

/// <summary>
/// Orchestrates the whole run: file enumeration, revert handling, per-file chapter
/// detection and writing (optionally several files at once, see
/// <see cref="RunConcurrentlyAsync"/>), plus the one-line-per-file console reporting.
/// </summary>
public sealed class FileProcessor
{
    private readonly CliOptions _options;
    private readonly ProgressRenderer _progress;
    private readonly Lock _statsLock = new();

    /// <summary>Number of files for which processing was aborted with a warning.</summary>
    private int _warnings;

    /// <summary>Number of files skipped because of pre-existing chapter markings.</summary>
    private int _skipped;

    /// <summary>Number of files that actually went through chapter detection.</summary>
    private int _processed;

    /// <summary>Number of processed files where detection found no chapter phrases at all
    /// (left unchanged), a subset of <see cref="_processed"/>.</summary>
    private int _noChaptersFound;

    /// <summary>Accumulated detection time of the processed files (for the --summary average).</summary>
    private TimeSpan _processingTime;

    /// <summary>Run-wide detection/confidence statistics and formatting for --verbose and
    /// --summary reporting; thread-safe, so every concurrently-processed file shares one
    /// instance.</summary>
    private readonly RunStatistics _runStats = new();

    /// <summary>Creates a processor for the given validated options.</summary>
    /// <param name="options">Validated command line options.</param>
    /// <param name="progress">Renderer for progress bars and summary lines.</param>
    public FileProcessor(CliOptions options, ProgressRenderer progress)
    {
        _options = options;
        _progress = progress;
    }

    /// <summary>Runs the tool in the mode selected by the options (revert, --no-op or abchapterize).</summary>
    /// <param name="ct">Cancellation token bound to Ctrl+C.</param>
    public async Task RunAsync(CancellationToken ct)
    {
        if (_options.Revert)
        {
            RunRevert(ct);
            return;
        }
        if (_options.NoOp)
        {
            RunNoOp();
            return;
        }
        await RunABChapterizeAsync(ct);
    }

    /// <summary>
    /// --no-op mode: lists every file --filter (and --recurse) would select, then returns
    /// without loading a Whisper model, invoking ffmpeg or touching any file - a quick way to
    /// check that a --filter regexp or extension list actually matches the intended files
    /// before committing to a real run. --filter is required to reach this mode at all (see
    /// <see cref="CliOptions"/>'s validation), so the listing is always filtered.
    /// <para>
    /// The only output path that stays on <see cref="Console"/> rather than going through
    /// <see cref="ProgressRenderer.Announce"/>: the listing is this mode's result, not a report
    /// about a run, and a --log-file copy of it would be a copy of the answer rather than a record
    /// of how it was reached.
    /// </para>
    /// </summary>
    private void RunNoOp()
    {
        var files = EnumerateTargets(_options.EffectiveExtensions);
        if (files.Count == 0)
        {
            Console.WriteLine("No audio files matching --filter found.");
            return;
        }
        if (!_options.Quiet)
            foreach (var file in files)
                Console.WriteLine(file);
        if (_options.Summary)
            Console.WriteLine($"Summary: {files.Count} file(s) would be processed");
    }

    /// <summary>
    /// Restores backups: for every supported audio file with an added ".bak" suffix the
    /// corresponding original is deleted and the backup renamed back to its original name.
    /// </summary>
    private void RunRevert(CancellationToken ct)
    {
        var bakSuffixes = _options.EffectiveExtensions.Select(e => e + ".bak").ToArray();
        var backups = EnumerateTargets(bakSuffixes);
        // Convenience: an audio file named directly is reverted from its own backup, so the
        // ".bak" suffix need not be typed out.
        var known = new HashSet<string>(backups.Select(CliOptions.NormalizePath), CliOptions.PathComparer);
        backups.AddRange(_options.Targets
            .Where(t => !t.IsDirectory && File.Exists(t.Path + ".bak"))
            .Select(t => t.Path + ".bak")
            .Where(bak => known.Add(CliOptions.NormalizePath(bak))));
        if (backups.Count == 0)
        {
            _progress.Announce("No .bak backups of supported audio files found; nothing to revert.");
            return;
        }
        var watch = Stopwatch.StartNew();
        foreach (var bak in backups)
        {
            ct.ThrowIfCancellationRequested();
            var original = bak[..^4]; // strip ".bak"
            if (File.Exists(original))
                File.Delete(original);
            File.Move(bak, original);
            if (!_options.Quiet)
                _progress.Announce($"{Path.GetFileName(original)}: reverted from backup");
        }
        if (_options.Summary)
        {
            _progress.AnnounceSummary($"Summary: {backups.Count} backup(s) encountered, {backups.Count} reverted");
            _progress.AnnounceSummary($"Total time: {FormatTime(watch.Elapsed)}");
        }
    }

    /// <summary>
    /// Runs chapter detection and writing for all selected files: enumerate, hand the list to
    /// whichever of the two per-file pipelines the options select (--import's sidecar write or
    /// the full Whisper detection run), then report. Both pipelines feed the same counters and
    /// the same <see cref="_runStats"/>, so the summary below does not care which one ran.
    /// </summary>
    /// <param name="ct">Cancellation token bound to Ctrl+C.</param>
    private async Task RunABChapterizeAsync(CancellationToken ct)
    {
        var groups = EnumerateTargetGroups(_options.EffectiveExtensions);
        if (groups.Sum(g => g.Files.Count) == 0)
        {
            _progress.Announce(_options.FilterRegex != null || _options.FilterExtensions != null
                ? "No audio files matching --filter found."
                : $"No supported audio files ({CliOptions.SupportedExtensionsText}) found.");
            return;
        }

        var files = ApplyBatchProgress(groups);
        if (files.Count == 0)
        {
            _progress.Announce("Every selected file was already processed by an earlier, " +
                              "interrupted run; nothing left to do (--ignore-progress redoes them).");
            return;
        }

        var (ffmpegPath, ffprobePath) = FfmpegLocator.Locate();
        var ffmpeg = new FfmpegClient(ffmpegPath, ffprobePath);
        var watch = Stopwatch.StartNew();

        if (_options.Import)
            await RunImportAsync(files, ffmpeg, ct);
        else
            await RunDetectionAsync(files, ffmpeg, ct);

        if (_options.Summary)
            PrintRunSummary(files.Count, watch.Elapsed);
    }

    /// <summary>
    /// One file waiting to be processed, together with the checkpoint to report it to when it is
    /// finished (null for a file named directly on the command line, or when checkpointing is off
    /// - see <see cref="ApplyBatchProgress"/>).
    /// </summary>
    /// <param name="Path">Full path of the file.</param>
    /// <param name="Progress">Batch checkpoint of the directory the file came from, if any.</param>
    private readonly record struct PendingFile(string Path, BatchProgress? Progress);

    /// <summary>
    /// Opens each directory target's <see cref="BatchProgress"/>, drops the files a previous,
    /// interrupted run already finished, and flattens what is left into the run's work list.
    /// Files named directly on the command line and runs that write nothing (--dry-run) are passed
    /// through unchecked - there is no directory to keep a record in, respectively nothing done
    /// that would be worth not doing twice.
    /// </summary>
    /// <param name="groups">The enumerated command line targets and their files.</param>
    /// <returns>The files to process, in target order.</returns>
    private List<PendingFile> ApplyBatchProgress(List<TargetGroup> groups)
    {
        var pending = new List<PendingFile>();
        var resumed = 0;
        foreach (var group in groups)
        {
            if (!group.Target.IsDirectory || _options.DryRun)
            {
                pending.AddRange(group.Files.Select(f => new PendingFile(f, null)));
                continue;
            }
            var progress = BatchProgress.Open(
                group.Target.Path, _options.RunFingerprint, _options.IgnoreProgress);
            var todo = group.Files.Where(f => !progress.IsDone(f)).ToList();
            resumed += group.Files.Count - todo.Count;
            progress.Begin(todo.Count);
            pending.AddRange(todo.Select(f => new PendingFile(f, progress)));
        }
        if (resumed > 0 && !_options.Quiet)
            _progress.Announce($"Resuming an interrupted run: {resumed} file(s) already processed, skipped.");
        return pending;
    }

    /// <summary>
    /// The --import pipeline: chapters come from a sidecar file, so Whisper is skipped entirely -
    /// there is nothing to detect and no model to load. Each file is just an ffprobe plus a direct
    /// write, so concurrency can scale further than detection's own hardware cap allows.
    /// </summary>
    /// <param name="files">The files to import chapters for.</param>
    /// <param name="ffmpeg">The run's shared ffmpeg client.</param>
    /// <param name="ct">Cancellation token bound to Ctrl+C.</param>
    private async Task RunImportAsync(List<PendingFile> files, FfmpegClient ffmpeg, CancellationToken ct)
    {
        var hardCap = ResolveConcurrency(files.Count, Math.Clamp(Environment.ProcessorCount, 1, 8));
        if (!_options.Quiet && hardCap > 1)
            _progress.Announce($"Importing chapters for {files.Count} file(s), up to {hardCap} at a time.");
        await RunConcurrentlyAsync(files, hardCap, ct,
            (file, token) => ProcessOneImportAsync(file, ffmpeg, token));
    }

    /// <summary>
    /// The normal pipeline: load the Whisper model(s), size the transcriber pool to the resolved
    /// concurrency, and run <see cref="ProcessOneAsync"/> for every file. Everything created here
    /// is disposed before returning, including after a failure or a Ctrl+C.
    /// </summary>
    /// <param name="files">The files to detect chapters in.</param>
    /// <param name="ffmpeg">The run's shared ffmpeg client.</param>
    /// <param name="ct">Cancellation token bound to Ctrl+C.</param>
    private async Task RunDetectionAsync(List<PendingFile> files, FfmpegClient ffmpeg, CancellationToken ct)
    {
        var modelPath = await ModelCatalog.EnsureModelAsync(_options.Model, ct);
        // The initial language is a placeholder: ChapterDetector always calls
        // ChangeLanguage before the first real transcription of every file, resolving
        // the actual language to use (the fixed --lang, or a fresh auto-detection).
        var initialLanguage = _options.AutoLanguage ? "en" : _options.Language;
        var gpu = ResolveGpu();
        var first = new WhisperTranscriber(modelPath, initialLanguage, forceCpu: _options.CpuOnly,
            gpuDevice: gpu.Selected?.Index);
        var hardCap = ResolveDetectionConcurrency(first, files.Count);

        // A different --pass3-model gets one shared, lazily-loaded instance for the whole run (see
        // SharedPass3Transcriber). Only gap work (pass 2.5 and 3) uses it, so serializing it there
        // costs little and beats loading a second model per concurrent file. The same model as
        // --model means no separate instance at all - gap work reuses each file's transcriber.
        var pass3Shared = _options.Pass3Model != _options.Model
            ? new SharedPass3Transcriber(_options.Pass3Model, initialLanguage, _options.CpuOnly, gpu.Selected?.Index)
            : null;

        if (!_options.Quiet)
            PrintModelBanner(first.RuntimeName, files.Count, hardCap, pass3Shared != null, gpu);

        var pool = await BuildTranscriberPoolAsync(first, hardCap, modelPath, initialLanguage, gpu.Selected?.Index);

        // Shared like ffmpeg above - one instance for the whole run, safe for concurrent use (see
        // SileroVadDetector's threading remarks) - and only needed for the VAD pre-pass.
        using var vad = _options.RunVadPrePass ? new SileroVadDetector() : null;

        try
        {
            var detectors = Channel.CreateUnbounded<ChapterDetector>();
            foreach (var w in pool)
            {
                // Each detector gets its own proxy onto the one shared pass-3 model, so every
                // concurrent file's pass 3 applies its own language against it (see the proxy).
                var pass3 = pass3Shared != null ? new Pass3TranscriberProxy(pass3Shared, initialLanguage) : null;
                detectors.Writer.TryWrite(new ChapterDetector(_options, ffmpeg, w, vad, pass3));
            }

            await RunConcurrentlyAsync(files, hardCap, ct, async (file, token) =>
            {
                var detector = await detectors.Reader.ReadAsync(token);
                try
                {
                    await ProcessOneAsync(file, ffmpeg, detector, token);
                }
                finally
                {
                    await detectors.Writer.WriteAsync(detector, CancellationToken.None);
                }
            });
        }
        finally
        {
            foreach (var w in pool)
                await w.DisposeAsync();
            if (pass3Shared != null)
                await pass3Shared.DisposeAsync();
        }
    }

    /// <summary>
    /// Resolves how many files detection may process at once. GPU backends are capped at one: a
    /// GPU context is not proven safe for concurrent inference, and loading the model into VRAM
    /// again per concurrent instance risks exhausting it. CPU backends scale with core count
    /// instead, since each pooled instance can be given a correspondingly smaller thread budget.
    /// Either way --jobs, when given, overrides the hardware-derived ceiling entirely.
    /// </summary>
    /// <param name="probe">The first loaded transcriber, consulted only for which native backend
    /// actually got loaded.</param>
    /// <param name="fileCount">Number of files in the run.</param>
    private int ResolveDetectionConcurrency(WhisperTranscriber probe, int fileCount)
        => probe.RuntimeName is "Cuda" or "Vulkan"
            ? ResolveConcurrency(fileCount, 1)
            : ResolveConcurrency(fileCount, Math.Clamp(Environment.ProcessorCount / 4, 1, 4));

    /// <summary>
    /// Builds the run's transcriber pool. At a concurrency of one the already-loaded
    /// <paramref name="probe"/> is the pool, so the model is never loaded twice; above that it is
    /// disposed and replaced by <paramref name="hardCap"/> instances that each get a fraction of
    /// the cores, so their combined thread budget still roughly matches the machine.
    /// </summary>
    /// <param name="probe">The transcriber loaded to determine the backend.</param>
    /// <param name="hardCap">Resolved concurrency, i.e. the pool size.</param>
    /// <param name="modelPath">Path of the GGML model to load.</param>
    /// <param name="initialLanguage">Placeholder language every instance starts with.</param>
    /// <param name="gpuDevice">Resolved --use-gpu device index, so every pool instance lands on the
    /// same GPU as the probe did.</param>
    private async Task<List<WhisperTranscriber>> BuildTranscriberPoolAsync(
        WhisperTranscriber probe, int hardCap, string modelPath, string initialLanguage, int? gpuDevice)
    {
        if (hardCap == 1)
            return [probe];
        await probe.DisposeAsync();
        var threadsPerInstance = Math.Max(2, Environment.ProcessorCount / hardCap);
        return [.. Enumerable.Range(0, hardCap)
            .Select(_ => new WhisperTranscriber(modelPath, initialLanguage, threadsPerInstance, _options.CpuOnly,
                gpuDevice))];
    }

    /// <summary>
    /// Resolves which GPU this run should use, from <c>--use-gpu</c> or the automatic preference
    /// for a discrete card, and which one the banner should name.
    /// </summary>
    /// <returns>The choice; see <see cref="GpuChoice"/> for why those are two different things.</returns>
    /// <exception cref="AppError">The user named a device that does not match exactly one GPU.
    /// Failing loudly is the point: silently falling back to the default would reproduce the very
    /// situation --use-gpu exists to end, where a run quietly uses the wrong card.</exception>
    /// <remarks>
    /// Skipped entirely under --cpu-only, which has no device to choose, and harmless on machines
    /// without Vulkan: the enumeration comes back empty and nothing is imposed.
    /// </remarks>
    private GpuChoice ResolveGpu()
    {
        if (_options.CpuOnly)
            return GpuChoice.None;

        var devices = VulkanDeviceEnumerator.Enumerate();

        // GGML_VK_VISIBLE_DEVICES hides devices from the backend and renumbers the rest, so an index
        // we hand over would no longer mean what we meant by it. Imposing nothing is therefore the
        // only safe answer - but the banner can still say which GPU the variable leaves in charge,
        // and must: staying silent about it is indistinguishable from GPU naming being broken.
        var visibleDevices = Environment.GetEnvironmentVariable("GGML_VK_VISIBLE_DEVICES");
        if (!string.IsNullOrWhiteSpace(visibleDevices))
        {
            if (_options.UseGpu != null)
                throw new AppError(
                    $"--use-gpu cannot be combined with GGML_VK_VISIBLE_DEVICES (set to \"{visibleDevices}\"), " +
                    "which hides GPUs from the backend and renumbers the rest. Unset it - --use-gpu replaces it.");

            return new GpuChoice(
                Selected: null,
                GpuSelector.ResolveVisibleDevices(devices, visibleDevices),
                DeferredTo: $"GGML_VK_VISIBLE_DEVICES={visibleDevices}");
        }

        var selection = GpuSelector.Select(devices, _options.UseGpu);
        if (selection.Error != null)
            throw new AppError(selection.Error);

        // Naming the device even when nothing was selected is the whole point of the banner: the
        // backend's own default is index 0, and a machine with a single Vulkan device can still be
        // running on something nobody wanted. A WSL2 distro with no GPU passthrough enumerates
        // exactly one device, llvmpipe - a software rasterizer that answers to "Vulkan backend"
        // just like a real GPU would, which is how a CPU-rasterized run went unnoticed here for ten
        // days (2026-07-18 to 2026-07-28).
        return new GpuChoice(selection.Device, selection.Device ?? devices.FirstOrDefault());
    }

    /// <summary>Prints the one-off "model loaded" line that opens a detection run.</summary>
    /// <param name="runtimeName">Native backend Whisper.net actually loaded.</param>
    /// <param name="fileCount">Number of files in the run.</param>
    /// <param name="hardCap">Resolved concurrency.</param>
    /// <param name="separatePass3Model">Whether --pass3-model asked for a second model.</param>
    /// <param name="gpu">What this run decided about GPUs.</param>
    /// <remarks>
    /// The device name is printed because its absence is what made a wrong GPU invisible: a banner
    /// saying only "Vulkan backend" looks identical whether the run is on a discrete card, on the
    /// integrated one at a fraction of the speed, or on a software rasterizer. Only named on the
    /// Vulkan backend, since that is where the enumeration the name comes from applies.
    /// </remarks>
    private void PrintModelBanner(string runtimeName, int fileCount, int hardCap, bool separatePass3Model,
        GpuChoice gpu)
        => _progress.Announce($"Whisper model \"{_options.Model}\" loaded ({runtimeName} backend" +
                             DescribeGpu(runtimeName, gpu) +
                             (_options.AutoLanguage ? ", auto language detection" : "") + "), " +
                             (separatePass3Model
                                 ? $"pass 3 model \"{_options.Pass3Model}\" (loaded on first use), " : "") +
                             $"{fileCount} file(s) to process" +
                             (hardCap > 1 ? $", up to {hardCap} at a time." : "."));

    /// <summary>
    /// The GPU clause of the startup banner: which device, and - when an environment variable rather
    /// than this tool chose it - which variable, so that a run nobody can explain has the reason in
    /// its first line.
    /// </summary>
    /// <param name="runtimeName">Native backend Whisper.net actually loaded.</param>
    /// <param name="gpu">What <see cref="ResolveGpu"/> decided.</param>
    private static string DescribeGpu(string runtimeName, GpuChoice gpu)
    {
        // GGML_VK_VISIBLE_DEVICES only filters Vulkan's device list, so on any other backend it is
        // as irrelevant as the device name itself.
        if (runtimeName != "Vulkan")
            return "";

        return (gpu.Reported, gpu.DeferredTo) switch
        {
            ({ } device, null) => $" on {device.Name}",
            ({ } device, { } note) => $" on {device.Name} via {note}",
            (null, { } note) => $", device chosen by {note}",
            _ => "",
        };
    }

    /// <summary>
    /// Prints the --summary report closing a run: the file counts and elapsed time this class
    /// tracks, followed by whatever run-wide statistics <see cref="RunStatistics"/> collected.
    /// </summary>
    /// <param name="fileCount">Number of files the run encountered.</param>
    /// <param name="elapsed">Wall-clock time the run took.</param>
    private void PrintRunSummary(int fileCount, TimeSpan elapsed)
    {
        var warningNote = _warnings > 0 ? $", {_warnings} with warnings" : "";
        var noChaptersNote = _noChaptersFound > 0 ? $", {_noChaptersFound} with no chapters found" : "";
        _progress.AnnounceSummary(
            $"Summary: {fileCount} file(s) encountered, {_processed} processed, " +
            $"{_skipped} skipped{warningNote}{noChaptersNote}");
        var average = _processed > 0
            ? $", average per processed file: {FormatTime(_processingTime / _processed)}"
            : "";
        _progress.AnnounceSummary($"Total time: {FormatTime(elapsed)}{average}");
        foreach (var line in _runStats.FormatRunSummaryLines())
            _progress.AnnounceSummary(line);
    }

    /// <summary>
    /// Resolves the effective degree of parallelism for a run: an explicit --jobs value
    /// always wins; otherwise the given hardware-derived ceiling applies. Either way it is
    /// never more than the number of files there actually are.
    /// </summary>
    /// <param name="fileCount">Number of files to process.</param>
    /// <param name="autoHardCap">Ceiling used when --jobs was not given.</param>
    private int ResolveConcurrency(int fileCount, int autoHardCap)
        => Math.Max(1, Math.Min(fileCount, _options.Jobs ?? autoHardCap));

    /// <summary>
    /// Runs <paramref name="processOne"/> for every file, at most <paramref name="hardCap"/>
    /// at a time. The effective limit is additionally throttled downward (never upward
    /// beyond <paramref name="hardCap"/>) by live CPU load via <see cref="ConcurrencyMonitor"/>.
    /// If any file's processing throws, admission of further files stops on a best-effort
    /// basis (a file or two already admitted concurrently with the failing one may still
    /// start - stopping instantly would require serializing admission, defeating the point
    /// of concurrency); already-running files are always left to finish normally. The first
    /// such exception is re-thrown once all started files have completed, so the run's
    /// overall outcome - stop and report the error - matches sequential processing even
    /// though a couple of extra files may have been attempted first.
    /// </summary>
    /// <param name="files">Files to process.</param>
    /// <param name="hardCap">Absolute ceiling on concurrent files (already resolved from --jobs/hardware).</param>
    /// <param name="ct">Cancellation token bound to Ctrl+C.</param>
    /// <param name="processOne">Processes one file.</param>
    private async Task RunConcurrentlyAsync(
        List<PendingFile> files, int hardCap, CancellationToken ct,
        Func<PendingFile, CancellationToken, Task> processOne)
    {
        var gate = new AdaptiveConcurrencyGate(hardCap, initialSoftLimit: 1);
        using var monitor = new ConcurrencyMonitor(gate, TimeSpan.FromSeconds(2),
            _options.LoggingEnabled ? msg => _progress.Log(msg) : null);

        var tasks = new List<Task>();
        Exception? firstError = null;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            if (Volatile.Read(ref firstError) != null)
                break;

            var slot = await gate.AcquireAsync(ct);
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await processOne(file, ct);
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref firstError, ex, null);
                    throw;
                }
                finally
                {
                    slot.Dispose();
                }
            }, CancellationToken.None));
        }

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (Exception) when (firstError != null)
        {
            // Task.WhenAll surfaces whichever faulted task it observes first, not necessarily the
            // one that failed first; re-throw the tracked one so callers see the same failure a
            // sequential run would report.
            throw firstError;
        }
    }

    /// <summary>
    /// Explanation printed when an xHE-AAC file is encountered and the installed ffmpeg
    /// has no libfdk_aac decoder to handle it.
    /// </summary>
    private const string XheAacHint =
        "file skipped - it uses the xHE-AAC (USAC) codec, which ffmpeg's native AAC decoder " +
        "cannot handle reliably, and the installed ffmpeg has no libfdk_aac decoder.\n" +
        "  libfdk_aac is license-restricted (\"nonfree\"), so it is not included in official\n" +
        "  ffmpeg downloads or distribution packages. To process this file you need a custom\n" +
        "  ffmpeg built with --enable-libfdk-aac --enable-nonfree (e.g. via the media-autobuild\n" +
        "  suite on Windows, https://github.com/m-ab-s/media-autobuild_suite, or a manual build\n" +
        "  on Linux). Point the FFMPEG_DIR environment variable at that build to use it.";

    /// <summary>
    /// Starts the "Muxing" phase on a file's progress bar and returns a callback translating
    /// ffmpeg's processed play time (what <see cref="FfmpegClient.WriteChaptersAsync"/> reports)
    /// into the byte-based progress <see cref="WorkTracker"/> expects - the same play-time-to-bytes
    /// conversion <see cref="ChapterDetector"/> uses for its own phases.
    /// </summary>
    /// <param name="work">The file's progress tracker.</param>
    /// <param name="info">The file's probe result, for its size and duration.</param>
    private static Action<double> BeginMuxingPhase(WorkTracker work, MediaInfo info)
    {
        work.BeginPhase("Muxing", info.SizeBytes);
        var bytesPerSecond = info.DurationSeconds > 0 ? info.SizeBytes / info.DurationSeconds : 0;
        return seconds => work.SetPhaseProgress((long)(seconds * bytesPerSecond));
    }

    /// <summary>
    /// Turns a detection result's chapters into titled <see cref="Chapter"/>s - merging in the
    /// named marks (prologue, epilogue, --custom), which are detected separately precisely because they carry no
    /// chapter number (see <see cref="DetectedMark"/>) and meet the numbered ones only here - and,
    /// when the first one starts past the very beginning and something was actually spoken there,
    /// prepends the intro chapter: audiobooks open with a prelude, and the mp4 muxer would
    /// otherwise snap the first mark to 0:00. When nothing precedes the first mark's phrase but
    /// silence, music or a jingle (<see cref="DetectionResult.LeadInHasSpeech"/> false), no intro
    /// is inserted either - there is no spoken prelude to give its own entry, so that same muxer
    /// start-snap is left to fold the lead-in into the first chapter instead. Shared by the normal
    /// write path and the partial-marks path so both lay out chapters identically. Internal for
    /// unit testing.
    /// </summary>
    /// <param name="result">The file's detection result.</param>
    /// <returns>The chapters to write and a note (" + intro" or "") for the summary line.</returns>
    internal static (List<Chapter> Chapters, string IntroNote) BuildChapters(DetectionResult result)
    {
        // A named mark sharing a chapter's exact timestamp sorts after it, so a prologue heard in
        // the same breath as chapter 1 cannot displace the numbered entry a player scrubs by.
        var chapters = result.Chapters
            .Select(c => (c.TimeSeconds, Title: $"{result.Profile.Title} {c.Number}", Named: 0))
            .Concat(result.NamedMarks.Select(m => (m.TimeSeconds, m.Title, Named: 1)))
            .OrderBy(c => c.TimeSeconds).ThenBy(c => c.Named)
            .Select(c => new Chapter(c.TimeSeconds, c.Title))
            .ToList();
        if (chapters.Count > 0 && chapters[0].StartSeconds > 0 && result.LeadInHasSpeech)
        {
            chapters.Insert(0, new Chapter(0, result.Profile.IntroTitle));
            return (chapters, " + intro");
        }
        return (chapters, "");
    }

    /// <summary>
    /// The most chapter numbers <see cref="MissingMarksPath"/> spells out in a file name before
    /// falling back to the unnumbered ".missing-marks" tag. A file missing this many chapters or
    /// fewer is worth naming them all - beyond that the name grows unwieldy (and can hit the
    /// platform's path length limit), and a gap that large is a sign that detection went off the
    /// rails rather than a shortlist worth resuming from.
    /// </summary>
    internal const int MaxNamedMissingNumbers = 10;

    /// <summary>
    /// Builds the name a file is renamed to when pass 3 leaves an unresolved chapter-sequence gap:
    /// the original name with a ".missing-marks-&lt;n&gt;-&lt;n&gt;-..." tag (the still-missing
    /// chapter numbers, "-"-delimited) inserted before the extension, e.g.
    /// "Book.m4b" with chapters 3 and 7 missing becomes "Book.missing-marks-3-7.m4b". Beyond
    /// <see cref="MaxNamedMissingNumbers"/> missing chapters the numbers are left out entirely
    /// ("Book.missing-marks.m4b"), which also takes the file out of
    /// <see cref="HasMissingMarksTag"/>'s auto-resume scope on purpose: a gap that wide is
    /// something to look at by hand, not to hand straight back to another automatic run. Any such
    /// tag already present is replaced rather than stacked, in either form. Internal for unit
    /// testing.
    /// </summary>
    /// <param name="file">Path of the file being renamed.</param>
    /// <param name="missingNumbers">The chapter numbers still missing after pass 3.</param>
    internal static string MissingMarksPath(string file, IReadOnlyList<int> missingNumbers)
    {
        var dir = Path.GetDirectoryName(file) ?? "";
        var stem = StripMissingMarksTag(Path.GetFileNameWithoutExtension(file));
        var ext = Path.GetExtension(file);
        var tag = missingNumbers.Count is > 0 and <= MaxNamedMissingNumbers
            ? $".missing-marks-{string.Join("-", missingNumbers)}"
            : ".missing-marks";
        return Path.Combine(dir, $"{stem}{tag}{ext}");
    }

    /// <summary>Removes a trailing ".missing-marks" tag - with or without its number list - from a
    /// file stem, so re-tagging an already-tagged file replaces the tag instead of appending a
    /// second one.</summary>
    /// <param name="stem">File name without directory or extension.</param>
    private static string StripMissingMarksTag(string stem)
        => System.Text.RegularExpressions.Regex.Replace(stem, @"\.missing-marks(-[0-9-]+)?$", "");

    /// <summary>True when a file name still carries a numbered ".missing-marks-&lt;n&gt;-..." tag
    /// (see <see cref="MissingMarksPath"/>) - i.e. a previous run left it with an unresolved
    /// chapter-sequence gap small enough to name, and it is a candidate for
    /// <see cref="ProcessOneAsync"/>'s auto-resume branch. The unnumbered ".missing-marks" form
    /// deliberately does not qualify; see <see cref="MissingMarksPath"/>. Internal for unit
    /// testing.</summary>
    /// <param name="file">Path of the file being considered.</param>
    internal static bool HasMissingMarksTag(string file)
        => System.Text.RegularExpressions.Regex.IsMatch(
            Path.GetFileNameWithoutExtension(file), @"\.missing-marks-[0-9-]+$");

    /// <summary>The file's own original name, with any ".missing-marks-..." tag stripped - what a
    /// resumed file is renamed back to once every previously-missing chapter is found.</summary>
    /// <param name="file">Path of the tagged file.</param>
    private static string StripMissingMarksPath(string file)
    {
        var dir = Path.GetDirectoryName(file) ?? "";
        var stem = StripMissingMarksTag(Path.GetFileNameWithoutExtension(file));
        var ext = Path.GetExtension(file);
        return Path.Combine(dir, stem + ext);
    }

    /// <summary>
    /// Formats still-missing chapter numbers for a summary line, listing at most
    /// <see cref="MaxNamedMissingNumbers"/> of them and summarizing the rest as a count - the same
    /// cut-off <see cref="MissingMarksPath"/> applies to the file name, so the message and the name
    /// it announces stay in step. Internal for unit testing.
    /// </summary>
    /// <param name="missingNumbers">The chapter numbers still missing.</param>
    internal static string FormatMissingList(IReadOnlyList<int> missingNumbers)
    {
        if (missingNumbers.Count <= MaxNamedMissingNumbers)
            return string.Join(", ", missingNumbers);
        return string.Join(", ", missingNumbers.Take(MaxNamedMissingNumbers)) +
               $" and {missingNumbers.Count - MaxNamedMissingNumbers} more";
    }

    /// <summary>
    /// The per-file plumbing every stage of <see cref="ProcessOneAsync"/> needs, bundled so each
    /// stage takes one parameter instead of five. <see cref="Ffmpeg"/> is the run's shared client
    /// rather than anything file-specific; it rides along purely so the write paths need not be
    /// handed it separately.
    /// </summary>
    /// <param name="File">Full path of the file being processed.</param>
    /// <param name="Name">Its bare file name, which every console line for it is prefixed with.</param>
    /// <param name="Work">Its progress tracker.</param>
    /// <param name="Logs">Its log sinks - the ordinary stream and, with --debug, the file's own
    /// troubleshooting log.</param>
    /// <param name="Info">Its probe result, with the input decoder already resolved (see
    /// <see cref="ResolveXheAacDecoderAsync"/>).</param>
    /// <param name="Ffmpeg">The run's shared ffmpeg client.</param>
    private readonly record struct FileContext(
        string File, string Name, WorkTracker Work, DetectionLog Logs, MediaInfo Info, FfmpegClient Ffmpeg);

    /// <summary>
    /// Processes a single audiobook file, prints its summary line and - once it is finished for
    /// good, whatever the outcome - reports it to its directory's batch checkpoint. An error or a
    /// Ctrl+C deliberately reports nothing, so the file is attempted again by the next run.
    /// </summary>
    /// <param name="pending">The file to process and the checkpoint it belongs to.</param>
    /// <param name="ffmpeg">The run's shared ffmpeg client.</param>
    /// <param name="detector">A detector borrowed from the run's pool for the duration of this file.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task ProcessOneAsync(
        PendingFile pending, FfmpegClient ffmpeg, ChapterDetector detector, CancellationToken ct)
    {
        var name = Path.GetFileName(pending.Path);
        var work = new WorkTracker();
        _progress.Start(name, work);
        try
        {
            var renamedTo = await ProcessOneCoreAsync(pending.Path, name, work, ffmpeg, detector, ct);
            pending.Progress?.MarkDone(pending.Path, renamedTo);
        }
        catch (OperationCanceledException)
        {
            _progress.FinishWithSummary(work, $"{name}: aborted");
            throw;
        }
        catch (AppError ex)
        {
            _progress.FinishWithSummary(work, $"{name}: ERROR - {ex.Message}", important: true);
            throw;
        }
    }

    /// <summary>
    /// The per-file pipeline itself: probe, decoder resolution, then whichever of the three
    /// outcomes applies - a resume of a previously tagged file, a skip, or a detection run whose
    /// result one of the report/write stages below commits.
    /// </summary>
    /// <param name="file">Path of the file to process.</param>
    /// <param name="name">Its bare file name, which every console line for it is prefixed with.</param>
    /// <param name="work">Its progress tracker, already started.</param>
    /// <param name="ffmpeg">The run's shared ffmpeg client.</param>
    /// <param name="detector">A detector borrowed from the run's pool for the duration of this file.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The name the file was renamed to (a ".missing-marks" tag added or dropped), or
    /// null when it kept its own.</returns>
    private async Task<string?> ProcessOneCoreAsync(
        string file, string name, WorkTracker work, FfmpegClient ffmpeg, ChapterDetector detector,
        CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        // The ordinary log sink; every message is prefixed with the file name.
        var log = _options.LoggingEnabled ? (Action<string>)(msg => _progress.Log($"{name}: {msg}")) : null;
        // The --debug file can only be opened once the probe has run, since its header describes
        // what the probe found - so the probe and the decoder resolution log to the ordinary sink
        // alone. Nothing is lost by that: the header restates everything those two lines carry.
        var info = await ProbeAndLogAsync(file, ffmpeg, log, ct);
        if (await ResolveXheAacDecoderAsync(
                new FileContext(file, name, work, new DetectionLog(log, null), info, ffmpeg), ct)
            is not { } probed)
            return null;
        using var debug = _options.Debug ? DebugLog.Open(file, _options, probed.Info) : null;
        var ctx = probed with { Logs = new DetectionLog(log, debug != null ? debug.Write : null) };

        // Auto-resume a ".missing-marks-<n>-<n>-..." file left by a previous run's unresolved
        // chapter-sequence gap: only the still-missing gap(s) are re-probed, the committed markings
        // are trusted as-is. --force means "redo the whole file from scratch" and takes priority,
        // falling through to the normal policy below.
        // The resume path is entirely about chapter numbers the tag names, so a run that forms no
        // opinion about them re-detects the file from scratch instead - the tag is simply not this
        // run's business.
        if (!_options.Force && !_options.IgnoreChapterNumbers && HasMissingMarksTag(file))
            return await ProcessResumeAsync(ctx, detector, watch, ct);

        if (await DetectChaptersAsync(ctx, detector, ct) is not { } outcome)
            return null;
        var (result, discardNote) = outcome;

        RecordProcessed(watch);
        _runStats.AccumulateStats(result.Stats, ctx.Info.DurationSeconds);
        ctx.Logs.Write(FormatFileStats(result.Stats, ctx.Info.DurationSeconds));

        if (result.GapRemains)
            return await ReportUnresolvedGapAsync(ctx, result, ct);
        if (result.Chapters.Count == 0 && result.NamedMarks.Count == 0)
            ReportNoChaptersFound(ctx, result);
        else
            await WriteDetectedChaptersAsync(ctx, result, discardNote, ct);
        return null;
    }

    /// <summary>Probes a file and emits the one-line --verbose note describing what came back.
    /// Shared by the detection and --import paths, which open identically.</summary>
    /// <param name="file">Path of the file to probe.</param>
    /// <param name="ffmpeg">The run's shared ffmpeg client.</param>
    /// <param name="log">The file's ordinary log sink, or null when nothing is listening.</param>
    /// <param name="ct">Cancellation token.</param>
    private static async Task<MediaInfo> ProbeAndLogAsync(
        string file, FfmpegClient ffmpeg, Action<string>? log, CancellationToken ct)
    {
        var info = await ffmpeg.ProbeAsync(file, ct);
        log?.Invoke($"probed: duration {FormatTime(TimeSpan.FromSeconds(info.DurationSeconds))}, " +
                    $"codec {info.AudioCodec}" +
                    (info.AudioProfile.Length > 0 ? $" ({info.AudioProfile})" : "") +
                    $", {info.ChapterCount} existing chapter marking(s)");
        return info;
    }

    /// <summary>
    /// Picks the decoder the rest of the pipeline decodes this file with. Only xHE-AAC (USAC)
    /// audio needs one named explicitly: ffmpeg's native AAC decoder cannot handle it reliably,
    /// so such files go through libfdk_aac - or are skipped with <see cref="XheAacHint"/> when
    /// the installed ffmpeg has no such decoder.
    /// </summary>
    /// <param name="ctx">The file's context, carrying the probe result to refine.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The context with the decoder applied, or null when the file was skipped - in
    /// which case it has already been counted and reported.</returns>
    private async Task<FileContext?> ResolveXheAacDecoderAsync(FileContext ctx, CancellationToken ct)
    {
        if (!ctx.Info.IsXheAac)
            return ctx;
        // A probe that could not even determine the duration means this ffmpeg build cannot
        // handle the file regardless of the decoder list.
        if (ctx.Info.DurationSeconds > 0 && await ctx.Ffmpeg.SupportsLibFdkAacAsync(ct))
        {
            ctx.Logs.Write("xHE-AAC audio: decoding with libfdk_aac");
            return ctx with { Info = ctx.Info with { InputDecoder = "libfdk_aac" } };
        }
        lock (_statsLock) _warnings++;
        _progress.FinishWithSummary(ctx.Work, $"{ctx.Name}: WARNING - {XheAacHint}", important: true);
        return null;
    }

    /// <summary>
    /// The auto-resume path for a file still carrying a numbered ".missing-marks" tag (see
    /// <see cref="HasMissingMarksTag"/>): the detector re-probes only the gap(s) the tag names,
    /// and the outcome is committed either as a re-tagged partial write or - when every missing
    /// chapter turned up - as a full write under the file's original name.
    /// </summary>
    /// <param name="ctx">The file's context.</param>
    /// <param name="detector">The detector borrowed for this file.</param>
    /// <param name="watch">Running stopwatch of this file, for the processing-time average.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The name the file was renamed to, or null under --dry-run, where it keeps its own.</returns>
    private async Task<string?> ProcessResumeAsync(
        FileContext ctx, ChapterDetector detector, Stopwatch watch, CancellationToken ct)
    {
        var resumed = await detector.ResumeMissingMarksAsync(ctx.File, ctx.Info, ctx.Work, ctx.Logs, ct);
        RecordProcessed(watch);
        _runStats.AccumulateStats(resumed.Stats, ctx.Info.DurationSeconds);
        ctx.Logs.Write(FormatFileStats(resumed.Stats, ctx.Info.DurationSeconds));
        _runStats.AccumulateConfidence(resumed.Chapters);
        var (chapters, introNote) = BuildChapters(resumed);

        return resumed.GapRemains
            ? await ReportIncompleteResumeAsync(ctx, resumed, chapters, introNote, ct)
            : await ReportCompleteResumeAsync(ctx, resumed, chapters, introNote, ct);
    }

    /// <summary>Commits a resume that did not find everything: the marks so far are written and
    /// the file re-tagged with the chapter numbers still missing, so a later run can pick it up
    /// again.</summary>
    /// <param name="ctx">The file's context.</param>
    /// <param name="resumed">The resume's detection result.</param>
    /// <param name="chapters">The titled chapters to write.</param>
    /// <param name="introNote">Note from <see cref="BuildChapters"/> about a prepended intro.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The name the file was re-tagged with, or null under --dry-run.</returns>
    private async Task<string?> ReportIncompleteResumeAsync(
        FileContext ctx, DetectionResult resumed, List<Chapter> chapters, string introNote, CancellationToken ct)
    {
        lock (_statsLock) _warnings++;
        var retarget = MissingMarksPath(ctx.File, resumed.MissingNumbers);
        var stillMissing = FormatMissingList(resumed.MissingNumbers);
        if (_options.DryRun)
        {
            _progress.FinishWithSummary(ctx.Work,
                $"{ctx.Name}: DRY RUN - resume incomplete, still missing: {stillMissing}; would write " +
                $"{resumed.Chapters.Count} partial mark(s){introNote} and re-tag as " +
                $"{Path.GetFileName(retarget)}:{Environment.NewLine}{FormatChapterListing(chapters)}",
                important: true);
            return null;
        }
        var backupNote = await CommitChaptersAsync(ctx, chapters, retarget, ct);
        _progress.FinishWithSummary(ctx.Work,
            $"{ctx.Name}: WARNING - resume incomplete, still missing: {stillMissing}; wrote " +
            $"{resumed.Chapters.Count} partial mark(s){introNote}, re-tagged as " +
            $"{Path.GetFileName(retarget)}{backupNote}", important: true);
        return retarget;
    }

    /// <summary>Commits a resume that closed every gap: the full chapter set is written and the
    /// ".missing-marks" tag dropped from the file name again.</summary>
    /// <param name="ctx">The file's context.</param>
    /// <param name="resumed">The resume's detection result.</param>
    /// <param name="chapters">The titled chapters to write.</param>
    /// <param name="introNote">Note from <see cref="BuildChapters"/> about a prepended intro.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The original name the file was restored to, or null under --dry-run.</returns>
    private async Task<string?> ReportCompleteResumeAsync(
        FileContext ctx, DetectionResult resumed, List<Chapter> chapters, string introNote, CancellationToken ct)
    {
        var restored = StripMissingMarksPath(ctx.File);
        var range = $"{resumed.Chapters[0].Number}-{resumed.Chapters[^1].Number}";
        if (_options.DryRun)
        {
            _progress.FinishWithSummary(ctx.Work,
                $"{ctx.Name}: DRY RUN - resume complete, all chapters found; would write " +
                $"{resumed.Chapters.Count} chapter(s) ({range})" +
                $"{introNote} and rename to {Path.GetFileName(restored)}:" +
                $"{Environment.NewLine}{FormatChapterListing(chapters)}");
            return null;
        }
        var backupNote = await CommitChaptersAsync(ctx, chapters, restored, ct);
        _progress.FinishWithSummary(ctx.Work,
            $"{ctx.Name}: resume complete - {resumed.Chapters.Count} chapter(s) written " +
            $"({range}){introNote}, renamed to {Path.GetFileName(restored)}{backupNote}");
        return restored;
    }

    /// <summary>
    /// Applies the pre-existing-marking policy and runs the detection it calls for: a plain
    /// whole-file detection when nothing stands in the way, the --verify decision tree when
    /// markings exist and are to be checked, or no detection at all when the file is simply
    /// skipped.
    /// </summary>
    /// <param name="ctx">The file's context.</param>
    /// <param name="detector">The detector borrowed for this file.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The detection result and the note describing what happened to any existing
    /// markings, or null when the file was skipped - in which case it has already been counted
    /// and reported.</returns>
    private async Task<(DetectionResult Result, string DiscardNote)?> DetectChaptersAsync(
        FileContext ctx, ChapterDetector detector, CancellationToken ct)
    {
        var (skip, discardNote) = EvaluateExistingChapters(ctx.Info);
        if (!skip)
            return (await detector.DetectAsync(ctx.File, ctx.Info, ctx.Work, ctx.Logs, ct), discardNote);
        if (_options.Verify)
            return await VerifyThenDetectAsync(ctx, detector, ct);

        lock (_statsLock) _skipped++;
        _progress.FinishWithSummary(ctx.Work,
            $"{ctx.Name}: skipped - has {ctx.Info.ChapterCount} chapter marking(s) (use --force to redo)");
        return null;
    }

    /// <summary>
    /// The --verify decision tree for a file that would otherwise be skipped: markings that all
    /// check out leave the file alone; a partial failure keeps the trusted markings and gap-recovers
    /// only around the unconfirmed ones; nothing trustworthy left - or more failures than
    /// --verify-threshold allows, which discredits even the survivors as gap-recovery anchors -
    /// falls back to a full whole-file redetect.
    /// </summary>
    /// <param name="ctx">The file's context.</param>
    /// <param name="detector">The detector borrowed for this file.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The detection result and its discard note, or null when the file was left
    /// unchanged - in which case it has already been counted and reported.</returns>
    private async Task<(DetectionResult Result, string DiscardNote)?> VerifyThenDetectAsync(
        FileContext ctx, ChapterDetector detector, CancellationToken ct)
    {
        var verify = await detector.VerifyExistingChaptersAsync(ctx.File, ctx.Info, ctx.Work, ctx.Logs, ct);
        if (verify.Checked == 0 || verify.Passed)
        {
            lock (_statsLock) _skipped++;
            var verifyNote = verify.Checked > 0
                ? $"{verify.Checked} pre-existing chapter marking(s) verified correct"
                : $"has {ctx.Info.ChapterCount} chapter marking(s) (none had a checkable number)";
            _progress.FinishWithSummary(ctx.Work, $"{ctx.Name}: skipped - {verifyNote} (use --force to redo)");
            return null;
        }

        var thresholdExceeded = _options.VerifyFailThreshold is { } threshold && verify.Failed > threshold;
        if (verify.ConfirmedChapters.Count > 0 && !thresholdExceeded)
        {
            // At least one marking is trusted - only the gap(s) around the unconfirmed one(s) get
            // their own Pass 2 (and, for a still-missing trailing chapter, Pass 3); everything
            // else in the file is left exactly as --verify found it.
            var trustedNote = $", {verify.ConfirmedChapters.Count} of {ctx.Info.ChapterCount} existing " +
                              $"marking(s) trusted, {verify.Failed} unconfirmed one(s) gap-recovered";
            return (await detector.DetectGapsAsync(ctx.File, ctx.Info, ctx.Work, ctx.Logs, verify, ct), trustedNote);
        }

        var thresholdNote = thresholdExceeded
            ? $", exceeding --verify-threshold {_options.VerifyFailThreshold}"
            : "";
        var discardNote = $", {ctx.Info.ChapterCount} existing marking(s) discarded " +
                          $"(--verify: {verify.Failed} of {verify.Checked} checked mark(s) not confirmed{thresholdNote})";
        return (await detector.DetectAsync(ctx.File, ctx.Info, ctx.Work, ctx.Logs, ct), discardNote);
    }

    /// <summary>
    /// Commits a detection that left a chapter-sequence gap. Rather than discard the work, the
    /// marks found so far are written and the file flagged by name (".missing-marks-&lt;n&gt;-...")
    /// so the still-missing chapters are visible and a later run - or
    /// <see cref="ProcessResumeAsync"/> - can pick them up.
    /// </summary>
    /// <param name="ctx">The file's context.</param>
    /// <param name="result">The file's detection result.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The name the file was tagged with, or null under --dry-run.</returns>
    private async Task<string?> ReportUnresolvedGapAsync(
        FileContext ctx, DetectionResult result, CancellationToken ct)
    {
        lock (_statsLock) _warnings++;
        _runStats.AccumulateConfidence(result.Chapters);
        var (chapters, introNote) = BuildChapters(result);
        var target = MissingMarksPath(ctx.File, result.MissingNumbers);
        var missingList = FormatMissingList(result.MissingNumbers);
        if (_options.DryRun)
        {
            _progress.FinishWithSummary(ctx.Work,
                $"{ctx.Name}: DRY RUN - unresolved chapter sequence gap (missing: {missingList}); " +
                $"would write {result.Chapters.Count} partial mark(s){introNote} and rename to " +
                $"{Path.GetFileName(target)}:{Environment.NewLine}{FormatChapterListing(chapters)}",
                important: true);
            return null;
        }
        var backupNote = await CommitChaptersAsync(ctx, chapters, target, ct);
        _progress.FinishWithSummary(ctx.Work,
            $"{ctx.Name}: WARNING - unresolved chapter sequence gap (missing: {missingList}); " +
            $"wrote {result.Chapters.Count} partial mark(s){introNote}, renamed to " +
            $"{Path.GetFileName(target)}{backupNote}", important: true);
        return target;
    }

    /// <summary>Reports a detection that produced no chapters at all - the file is left
    /// untouched - naming whichever of the three reasons applies.</summary>
    /// <param name="ctx">The file's context.</param>
    /// <param name="result">The file's detection result.</param>
    private void ReportNoChaptersFound(FileContext ctx, DetectionResult result)
    {
        lock (_statsLock) _noChaptersFound++;
        var langHint = _options.AutoLanguage ? $" (language used: {result.Profile.Language})" : "";
        var summary = result.EarlyAborted
            ? $"{ctx.Name}: early-abort - no chapter found within the first " +
              $"{_options.EarlyAbortMinutes:0.#} minute(s) of play time; file unchanged{langHint}"
            : result.BelowExpectedStartNumber is { } foundNumber
                ? $"{ctx.Name}: first chapter found ({foundNumber}) is below --expected-start-chapter " +
                  $"{_options.ExpectedStartChapter}; file unchanged{langHint}"
                : $"{ctx.Name}: no chapter phrases found; file unchanged{langHint}";
        _progress.FinishWithSummary(ctx.Work, summary);
    }

    /// <summary>The normal, successful outcome: writes the detected chapters into the file (or,
    /// under --dry-run, lists what would be written) and prints the file's summary line.</summary>
    /// <param name="ctx">The file's context.</param>
    /// <param name="result">The file's detection result.</param>
    /// <param name="discardNote">Note about any pre-existing markings that were discarded.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task WriteDetectedChaptersAsync(
        FileContext ctx, DetectionResult result, string discardNote, CancellationToken ct)
    {
        _runStats.AccumulateConfidence(result.Chapters);
        var (chapters, introNote) = BuildChapters(result);
        var notes = introNote + discardNote + FormatNamedMarksNote(result) + FormatLowConfidenceNote(result) +
                    FormatLanguageNote(result) + await ExportSidecarAsync(ctx, chapters, ct);
        var what = FormatWrittenCount(result);
        // A low-confidence mark is the one thing here worth surfacing above the progress bar.
        var important = result.LowConfidenceNumbers.Count > 0;

        if (_options.DryRun)
        {
            _progress.FinishWithSummary(ctx.Work,
                $"{ctx.Name}: DRY RUN - would write {what}" +
                $"{notes}:{Environment.NewLine}{FormatChapterListing(chapters)}", important);
            return;
        }
        var backupNote = await CommitChaptersAsync(ctx, chapters, null, ct);
        _progress.FinishWithSummary(ctx.Work,
            $"{ctx.Name}: {what} written{notes}{backupNote}", important);
    }

    /// <summary>What the summary line calls the marks this file yielded: the numbered chapters with
    /// their range, or - when the run produced no numbered chapter, either because the book had
    /// none or because --ignore-chapter-numbers puts them all in the named list - the named marks
    /// on their own.</summary>
    /// <param name="result">The file's detection result.</param>
    private static string FormatWrittenCount(DetectionResult result)
        => result.Chapters.Count > 0
            ? $"{result.Chapters.Count} chapter(s) " +
              $"({result.Chapters[0].Number}-{result.Chapters[^1].Number})"
            : $"{result.NamedMarks.Count} mark(s)";

    /// <summary>Note counting the prologue/epilogue/--custom marks written alongside the numbered
    /// chapters, and flagging a file that hit the per-file --custom cap: marks the user asked for
    /// were dropped there, which would otherwise look exactly like a mapping that stopped matching
    /// halfway through the book. Empty when neither applies.</summary>
    /// <param name="result">The file's detection result.</param>
    private static string FormatNamedMarksNote(DetectionResult result)
    {
        var limitNote = result.CustomMarkLimitHit
            ? $", custom-mark limit of {DetectionTuning.MaxCustomMarksPerFile} reached - further matches dropped"
            : "";
        return result.Chapters.Count > 0 && result.NamedMarks.Count > 0
            ? $", {result.NamedMarks.Count} named mark(s){limitNote}"
            : limitNote;
    }

    /// <summary>Note naming the marks Whisper was unsure about, so they can be spot-checked;
    /// empty when every mark was confident.</summary>
    /// <param name="result">The file's detection result.</param>
    private static string FormatLowConfidenceNote(DetectionResult result)
        => result.LowConfidenceNumbers.Count > 0
            ? $", {result.LowConfidenceNumbers.Count} low-confidence mark(s) " +
              $"(chapter {string.Join(", ", result.LowConfidenceNumbers)}; see --verbose)"
            : "";

    /// <summary>With --lang auto, the note stating which language this file was actually
    /// processed in - the detected one, or "en" when detection was inconclusive or skipped.
    /// Empty for an explicit --lang, where there is nothing to report.</summary>
    /// <param name="result">The file's detection result.</param>
    private string FormatLanguageNote(DetectionResult result)
    {
        if (!_options.AutoLanguage)
            return "";
        return result.DetectedLanguage switch
        {
            { } lang when lang.Equals(result.Profile.Language, StringComparison.OrdinalIgnoreCase) =>
                $", language: {result.Profile.Language} (p={result.DetectedProbability:0.00})",
            { } lang =>
                $", language: {result.Profile.Language} (auto-detected {lang} p={result.DetectedProbability:0.00}, below threshold)",
            null => $", language: {result.Profile.Language} (auto-detection unavailable)",
        };
    }

    /// <summary>Writes the --export sidecar, if asked for, and returns the note announcing it.
    /// Runs regardless of --dry-run, so a run can be previewed and saved for hand-editing in one
    /// pass.</summary>
    /// <param name="ctx">The file's context.</param>
    /// <param name="chapters">The chapters to export.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<string> ExportSidecarAsync(FileContext ctx, List<Chapter> chapters, CancellationToken ct)
    {
        if (!_options.Export)
            return "";
        var sidecarPath = ChapterSidecar.PathFor(ctx.File, _options.SimpleMetadata);
        var sidecarText = _options.SimpleMetadata
            ? ChapterSidecar.BuildSimple(chapters)
            : FfmpegClient.BuildFfMetadata(chapters, ctx.Info.DurationSeconds);
        await File.WriteAllTextAsync(sidecarPath, sidecarText, new UTF8Encoding(false), ct);
        return $", sidecar exported to {Path.GetFileName(sidecarPath)}";
    }

    /// <summary>
    /// Muxes the given chapters into the file and, when the outcome calls for it, renames the
    /// result - the "write, then re-tag or un-tag the file name" step the gap and resume paths
    /// share with the plain write.
    /// </summary>
    /// <param name="ctx">The file's context.</param>
    /// <param name="chapters">The chapters to write.</param>
    /// <param name="renameTo">Path to move the written file to, or null to leave its name alone.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The backup note for the file's summary line.</returns>
    private async Task<string> CommitChaptersAsync(
        FileContext ctx, List<Chapter> chapters, string? renameTo, CancellationToken ct)
    {
        var bakReplaced = await ctx.Ffmpeg.WriteChaptersAsync(
            ctx.File, chapters, ctx.Info.DurationSeconds, _options.Backup,
            BeginMuxingPhase(ctx.Work, ctx.Info), ct);
        if (renameTo != null)
            File.Move(ctx.File, renameTo, overwrite: true);
        return FormatBackupNote(_options.Backup, bakReplaced);
    }

    /// <summary>Formats the indented "&lt;timestamp&gt;  &lt;title&gt;" block every --dry-run
    /// summary line ends with.</summary>
    /// <param name="chapters">The chapters that would be written.</param>
    private static string FormatChapterListing(IEnumerable<Chapter> chapters)
        => string.Join(Environment.NewLine,
            chapters.Select(c => $"  {FormatTimestamp(c.StartSeconds)}  {c.Title}"));

    /// <summary>Counts one more file as processed and folds its elapsed time into the run's
    /// per-file average. Thread-safe.</summary>
    /// <param name="watch">The file's running stopwatch.</param>
    private void RecordProcessed(Stopwatch watch)
    {
        lock (_statsLock)
        {
            _processed++;
            _processingTime += watch.Elapsed;
        }
    }

    /// <summary>
    /// Applies the policy for pre-existing chapter markings, shared between normal
    /// detection and --import: without --force, a file with any markings is skipped;
    /// with --max-chapters, a marking count above the threshold is treated as bogus and
    /// discarded even without --force.
    /// </summary>
    /// <param name="info">Probed media info of the file being processed.</param>
    /// <returns>Whether the file should be skipped, and a note describing discarded markings.</returns>
    private (bool Skip, string DiscardNote) EvaluateExistingChapters(MediaInfo info)
    {
        if (info.ChapterCount == 0)
            return (false, "");
        var bogus = _options.MaxChapters is { } max && info.ChapterCount > max;
        if (!_options.Force && !bogus)
            return (true, "");
        var discardNote = bogus && !_options.Force
            ? $", {info.ChapterCount} bogus marking(s) discarded (> --max-chapters)"
            : $", {info.ChapterCount} existing marking(s) discarded";
        return (false, discardNote);
    }

    /// <summary>
    /// Processes a single audiobook file in --import mode: reads its sidecar file and
    /// writes the chapters it contains, without running Whisper detection at all. Reports the
    /// file to its directory's batch checkpoint on the way out, exactly as the detection path
    /// does.
    /// </summary>
    /// <param name="pending">The file to import and the checkpoint it belongs to.</param>
    /// <param name="ffmpeg">The run's shared ffmpeg client.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task ProcessOneImportAsync(PendingFile pending, FfmpegClient ffmpeg, CancellationToken ct)
    {
        var file = pending.Path;
        var name = Path.GetFileName(file);
        var work = new WorkTracker();
        _progress.Start(name, work);
        try
        {
            await ImportOneCoreAsync(file, name, work, ffmpeg, ct);
            pending.Progress?.MarkDone(file, null);
        }
        catch (OperationCanceledException)
        {
            _progress.FinishWithSummary(work, $"{name}: aborted");
            throw;
        }
        catch (AppError ex)
        {
            _progress.FinishWithSummary(work, $"{name}: ERROR - {ex.Message}", important: true);
            throw;
        }
    }

    /// <summary>The --import pipeline for one file: read its sidecar, apply the same
    /// pre-existing-marking policy detection uses, and write what it contains.</summary>
    /// <param name="file">Path of the file to import chapters for.</param>
    /// <param name="name">Its bare file name, which every console line for it is prefixed with.</param>
    /// <param name="work">Its progress tracker, already started.</param>
    /// <param name="ffmpeg">The run's shared ffmpeg client.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task ImportOneCoreAsync(
        string file, string name, WorkTracker work, FfmpegClient ffmpeg, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var log = _options.LoggingEnabled ? (Action<string>)(msg => _progress.Log($"{name}: {msg}")) : null;
        var sidecarPath = ChapterSidecar.PathFor(file, _options.SimpleMetadata);
        if (!File.Exists(sidecarPath))
        {
            _progress.FinishWithSummary(work,
                $"{name}: skipped - no sidecar file found ({Path.GetFileName(sidecarPath)}); use --export to create one",
                important: true);
            return;
        }

        var info = await ProbeAndLogAsync(file, ffmpeg, log, ct);

        var (skip, discardNote) = EvaluateExistingChapters(info);
        if (skip)
        {
            lock (_statsLock) _skipped++;
            _progress.FinishWithSummary(work,
                $"{name}: skipped - has {info.ChapterCount} chapter marking(s) (use --force to redo)");
            return;
        }

        var text = await File.ReadAllTextAsync(sidecarPath, ct);
        var chapters = _options.SimpleMetadata
            ? ChapterSidecar.ParseSimple(text, sidecarPath)
            : ChapterSidecar.ParseFfMetadata(text, sidecarPath);
        RecordProcessed(watch);

        if (_options.DryRun)
        {
            _progress.FinishWithSummary(work,
                $"{name}: DRY RUN - would import {chapters.Count} chapter(s) from " +
                $"{Path.GetFileName(sidecarPath)}{discardNote}:" +
                $"{Environment.NewLine}{FormatChapterListing(chapters)}");
            return;
        }

        var backupNote = await CommitChaptersAsync(
            new FileContext(file, name, work, new DetectionLog(log, null), info, ffmpeg), chapters, null, ct);
        _progress.FinishWithSummary(work,
            $"{name}: {chapters.Count} chapter(s) imported from {Path.GetFileName(sidecarPath)}" +
            $"{discardNote}{backupNote}");
    }

    /// <summary>
    /// The files selected under one file or directory named on the command line, kept together
    /// rather than merged into one flat list because a directory's own batch progress is tracked
    /// per directory (see <see cref="BatchProgress"/>).
    /// </summary>
    /// <param name="Target">The file or directory as given on the command line.</param>
    /// <param name="Files">The files selected under it, in processing order.</param>
    internal readonly record struct TargetGroup(CliOptions.Target Target, List<string> Files);

    /// <summary>
    /// Builds the ordered list of files to work on across every command line target, honoring
    /// --recurse. Temporary files created by this tool are always excluded. Internal for unit
    /// testing.
    /// </summary>
    /// <param name="suffixes">Case-insensitive file name suffixes to accept.</param>
    internal List<string> EnumerateTargets(string[] suffixes)
        => [.. EnumerateTargetGroups(suffixes).SelectMany(g => g.Files)];

    /// <summary>
    /// Enumerates every command line target, in the order given, keeping each one's files
    /// separate. A file reachable from more than one target - the same path named twice, or one
    /// listed directory nested inside another - is only ever returned once, under the target that
    /// reached it first.
    /// </summary>
    /// <param name="suffixes">Case-insensitive file name suffixes to accept.</param>
    internal List<TargetGroup> EnumerateTargetGroups(string[] suffixes)
    {
        var seen = new HashSet<string>(CliOptions.PathComparer);
        return [.. _options.Targets.Select(target => new TargetGroup(
            target,
            [.. SelectFiles(target, suffixes).Where(f => seen.Add(CliOptions.NormalizePath(f)))]))];
    }

    /// <summary>Applies the extension, temporary-file, backup and --filter rules to one target's
    /// candidates and sorts what survives into <see cref="NaturalPathComparer">natural</see>
    /// order.</summary>
    /// <param name="target">The file or directory to look at.</param>
    /// <param name="suffixes">Case-insensitive file name suffixes to accept.</param>
    private IEnumerable<string> SelectFiles(CliOptions.Target target, string[] suffixes)
    {
        var candidates = target.IsDirectory
            ? Directory.EnumerateFiles(target.Path, "*",
                _options.Recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            : [target.Path];

        return candidates
            .Where(f => suffixes.Any(s => f.EndsWith(s, StringComparison.OrdinalIgnoreCase)))
            .Where(f => !f.Contains(".abchapterize.", StringComparison.OrdinalIgnoreCase))
            .Where(f => _options.Revert || !f.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
            .Where(f => _options.FilterRegex == null || _options.FilterRegex.IsMatch(f))
            .OrderBy(f => f, NaturalPathComparer.Instance);
    }
}
