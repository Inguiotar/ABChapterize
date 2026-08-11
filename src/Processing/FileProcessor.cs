// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using System.Diagnostics;
using System.Text;
using ABChapterize.Audio;
using ABChapterize.Cli;
using ABChapterize.Concurrency;
using ABChapterize.Detection;
using ABChapterize.Errors;
using ABChapterize.Gpu;
using ABChapterize.Transcription;
using ABChapterize.Ui;
using ABChapterize.Vad;
using static ABChapterize.Processing.RunStatistics;

namespace ABChapterize.Processing;

/// <summary>
/// Orchestrates the whole run: file enumeration, revert handling, per-file chapter detection and
/// writing, plus the one-line-per-file console reporting.
/// </summary>
/// <remarks>
/// Files are processed strictly one at a time, and everything below is single-threaded because of
/// it - no locks around the counters, one transcriber, one detector, one progress bar. The
/// parallelism that used to live here (several files at once, throttled to live CPU load) was given
/// up so that the whole machine goes into one file: the GPU backends already ran one file at a time
/// for VRAM safety, and on the CPU backend concurrent files only ever divided one fixed thread
/// budget between them rather than adding to it. What that buys is the voice-activity pre-pass,
/// which now spreads a single file's audio across every core (see
/// <see cref="ABChapterize.Vad.SileroVadDetector"/>) instead of competing with three other files
/// for one.
/// </remarks>
public sealed class FileProcessor
{
    private readonly CliOptions _options;
    private readonly ProgressRenderer _progress;

    /// <summary>Number of files for which processing was aborted with a warning.</summary>
    private int _warnings;

    /// <summary>Number of files that actually went through chapter detection.</summary>
    private int _processed;

    /// <summary>Accumulated detection time of the processed files (for the --summary average).</summary>
    private TimeSpan _processingTime;

    /// <summary>Run-wide detection/confidence statistics and formatting for --verbose and
    /// --summary reporting, accumulated across every file of the run.</summary>
    private readonly RunStatistics _runStats = new();

    /// <summary>The files --summary names one by one at the end: those skipped, those detection
    /// found nothing in, and those left with chapter marks still missing.</summary>
    private readonly RunOutcomes _outcomes = new();

    /// <summary>Creates a processor for the given validated options.</summary>
    /// <param name="options">Validated command line options.</param>
    /// <param name="progress">Renderer for the progress bar and the summary lines.</param>
    public FileProcessor(CliOptions options, ProgressRenderer progress)
    {
        _options = options;
        _progress = progress;
    }

