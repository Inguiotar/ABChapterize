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
    /// Restores backups: for every *.m4a.bak / *.m4b.bak the corresponding original is
    /// deleted and the backup renamed back to its original name.
    /// </summary>
    private void RunRevert(CancellationToken ct)
    {
        var backups = EnumerateTargets([".m4a.bak", ".m4b.bak"]);
        // Convenience: when a single .m4a/.m4b file is given, revert its backup.
        if (backups.Count == 0 && !_options.TargetIsDirectory && File.Exists(_options.TargetPath + ".bak"))
            backups = [_options.TargetPath + ".bak"];
        if (backups.Count == 0)
        {
            Console.WriteLine("No .m4a.bak/.m4b.bak files found; nothing to revert.");
            return;
        }
        foreach (var bak in backups)
        {
            ct.ThrowIfCancellationRequested();
            var original = bak[..^4]; // strip ".bak"
            if (File.Exists(original))
                File.Delete(original);
            File.Move(bak, original);
            Console.WriteLine($"{Path.GetFileName(original)}: reverted from backup");
        }
    }

    /// <summary>Runs chapter detection and writing for all selected files.</summary>
    private async Task RunChapterizeAsync(CancellationToken ct)
    {
        var files = EnumerateTargets([".m4a", ".m4b"]);
        if (files.Count == 0)
        {
            Console.WriteLine("No .m4a/.m4b files found.");
            return;
        }

        var (ffmpegPath, ffprobePath) = FfmpegLocator.Locate();
        var ffmpeg = new FfmpegClient(ffmpegPath, ffprobePath);

        var modelPath = ModelCatalog.GetModelPath(_options.Model);
        using var whisper = new WhisperTranscriber(modelPath, _options.Language);
        Console.WriteLine($"Whisper model \"{_options.Model}\" loaded ({whisper.RuntimeName} backend), " +
                          $"{files.Count} file(s) to process.");

        var detector = new ChapterDetector(_options, ffmpeg, whisper);
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            await ProcessOneAsync(file, ffmpeg, detector, ct);
        }
    }

    /// <summary>Processes a single audiobook file and prints its summary line.</summary>
    private async Task ProcessOneAsync(
        string file, FfmpegClient ffmpeg, ChapterDetector detector, CancellationToken ct)
    {
        var name = Path.GetFileName(file);
        var work = new WorkTracker();
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
                    _progress.FinishWithSummary(
                        $"{name}: skipped - has {info.ChapterCount} chapter marking(s) (use --force to redo)");
                    return;
                }
                discardNote = bogus && !_options.Force
                    ? $", {info.ChapterCount} bogus marking(s) discarded (> --max-chapters)"
                    : $", {info.ChapterCount} existing marking(s) discarded";
            }

            var result = await detector.DetectAsync(file, info, work, ct);

            if (result.GapRemains)
            {
                WarningCount++;
                _progress.FinishWithSummary(
                    $"{name}: WARNING - unresolved chapter sequence gap (missing: " +
                    $"{string.Join(", ", result.MissingNumbers)}); file unchanged");
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
            _progress.FinishWithSummary($"{name}: ERROR - {ex.Message}");
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
