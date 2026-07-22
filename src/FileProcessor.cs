// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using System.Diagnostics;
using System.Text;
using System.Threading.Channels;

namespace ABChapterize;

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

    /// <summary>Accumulated detection time of the processed files (for the --summary average).</summary>
    private TimeSpan _processingTime;

    /// <summary>Confidence stats (for --summary) across every chapter mark actually written,
    /// run-wide - not every probe attempted, only the ones that produced a mark.</summary>
    private double _confidenceMin = double.PositiveInfinity;
    private double _confidenceMax = double.NegativeInfinity;
    private double _confidenceSum;
    private int _confidenceCount;

    /// <summary>Run-wide detection statistics (for --summary), aggregated across every processed
    /// file: the shortest silence and longest jingle seen before any chapter (each both over all
    /// chapters and over the inter-chapter subset that excludes chapter 1), and the total audio
    /// fed to Whisper and time spent transcribing it, against the total run length processed. The
    /// silence/jingle extremes stay at their infinities when no file contributed one.</summary>
    private double _minPrecedingSilence = double.PositiveInfinity;
    private double _minInterChapterSilence = double.PositiveInfinity;
    private double _maxJingle = double.NegativeInfinity;
    private double _maxInterChapterJingle = double.NegativeInfinity;
    private double _whisperAudioSecondsTotal;
    private double _whisperTranscribeSecondsTotal;
    private double _runLengthSecondsTotal;

    /// <summary>Creates a processor for the given validated options.</summary>
    /// <param name="options">Validated command line options.</param>
    /// <param name="progress">Renderer for progress bars and summary lines.</param>
    public FileProcessor(CliOptions options, ProgressRenderer progress)
    {
        _options = options;
        _progress = progress;
    }

    /// <summary>Runs the tool in the mode selected by the options (revert or abchapterize).</summary>
    /// <param name="ct">Cancellation token bound to Ctrl+C.</param>
    public async Task RunAsync(CancellationToken ct)
    {
        if (_options.Revert)
        {
            RunRevert(ct);
            return;
        }
        await RunABChapterizeAsync(ct);
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
            var first = new WhisperTranscriber(modelPath, initialLanguage);

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
                ? new SharedPass3Transcriber(_options.Pass3Model, initialLanguage)
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
                    .Select(_ => new WhisperTranscriber(modelPath, initialLanguage, threadsPerInstance))];
            }

            // Shared like ffmpeg above (one instance for the whole run, safe for concurrent
            // use - see SileroVadDetector's threading remarks); only needed with --jingle,
            // where ChapterDetector runs its full-file VAD pre-pass.
            using var vad = _options.Jingle ? new SileroVadDetector() : null;

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
            Console.WriteLine(
                $"Summary: {files.Count} file(s) encountered, {_processed} processed, " +
                $"{_skipped} skipped{warningNote}");
            var average = _processed > 0
                ? $", average per processed file: {FormatTime(_processingTime / _processed)}"
                : "";
            Console.WriteLine($"Total time: {FormatTime(watch.Elapsed)}{average}");
            if (_confidenceCount > 0)
                Console.WriteLine(
                    $"Confidence of written chapter marks: min {_confidenceMin:0.00}, " +
                    $"max {_confidenceMax:0.00}, avg {_confidenceSum / _confidenceCount:0.00}");
            if (_runLengthSecondsTotal > 0)
            {
                var extremes = new List<string>();
                if (!double.IsPositiveInfinity(_minPrecedingSilence))
                    extremes.Add($"shortest silence before a chapter {_minPrecedingSilence:0.00} s" +
                                 FormatInterChapter(double.IsPositiveInfinity(_minInterChapterSilence) ? null : _minInterChapterSilence));
                if (!double.IsNegativeInfinity(_maxJingle))
                    extremes.Add($"longest jingle before a chapter {_maxJingle:0.00} s" +
                                 FormatInterChapter(double.IsNegativeInfinity(_maxInterChapterJingle) ? null : _maxInterChapterJingle));
                if (extremes.Count > 0)
                    Console.WriteLine(string.Join(", ", extremes));
                var speed = FormatSpeed(_whisperAudioSecondsTotal, _whisperTranscribeSecondsTotal);
                Console.WriteLine(
                    $"Whisper audio processed: {FormatTime(TimeSpan.FromSeconds(_whisperAudioSecondsTotal))} " +
                    $"of {FormatTime(TimeSpan.FromSeconds(_runLengthSecondsTotal))} run length " +
                    $"({100 * _whisperAudioSecondsTotal / _runLengthSecondsTotal:0.0}%)" +
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

    /// <summary>Formats a duration as h:mm:ss (or m:ss below one hour) for the summary.</summary>
    /// <param name="t">The duration to format.</param>
    private static string FormatTime(TimeSpan t)
        => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

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

    /// <summary>Formats a chapter mark position as h:mm:ss.ff for the --dry-run listing.</summary>
    /// <param name="seconds">Position in seconds.</param>
    private static string FormatTimestamp(double seconds)
        => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"h\:mm\:ss\.ff");

    /// <summary>Formats a "(NN.N% of run length)" share, or empty when the run length is unknown.</summary>
    /// <param name="part">The measured quantity (seconds).</param>
    /// <param name="whole">The file's or run's total length (seconds).</param>
    private static string FormatPercent(double part, double whole)
        => whole > 0 ? $" ({100 * part / whole:0.0}% of run length)" : "";

    /// <summary>Appends " (inter-chapter Y.YY s)" when the chapter-1-excluded value is present,
    /// marking the extreme taken over the book's regular chapter breaks alone.</summary>
    /// <param name="interChapter">The extreme excluding chapter 1, or null when unavailable.</param>
    private static string FormatInterChapter(double? interChapter)
        => interChapter is { } v ? $" (inter-chapter {v:0.00} s)" : "";

    /// <summary>Formats the Whisper transcription speed as a "NNN% of real-time" clause (audio
    /// transcribed over the wall-clock time it took), or empty when it could not be measured.</summary>
    /// <param name="audioSeconds">Audio handed to Whisper.</param>
    /// <param name="transcribeSeconds">Wall-clock time spent transcribing it.</param>
    private static string FormatSpeed(double audioSeconds, double transcribeSeconds)
        => transcribeSeconds > 0 ? $"transcription speed {100 * audioSeconds / transcribeSeconds:0} % of real-time" : "";

    /// <summary>
    /// Builds the per-file statistics log line shown under --verbose (or --verbose-transcripts):
    /// the shortest silence and, in --jingle mode, the longest jingle preceding a detected chapter
    /// (each with its inter-chapter, chapter-1-excluded counterpart), the total audio fed to
    /// Whisper and its share of the file's run length, and the transcription speed.
    /// </summary>
    /// <param name="stats">The file's detection statistics.</param>
    /// <param name="runLengthSeconds">The file's run length, for the Whisper-audio share.</param>
    private static string FormatFileStats(DetectionStats stats, double runLengthSeconds)
    {
        var parts = new List<string>();
        if (stats.MinPrecedingSilenceSeconds is { } silence)
            parts.Add($"shortest silence before a chapter {silence:0.00} s" +
                      FormatInterChapter(stats.MinInterChapterSilenceSeconds));
        if (stats.MaxJingleLengthSeconds is { } jingle)
            parts.Add($"longest jingle {jingle:0.00} s" +
                      FormatInterChapter(stats.MaxInterChapterJingleSeconds));
        parts.Add($"Whisper audio {FormatTime(TimeSpan.FromSeconds(stats.WhisperAudioSeconds))}" +
                  FormatPercent(stats.WhisperAudioSeconds, runLengthSeconds));
        if (FormatSpeed(stats.WhisperAudioSeconds, stats.WhisperTranscribeSeconds) is { Length: > 0 } speed)
            parts.Add(speed);
        return "stats - " + string.Join(", ", parts);
    }

    /// <summary>Folds one processed file's detection statistics into the run-wide totals for the
    /// --summary report. Must be called while holding <see cref="_statsLock"/>.</summary>
    /// <param name="stats">The file's detection statistics.</param>
    /// <param name="runLengthSeconds">The file's run length, summed toward the run-wide share.</param>
    private void AccumulateStats(DetectionStats stats, double runLengthSeconds)
    {
        if (stats.MinPrecedingSilenceSeconds is { } silence)
            _minPrecedingSilence = Math.Min(_minPrecedingSilence, silence);
        if (stats.MinInterChapterSilenceSeconds is { } interSilence)
            _minInterChapterSilence = Math.Min(_minInterChapterSilence, interSilence);
        if (stats.MaxJingleLengthSeconds is { } jingle)
            _maxJingle = Math.Max(_maxJingle, jingle);
        if (stats.MaxInterChapterJingleSeconds is { } interJingle)
            _maxInterChapterJingle = Math.Max(_maxInterChapterJingle, interJingle);
        _whisperAudioSecondsTotal += stats.WhisperAudioSeconds;
        _whisperTranscribeSecondsTotal += stats.WhisperTranscribeSeconds;
        _runLengthSecondsTotal += runLengthSeconds;
    }

    /// <summary>
    /// Turns a detection result's chapters into titled <see cref="Chapter"/>s and, when the first
    /// one starts past the very beginning, prepends the intro chapter - audiobooks open with a
    /// prelude, and the mp4 muxer would otherwise snap the first mark to 0:00. Shared by the normal
    /// write path and the partial-marks path so both lay out chapters identically.
    /// </summary>
    /// <param name="result">The file's detection result.</param>
    /// <returns>The chapters to write and a note (" + intro" or "") for the summary line.</returns>
    private static (List<Chapter> Chapters, string IntroNote) BuildChapters(DetectionResult result)
    {
        var chapters = result.Chapters
            .Select(c => new Chapter(c.TimeSeconds, $"{result.Profile.Title} {c.Number}"))
            .ToList();
        if (chapters.Count > 0 && chapters[0].StartSeconds > 1.0)
        {
            chapters.Insert(0, new Chapter(0, result.Profile.IntroTitle));
            return (chapters, " + intro");
        }
        return (chapters, "");
    }

    /// <summary>Folds a set of written chapter marks into the run-wide confidence stats
    /// (for --summary). Takes <see cref="_statsLock"/> itself.</summary>
    /// <param name="chapters">The chapters whose confidences were actually written.</param>
    private void AccumulateConfidence(IEnumerable<DetectedChapter> chapters)
    {
        lock (_statsLock)
            foreach (var c in chapters)
            {
                _confidenceSum += c.Confidence;
                _confidenceCount++;
                _confidenceMin = Math.Min(_confidenceMin, c.Confidence);
                _confidenceMax = Math.Max(_confidenceMax, c.Confidence);
            }
    }

    /// <summary>
    /// Builds the name a file is renamed to when pass 3 leaves an unresolved chapter-sequence gap:
    /// the original name with a ".missing-marks-&lt;n&gt;-&lt;n&gt;-..." tag (the still-missing
    /// chapter numbers, "-"-delimited) inserted before the extension, e.g.
    /// "Book.m4b" with chapters 3 and 7 missing becomes "Book.missing-marks-3-7.m4b". Any such tag
    /// already present is replaced rather than stacked. Internal for unit testing.
    /// </summary>
    /// <param name="file">Path of the file being renamed.</param>
    /// <param name="missingNumbers">The chapter numbers still missing after pass 3.</param>
    internal static string MissingMarksPath(string file, IReadOnlyList<int> missingNumbers)
    {
        var dir = Path.GetDirectoryName(file) ?? "";
        var stem = StripMissingMarksTag(Path.GetFileNameWithoutExtension(file));
        var ext = Path.GetExtension(file);
        return Path.Combine(dir, $"{stem}.missing-marks-{string.Join("-", missingNumbers)}{ext}");
    }

    /// <summary>Removes a trailing ".missing-marks-&lt;digits and dashes&gt;" tag from a file
    /// stem, so re-tagging an already-tagged file replaces the tag instead of appending a second.</summary>
    /// <param name="stem">File name without directory or extension.</param>
    private static string StripMissingMarksTag(string stem)
        => System.Text.RegularExpressions.Regex.Replace(stem, @"\.missing-marks-[0-9-]+$", "");

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

            // Policy for pre-existing chapter markings.
            var (skip, discardNote) = EvaluateExistingChapters(info);
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
                skip = false;
                discardNote = $", {info.ChapterCount} existing marking(s) discarded " +
                              $"(--verify: {verify.Failed} of {verify.Checked} checked mark(s) not confirmed)";
            }
            if (skip)
            {
                lock (_statsLock) _skipped++;
                _progress.FinishWithSummary(work,
                    $"{name}: skipped - has {info.ChapterCount} chapter marking(s) (use --force to redo)");
                return;
            }

            var result = await detector.DetectAsync(file, info, work, log, ct);
            lock (_statsLock)
            {
                _processed++;
                _processingTime += watch.Elapsed;
                AccumulateStats(result.Stats, info.DurationSeconds);
            }
            log?.Invoke(FormatFileStats(result.Stats, info.DurationSeconds));

            if (result.GapRemains)
            {
                lock (_statsLock) _warnings++;
                // Rather than discard the work, commit the marks found so far and flag the file by
                // name (".missing-marks-<n>-<n>-...") so the still-missing chapters are visible and
                // a future run can pick them up. (Re-processing such files is a separate TODO item.)
                AccumulateConfidence(result.Chapters);
                var (partial, partialIntro) = BuildChapters(result);
                var target = MissingMarksPath(file, result.MissingNumbers);
                var missingList = string.Join(", ", result.MissingNumbers);
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
                await ffmpeg.WriteChaptersAsync(file, partial, info.DurationSeconds, _options.Backup,
                    BeginMuxingPhase(work, info), ct);
                File.Move(file, target, overwrite: true);
                var partialBackup = _options.Backup ? ", backup kept" : "";
                _progress.FinishWithSummary(work,
                    $"{name}: WARNING - unresolved chapter sequence gap (missing: {missingList}); " +
                    $"wrote {result.Chapters.Count} partial mark(s){partialIntro}, renamed to " +
                    $"{Path.GetFileName(target)}{partialBackup}", important: true);
                return;
            }
            if (result.Chapters.Count == 0)
            {
                var langHint = _options.AutoLanguage ? $" (language used: {result.Profile.Language})" : "";
                _progress.FinishWithSummary(work, $"{name}: no chapter phrases found; file unchanged{langHint}");
                return;
            }

            AccumulateConfidence(result.Chapters);

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

            await ffmpeg.WriteChaptersAsync(file, chapters, info.DurationSeconds, _options.Backup,
                BeginMuxingPhase(work, info), ct);

            var backupNote = _options.Backup ? ", backup kept" : "";
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

            await ffmpeg.WriteChaptersAsync(file, chapters, info.DurationSeconds, _options.Backup,
                BeginMuxingPhase(work, info), ct);

            var backupNote = _options.Backup ? ", backup kept" : "";
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