    /// <summary>Runs the tool in the mode selected by the options (cleanup, revert, --no-op or
    /// abchapterize).</summary>
    /// <param name="ct">Cancellation token bound to Ctrl+C.</param>
    public async Task RunAsync(CancellationToken ct)
    {
        // Before the --revert branch: --cleanup --revert is one mode, not two, and the restoring
        // has to happen in the order CleanupRunner plans it in rather than ahead of everything.
        if (_options.Cleanup)
        {
            await new CleanupRunner(_options, _progress).RunAsync(ct);
            return;
        }
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
                group.Target.Path, _options.RunFingerprint, _options.IgnoreProgress, _progress.Announce);
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
    /// write.
    /// </summary>
    /// <param name="files">The files to import chapters for.</param>
    /// <param name="ffmpeg">The run's shared ffmpeg client.</param>
    /// <param name="ct">Cancellation token bound to Ctrl+C.</param>
    private async Task RunImportAsync(List<PendingFile> files, FfmpegClient ffmpeg, CancellationToken ct)
    {
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            await ProcessOneImportAsync(file, ffmpeg, ct);
        }
    }

    /// <summary>
    /// The normal pipeline: load the Whisper model(s), build the one detector the run needs, and put
    /// every file through <see cref="ProcessOneAsync"/> in turn. Everything created here is disposed
    /// before returning, including after a failure or a Ctrl+C.
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
        var transcriber = new WhisperTranscriber(modelPath, initialLanguage, _options.EffectiveWhisperThreads,
            _options.CpuOnly, gpu.Selected?.Index);

        // Everything from here on is inside the try, so that a failure while setting the rest of the
        // run up - loading the VAD model, say - still releases the Whisper context that is already
        // holding a model in memory or in VRAM.
        Pass3Transcriber? pass3 = null;
        try
        {
            // A different --pass3-model gets one lazily-loaded instance for the whole run (see
            // Pass3Transcriber). Only gap work (pass 2.5 and 3) uses it, so a book that never opens
            // a gap never pays for the second model at all. The same model as --model means no
            // separate instance either way - gap work reuses the run's own transcriber.
            pass3 = _options.Pass3Model != _options.Model
                ? new Pass3Transcriber(_options.Pass3Model, initialLanguage,
                    _options.EffectiveWhisperThreads, _options.CpuOnly, gpu.Selected?.Index,
                    _progress.Announce)
                : null;

            if (!_options.Quiet)
                PrintModelBanner(transcriber.RuntimeName, files.Count, pass3 != null, gpu);

            // Only needed for the VAD pre-pass, and the one thing in the run that uses more than one
            // thread of its own accord.
            using var vad = _options.RunVadPrePass ? new SileroVadDetector(_options.EffectiveVadThreads) : null;
            LogThreadBudget(vad);

            var detector = new ChapterDetector(_options, ffmpeg, transcriber, vad, pass3);
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                await ProcessOneAsync(file, ffmpeg, detector, ct);
            }
        }
        finally
        {
            await transcriber.DisposeAsync();
            if (pass3 != null)
                await pass3.DisposeAsync();
        }
    }

    /// <summary>
    /// Records what the run was given to work with, for the log alone: a run that turns out slower
    /// than expected is usually a thread-count question, and the answer is otherwise nowhere.
    /// </summary>
    /// <param name="vad">The run's voice-activity detector, or null when the pre-pass is off.</param>
    private void LogThreadBudget(SileroVadDetector? vad)
        => _progress.Log($"threads: Whisper {_options.EffectiveWhisperThreads}" +
                         (vad != null ? $", voice-activity pre-pass {vad.Workers}" : ", no voice-activity pre-pass") +
                         $" (cores: {ProcessorTopology.PhysicalCoreCount} physical, " +
                         $"{Environment.ProcessorCount} logical)");

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
    /// <param name="separatePass3Model">Whether --pass3-model asked for a second model.</param>
    /// <param name="gpu">What this run decided about GPUs.</param>
    /// <remarks>
    /// The device name is printed because its absence is what made a wrong GPU invisible: a banner
    /// saying only "Vulkan backend" looks identical whether the run is on a discrete card, on the
    /// integrated one at a fraction of the speed, or on a software rasterizer. Only named on the
    /// Vulkan backend, since that is where the enumeration the name comes from applies.
    /// </remarks>
    private void PrintModelBanner(string runtimeName, int fileCount, bool separatePass3Model, GpuChoice gpu)
        => _progress.Announce($"Whisper model \"{_options.Model}\" loaded ({runtimeName} backend" +
                             DescribeGpu(runtimeName, gpu) +
                             (_options.AutoLanguage ? ", auto language detection" : "") + "), " +
                             (separatePass3Model
                                 ? $"pass 3 model \"{_options.Pass3Model}\" (loaded on first use), " : "") +
                             $"{fileCount} file(s) to process.");

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
    /// tracks, whatever run-wide statistics <see cref="RunStatistics"/> collected, and last the
    /// per-file listings <see cref="RunOutcomes"/> gathered - last because they are the only part
    /// that grows with the size of the run.
    /// </summary>
    /// <param name="fileCount">Number of files the run encountered.</param>
    /// <param name="elapsed">Wall-clock time the run took.</param>
    private void PrintRunSummary(int fileCount, TimeSpan elapsed)
    {
        var warningNote = _warnings > 0 ? $", {_warnings} with warnings" : "";
        var noChapters = _outcomes.NoChaptersCount;
        var noChaptersNote = noChapters > 0 ? $", {noChapters} with no chapters found" : "";
        _progress.AnnounceSummary(
            $"Summary: {fileCount} file(s) encountered, {_processed} processed, " +
            $"{_outcomes.SkippedCount} skipped{warningNote}{noChaptersNote}");
        var average = _processed > 0
            ? $", average per processed file: {FormatTime(_processingTime / _processed)}"
            : "";
        _progress.AnnounceSummary($"Total time: {FormatTime(elapsed)}{average}");
        foreach (var line in _runStats.FormatRunSummaryLines())
            _progress.AnnounceSummary(line);
        foreach (var line in _outcomes.FormatListings())
            _progress.AnnounceSummarySegments(line);
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
    /// chapter number (see <see cref="DetectedMark"/>) and meet the numbered ones only here, one of
    /// them folding into a chapter's own title where it lands too close to it (see
    /// <see cref="MergeCrowdedNamedMarks"/>) - and,
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
    /// <param name="namedMarkDistanceSeconds">How close a named mark may come to a chapter before
    /// the two are written as one entry (<see cref="CliOptions.NamedMarkDistanceSeconds"/>); 0
    /// leaves every mark where it is.</param>
    /// <returns>The chapters to write, a note (" + intro" or "") for the summary line, and how many
    /// named marks were merged away - which only the caller reporting named marks at all has any
    /// use for.</returns>
    internal static (List<Chapter> Chapters, string IntroNote, int MergedNamedMarks) BuildChapters(
        DetectionResult result, double namedMarkDistanceSeconds)
    {
        // A named mark sharing a chapter's exact timestamp sorts after it, so a prologue heard in
        // the same breath as chapter 1 cannot displace the numbered entry a player scrubs by.
        var entries = result.Chapters
            .Select(c => new MarkEntry(c.TimeSeconds, $"{result.Profile.Title} {c.Number}", false, true))
            .Concat(result.NamedMarks.Select(m => new MarkEntry(
                m.TimeSeconds, m.Title, true, m.Kind == result.Profile.ChapterAnnouncement.Kind)))
            .OrderBy(c => c.TimeSeconds).ThenBy(c => c.Named ? 1 : 0)
            .ToList();
        var merged = MergeCrowdedNamedMarks(entries, namedMarkDistanceSeconds);
        var chapters = entries.Select(e => new Chapter(e.TimeSeconds, e.Title)).ToList();
        if (chapters.Count > 0 && chapters[0].StartSeconds > 0 && result.LeadInHasSpeech)
        {
            chapters.Insert(0, new Chapter(0, result.Profile.IntroTitle));
            return (chapters, " + intro", merged);
        }
        return (chapters, "", merged);
    }

    /// <summary>One mark on its way into the written chapter list, while it is still known where it
    /// came from - which is what <see cref="MergeCrowdedNamedMarks"/> needs and a
    /// <see cref="Chapter"/> no longer says.</summary>
    /// <param name="TimeSeconds">Position of the mark.</param>
    /// <param name="Title">The title it is written under, so far.</param>
    /// <param name="Named">Whether it came from the named marks rather than the chapter sequence -
    /// the tiebreak that keeps a numbered entry ahead of a named one at the same timestamp.</param>
    /// <param name="IsChapter">Whether it counts as a chapter for the merge below. Not simply
    /// <c>!Named</c>: under <c>--ignore-chapter-numbers</c> the chapter announcements are themselves
    /// named marks, and they are still what a nearby interlude belongs to.</param>
    private readonly record struct MarkEntry(
        double TimeSeconds, string Title, bool Named, bool IsChapter);

    /// <summary>
    /// Folds every named mark that sits within <paramref name="distanceSeconds"/> of a chapter into
    /// that chapter's own entry, appending its title in brackets: "Chapter 10 (Interlude)".
    /// <para>
    /// Two entries a few seconds apart are worse than one. A player scrubbing by chapter lands the
    /// listener either in the last sentence of the previous section or a few seconds into the new
    /// one, depending on which of the two they hit, and no listener asked to make that distinction.
    /// The chapter always wins the position, being the entry the whole tool exists to place and the
    /// one people navigate by; the named mark keeps the only part of itself that carries any
    /// information here, its title. Nothing is silently lost, which is what makes discarding the
    /// position defensible.
    /// </para>
    /// <para>
    /// This is also what settles the two marks one probe window can produce from a single
    /// transcript - a chapter announcement and a named phrase heard in the same breath ("Chapter
    /// ten. Interlude."). They are found by two independent scans that know nothing of each other,
    /// so the earliest they can be compared is here, where both have their final positions.
    /// </para>
    /// </summary>
    /// <param name="entries">The merged mark list, ascending in time; edited in place.</param>
    /// <param name="distanceSeconds">The minimum distance a named mark must keep to stay an entry
    /// of its own; 0 or less switches the whole thing off.</param>
    /// <returns>How many named marks were merged away.</returns>
    private static int MergeCrowdedNamedMarks(List<MarkEntry> entries, double distanceSeconds)
    {
        if (distanceSeconds <= 0)
            return 0;
        var appended = new Dictionary<int, List<string>>();
        var merged = new List<int>();
        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i] is not { Named: true, IsChapter: false } mark ||
                NearestChapterWithin(entries, i, distanceSeconds) is not { } chapter)
                continue;
            if (!appended.TryGetValue(chapter, out var titles))
                appended[chapter] = titles = [];
            titles.Add(mark.Title);
            merged.Add(i);
        }
        foreach (var (index, titles) in appended)
            entries[index] = entries[index] with
            {
                Title = $"{entries[index].Title} ({string.Join(", ", titles)})",
            };
        // Backwards, so each removal leaves the indices still to be removed where they were.
        for (var i = merged.Count - 1; i >= 0; i--)
            entries.RemoveAt(merged[i]);
        return merged.Count;
    }

    /// <summary>The chapter entry a named mark belongs to, or null when none is close enough. Ties
    /// go to the earlier chapter, which is the one a listener scrubbing backwards reaches first.</summary>
    /// <param name="entries">The merged mark list, ascending in time.</param>
    /// <param name="index">The named mark in question.</param>
    /// <param name="distanceSeconds">The minimum distance that keeps a mark independent; a mark
    /// exactly that far from a chapter is left alone.</param>
    private static int? NearestChapterWithin(
        List<MarkEntry> entries, int index, double distanceSeconds)
    {
        int? nearest = null;
        var closest = distanceSeconds;
        for (var i = 0; i < entries.Count; i++)
        {
            if (!entries[i].IsChapter)
                continue;
            var gap = Math.Abs(entries[i].TimeSeconds - entries[index].TimeSeconds);
            if (gap >= closest)
                continue;
            (nearest, closest) = (i, gap);
        }
        return nearest;
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
    /// <param name="detector">The run's single detector, reused for every file: there is one
    /// per run rather than one per file, and files are processed strictly one at a time.</param>
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
    /// The per-file pipeline's opening and closing: probe, decoder resolution, the --debug log, and
    /// - once <see cref="CommitOneAsync"/> has decided the file's fate - taking that log along when
    /// the file loses its ".missing-marks" tag.
    /// </summary>
    /// <param name="file">Path of the file to process.</param>
    /// <param name="name">Its bare file name, which every console line for it is prefixed with.</param>
    /// <param name="work">Its progress tracker, already started.</param>
    /// <param name="ffmpeg">The run's shared ffmpeg client.</param>
    /// <param name="detector">The run's single detector, reused for every file: there is one
    /// per run rather than one per file, and files are processed strictly one at a time.</param>
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

        var renamedTo = await CommitOneAsync(ctx, detector, watch, ct);
        // The debug log belongs beside the audiobook under the book's own name, so it follows the
        // file back when the tag comes off - but not when one is put on, where the untagged name it
        // already has is the right one (see DebugLog.PathFor).
        if (renamedTo != null && !MissingMarksTag.IsTagged(renamedTo))
            debug?.FollowTo(renamedTo);
        return renamedTo;
    }

    /// <summary>
    /// Decides and commits one file's fate: a resume of a previously tagged file, a skip, or a
    /// detection run whose result one of the report/write stages below writes out.
    /// </summary>
    /// <param name="ctx">The file's context.</param>
    /// <param name="detector">The run's single detector, reused for every file: there is one
    /// per run rather than one per file, and files are processed strictly one at a time.</param>
    /// <param name="watch">Running stopwatch of this file, for the processing-time average.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The name the file was renamed to, or null when it kept its own.</returns>
    private async Task<string?> CommitOneAsync(
        FileContext ctx, ChapterDetector detector, Stopwatch watch, CancellationToken ct)
    {
        // Auto-resume a ".missing-marks-<n>-<n>-..." file left by a previous run's unresolved
        // chapter-sequence gap: only the still-missing gap(s) are re-probed, the committed markings
        // are trusted as-is. --force means "redo the whole file from scratch" and takes priority,
        // falling through to the normal policy below.
        // The resume path is entirely about chapter numbers the tag names, so a run that forms no
        // opinion about them re-detects the file from scratch instead - the tag is simply not this
        // run's business.
        if (!_options.Force && !_options.IgnoreChapterNumbers && MissingMarksTag.IsResumable(ctx.File))
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
        {
            ReportNoChaptersFound(ctx, result);
            return null;
        }
        return await WriteDetectedChaptersAsync(ctx, result, discardNote, ct);
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
        _warnings++;
        // Counted as a warning *and* listed as a skip: the file is not processed, and "which files
        // did you not do" is exactly the question the listing answers. The listing gets the one-line
        // form - XheAacHint runs to six lines of build advice that belong on the result line alone.
        _outcomes.RecordSkipped(ctx.Name, "xHE-AAC (USAC) audio and no libfdk_aac decoder available");
        _progress.FinishWithSummary(ctx.Work, $"{ctx.Name}: WARNING - {XheAacHint}", important: true);
        return null;
    }

    /// <summary>
    /// The auto-resume path for a file still carrying a numbered ".missing-marks" tag (see
    /// <see cref="MissingMarksTag.IsResumable"/>): the detector re-probes only the gap(s) the tag names,
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
        var (chapters, introNote, _) = BuildChapters(resumed, _options.NamedMarkDistanceSeconds);

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
        _warnings++;
        var retarget = MissingMarksTag.PathFor(ctx.File, resumed.MissingNumbers);
        var stillMissing = MissingMarksTag.FormatList(resumed.MissingNumbers);
        RecordStillMissing(ctx, retarget, resumed.MissingNumbers);
        RecordLowConfidence(ctx, resumed, retarget);
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
        var restored = MissingMarksTag.StripFrom(ctx.File);
        RecordLowConfidence(ctx, resumed, restored);
        // Through FormatWrittenCount rather than indexing the chapter list directly: a tagged file
        // whose markings have since been stripped by hand resumes with nothing to seed from and
        // nothing to find, and reaches here with an empty list rather than a completed sequence.
        var written = FormatWrittenCount(resumed);
        if (_options.DryRun)
        {
            _progress.FinishWithSummary(ctx.Work,
                $"{ctx.Name}: DRY RUN - resume complete, all chapters found; would write {written}" +
                $"{introNote} and rename to {Path.GetFileName(restored)}:" +
                $"{Environment.NewLine}{FormatChapterListing(chapters)}");
            return null;
        }
        var backupNote = await CommitChaptersAsync(ctx, chapters, restored, ct);
        _progress.FinishWithSummary(ctx.Work,
            $"{ctx.Name}: resume complete - {written} written" +
            $"{introNote}, renamed to {Path.GetFileName(restored)}{backupNote}");
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

        ReportSkipped(ctx.Work, ctx.Name, $"has {ctx.Info.ChapterCount} chapter marking(s)");
        return null;
    }

    /// <summary>
    /// Lists and reports one skipped file. The reason is worded so that it can stand alone under
    /// the file's name in --summary's listing, which is why the advice that follows it on the
    /// result line is a separate argument: repeated under every entry of a two-hundred-file
    /// listing, the same "(use --force to redo)" is noise.
    /// </summary>
    /// <param name="work">The file's progress tracker.</param>
    /// <param name="name">Its bare file name.</param>
    /// <param name="reason">Why it was skipped.</param>
    /// <param name="hint">Advice appended to the result line only.</param>
    /// <param name="important">Whether the result line is worth surfacing above the progress bar.</param>
    private void ReportSkipped(
        WorkTracker work, string name, string reason,
        string hint = " (use --force to redo)", bool important = false)
    {
        _outcomes.RecordSkipped(name, reason);
        _progress.FinishWithSummary(work, $"{name}: skipped - {reason}{hint}", important);
    }

    /// <summary>
    /// The --verify decision tree for a file that would otherwise be skipped: markings that all
    /// check out leave the file alone; some of them wrong keeps the trusted ones and gap-recovers
    /// only around the unconfirmed ones; nearly all of them wrong warns and leaves the file
    /// completely alone (see <see cref="IsWholesaleFailure"/>).
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
            // --fix may have found marks worth moving even where every one of them checked out;
            // that is the point of it, and the file then gets rewritten rather than skipped.
            if (verify.Markings.Any(m => m.CorrectedStartSeconds != null))
                return await ApplyMarkingFixesAsync(ctx, verify, ct);
            var verifyNote = verify.Checked > 0
                ? $"{verify.Checked} pre-existing chapter marking(s) verified correct"
                : $"has {ctx.Info.ChapterCount} chapter marking(s) (none had a checkable number)";
            ReportSkipped(ctx.Work, ctx.Name, verifyNote);
            return null;
        }

        if (IsWholesaleFailure(verify, _options.VerifyFailThreshold))
        {
            _warnings++;
            var thresholdNote = _options.VerifyFailThreshold is { } threshold
                ? $", over the --verify-threshold of {threshold}"
                : "";
            ReportSkipped(ctx.Work, ctx.Name,
                $"--verify could not confirm {verify.Failed} of {verify.Checked} checked chapter " +
                $"marking(s){thresholdNote} - existing markings left untouched",
                " (use --force without --verify to mark it from scratch)", important: true);
            return null;
        }

        // At least one marking is trusted, and they still outnumber the failures - only the gap(s)
        // around the unconfirmed one(s) get their own Pass 2 (and, for a still-missing trailing
        // chapter, Pass 3); everything else in the file is left exactly as --verify found it.
        var trustedNote = $", {verify.ConfirmedChapters.Count} of {ctx.Info.ChapterCount} existing " +
                          $"marking(s) trusted, {verify.Failed} unconfirmed one(s) gap-recovered";
        return (await detector.DetectGapsAsync(ctx.File, ctx.Info, ctx.Work, ctx.Logs, verify, ct), trustedNote);
    }

    /// <summary>
    /// The <c>--verify --fix</c> outcome for a file whose markings all check out but some of which
    /// sit away from their announcements: the file's existing marking list is written back with the
    /// corrected timestamps and nothing else touched.
    /// </summary>
    /// <remarks>
    /// Built from the markings themselves rather than through <see cref="BuildChapters"/>, which is
    /// what every detection path uses. That is deliberate: this mode's whole promise is that it
    /// moves marks and changes nothing else, so it must not be able to rename one, drop one it did
    /// not recognize, or invent an intro entry the file never had. Matching the corrections back by
    /// timestamp is exact - <see cref="VerifyMarkingOutcome.StartSeconds"/> is a verbatim copy of
    /// the marking's own, not a recomputed figure.
    /// </remarks>
    /// <param name="ctx">The file's context.</param>
    /// <param name="verify">The verification result carrying the corrections.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Always null: the file is finished here, and the caller's detection path must not
    /// run for it.</returns>
    private async Task<(DetectionResult Result, string DiscardNote)?> ApplyMarkingFixesAsync(
        FileContext ctx, VerifyResult verify, CancellationToken ct)
    {
        var corrections = verify.Markings
            .Where(m => m.CorrectedStartSeconds != null)
            .ToDictionary(m => m.StartSeconds, m => m.CorrectedStartSeconds!.Value);
        var chapters = ctx.Info.ExistingChapters
            .Select(c => corrections.TryGetValue(c.StartSeconds, out var fixedStart)
                ? c with { StartSeconds = fixedStart }
                : c)
            .OrderBy(c => c.StartSeconds)
            .ToList();
        var largest = corrections.Max(kv => Math.Abs(kv.Value - kv.Key));
        var what = $"{corrections.Count} of {verify.Checked} verified marking(s) nudged onto their " +
                   $"announcements (largest correction {largest:0.##} s)";

        _processed++;
        if (_options.DryRun)
        {
            _progress.FinishWithSummary(ctx.Work,
                $"{ctx.Name}: DRY RUN - would write {what}:" +
                $"{Environment.NewLine}{FormatChapterListing(chapters)}");
            return null;
        }
        var backupNote = await CommitChaptersAsync(ctx, chapters, null, ct);
        _progress.FinishWithSummary(ctx.Work, $"{ctx.Name}: {what}{backupNote}");
        return null;
    }

    /// <summary>
    /// Whether a file's markings failed verification <em>wholesale</em> rather than individually -
    /// the difference between a book with a few bad marks and a book whose marks were never what
    /// --verify assumes they are.
    /// <para>
    /// It is worth being explicit about why the two get opposite treatment, because the wholesale
    /// case used to be the one that redetected the file from scratch. A mark set that fails almost
    /// entirely is far more likely to be marks that mean something other than "one numbered chapter"
    /// - a retailer's marks lumping several book chapters into one entry are the case on record, and
    /// a mark titled "Capitolo due" sitting where the narrator says "capitolo quattro" is correct for
    /// what it is and unconfirmable by anything here - than it is to be a book whose every single
    /// mark drifted. Silently replacing that user's marks is the worst thing this tool can do to
    /// them, and it did it to precisely the population it was already serving worst. So the file is
    /// skipped with a warning, and fixing it is made a deliberate per-file decision (--force without
    /// --verify) rather than something a batch run does on its own.
    /// </para>
    /// <para>
    /// The default rule is a ratio, because <see cref="CliOptions.VerifyFailThreshold"/> is an
    /// absolute count that is off unless asked for and so cannot supply one. Where the failures no
    /// longer outnumber the confirmations, gap recovery's own premise still holds: the survivors are
    /// trustworthy anchors bracketing the failures. Where they do, the "anchors" are the minority
    /// reading. Nothing confirmed at all is always wholesale, whatever an explicit threshold says -
    /// there would be no anchor to recover a gap between.
    /// </para>
    /// Internal (and pure) for unit testing.
    /// </summary>
    /// <param name="verify">The verification result to judge.</param>
    /// <param name="failThreshold">The explicit <c>--verify-threshold</c>, or null for the ratio.</param>
    internal static bool IsWholesaleFailure(VerifyResult verify, int? failThreshold)
        => verify.ConfirmedChapters.Count == 0 ||
           (failThreshold is { } threshold
               ? verify.Failed > threshold
               : verify.Failed > verify.ConfirmedChapters.Count);

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
        _warnings++;
        _runStats.AccumulateConfidence(result.Chapters);
        var (chapters, introNote, _) = BuildChapters(result, _options.NamedMarkDistanceSeconds);
        var target = MissingMarksTag.PathFor(ctx.File, result.MissingNumbers);
        var missingList = MissingMarksTag.FormatList(result.MissingNumbers);
        RecordStillMissing(ctx, target, result.MissingNumbers);
        RecordLowConfidence(ctx, result, target);
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

    /// <summary>
    /// Lists one file --summary is to report as still incomplete, under the name its reader will
    /// actually find in the folder: the freshly tagged one, or - under --dry-run, where nothing is
    /// renamed - the one it already has.
    /// </summary>
    /// <param name="ctx">The file's context.</param>
    /// <param name="taggedPath">The ".missing-marks" path it is being renamed to.</param>
    /// <param name="missingNumbers">The chapter numbers still missing.</param>
    private void RecordStillMissing(FileContext ctx, string taggedPath, IReadOnlyList<int> missingNumbers)
        => _outcomes.RecordMissingMarks(
            _options.DryRun ? ctx.Name : Path.GetFileName(taggedPath), missingNumbers);

    /// <summary>
    /// Lists one file --summary is to report as carrying marks worth a manual check, under the name
    /// its reader will find in the folder - the same rule <see cref="RecordStillMissing"/> follows.
    /// Called from each of the four paths that write chapters rather than from the one place they
    /// share, because the final name is only settled inside each of them.
    /// </summary>
    /// <param name="ctx">The file's context.</param>
    /// <param name="result">The file's detection result.</param>
    /// <param name="finalPath">The path the file ends the run under, or null where it keeps its own.</param>
    private void RecordLowConfidence(FileContext ctx, DetectionResult result, string? finalPath)
    {
        if (result.LowConfidenceNumbers.Count == 0)
            return;
        // The profile is the one this file actually resolved to, which with --lang auto is a
        // per-file answer - so a batch mixing a bare-number book with ordinary ones earns the
        // block's footnote from the one book it applies to.
        _outcomes.RecordLowConfidence(
            _options.DryRun || finalPath == null ? ctx.Name : Path.GetFileName(finalPath),
            result.LowConfidenceNumbers, result.Profile.BareNumberAnnouncements);
    }

    /// <summary>Reports a detection that produced no chapters at all - the file is left
    /// untouched - naming whichever of the three reasons applies, and lists the file for
    /// --summary's closing roster.</summary>
    /// <param name="ctx">The file's context.</param>
    /// <param name="result">The file's detection result.</param>
    private void ReportNoChaptersFound(FileContext ctx, DetectionResult result)
    {
        // The language hint is deliberately not carried into the listing: it says which profile the
        // file was read with, which is per-file diagnostics rather than an answer to "why is this
        // book on the list", and in a batch of two hundred it would repeat on nearly every entry.
        var reason = DescribeNoChapters(result);
        _outcomes.RecordNoChapters(ctx.Name, reason);
        var langHint = _options.AutoLanguage ? $" (language used: {result.Profile.Language})" : "";
        _progress.FinishWithSummary(ctx.Work, $"{ctx.Name}: {reason}; file unchanged{langHint}");
    }

    /// <summary>Which of the three ways a detection can come back empty-handed this one was, as the
    /// fragment following the file name. One wording feeding both the file's own result line and
    /// its --summary entry, so the two can never end up disagreeing.</summary>
    /// <param name="result">The file's detection result.</param>
    private string DescribeNoChapters(DetectionResult result)
        => result.EarlyAborted
            ? "early-abort - no chapter found within the first " +
              $"{_options.EarlyAbortMinutes:0.#} minute(s) of play time"
            : result.BelowExpectedStartNumber is { } foundNumber
                ? $"first chapter found ({foundNumber}) is below --expected-start-chapter " +
                  $"{_options.ExpectedStartChapter}"
                : "no chapter phrases found";

    /// <summary>The normal, successful outcome: writes the detected chapters into the file (or,
    /// under --dry-run, lists what would be written), takes any ".missing-marks" tag back off the
    /// file name, and prints the file's summary line.</summary>
    /// <param name="ctx">The file's context.</param>
    /// <param name="result">The file's detection result.</param>
    /// <param name="discardNote">Note about any pre-existing markings that were discarded.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The original name the file was restored to when this run completed a previously
    /// tagged one, or null when it kept the name it came in under.</returns>
    private async Task<string?> WriteDetectedChaptersAsync(
        FileContext ctx, DetectionResult result, string discardNote, CancellationToken ct)
    {
        _runStats.AccumulateConfidence(result.Chapters);
        var (chapters, introNote, merged) = BuildChapters(result, _options.NamedMarkDistanceSeconds);
        var notes = introNote + discardNote + FormatNamedMarksNote(result, merged) + FormatLowConfidenceNote(result) +
                    FormatSequenceRestartNote(result) + FormatUnverifiedNumbersNote(result) +
                    FormatLanguageNote(result) + await ExportSidecarAsync(ctx, chapters, ct);
        var what = FormatWrittenCount(result);
        // A low-confidence mark is worth surfacing above the progress bar; so is a book that gave up
        // most of its chapters to a restarting sequence, which is otherwise indistinguishable from
        // one that simply ends early, and so is a number nothing could corroborate, whose whole
        // point is that the output looks clean.
        var important = result.LowConfidenceNumbers.Count > 0 || result.SequenceRestartSkips > 0 ||
                        result.UnverifiedNumbers is { Count: > 0 };
        // The tag is a to-do note left on the file name, and a run that reached here left nothing
        // to do: every chapter of the sequence is present. So the file gets its own name back.
        // A *numbered* tag normally takes the resume path instead and is untagged there; what
        // reaches this point is the unnumbered form, which no resume picks up, or either form under
        // --force, which redetects the whole file from scratch.
        var restored = MissingMarksTag.IsTagged(ctx.File) ? MissingMarksTag.StripFrom(ctx.File) : null;
        RecordLowConfidence(ctx, result, restored);
        var renameNote = restored != null ? $", renamed to {Path.GetFileName(restored)}" : "";

        if (_options.DryRun)
        {
            var wouldRename = restored != null ? $" and rename to {Path.GetFileName(restored)}" : "";
            _progress.FinishWithSummary(ctx.Work,
                $"{ctx.Name}: DRY RUN - would write {what}{notes}{wouldRename}:" +
                $"{Environment.NewLine}{FormatChapterListing(chapters)}", important);
            return null;
        }
        var backupNote = await CommitChaptersAsync(ctx, chapters, restored, ct);
        _progress.FinishWithSummary(ctx.Work,
            $"{ctx.Name}: {what} written{notes}{renameNote}{backupNote}", important);
        return restored;
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
    /// <param name="merged">How many of the named marks were folded into a chapter's title rather
    /// than written as entries of their own (see <see cref="MergeCrowdedNamedMarks"/>). Reported
    /// because the count above would otherwise promise entries the listing does not have.</param>
    private static string FormatNamedMarksNote(DetectionResult result, int merged)
    {
        var limitNote = result.CustomMarkLimitHit
            ? $", custom-mark limit of {DetectionTuning.MaxCustomMarksPerFile} reached - further matches dropped"
            : "";
        var mergedNote = merged > 0
            ? $" ({merged} of them merged into a neighbouring chapter's title)"
            : "";
        return result.Chapters.Count > 0 && result.NamedMarks.Count > 0
            ? $", {result.NamedMarks.Count} named mark(s){mergedNote}{limitNote}"
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

    /// <summary>Note naming the chapter numbers that were heard, marked, and never corroborated
    /// (see <see cref="DetectedChapter.NumberUnverified"/>). The one note here that reports something
    /// the output cannot show: such a mark is written like any other, and the searching that would
    /// normally follow a hole of that size was deliberately not done. Empty for the ordinary
    /// file.</summary>
    /// <param name="result">The file's detection result.</param>
    private static string FormatUnverifiedNumbersNote(DetectionResult result)
        => result.UnverifiedNumbers is { Count: > 0 } numbers
            ? $", {numbers.Count} number(s) nothing could corroborate " +
              $"(chapter {string.Join(", ", numbers)}) - marked as heard, and the chapters under " +
              "them not searched for; see --verbose"
            : "";

    /// <summary>Note for a file whose chapter numbering restarts partway through (see
    /// <see cref="RegionProber.SequenceRestartSkips"/>): the announcements after the restart were
    /// heard and understood, and dropped only because their numbers had already been used. Without
    /// this the file just stops yielding chapters halfway through, which reads as a detection
    /// failure. Empty for the ordinary book.</summary>
    /// <param name="result">The file's detection result.</param>
    private static string FormatSequenceRestartNote(DetectionResult result)
        => result.SequenceRestartSkips > 0
            ? $", {result.SequenceRestartSkips} announcement(s) skipped - the chapter numbering " +
              "appears to restart partway through (a book in parts?); try --ignore-chapter-numbers"
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
        var earlierBakKept = await ctx.Ffmpeg.WriteChaptersAsync(
            ctx.File, chapters, ctx.Info.DurationSeconds, _options.Backup,
            BeginMuxingPhase(ctx.Work, ctx.Info), ct);
        if (renameTo != null)
            File.Move(ctx.File, renameTo, overwrite: true);
        return FormatBackupNote(_options.Backup, earlierBakKept);
    }

    /// <summary>Formats the indented "&lt;timestamp&gt;  &lt;title&gt;" block every --dry-run
    /// summary line ends with.</summary>
    /// <param name="chapters">The chapters that would be written.</param>
    private static string FormatChapterListing(IEnumerable<Chapter> chapters)
        => string.Join(Environment.NewLine,
            chapters.Select(c => $"  {FormatTimestamp(c.StartSeconds)}  {c.Title}"));

    /// <summary>Counts one more file as processed and folds its elapsed time into the run's
    /// per-file average.</summary>
    /// <param name="watch">The file's running stopwatch.</param>
    private void RecordProcessed(Stopwatch watch)
    {
        _processed++;
        _processingTime += watch.Elapsed;
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
            ReportSkipped(work, name, $"no sidecar file found ({Path.GetFileName(sidecarPath)})",
                "; use --export to create one", important: true);
            return;
        }

        var info = await ProbeAndLogAsync(file, ffmpeg, log, ct);

        var (skip, discardNote) = EvaluateExistingChapters(info);
        if (skip)
        {
            ReportSkipped(work, name, $"has {info.ChapterCount} chapter marking(s)");
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
