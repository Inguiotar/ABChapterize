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
        // Convenience: when a single audio file is given, revert its backup.
        if (backups.Count == 0 && !_options.TargetIsDirectory && File.Exists(_options.TargetPath + ".bak"))
            backups = [_options.TargetPath + ".bak"];
        if (backups.Count == 0)
        {
            Console.WriteLine("No .bak backups of supported audio files found; nothing to revert.");
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
                Console.WriteLine($"{Path.GetFileName(original)}: reverted from backup");
        }
        if (_options.Summary)
        {
            Console.WriteLine($"Summary: {backups.Count} backup(s) encountered, {backups.Count} reverted");
            Console.WriteLine($"Total time: {FormatTime(watch.Elapsed)}");
        }
    }

    /// <summary>Runs chapter detection and writing for all selected files.</summary>
    private async Task RunABChapterizeAsync(CancellationToken ct)
    {
        var files = EnumerateTargets(_options.EffectiveExtensions);
        if (files.Count == 0)
        {
            Console.WriteLine(_options.FilterRegex != null || _options.FilterExtensions != null
                ? "No audio files matching --filter found."
                : $"No supported audio files ({CliOptions.SupportedExtensionsText}) found.");
            return;
        }

        var (ffmpegPath, ffprobePath) = FfmpegLocator.Locate();
        var ffmpeg = new FfmpegClient(ffmpegPath, ffprobePath);
        var watch = Stopwatch.StartNew();

        if (_options.Import)
        {
            // --import skips Whisper entirely: chapters come from a sidecar file, so
            // there is nothing to detect and no model to load. It is just ffprobe + a
            // direct write per file, so concurrency can scale further than detection does.
            var hardCap = ResolveConcurrency(files.Count, Math.Clamp(Environment.ProcessorCount, 1, 8));
            if (!_options.Quiet && hardCap > 1)
                Console.WriteLine($"Importing chapters for {files.Count} file(s), up to {hardCap} at a time.");
            await RunConcurrentlyAsync(files, hardCap, ct,
                (file, token) => ProcessOneImportAsync(file, ffmpeg, token));
        }
        else
        {
            var modelPath = await ModelCatalog.EnsureModelAsync(_options.Model, ct);
            // The initial language is a placeholder: ChapterDetector always calls
            // ChangeLanguage before the first real transcription of every file, resolving
            // the actual language to use (the fixed --lang, or a fresh auto-detection).
            var initialLanguage = _options.AutoLanguage ? "en" : _options.Language;
            var first = new WhisperTranscriber(modelPath, initialLanguage, forceCpu: _options.CpuOnly);

            // GPU backends are capped at one file at a time: a GPU context is not proven safe
            // for concurrent inference, and loading the model into VRAM again per concurrent
            // instance risks exhausting it. CPU backends scale with core count instead, since
            // each pooled instance can be given a correspondingly smaller thread budget.
            var gpuBound = first.RuntimeName is "Cuda" or "Vulkan";
            var hardCap = gpuBound
                ? ResolveConcurrency(files.Count, 1)
                : ResolveConcurrency(files.Count, Math.Clamp(Environment.ProcessorCount / 4, 1, 4));

            // A different --pass3-model gets one shared, lazily-loaded instance for the whole run
            // (see SharedPass3Transcriber); pass 3 is the exception, so serializing it there costs
            // little and avoids loading a second model per concurrent file. The same model as
            // --model means no separate instance at all - pass 3 reuses each file's own transcriber.
            var pass3Differs = _options.Pass3Model != _options.Model;
            var pass3Shared = pass3Differs
                ? new SharedPass3Transcriber(_options.Pass3Model, initialLanguage, _options.CpuOnly)
                : null;

            if (!_options.Quiet)
                Console.WriteLine($"Whisper model \"{_options.Model}\" loaded ({first.RuntimeName} backend" +
                                  (_options.AutoLanguage ? ", auto language detection" : "") + "), " +
                                  (pass3Differs ? $"pass 3 model \"{_options.Pass3Model}\" (loaded on first use), " : "") +
                                  $"{files.Count} file(s) to process" +
                                  (hardCap > 1 ? $", up to {hardCap} at a time." : "."));

            List<WhisperTranscriber> pool;
            if (hardCap == 1)
            {
                pool = [first];
            }
            else
            {
                await first.DisposeAsync();
                var threadsPerInstance = Math.Max(2, Environment.ProcessorCount / hardCap);
                pool = [.. Enumerable.Range(0, hardCap)
                    .Select(_ => new WhisperTranscriber(modelPath, initialLanguage, threadsPerInstance, _options.CpuOnly))];
            }

            // Shared like ffmpeg above (one instance for the whole run, safe for concurrent
            // use - see SileroVadDetector's threading remarks); only needed when
            // ChapterDetector runs its full-file VAD pre-pass (see RunVadPrePass).
            using var vad = _options.RunVadPrePass ? new SileroVadDetector() : null;

            try
            {
                var channel = Channel.CreateUnbounded<ChapterDetector>();
                foreach (var w in pool)
                {
                    // Each detector gets its own proxy onto the one shared pass-3 model, so every
                    // concurrent file's pass 3 applies its own language against it (see the proxy).
                    var pass3 = pass3Shared != null ? new Pass3TranscriberProxy(pass3Shared, initialLanguage) : null;
                    channel.Writer.TryWrite(new ChapterDetector(_options, ffmpeg, w, vad, pass3));
                }

                await RunConcurrentlyAsync(files, hardCap, ct, async (file, token) =>
                {
                    var detector = await channel.Reader.ReadAsync(token);
                    try
                    {
                        await ProcessOneAsync(file, ffmpeg, detector, token);
                    }
                    finally
                    {
                        await channel.Writer.WriteAsync(detector, CancellationToken.None);
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

        if (_options.Summary)
        {
            var warningNote = _warnings > 0 ? $", {_warnings} with warnings" : "";
            var noChaptersNote = _noChaptersFound > 0 ? $", {_noChaptersFound} with no chapters found" : "";
            Console.WriteLine(
                $"Summary: {files.Count} file(s) encountered, {_processed} processed, " +
                $"{_skipped} skipped{warningNote}{noChaptersNote}");
            var average = _processed > 0
                ? $", average per processed file: {FormatTime(_processingTime / _processed)}"
                : "";
            Console.WriteLine($"Total time: {FormatTime(watch.Elapsed)}{average}");
            if (_runStats.ConfidenceCount > 0)
                Console.WriteLine(
                    $"Confidence of written chapter marks: min {_runStats.ConfidenceMin:0.00}, " +
                    $"max {_runStats.ConfidenceMax:0.00}, avg {_runStats.ConfidenceSum / _runStats.ConfidenceCount:0.00}");
            if (_runStats.RunLengthSecondsTotal > 0)
            {
                var extremes = new List<string>();
                if (!double.IsPositiveInfinity(_runStats.MinPrecedingSilence))
                    extremes.Add($"shortest silence before a chapter {_runStats.MinPrecedingSilence:0.00} s" +
                                 FormatInterChapter(double.IsPositiveInfinity(_runStats.MinInterChapterSilence) ? null : _runStats.MinInterChapterSilence));
                if (!double.IsNegativeInfinity(_runStats.MaxJingle))
                    extremes.Add($"longest jingle before a chapter {_runStats.MaxJingle:0.00} s" +
                                 FormatInterChapter(double.IsNegativeInfinity(_runStats.MaxInterChapterJingle) ? null : _runStats.MaxInterChapterJingle));
                if (extremes.Count > 0)
                    Console.WriteLine(string.Join(", ", extremes));
                var speed = FormatSpeed(_runStats.WhisperAudioSecondsTotal, _runStats.WhisperTranscribeSecondsTotal);
                Console.WriteLine(
                    $"Whisper audio processed: {FormatTime(TimeSpan.FromSeconds(_runStats.WhisperAudioSecondsTotal))} " +
                    $"of {FormatTime(TimeSpan.FromSeconds(_runStats.RunLengthSecondsTotal))} run length " +
                    $"({100 * _runStats.WhisperAudioSecondsTotal / _runStats.RunLengthSecondsTotal:0.0}%)" +
                    (speed.Length > 0 ? $", {speed}" : ""));
            }
        }
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
        List<string> files, int hardCap, CancellationToken ct, Func<string, CancellationToken, Task> processOne)
    {
        var gate = new AdaptiveConcurrencyGate(hardCap, initialSoftLimit: 1);
        using var monitor = new ConcurrencyMonitor(gate, TimeSpan.FromSeconds(2),
            _options.Verbose ? msg => _progress.Log(msg) : null);

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
            // Task.WhenAll surfaces whichever faulted task it happens to observe first,
            // which need not be the one that failed first chronologically; re-throw the
            // one we tracked so callers see the same failure a sequential run would report.
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
    /// Turns a detection result's chapters into titled <see cref="Chapter"/>s and, when the first
    /// one starts past the very beginning and something was actually spoken there, prepends the
    /// intro chapter - audiobooks open with a prelude, and the mp4 muxer would otherwise snap the
    /// first mark to 0:00. When nothing precedes the first chapter's phrase but silence, music or
    /// a jingle (<see cref="DetectionResult.LeadInHasSpeech"/> false), no intro is inserted either
    /// - there is no spoken prelude to give its own entry, so that same muxer start-snap is left
    /// to fold the lead-in into chapter 1 instead. Shared by the normal write path and the
    /// partial-marks path so both lay out chapters identically. Internal for unit testing.
    /// </summary>
    /// <param name="result">The file's detection result.</param>
    /// <returns>The chapters to write and a note (" + intro" or "") for the summary line.</returns>
    internal static (List<Chapter> Chapters, string IntroNote) BuildChapters(DetectionResult result)
    {
        var chapters = result.Chapters
            .Select(c => new Chapter(c.TimeSeconds, $"{result.Profile.Title} {c.Number}"))
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

    /// <summary>Processes a single audiobook file and prints its summary line.</summary>
    private async Task ProcessOneAsync(
        string file, FfmpegClient ffmpeg, ChapterDetector detector, CancellationToken ct)
    {
        var name = Path.GetFileName(file);
        var work = new WorkTracker();
        var watch = Stopwatch.StartNew();
        _progress.Start(name, work);
        // --verbose log sink; every message is prefixed with the file name.
        var log = _options.Verbose ? (Action<string>)(msg => _progress.Log($"{name}: {msg}")) : null;
        try
        {
            var info = await ffmpeg.ProbeAsync(file, ct);
            log?.Invoke($"probed: duration {FormatTime(TimeSpan.FromSeconds(info.DurationSeconds))}, " +
                        $"codec {info.AudioCodec}" +
                        (info.AudioProfile.Length > 0 ? $" ({info.AudioProfile})" : "") +
                        $", {info.ChapterCount} existing chapter marking(s)");

            // xHE-AAC (USAC) audio: ffmpeg's native AAC decoder cannot handle it reliably,
            // so decode such files with libfdk_aac - or skip them when it is unavailable.
            if (info.IsXheAac)
            {
                // A probe that could not even determine the duration means this ffmpeg
                // build cannot handle the file regardless of the decoder list.
                if (info.DurationSeconds > 0 && await ffmpeg.SupportsLibFdkAacAsync(ct))
                {
                    info = info with { InputDecoder = "libfdk_aac" };
                    log?.Invoke("xHE-AAC audio: decoding with libfdk_aac");
                }
                else
                {
                    lock (_statsLock) _warnings++;
                    _progress.FinishWithSummary(work, $"{name}: WARNING - {XheAacHint}", important: true);
                    return;
                }
            }

            // Auto-resume a ".missing-marks-<n>-<n>-..." file left behind by a previous run's
            // unresolved chapter-sequence gap: only the still-missing gap(s) are re-probed, the
            // committed markings are trusted as-is. --force means "redo the whole file from
            // scratch" and takes priority - it falls through to the normal policy below, which
            // discards every existing marking (including these) and runs a fresh full detection.
            if (!_options.Force && HasMissingMarksTag(file))
            {
                var resumed = await detector.ResumeMissingMarksAsync(file, info, work, log, ct);
                lock (_statsLock)
                {
                    _processed++;
                    _processingTime += watch.Elapsed;
                }
                _runStats.AccumulateStats(resumed.Stats, info.DurationSeconds);
                log?.Invoke(FormatFileStats(resumed.Stats, info.DurationSeconds));
                _runStats.AccumulateConfidence(resumed.Chapters);
                var (resumedChapters, resumedIntroNote) = BuildChapters(resumed);

                if (resumed.GapRemains)
                {
                    lock (_statsLock) _warnings++;
                    var retarget = MissingMarksPath(file, resumed.MissingNumbers);
                    var stillMissing = FormatMissingList(resumed.MissingNumbers);
                    if (_options.DryRun)
                    {
                        var partialListing = string.Join(Environment.NewLine,
                            resumedChapters.Select(c => $"  {FormatTimestamp(c.StartSeconds)}  {c.Title}"));
                        _progress.FinishWithSummary(work,
                            $"{name}: DRY RUN - resume incomplete, still missing: {stillMissing}; would write " +
                            $"{resumed.Chapters.Count} partial mark(s){resumedIntroNote} and re-tag as " +
                            $"{Path.GetFileName(retarget)}:{Environment.NewLine}{partialListing}", important: true);
                        return;
                    }
                    var resumedPartialBakReplaced = await ffmpeg.WriteChaptersAsync(file, resumedChapters, info.DurationSeconds, _options.Backup,
                        BeginMuxingPhase(work, info), ct);
                    File.Move(file, retarget, overwrite: true);
                    var resumedPartialBackup = FormatBackupNote(_options.Backup, resumedPartialBakReplaced);
                    _progress.FinishWithSummary(work,
                        $"{name}: WARNING - resume incomplete, still missing: {stillMissing}; wrote " +
                        $"{resumed.Chapters.Count} partial mark(s){resumedIntroNote}, re-tagged as " +
                        $"{Path.GetFileName(retarget)}{resumedPartialBackup}", important: true);
                    return;
                }

                var restored = StripMissingMarksPath(file);
                if (_options.DryRun)
                {
                    var listing = string.Join(Environment.NewLine,
                        resumedChapters.Select(c => $"  {FormatTimestamp(c.StartSeconds)}  {c.Title}"));
                    _progress.FinishWithSummary(work,
                        $"{name}: DRY RUN - resume complete, all chapters found; would write " +
                        $"{resumed.Chapters.Count} chapter(s) ({resumed.Chapters[0].Number}-{resumed.Chapters[^1].Number})" +
                        $"{resumedIntroNote} and rename to {Path.GetFileName(restored)}:{Environment.NewLine}{listing}");
                    return;
                }
                var restoredBakReplaced = await ffmpeg.WriteChaptersAsync(file, resumedChapters, info.DurationSeconds, _options.Backup,
                    BeginMuxingPhase(work, info), ct);
                File.Move(file, restored, overwrite: true);
                var restoredBackup = FormatBackupNote(_options.Backup, restoredBakReplaced);
                _progress.FinishWithSummary(work,
                    $"{name}: resume complete - {resumed.Chapters.Count} chapter(s) written " +
                    $"({resumed.Chapters[0].Number}-{resumed.Chapters[^1].Number}){resumedIntroNote}, renamed to " +
                    $"{Path.GetFileName(restored)}{restoredBackup}");
                return;
            }

            // Policy for pre-existing chapter markings.
            var (skip, discardNote) = EvaluateExistingChapters(info);
            DetectionResult result;
            if (skip && _options.Verify)
            {
                var verify = await detector.VerifyExistingChaptersAsync(file, info, work, log, ct);
                if (verify.Checked == 0 || verify.Passed)
                {
                    lock (_statsLock) _skipped++;
                    var verifyNote = verify.Checked > 0
                        ? $"{verify.Checked} pre-existing chapter marking(s) verified correct"
                        : $"has {info.ChapterCount} chapter marking(s) (none had a checkable number)";
                    _progress.FinishWithSummary(work, $"{name}: skipped - {verifyNote} (use --force to redo)");
                    return;
                }
                // --verify-threshold: too many unconfirmed markings means even the survivors are
                // no longer trusted as gap-recovery anchors - the whole set is treated exactly
                // like the "nothing confirmed" case below.
                var thresholdExceeded = _options.VerifyFailThreshold is { } threshold && verify.Failed > threshold;
                if (verify.ConfirmedChapters.Count > 0 && !thresholdExceeded)
                {
                    // At least one marking is trusted - only the gap(s) around the unconfirmed
                    // one(s) get their own Pass 2 (and, for a still-missing trailing chapter,
                    // Pass 3); everything else in the file is left exactly as --verify found it.
                    discardNote = $", {verify.ConfirmedChapters.Count} of {info.ChapterCount} existing " +
                                  $"marking(s) trusted, {verify.Failed} unconfirmed one(s) gap-recovered";
                    result = await detector.DetectGapsAsync(file, info, work, log, verify, ct);
                }
                else
                {
                    // Nothing survived verification, or too many markings failed for the
                    // --verify-threshold to keep trusting the rest - no anchor(s) trustworthy
                    // enough to scope a gap recovery around, so fall back to a full whole-file
                    // redetect.
                    var thresholdNote = thresholdExceeded
                        ? $", exceeding --verify-threshold {_options.VerifyFailThreshold}"
                        : "";
                    discardNote = $", {info.ChapterCount} existing marking(s) discarded " +
                                  $"(--verify: {verify.Failed} of {verify.Checked} checked mark(s) not confirmed{thresholdNote})";
                    result = await detector.DetectAsync(file, info, work, log, ct);
                }
            }
            else if (skip)
            {
                lock (_statsLock) _skipped++;
                _progress.FinishWithSummary(work,
                    $"{name}: skipped - has {info.ChapterCount} chapter marking(s) (use --force to redo)");
                return;
            }
            else
            {
                result = await detector.DetectAsync(file, info, work, log, ct);
            }

            lock (_statsLock)
            {
                _processed++;
                _processingTime += watch.Elapsed;
            }
            _runStats.AccumulateStats(result.Stats, info.DurationSeconds);
            log?.Invoke(FormatFileStats(result.Stats, info.DurationSeconds));

            if (result.GapRemains)
            {
                lock (_statsLock) _warnings++;
                // Rather than discard the work, commit the marks found so far and flag the file by
                // name (".missing-marks-<n>-<n>-...") so the still-missing chapters are visible and
                // a future run can pick them up. (Re-processing such files is a separate TODO item.)
                _runStats.AccumulateConfidence(result.Chapters);
                var (partial, partialIntro) = BuildChapters(result);
                var target = MissingMarksPath(file, result.MissingNumbers);
                var missingList = FormatMissingList(result.MissingNumbers);
                if (_options.DryRun)
                {
                    var partialListing = string.Join(Environment.NewLine,
                        partial.Select(c => $"  {FormatTimestamp(c.StartSeconds)}  {c.Title}"));
                    _progress.FinishWithSummary(work,
                        $"{name}: DRY RUN - unresolved chapter sequence gap (missing: {missingList}); " +
                        $"would write {result.Chapters.Count} partial mark(s){partialIntro} and rename to " +
                        $"{Path.GetFileName(target)}:{Environment.NewLine}{partialListing}", important: true);
                    return;
                }
                var partialBakReplaced = await ffmpeg.WriteChaptersAsync(file, partial, info.DurationSeconds, _options.Backup,
                    BeginMuxingPhase(work, info), ct);
                File.Move(file, target, overwrite: true);
                var partialBackup = FormatBackupNote(_options.Backup, partialBakReplaced);
                _progress.FinishWithSummary(work,
                    $"{name}: WARNING - unresolved chapter sequence gap (missing: {missingList}); " +
                    $"wrote {result.Chapters.Count} partial mark(s){partialIntro}, renamed to " +
                    $"{Path.GetFileName(target)}{partialBackup}", important: true);
                return;
            }
            if (result.Chapters.Count == 0)
            {
                lock (_statsLock) _noChaptersFound++;
                var langHint = _options.AutoLanguage ? $" (language used: {result.Profile.Language})" : "";
                var summary = result.EarlyAborted
                    ? $"{name}: early-abort - no chapter found within the first " +
                      $"{_options.EarlyAbortMinutes:0.#} minute(s) of play time; file unchanged{langHint}"
                    : result.BelowExpectedStartNumber is { } foundNumber
                        ? $"{name}: first chapter found ({foundNumber}) is below --expected-start-chapter " +
                          $"{_options.ExpectedStartChapter}; file unchanged{langHint}"
                        : $"{name}: no chapter phrases found; file unchanged{langHint}";
                _progress.FinishWithSummary(work, summary);
                return;
            }

            _runStats.AccumulateConfidence(result.Chapters);

            var (chapters, introNote) = BuildChapters(result);

            var lowConfidenceNote = result.LowConfidenceNumbers.Count > 0
                ? $", {result.LowConfidenceNumbers.Count} low-confidence mark(s) " +
                  $"(chapter {string.Join(", ", result.LowConfidenceNumbers)}; see --verbose)"
                : "";

            // With --lang auto, note which language was actually used for this file - the
            // detected one, or "en" when detection was inconclusive or skipped.
            var languageNote = "";
            if (_options.AutoLanguage)
            {
                languageNote = result.DetectedLanguage switch
                {
                    { } lang when lang.Equals(result.Profile.Language, StringComparison.OrdinalIgnoreCase) =>
                        $", language: {result.Profile.Language} (p={result.DetectedProbability:0.00})",
                    { } lang =>
                        $", language: {result.Profile.Language} (auto-detected {lang} p={result.DetectedProbability:0.00}, below threshold)",
                    null => $", language: {result.Profile.Language} (auto-detection unavailable)",
                };
            }

            // --export writes the sidecar regardless of --dry-run, so a run can be
            // previewed and saved for hand-editing in one pass.
            var exportNote = "";
            if (_options.Export)
            {
                var sidecarPath = ChapterSidecar.PathFor(file, _options.SimpleMetadata);
                var sidecarText = _options.SimpleMetadata
                    ? ChapterSidecar.BuildSimple(chapters)
                    : FfmpegClient.BuildFfMetadata(chapters, info.DurationSeconds);
                await File.WriteAllTextAsync(sidecarPath, sidecarText, new UTF8Encoding(false), ct);
                exportNote = $", sidecar exported to {Path.GetFileName(sidecarPath)}";
            }

            if (_options.DryRun)
            {
                var listing = string.Join(Environment.NewLine,
                    chapters.Select(c => $"  {FormatTimestamp(c.StartSeconds)}  {c.Title}"));
                _progress.FinishWithSummary(work,
                    $"{name}: DRY RUN - would write {result.Chapters.Count} chapter(s) " +
                    $"({result.Chapters[0].Number}-{result.Chapters[^1].Number})" +
                    $"{introNote}{discardNote}{lowConfidenceNote}{languageNote}{exportNote}:{Environment.NewLine}{listing}",
                    important: lowConfidenceNote.Length > 0);
                return;
            }

            var bakReplaced = await ffmpeg.WriteChaptersAsync(file, chapters, info.DurationSeconds, _options.Backup,
                BeginMuxingPhase(work, info), ct);

            var backupNote = FormatBackupNote(_options.Backup, bakReplaced);
            _progress.FinishWithSummary(work,
                $"{name}: {result.Chapters.Count} chapter(s) written " +
                $"({result.Chapters[0].Number}-{result.Chapters[^1].Number})" +
                $"{introNote}{discardNote}{lowConfidenceNote}{languageNote}{exportNote}{backupNote}",
                important: lowConfidenceNote.Length > 0);
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
    /// writes the chapters it contains, without running Whisper detection at all.
    /// </summary>
    private async Task ProcessOneImportAsync(string file, FfmpegClient ffmpeg, CancellationToken ct)
    {
        var name = Path.GetFileName(file);
        var work = new WorkTracker();
        var watch = Stopwatch.StartNew();
        _progress.Start(name, work);
        var log = _options.Verbose ? (Action<string>)(msg => _progress.Log($"{name}: {msg}")) : null;
        try
        {
            var sidecarPath = ChapterSidecar.PathFor(file, _options.SimpleMetadata);
            if (!File.Exists(sidecarPath))
            {
                _progress.FinishWithSummary(work,
                    $"{name}: skipped - no sidecar file found ({Path.GetFileName(sidecarPath)}); use --export to create one",
                    important: true);
                return;
            }

            var info = await ffmpeg.ProbeAsync(file, ct);
            log?.Invoke($"probed: duration {FormatTime(TimeSpan.FromSeconds(info.DurationSeconds))}, " +
                        $"codec {info.AudioCodec}" +
                        (info.AudioProfile.Length > 0 ? $" ({info.AudioProfile})" : "") +
                        $", {info.ChapterCount} existing chapter marking(s)");

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
            lock (_statsLock)
            {
                _processed++;
                _processingTime += watch.Elapsed;
            }

            if (_options.DryRun)
            {
                var listing = string.Join(Environment.NewLine,
                    chapters.Select(c => $"  {FormatTimestamp(c.StartSeconds)}  {c.Title}"));
                _progress.FinishWithSummary(work,
                    $"{name}: DRY RUN - would import {chapters.Count} chapter(s) from " +
                    $"{Path.GetFileName(sidecarPath)}{discardNote}:{Environment.NewLine}{listing}");
                return;
            }

            var bakReplaced = await ffmpeg.WriteChaptersAsync(file, chapters, info.DurationSeconds, _options.Backup,
                BeginMuxingPhase(work, info), ct);

            var backupNote = FormatBackupNote(_options.Backup, bakReplaced);
            _progress.FinishWithSummary(work,
                $"{name}: {chapters.Count} chapter(s) imported from {Path.GetFileName(sidecarPath)}" +
                $"{discardNote}{backupNote}");
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
    /// Builds the ordered list of files to work on, honoring --recurse. Temporary files
    /// created by this tool are always excluded. Internal for unit testing.
    /// </summary>
    /// <param name="suffixes">Case-insensitive file name suffixes to accept.</param>
    internal List<string> EnumerateTargets(string[] suffixes)
    {
        IEnumerable<string> candidates;
        if (_options.TargetIsDirectory)
        {
            var searchOption = _options.Recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            candidates = Directory.EnumerateFiles(_options.TargetPath, "*", searchOption);
        }
        else
        {
            candidates = [_options.TargetPath];
        }

        return candidates
            .Where(f => suffixes.Any(s => f.EndsWith(s, StringComparison.OrdinalIgnoreCase)))
            .Where(f => !f.Contains(".abchapterize.", StringComparison.OrdinalIgnoreCase))
            .Where(f => _options.Revert || !f.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
            .Where(f => _options.FilterRegex == null || _options.FilterRegex.IsMatch(f))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
