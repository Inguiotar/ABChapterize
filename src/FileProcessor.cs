using System.Diagnostics;

namespace Chapterize;

/// <summary>
/// Orchestrates the whole run: file enumeration, revert handling, per-file chapter
/// detection and writing, plus the one-line-per-file console reporting.
/// </summary>
public sealed class FileProcessor
{
    private readonly CliOptions _options;
    private readonly ProgressRenderer _progress;

    /// <summary>Number of files for which processing was aborted with a warning.</summary>
    public int WarningCount { get; private set; }

    /// <summary>Number of files skipped because of pre-existing chapter markings.</summary>
    private int _skipped;

    /// <summary>Number of files that actually went through chapter detection.</summary>
    private int _processed;

    /// <summary>Accumulated detection time of the processed files (for the --summary average).</summary>
    private TimeSpan _processingTime;

    /// <summary>Creates a processor for the given validated options.</summary>
    /// <param name="options">Validated command line options.</param>
    /// <param name="progress">Renderer for progress bars and summary lines.</param>
    public FileProcessor(CliOptions options, ProgressRenderer progress)
    {
        _options = options;
        _progress = progress;
    }

    /// <summary>Runs the tool in the mode selected by the options (revert or chapterize).</summary>
    /// <param name="ct">Cancellation token bound to Ctrl+C.</param>
    public async Task RunAsync(CancellationToken ct)
    {
        if (_options.Revert)
        {
            RunRevert(ct);
            return;
        }
        await RunChapterizeAsync(ct);
    }

    /// <summary>
    /// Restores backups: for every supported audio file with an added ".bak" suffix the
    /// corresponding original is deleted and the backup renamed back to its original name.
    /// </summary>
    private void RunRevert(CancellationToken ct)
    {
        var bakSuffixes = CliOptions.SupportedExtensions.Select(e => e + ".bak").ToArray();
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
    private async Task RunChapterizeAsync(CancellationToken ct)
    {
        var files = EnumerateTargets(CliOptions.SupportedExtensions);
        if (files.Count == 0)
        {
            Console.WriteLine($"No supported audio files ({CliOptions.SupportedExtensionsText}) found.");
            return;
        }

        var (ffmpegPath, ffprobePath) = FfmpegLocator.Locate();
        var ffmpeg = new FfmpegClient(ffmpegPath, ffprobePath);

        var modelPath = await ModelCatalog.EnsureModelAsync(_options.Model, ct);
        await using var whisper = new WhisperTranscriber(modelPath, _options.Language);
        if (!_options.Quiet)
            Console.WriteLine($"Whisper model \"{_options.Model}\" loaded ({whisper.RuntimeName} backend), " +
                              $"{files.Count} file(s) to process.");

        var watch = Stopwatch.StartNew();
        var detector = new ChapterDetector(_options, ffmpeg, whisper);
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            await ProcessOneAsync(file, ffmpeg, detector, ct);
        }

        if (_options.Summary)
        {
            var warningNote = WarningCount > 0 ? $", {WarningCount} with warnings" : "";
            Console.WriteLine(
                $"Summary: {files.Count} file(s) encountered, {_processed} processed, " +
                $"{_skipped} skipped{warningNote}");
            var average = _processed > 0
                ? $", average per processed file: {FormatTime(_processingTime / _processed)}"
                : "";
            Console.WriteLine($"Total time: {FormatTime(watch.Elapsed)}{average}");
        }
    }

    /// <summary>Formats a duration as h:mm:ss (or m:ss below one hour) for the summary.</summary>
    /// <param name="t">The duration to format.</param>
    private static string FormatTime(TimeSpan t)
        => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    /// <summary>Processes a single audiobook file and prints its summary line.</summary>
    private async Task ProcessOneAsync(
        string file, FfmpegClient ffmpeg, ChapterDetector detector, CancellationToken ct)
    {
        var name = Path.GetFileName(file);
        var work = new WorkTracker();
        var watch = Stopwatch.StartNew();
        _progress.Start(name, work);
        try
        {
            var info = await ffmpeg.ProbeAsync(file, ct);

            // Policy for pre-existing chapter markings.
            var discardNote = "";
            if (info.ChapterCount > 0)
            {
                var bogus = _options.MaxChapters is { } max && info.ChapterCount > max;
                if (!_options.Force && !bogus)
                {
                    _skipped++;
                    _progress.FinishWithSummary(
                        $"{name}: skipped - has {info.ChapterCount} chapter marking(s) (use --force to redo)");
                    return;
                }
                discardNote = bogus && !_options.Force
                    ? $", {info.ChapterCount} bogus marking(s) discarded (> --max-chapters)"
                    : $", {info.ChapterCount} existing marking(s) discarded";
            }

            var result = await detector.DetectAsync(file, info, work, ct);
            _processed++;
            _processingTime += watch.Elapsed;

            if (result.GapRemains)
            {
                WarningCount++;
                _progress.FinishWithSummary(
                    $"{name}: WARNING - unresolved chapter sequence gap (missing: " +
                    $"{string.Join(", ", result.MissingNumbers)}); file unchanged", important: true);
                return;
            }
            if (result.Chapters.Count == 0)
            {
                _progress.FinishWithSummary($"{name}: no chapter phrases found; file unchanged");
                return;
            }

            var chapters = result.Chapters
                .Select(c => new Chapter(c.TimeSeconds, $"{_options.Title} {c.Number}"))
                .ToList();
            // Audiobooks usually start with a prelude, and the mp4 muxer silently moves the
            // first chapter mark to 0:00. Prepend an intro chapter so the first detected
            // chapter keeps its real start time.
            var introNote = "";
            if (chapters[0].StartSeconds > 1.0)
            {
                chapters.Insert(0, new Chapter(0, _options.IntroTitle));
                introNote = " + intro";
            }
            await ffmpeg.WriteChaptersAsync(file, chapters, info.DurationSeconds, _options.Backup, ct);

            var backupNote = _options.Backup ? ", backup kept" : "";
            _progress.FinishWithSummary(
                $"{name}: {result.Chapters.Count} chapter(s) written " +
                $"({result.Chapters[0].Number}-{result.Chapters[^1].Number}){introNote}{discardNote}{backupNote}");
        }
        catch (OperationCanceledException)
        {
            _progress.FinishWithSummary($"{name}: aborted");
            throw;
        }
        catch (AppError ex)
        {
            _progress.FinishWithSummary($"{name}: ERROR - {ex.Message}", important: true);
            throw;
        }
    }

    /// <summary>
    /// Builds the ordered list of files to work on, honoring --recurse. Temporary files
    /// created by this tool are always excluded.
    /// </summary>
    /// <param name="suffixes">Case-insensitive file name suffixes to accept.</param>
    private List<string> EnumerateTargets(string[] suffixes)
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
            .Where(f => !f.Contains(".chapterize.", StringComparison.OrdinalIgnoreCase))
            .Where(f => _options.Revert || !f.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
