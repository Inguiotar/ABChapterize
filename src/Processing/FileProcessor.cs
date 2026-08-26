// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using System.Diagnostics;
using System.Text;
using ABChapterize.Abs;
using ABChapterize.Audio;
using ABChapterize.Cli;
using ABChapterize.Concurrency;
using ABChapterize.Detection;
using ABChapterize.Errors;
using ABChapterize.Gpu;
using ABChapterize.Hooks;
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

    /// <summary>
    /// The Audiobookshelf side of the run, or null when the run talks to no server. Set by
    /// <see cref="RunAsync"/> rather than by the constructor because it owns a connection and a
    /// temporary folder, and both have to be released before the run returns - so the one place
    /// that can hold it in a <c>using</c> is the one that also assigns it.
    /// </summary>
    private AbsFileFlow? _abs;

    /// <summary>
    /// The instant a file must have been written after to pass <c>--newer-than</c>, or null when
    /// the option was not given.
    /// </summary>
    /// <remarks>
    /// Fixed when the run starts rather than asked of the clock per file. Enumeration is cheap,
    /// but <c>--summary</c>'s counts, the <c>--no-op</c> listing and the actual work all read the
    /// same selection, and a batch of two hundred books runs for hours - a cutoff that crept
    /// forward with it would be answering a slightly different question each time it was asked.
    /// </remarks>
    private readonly DateTime? _newerThanUtc;

    /// <summary>Number of files for which processing was aborted with a warning.</summary>
    private int _warnings;

    /// <summary>Number of files that actually went through chapter detection.</summary>
    private int _processed;

    /// <summary>
    /// Number of files the run got as far as starting, whatever became of them. Only a
    /// <c>--summary</c> printed for a run that did not finish quotes it, and only that summary can
    /// ask the question it answers - where the run stopped.
    /// </summary>
    /// <remarks>
    /// Counted at the per-file entry points rather than derived from the other counters, which
    /// would be a guess: a file cut off mid-transcription is neither processed nor skipped, and
    /// subtracting the two from the run's total would silently move it into the files that were
    /// never started.
    /// </remarks>
    private int _reached;

    /// <summary>Accumulated detection time of the processed files (for the --summary average).</summary>
    private TimeSpan _processingTime;

    /// <summary>Run-wide detection/confidence statistics and formatting for --verbose and
    /// --summary reporting, accumulated across every file of the run.</summary>
    private readonly RunStatistics _runStats = new();

    /// <summary>The files --summary names one by one at the end: those skipped, those detection
    /// found nothing in, those left with chapter marks still missing, and those finished but
    /// carrying marks the recognizer was unsure of.</summary>
    private readonly RunOutcomes _outcomes = new();

    /// <summary>Creates a processor for the given validated options.</summary>
    /// <param name="options">Validated command line options.</param>
    /// <param name="progress">Renderer for the progress bar and the summary lines.</param>
    public FileProcessor(CliOptions options, ProgressRenderer progress)
    {
        _options = options;
        _progress = progress;
        _newerThanUtc = options.NewerThan is { } age ? DateTime.UtcNow - age : null;
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
        // --verify-only is --no-op too, and is the one spelling of it that reads the files rather
        // than listing them - so it goes to the ordinary pipeline below, not to the listing.
        if (_options.NoOp && !_options.Abs && !_options.VerifyOnly)
        {
            RunNoOp();
            return;
        }
        if (!_options.UsesAbs)
        {
            await RunABChapterizeAsync(ct);
            return;
        }

        // Everything from here on has a server behind it. The using is what removes the run's
        // downloads, so a Ctrl+C or a failure part way through a library leaves nothing behind.
        using var abs = new AbsFileFlow(_options, _progress);
        _abs = abs;
        await abs.ConnectAsync(ct);
        if (_options.NoOp)
        {
            await RunAbsNoOpAsync(ct);
            return;
        }
        await RunABChapterizeAsync(ct);
    }

    /// <summary>
    /// --no-op in ABS mode: lists the books the selectors and --filter picked out, saying of each
    /// what would happen to it, and returns without fetching a single byte of audio.
    /// <para>
    /// Worth rather more here than it is over a folder. A selector can name a hundred books without
    /// looking as though it does, and each of them is a download; checking first costs one request.
    /// </para>
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    private async Task RunAbsNoOpAsync(CancellationToken ct)
    {
        var books = await _abs!.SelectAsync(ct);
        if (books.Count == 0)
        {
            Console.WriteLine("No books matching the given selectors found.");
            return;
        }
        if (!_options.Quiet)
            foreach (var book in books)
                Console.WriteLine($"{book.Describe} - {DescribeAbsPlan(book)}");
        if (_options.Summary)
            Console.WriteLine(
                $"Summary: {books.Count(WouldProcess)} of {books.Count} book(s) would be processed");
    }

    /// <summary>What a --no-op listing says would become of one book.</summary>
    /// <param name="book">The selected book.</param>
    private string DescribeAbsPlan(AbsBook book)
    {
        if (!book.IsSingleFile)
            return $"skipped, {book.AudioFileCount} audio files";
        if (WouldProcess(book))
            return _options.AbsPushOnly ? "existing marks sent to the server" : "processed";
        return $"skipped, {book.ChapterCount} chapter mark(s) (use --force to redo)";
    }

    /// <summary>
    /// Whether a --no-op listing would count this book as one the run works on - asked of the same
    /// rule that decides it for real, so the listing and the summary under it cannot disagree.
    /// </summary>
    /// <param name="book">The selected book.</param>
    /// <remarks>
    /// It can only ever be an estimate: what a book really carries is settled by the probe of the
    /// downloaded file, and a --abs-push-only book with no marks at all is skipped there rather than
    /// here. But it is the estimate the run itself works from.
    /// </remarks>
    private bool WouldProcess(AbsBook book) => _abs!.WouldProcess(book);

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
            Console.WriteLine(NothingFoundNote);
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
    /// <remarks>
    /// A file that cannot be restored is reported and the rest still run, exactly as in
    /// <see cref="CleanupRunner"/> and for the same reason: one audiobook held open by a player is
    /// no reason to leave the other ninety-nine as this tool left them. The run as a whole then
    /// ends in an error, so a script cannot mistake a partial revert for a complete one. The two
    /// modes are deliberately the same rule - both undo work on files the user already had, and a
    /// mode that stopped where its sibling carried on would be a difference nobody could predict.
    /// </remarks>
    /// <param name="ct">Cancellation token bound to Ctrl+C.</param>
    /// <exception cref="AppError">At least one backup could not be restored.</exception>
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
        // Counted as they happen rather than taken from the list at the end, so the figures survive
        // a Ctrl+C: the summary below is printed either way, and "N reverted" has to be N.
        var reverted = 0;
        var failed = 0;
        // A file that could not be restored is not a run that stopped - it is reported and the rest
        // still run - so only a Ctrl+C marks the summary as covering an unfinished run. Same rule,
        // and same reasoning, as CleanupRunner.Apply.
        var finished = false;
        try
        {
            foreach (var bak in backups)
            {
                ct.ThrowIfCancellationRequested();
                var original = bak[..^4]; // strip ".bak"
                // One replacing move rather than a delete followed by a rename: the two-step version
                // has a window in which the processed file is already gone and the backup has not yet
                // taken its place, and a move that fails there leaves the folder looking emptier than
                // it is. Overwriting is what --revert is for, so nothing here needs the refusal
                // CommitChaptersAsync makes.
                try
                {
                    File.Move(bak, original, overwrite: true);
                    reverted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Named by us: File.Move carries no path of its own - a file held open by a
                    // player gives the bare "Access to the path is denied." - so which of a folder
                    // full of books this was would otherwise be the one thing missing. Not
                    // suppressed by --quiet, unlike the line below it: a warning is the reason the
                    // run will end in an error, and a quiet run still has to say why.
                    failed++;
                    _progress.Announce($"{Path.GetFileName(original)}: WARNING - not restored " +
                                       $"from its backup: {ex.Message}");
                    continue;
                }
                if (!_options.Quiet)
                    _progress.Announce($"{Path.GetFileName(original)}: reverted from backup");
            }
            finished = true;
        }
        finally
        {
            if (_options.Summary)
            {
                _progress.AnnounceSummaryHeading(
                    $"{backups.Count} backup(s) encountered, {reverted} reverted" +
                    (failed > 0 ? $", {failed} failed" : ""), finished);
                _progress.AnnounceSummary($"Total time: {FormatTime(watch.Elapsed)}");
            }
        }
        if (failed > 0)
            throw new AppError($"{failed} backup(s) could not be restored; see the warnings above.");
    }

    /// <summary>
    /// Runs chapter detection and writing for all selected files: enumerate, hand the list to
    /// whichever of the two per-file pipelines the options select (--import's sidecar write or
    /// the full Whisper detection run), then report. Both pipelines feed the same counters and
    /// the same <see cref="_runStats"/>, so the summary below does not care which one ran.
    /// </summary>
    /// <remarks>
    /// The report is in a <c>finally</c>, so a Ctrl+C or a failure part way through a batch still
    /// gets one - marked as covering an unfinished run, and with the exception left to carry on
    /// out to <see cref="ABChapterize.Cli.Program"/>, which decides the exit code. The file
    /// selection deliberately sits outside it: a run that never reached its first file has nothing
    /// to summarize, and a block of zeroes on top of the error that caused it would only be in the
    /// way.
    /// </remarks>
    /// <param name="ct">Cancellation token bound to Ctrl+C.</param>
    private async Task RunABChapterizeAsync(CancellationToken ct)
    {
        var files = _options.Abs ? await SelectAbsBooksAsync(ct) : SelectLocalFiles();
        if (files == null)
            return;

        var (ffmpegPath, ffprobePath) = FfmpegLocator.Locate();
        var ffmpeg = new FfmpegClient(ffmpegPath, ffprobePath);
        var watch = Stopwatch.StartNew();
        var finished = false;

        try
        {
            if (_options.Import)
                await RunImportAsync(files, ffmpeg, ct);
            else if (_options.AbsPushOnly || _options.AbsPullOnly)
                await RunWithoutDetectionAsync(files, ffmpeg, ct);
            else
                await RunDetectionAsync(files, ffmpeg, ct);
            finished = true;
        }
        finally
        {
            if (_options.Summary)
                PrintRunSummary(files.Count, watch.Elapsed, finished);
        }
    }

    /// <summary>
    /// The ordinary file selection: every command line target enumerated, minus what an earlier
    /// interrupted run already finished.
    /// </summary>
    /// <returns>The files to process, or null when there are none - in which case the reason has
    /// already been reported.</returns>
    private List<PendingFile>? SelectLocalFiles()
    {
        var groups = EnumerateTargetGroups(_options.EffectiveExtensions);
        if (groups.Sum(g => g.Files.Count) == 0)
        {
            _progress.Announce(NothingFoundNote);
            return null;
        }

        var files = ApplyBatchProgress(groups);
        if (files.Count == 0)
        {
            _progress.Announce("Every selected file was already processed by an earlier, " +
                              "interrupted run; nothing left to do (--ignore-progress redoes them).");
            return null;
        }
        return files;
    }

    /// <summary>
    /// The ABS mode file selection: the selectors resolved against the server, each book standing
    /// in for a file that does not exist yet.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The books to process, or null when there are none.</returns>
    /// <remarks>
    /// No <see cref="BatchProgress"/> here, and none is missing. That checkpoint lives in the
    /// directory being worked through, and ABS mode has no such directory - what it has instead is
    /// a server that already knows which books carry chapters, so an interrupted run resumed by
    /// repeating the command skips what it finished for the ordinary reason: the marks are there.
    /// </remarks>
    private async Task<List<PendingFile>?> SelectAbsBooksAsync(CancellationToken ct)
    {
        var books = await _abs!.SelectAsync(ct);
        if (books.Count == 0)
        {
            _progress.Announce("No books matching the given selectors found.");
            return null;
        }
        // The command line could only check that exactly one selector was given; what it actually
        // matched is knowable no earlier than here.
        if (_options.ChapterCount != null && books.Count != 1)
            throw new AppError(
                $"--chapter-count states how many chapters one particular book has, but the "
                + $"selector matched {books.Count} books.");
        if (!_options.Quiet)
            _progress.Announce($"{books.Count} book(s) selected on Audiobookshelf.");
        return [.. books.Select(b => new PendingFile("", null, "", b))];
    }

    /// <summary>
    /// The pipeline the two server-only modes share: no model is loaded and nothing is detected, so
    /// each file is an ffprobe, a look-up on the server and one request.
    /// </summary>
    /// <param name="files">The files, or the books, to move marks for.</param>
    /// <param name="ffmpeg">The run's shared ffmpeg client.</param>
    /// <param name="ct">Cancellation token bound to Ctrl+C.</param>
    /// <remarks>
    /// One loop for <c>--abs-push-only</c> and <c>--abs-pull-only</c> rather than one each: they
    /// differ only in which side of the exchange holds the good copy, and that difference is
    /// settled by <see cref="PlanFor"/>. What they have in common - no model, no detector, one
    /// file at a time - is all this method is.
    /// </remarks>
    private async Task RunWithoutDetectionAsync(
        List<PendingFile> files, FfmpegClient ffmpeg, CancellationToken ct)
    {
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            await ProcessOneAsync(file, ffmpeg, detectorFor: null, ct);
        }
    }

    /// <summary>
    /// One file waiting to be processed, together with the checkpoint to report it to when it is
    /// finished (null for a file named directly on the command line, or when checkpointing is off
    /// - see <see cref="ApplyBatchProgress"/>).
    /// </summary>
    /// <param name="Path">Full path of the file; the empty string for a book not fetched yet.</param>
    /// <param name="Progress">Batch checkpoint of the directory the file came from, if any.</param>
    /// <param name="TargetRoot">The command line target this file was found through - the directory
    /// that was named, or the file itself. It bounds how far up <see cref="FolderConfig"/> looks for
    /// a per-folder settings file, so a run never reads one from outside what it was asked to
    /// process.</param>
    /// <param name="Book">The Audiobookshelf book this file is a copy of, or null for a local
    /// file. Set before <see cref="Path"/> is: in ABS mode the book is what the run selected and
    /// the path only exists once it has been downloaded.</param>
    private readonly record struct PendingFile(
        string Path, BatchProgress? Progress, string TargetRoot, AbsBook? Book = null);

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
            // --verify-only joins --dry-run here for the same reason: it writes nothing, so there
            // is nothing worth not doing twice - and recording its files as done would make the
            // real run that follows skip every one of them.
            if (!group.Target.IsDirectory || _options.DryRun || _options.VerifyOnly)
            {
                pending.AddRange(group.Files.Select(f => new PendingFile(f, null, group.Target.Path)));
                continue;
            }
            var progress = BatchProgress.Open(
                group.Target.Path, _options.RunFingerprint, _options.IgnoreProgress, _progress.Announce);
            var todo = group.Files.Where(f => !progress.IsDone(f)).ToList();
            resumed += group.Files.Count - todo.Count;
            progress.Begin(todo.Count);
            pending.AddRange(todo.Select(f => new PendingFile(f, progress, group.Target.Path)));
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
        UpgradeTranscriber? upgrade = null;
        try
        {
            // A different --upgrade-model gets one lazily-loaded instance for the whole run (see
            // UpgradeTranscriber). Only gap work (Re-probe and 3) uses it, so a book that never opens
            // a gap never pays for the second model at all. The same model as --model means no
            // separate instance either way - gap work reuses the run's own transcriber.
            upgrade = _options.UpgradeModel != _options.Model
                ? new UpgradeTranscriber(_options.UpgradeModel, initialLanguage,
                    _options.EffectiveWhisperThreads, _options.CpuOnly, gpu.Selected?.Index,
                    _progress.Announce)
                : null;

            if (!_options.Quiet)
                PrintModelBanner(transcriber.RuntimeName, files.Count, upgrade != null, gpu);

            // The one thing in the run that uses more than one thread of its own accord. Always
            // built since 0.12.0: a file's jingles are what its probe windows and its mark
            // placement are measured from, so there is no longer a way to ask for a run without
            // them. A load failure is therefore fatal - it propagates out of here rather than
            // falling back to the blinder path that used to exist, which would silently produce a
            // different mark than the one the run asked for.
            using var vad = new SileroVadDetector(_options.EffectiveVadThreads);
            LogThreadBudget(vad);

            // One detector per file rather than one per run, because a folder's own
            // .abchapterize-config may hand this file a different chapter phrase, language or mark
            // placement than the last one (see FolderConfig). Costs nothing - the constructor only
            // stores the references it is given, and the run-scoped ones (the models, the VAD, the
            // ffmpeg client) are the same objects every time - and it makes "no per-file state
            // survives into the next file" structural rather than a matter of SetLog remembering to
            // clear it.
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                // A factory rather than a detector, because in ABS mode the path a folder's
                // settings would be resolved from does not exist until the book has been fetched -
                // which happens inside ProcessOneAsync, where the file has a progress block to
                // download into.
                await ProcessOneAsync(file, ffmpeg, ResolveDetector, ct);
            }
            ChapterDetector ResolveDetector(string file, string targetRoot)
                // A book from a server sits in a temporary folder that no .abchapterize-config
                // could sensibly be put in, so ABS mode takes the run's own options as they are.
                => new(_options.Abs ? _options : FolderConfig.ResolveForFile(_options, file, targetRoot),
                    ffmpeg, transcriber, vad, upgrade);
        }
        finally
        {
            await transcriber.DisposeAsync();
            if (upgrade != null)
                await upgrade.DisposeAsync();
        }
    }

    /// <summary>
    /// Records what the run was given to work with, for the log alone: a run that turns out slower
    /// than expected is usually a thread-count question, and the answer is otherwise nowhere.
    /// </summary>
    /// <param name="vad">The run's voice-activity detector, which every run since 0.12.0 has.</param>
    private void LogThreadBudget(SileroVadDetector vad)
        => _progress.Log($"threads: Whisper {_options.EffectiveWhisperThreads}" +
                         $", voice-activity pre-pass {vad.Workers}" +
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
            // GpuSelector stays free of platform knowledge, so the "why is the list empty here"
            // half of the message is appended at the one place that already knows the machine.
            throw new AppError($"{selection.Error}. {VulkanDeviceEnumerator.AbsenceNote}");

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
    /// <param name="separateUpgradeModel">Whether --upgrade-model asked for a second model.</param>
    /// <param name="gpu">What this run decided about GPUs.</param>
    /// <remarks>
    /// The device name is printed because its absence is what made a wrong GPU invisible: a banner
    /// saying only "Vulkan backend" looks identical whether the run is on a discrete card, on the
    /// integrated one at a fraction of the speed, or on a software rasterizer. Only named on the
    /// Vulkan backend, since that is where the enumeration the name comes from applies.
    /// </remarks>
    private void PrintModelBanner(string runtimeName, int fileCount, bool separateUpgradeModel, GpuChoice gpu)
        => _progress.Announce($"Whisper model \"{_options.Model}\" loaded ({runtimeName} backend" +
                             DescribeGpu(runtimeName, gpu) +
                             (_options.AutoLanguage ? ", auto language detection" : "") + "), " +
                             (separateUpgradeModel
                                 ? $"upgrade model \"{_options.UpgradeModel}\" (loaded on first use), " : "") +
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
    /// <param name="fileCount">Number of files the run selected.</param>
    /// <param name="elapsed">Wall-clock time the run took.</param>
    /// <param name="finished">Whether the run got to the end of its file list. When it did not,
    /// the heading says so and quotes how far it got instead of claiming every selected file was
    /// encountered - the listings under it are then a report of an unfinished job, which is a
    /// different thing to read and has to look like one.</param>
    private void PrintRunSummary(int fileCount, TimeSpan elapsed, bool finished)
    {
        var warningNote = _warnings > 0 ? $", {_warnings} with warnings" : "";
        var noChapters = _outcomes.NoChaptersCount;
        var noChaptersNote = noChapters > 0 ? $", {noChapters} with no chapters found" : "";
        var reached = finished
            ? $"{fileCount} file(s) encountered"
            : $"{_reached} of {fileCount} file(s) reached";
        _progress.AnnounceSummaryHeading(
            $"{reached}, {_processed} processed, " +
            $"{_outcomes.SkippedCount} skipped{warningNote}{noChaptersNote}", finished);
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
    /// Starts the "Finish" phase on a file's progress bar and returns a callback translating
    /// ffmpeg's processed play time (what <see cref="FfmpegClient.WriteChaptersAsync"/> reports)
    /// into the byte-based progress <see cref="WorkTracker"/> expects - the same play-time-to-bytes
    /// conversion <see cref="ChapterDetector"/> uses for its own phases.
    /// </summary>
    /// <param name="work">The file's progress tracker.</param>
    /// <param name="info">The file's probe result, for its size and duration.</param>
    private static Action<double> BeginFinishPhase(WorkTracker work, MediaInfo info)
    {
        work.BeginPhase(PhaseNames.Finish, info.SizeBytes);
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
    /// <returns>The chapters to write, and how many named marks were merged away - which only the
    /// caller reporting named marks at all has any use for.</returns>
    internal static (List<Chapter> Chapters, int MergedNamedMarks) BuildChapters(
        DetectionResult result, double namedMarkDistanceSeconds)
    {
        // Only a file that really turned out to hold more than one chapter sequence gets the part
        // prefix - every ordinary book is written exactly as it was before parts existed, and a
        // lone "Part 1" in front of every chapter of a book that has no part 2 would be noise.
        var parts = result.SequenceCount > 1;
        // A named mark sharing a chapter's exact timestamp sorts after it, so a prologue heard in
        // the same breath as chapter 1 cannot displace the numbered entry a player scrubs by.
        var entries = result.Chapters
            .Select(c => new MarkEntry(
                c.TimeSeconds,
                result.Profile.ChapterTitleFor(c.Number, parts ? c.Sequence + 1 : null),
                false, true))
            .Concat(result.NamedMarks.Select(m => new MarkEntry(
                m.TimeSeconds, m.Title, true, m.Kind == result.Profile.ChapterAnnouncement.Kind)))
            .OrderBy(c => c.TimeSeconds).ThenBy(c => c.Named ? 1 : 0)
            .ToList();
        var merged = MergeCrowdedNamedMarks(entries, namedMarkDistanceSeconds);
        var chapters = entries.Select(e => new Chapter(e.TimeSeconds, e.Title)).ToList();
        if (chapters.Count > 0 && chapters[0].StartSeconds > 0 && result.LeadInHasSpeech)
            chapters.Insert(0, new Chapter(0, result.Profile.IntroTitle));
        return (chapters, merged);
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
    /// <param name="Options">The settings this file is worked on under. The run's own, except where
    /// a per-folder <c>.abchapterize-config</c> changed how the book is read (see
    /// <see cref="FolderConfig"/>) - so anything describing what detection did takes it from here
    /// rather than from the run.</param>
    /// <param name="Abs">The Audiobookshelf book this file is a temporary copy of, or null for a
    /// local file. Carried in the context rather than passed alongside it because the two places
    /// that need it - the merge that settles which marks the book already has, and the write that
    /// sends the finished ones back - sit at opposite ends of the pipeline.</param>
    /// <param name="Pull">What <c>--abs-pull</c> found for this local file, or null when the run
    /// does not pull. Carried for the same reason as <paramref name="Abs"/>, and it holds both
    /// chapter lists as they stood before the merge - which is what lets the commit decide whether
    /// either side has anything left to be given.</param>
    private readonly record struct FileContext(
        string File, string Name, WorkTracker Work, DetectionLog Logs, MediaInfo Info,
        FfmpegClient Ffmpeg, CliOptions Options, AbsLocalCopy? Abs = null, AbsPull? Pull = null);

    /// <summary>
    /// Processes a single audiobook file, prints its summary line and - once it is finished for
    /// good, whatever the outcome - reports it to its directory's batch checkpoint. An error or a
    /// Ctrl+C deliberately reports nothing, so the file is attempted again by the next run.
    /// </summary>
    /// <param name="pending">The file to process and the checkpoint it belongs to.</param>
    /// <param name="ffmpeg">The run's shared ffmpeg client.</param>
    /// <param name="detectorFor">Builds the detector for this file once its path is known, or null
    /// for a mode that detects nothing (--abs-push-only).</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task ProcessOneAsync(
        PendingFile pending, FfmpegClient ffmpeg, DetectorFactory? detectorFor, CancellationToken ct)
    {
        // A book from a server is named by its title rather than by the file it happens to be
        // stored in: the title is what the user picked it out by and what the server calls it.
        var name = pending.Book?.Title ?? Path.GetFileName(pending.Path);
        _reached++;
        var work = new WorkTracker();
        _progress.Start(name, work);
        AbsLocalCopy? copy = null;
        try
        {
            if (pending.Book is { } book)
            {
                var (fetched, refusal) = await _abs!.FetchAsync(book, work, ct);
                if (fetched == null)
                {
                    ReportSkipped(work, name, refusal, hint: "");
                    return;
                }
                copy = fetched;
                pending = pending with { Path = fetched.Path };
            }
            var renamedTo = await ProcessOneCoreAsync(
                pending.Path, name, work, ffmpeg,
                detectorFor?.Invoke(pending.Path, pending.TargetRoot), copy, ct);
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
        finally
        {
            // In the finally, so a book whose processing threw does not leave a gigabyte behind -
            // and before the exception propagates out of the run, where nothing would be looking.
            if (copy != null)
                _abs!.Discard(copy);
        }
    }

    /// <summary>
    /// Builds the detector for one file, once the run knows where that file is.
    /// </summary>
    /// <param name="file">Path of the file about to be processed.</param>
    /// <param name="targetRoot">The command line target it was reached through, which the
    /// per-folder settings are resolved along.</param>
    private delegate ChapterDetector DetectorFactory(string file, string targetRoot);

    /// <summary>
    /// The per-file pipeline's opening and closing: probe, decoder resolution, the two hooks, the
    /// --debug log, and - once <see cref="CommitOneAsync"/> has decided the file's fate - taking
    /// that log along when the file loses its ".missing-marks" tag.
    /// </summary>
    /// <param name="file">Path of the file to process.</param>
    /// <param name="name">Its bare file name, which every console line for it is prefixed with.</param>
    /// <param name="work">Its progress tracker, already started.</param>
    /// <param name="ffmpeg">The run's shared ffmpeg client.</param>
    /// <param name="detector">This file's detector, or null for a mode that detects nothing
    /// (--abs-push-only).</param>
    /// <param name="abs">The Audiobookshelf book this file was fetched for, or null.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The name the file was renamed to (a ".missing-marks" tag added or dropped), or
    /// null when it kept its own.</returns>
    private async Task<string?> ProcessOneCoreAsync(
        string file, string name, WorkTracker work, FfmpegClient ffmpeg, ChapterDetector? detector,
        AbsLocalCopy? abs, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        // The ordinary log sink; every message is prefixed with the file name.
        var log = _options.LoggingEnabled ? (Action<string>)(msg => _progress.Log($"{name}: {msg}")) : null;
        // The --debug file can only be opened once the probe has run, since its header describes
        // what the probe found - so the probe, the decoder resolution and --run-before log to the
        // ordinary sink alone. Nothing is lost by that: the header restates everything the first
        // two carry, and a log opened before the hook would describe a file the hook then replaced.
        var options = detector?.Options ?? _options;
        if (await OpenAndPlanAsync(file, name, work, ffmpeg, log, options, abs, ct) is not { } opened)
            return null;
        var (probed, plan) = opened;

        if (_options.RunBefore is { } before)
        {
            if (!await RunBeforeHookAsync(before, probed, ct))
                return null;
            // The command may have joined a split book, re-encoded it or replaced it outright, so
            // everything the first probe established about the file is hearsay from here on.
            // Skipped under --dry-run, where nothing ran and so nothing can have changed.
            if (!_options.DryRun)
            {
                if (await OpenAndPlanAsync(file, name, work, ffmpeg, log, options, abs, ct) is not { } reopened)
                    return null;
                (probed, plan) = reopened;
            }
        }

        using var debug = _options.Debug ? DebugLog.Open(file, probed.Options, probed.Info) : null;
        var ctx = probed with { Logs = new DetectionLog(log, debug != null ? debug.Write : null) };

        // Whether the run ended up leaving the file alone is asked of RunOutcomes rather than
        // tracked separately: every skip there is - the pre-existing marks, --verify's two refusals
        // - is recorded there already, and a second opinion could only ever disagree with the
        // listing --summary prints from it.
        var skippedBefore = _outcomes.SkippedCount;
        // And the same question for "did this file finish": a book left with chapters missing is
        // coming back in a later run, so --run-after must not be told it is done. Asked here rather
        // than off the name below, because the tag that says so is what a rename onto an occupied
        // name fails to apply.
        var incompleteBefore = _outcomes.MissingMarksCount;
        var renamedTo = await CommitOneAsync(ctx, detector, plan, watch, ct);
        // The debug log belongs beside the audiobook under the book's own name, so it follows the
        // file back when the tag comes off - but not when one is put on, where the untagged name it
        // already has is the right one (see DebugLog.PathFor).
        if (renamedTo != null && !MissingMarksTag.IsTagged(renamedTo))
            debug?.FollowTo(renamedTo);
        if (_outcomes.SkippedCount == skippedBefore && _outcomes.MissingMarksCount == incompleteBefore)
            await RunAfterHookAsync(renamedTo ?? file, name, ct);
        return renamedTo;
    }

    /// <summary>
    /// What the marks a file already carries, and its own name, say is to happen to it.
    /// </summary>
    /// <remarks>
    /// Decided once, ahead of the <c>--run-before</c> hook, because a file this run is not going to
    /// touch must not run a hook either - and read again by <see cref="CommitOneAsync"/>, so that
    /// the decision is reached in one place rather than in two that can drift apart.
    /// </remarks>
    private enum FilePlan
    {
        /// <summary>Re-probe only the gap(s) the file's ".missing-marks" tag names.</summary>
        Resume,

        /// <summary>Detect the whole file.</summary>
        Detect,

        /// <summary>Check the marks the file already carries before deciding (--verify).</summary>
        Verify,

        /// <summary>Leave the file alone: it is already marked and nothing says to redo it.</summary>
        Skip,

        /// <summary>Send the marks it already carries to Audiobookshelf and change nothing
        /// (--abs-push-only).</summary>
        Push,

        /// <summary>
        /// Detect nothing and give each side the chapter list the pull settled on, where it has
        /// not got it already (<c>--abs-pull</c>, <c>--abs-pull-only</c>).
        /// </summary>
        Reconcile,
    }

    /// <summary>Applies the "what happens to this file" policy - see <see cref="FilePlan"/>.</summary>
    /// <param name="ctx">The file's context, carrying its probe result.</param>
    private FilePlan PlanFor(FileContext ctx)
    {
        // First, and unconditional: --abs-push-only forms no opinion about a book beyond what marks it
        // has, so none of the policy below - the resume tag, the pre-existing marks, --verify - has
        // anything to decide. A book with no marks at all is caught where they are read.
        // Before everything: --verify-only forms no opinion about a file beyond what its marks
        // turn out to be, so neither the resume tag nor the pre-existing-mark policy has anything
        // to decide - and a file with no marks at all reaches VerifyThenDetectAsync too, which is
        // where "nothing to verify" is reported.
        if (_options.VerifyOnly)
            return FilePlan.Verify;

        if (_options.AbsPushOnly)
            return FilePlan.Push;

        // --abs-pull-only for the same reason from the other side: what the server holds is what the
        // file gets, so the marks it already has are not a reason to leave it alone - they are the
        // thing being replaced. A server with no chapters for it is caught where they are read.
        if (_options.AbsPullOnly)
            return FilePlan.Reconcile;

        // Auto-resume a ".missing-marks-<n>-<n>-..." file left by a previous run's unresolved
        // chapter-sequence gap: only the still-missing gap(s) are re-probed, the committed marks
        // are trusted as-is. --force means "redo the whole file from scratch" and takes priority,
        // falling through to the normal policy below.
        // The resume path is entirely about chapter numbers the tag names, so a run that forms no
        // opinion about them ignores the tag - the file falls through to the ordinary
        // pre-existing-mark policy below and is skipped for the partial marks it carries, unless
        // --force asks for a detection from scratch.
        if (!_options.Force && !_options.IgnoreChapterNumbers && MissingMarksTag.IsResumable(ctx.File))
            return FilePlan.Resume;
        if (!EvaluateExistingChapters(ctx.Info).Skip)
            return FilePlan.Detect;
        if (_options.Verify)
            return FilePlan.Verify;
        // Last, and only for a pulling run: the marks this file is "already" carrying may be the
        // server's rather than its own, and a file the run has nothing to detect for still has to
        // be given them - and the server told, where the two started out disagreeing. What there is
        // left to do is worked out in ReconcileMarksAsync, which reports a skip when the answer is
        // nothing at all.
        return _options.AbsPull ? FilePlan.Reconcile : FilePlan.Skip;
    }

    /// <summary>
    /// Opens one file: probes it, settles which decoder its audio needs, and decides what is to
    /// happen to it. A unit of its own because <c>--run-before</c> may rewrite the very file it ran
    /// for, so the whole sequence is repeated afterwards rather than any part of it being trusted
    /// across the hook.
    /// </summary>
    /// <param name="file">Path of the file to open.</param>
    /// <param name="name">Its bare file name.</param>
    /// <param name="work">Its progress tracker, already started.</param>
    /// <param name="ffmpeg">The run's shared ffmpeg client.</param>
    /// <param name="log">Its ordinary log sink, or null when nothing is listening.</param>
    /// <param name="options">The settings this file is detected under; see
    /// <see cref="FileContext.Options"/>.</param>
    /// <param name="abs">The Audiobookshelf book this file was fetched for, or null.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The file's context and what is to be done with it, or null when it is not going to
    /// be worked on at all - in which case it has already been counted and reported.</returns>
    private async Task<(FileContext Ctx, FilePlan Plan)?> OpenAndPlanAsync(
        string file, string name, WorkTracker work, FfmpegClient ffmpeg, Action<string>? log,
        CliOptions options, AbsLocalCopy? abs, CancellationToken ct)
    {
        var info = await ProbeAndLogAsync(file, ffmpeg, log, ct);
        // Between the probe and every decision that reads it, because "what marks does this book
        // already have" is a question the server has the better answer to - see AbsChapterMerge.
        // The two ways a book can arrive from a server meet here and nowhere else: fetched whole
        // (--abs), or matched to a file already on this machine (--abs-pull). One merge rule
        // afterwards, so the rest of the run cannot tell which of them it was.
        AbsPull? pull = null;
        if (abs != null)
        {
            var (merged, note) = _abs!.Merge(info, abs);
            info = merged;
            if (note.Length > 0)
                log?.Invoke(note);
        }
        else if (_options.UsesAbsPull)
        {
            pull = await _abs!.PullAsync(file, info, ct);
            log?.Invoke(pull.Value.Note);
            // Only where a book was settled on. A pull that found none - or refused the one it
            // found - has nothing to merge, and running the rule anyway would answer with "the
            // server has no chapters", which reads as a fact about the book rather than as what it
            // is: this file never got as far as asking one.
            if (pull.Value.Book != null)
            {
                var (merged, note) = AbsChapterMerge.Apply(info, pull.Value.FromServer);
                info = merged;
                if (note.Length > 0)
                    log?.Invoke(note);
            }
        }
        if (await ResolveXheAacDecoderAsync(
                new FileContext(
                    file, name, work, new DetectionLog(log, null), info, ffmpeg, options, abs, pull),
                ct)
            is not { } probed)
            return null;
        var plan = PlanFor(probed);
        if (plan is not FilePlan.Skip)
            return (probed, plan);
        ReportSkipped(work, name, $"has {info.ChapterCount} chapter mark(s)");
        return null;
    }

    /// <summary>
    /// Decides and commits one file's fate: a resume of a previously tagged file, a skip, or a
    /// detection run whose result one of the report/write stages below writes out.
    /// </summary>
    /// <param name="ctx">The file's context.</param>
    /// <param name="detector">The run's single detector, reused for every file: there is one
    /// per run rather than one per file, and files are processed strictly one at a time.</param>
    /// <param name="plan">What <see cref="PlanFor"/> decided is to happen to this file, before
    /// the <c>--run-before</c> hook was given its chance to change it.</param>
    /// <param name="watch">Running stopwatch of this file, for the processing-time average.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The name the file was renamed to, or null when it kept its own.</returns>
    private async Task<string?> CommitOneAsync(
        FileContext ctx, ChapterDetector? detector, FilePlan plan, Stopwatch watch, CancellationToken ct)
    {
        if (plan is FilePlan.Reconcile)
            return await ReconcileMarksAsync(ctx, watch, ct);

        // Also the null-detector case, and not by coincidence: of the two modes that load no model,
        // --abs-pull-only is answered above and --abs-push-only here, PlanFor giving each of them
        // exactly one plan.
        if (plan is FilePlan.Push || detector == null)
            return await PushExistingMarksAsync(ctx, watch, ct);

        if (plan is FilePlan.Resume)
            return await ProcessResumeAsync(ctx, detector, watch, ct);

        if (await DetectChaptersAsync(ctx, detector, plan, watch, ct) is not { } outcome)
            return null;
        var (result, dropped, note) = outcome;

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
        return await WriteDetectedChaptersAsync(ctx, result, dropped, note, ct);
    }

    /// <summary>
    /// The --abs-push-only outcome for one file: send Audiobookshelf the marks the file already
    /// carries, having first worked out which book they belong to.
    /// </summary>
    /// <param name="ctx">The file's context.</param>
    /// <param name="watch">Running stopwatch of this file, for the processing-time average.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Always null: nothing is written and nothing is renamed.</returns>
    /// <remarks>
    /// The two refusals are skips rather than failures, deliberately. A folder pushed to a server
    /// will hold books the server does not have and books nobody has marked yet, and neither is a
    /// reason to stop a batch - they are exactly what the --summary listing of skipped files is
    /// for.
    /// </remarks>
    private async Task<string?> PushExistingMarksAsync(
        FileContext ctx, Stopwatch watch, CancellationToken ct)
    {
        // In ABS mode the book is already known; outside it, the file has to be recognized.
        var book = ctx.Abs?.Book;
        if (book == null)
        {
            var match = await _abs!.MatchAsync(ctx.File, ctx.Info, ct);
            if (match.Book == null)
            {
                ReportSkipped(ctx.Work, ctx.Name, match.Reason, hint: "");
                return null;
            }
            book = match.Book;
            ctx.Logs.Write(match.Reason);
        }

        var chapters = ctx.Info.ExistingChapters;
        if (chapters.Count == 0)
        {
            ReportSkipped(ctx.Work, ctx.Name, "has no chapter marks to send", hint: "");
            return null;
        }

        RecordProcessed(watch);
        // The push clause names the book itself, so this line no longer opens with it - printing
        // the title twice in one line was what naming it in the clause replaced. Its leading
        // separator comes off with it, the clause being the only thing this line has to say.
        var (note, mismatch) = await _abs!.PushAsync(book, chapters, ctx.Info.DurationSeconds, ct);
        if (mismatch)
            _warnings++;
        // Not flagged important on a mismatch, here or on any other push path: the warning itself is
        // announced by AbsFileFlow.PushAsync, which no verbosity setting holds back, and this line
        // would only repeat it.
        _progress.FinishWithSummary(ctx.Work, $"{ctx.Name}: {note.TrimStart(',', ' ')}");
        return null;
    }

    /// <summary>
    /// The <c>--abs-pull</c> outcome for one file that has nothing left to detect: give each side
    /// the chapter list the pull settled on, where it has not got it already.
    /// </summary>
    /// <param name="ctx">The file's context, its <see cref="FileContext.Info"/> already merged so
    /// that its chapter list is the one the pull settled on.</param>
    /// <param name="watch">Running stopwatch of this file, for the processing-time average.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Always null: the file keeps its name, this path having formed no opinion about
    /// chapter numbers and so none about whether any are missing.</returns>
    /// <remarks>
    /// <para>
    /// Two rules, one line each, and the symmetry is the point: <b>write to the file unless the
    /// marks came from the file and are unchanged; send to the server unless they came from the
    /// server and are unchanged.</b> Everything this mode does falls out of those - a file with no
    /// marks gets the server's list, a server with no chapters gets the file's, two sides that
    /// already agree get nothing and the file is reported as skipped.
    /// </para>
    /// <para>
    /// The comparison is against the lists as they stood <em>before</em> the merge, which is why
    /// <see cref="AbsPull"/> carries both. By the time this runs, <see cref="FileContext.Info"/>
    /// holds the merged list for everything downstream, and asking it what the file itself had
    /// would get the answer the merge just put there.
    /// </para>
    /// </remarks>
    private async Task<string?> ReconcileMarksAsync(
        FileContext ctx, Stopwatch watch, CancellationToken ct)
    {
        var pull = ctx.Pull ?? default;
        var settled = ctx.Info.ExistingChapters;
        // No book means no side to take marks from and none to send them to, whether the file
        // matched nothing or matched something that turned out to be different audio. Its own note
        // is the only one that says which, so it is what the skip line carries.
        if (pull.Book == null)
        {
            ReportSkipped(ctx.Work, ctx.Name, pull.Note, hint: "");
            return null;
        }
        if (settled.Count == 0)
        {
            ReportSkipped(ctx.Work, ctx.Name,
                "neither this file nor Audiobookshelf has any chapter marks", hint: "");
            return null;
        }

        // Which side the settled list came from, asked of the merge rule rather than of the list:
        // the server's wins whenever it has one, so a non-empty server list is what "these are the
        // server's marks" means.
        var fromServer = pull.FromServer.Count > 0;
        var source = fromServer ? "Audiobookshelf" : "the file";

        var writeIt = !AbsChapterMerge.SameMarks(settled, pull.FromFile);
        var sendIt = _options.AbsPush && !AbsChapterMerge.SameMarks(settled, pull.FromServer);
        if (!writeIt && !sendIt)
        {
            ReportSkipped(ctx.Work, ctx.Name,
                fromServer
                    ? $"has the {settled.Count} chapter mark(s) Audiobookshelf holds"
                    : $"Audiobookshelf has no chapters for this book; the file keeps its {settled.Count}",
                hint: "");
            return null;
        }
        if (_options.DryRun)
        {
            _progress.FinishWithSummary(ctx.Work,
                $"{ctx.Name}: DRY RUN - would take {settled.Count} chapter mark(s) from {source}"
                + (writeIt ? " and write them to the file" : "")
                + (sendIt ? " and send them to ABS" : "") + ":"
                + $"{Environment.NewLine}{FormatChapterListing(settled)}");
            return null;
        }

        RecordProcessed(watch);
        var writeNote = writeIt
            ? await WriteChaptersIfTheContainerHoldsThemAsync(ctx, [.. settled], ct)
            : "";
        // complete: true - there is no gap to speak of. This path forms no opinion about chapter
        // numbers at all, so it cannot be holding a partial sequence back the way a detection run
        // that failed to close a gap is (see PushLocalFileAsync).
        var (pushNote, pushMismatch) = sendIt
            ? await PushLocalFileAsync(ctx, [.. settled], complete: true, ct)
            : ("", false);
        if (pushMismatch)
            _warnings++;
        // Where nothing was written the source is not worth naming: a list this file already had is
        // its own by definition, and "from the file already in the file" is what saying so gets.
        _progress.FinishWithSummary(ctx.Work,
            $"{ctx.Name}: " + (writeIt
                ? $"{settled.Count} chapter mark(s) from {source} written to the file"
                : $"{settled.Count} chapter mark(s) already in the file")
            + writeNote + pushNote);
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
                    $", {info.ChapterCount} existing chapter mark(s)");
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
        var (chapters, _) = BuildChapters(resumed, _options.NamedMarkDistanceSeconds);

        return resumed.GapRemains
            ? await ReportIncompleteResumeAsync(ctx, resumed, chapters, ct)
            : await ReportCompleteResumeAsync(ctx, resumed, chapters, ct);
    }

    /// <summary>Commits a resume that did not find everything: the marks so far are written and
    /// the file re-tagged with the chapter numbers still missing, so a later run can pick it up
    /// again.</summary>
    /// <param name="ctx">The file's context.</param>
    /// <param name="resumed">The resume's detection result.</param>
    /// <param name="chapters">The titled chapters to write.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The name the file was re-tagged with, or null under --dry-run.</returns>
    private async Task<string?> ReportIncompleteResumeAsync(
        FileContext ctx, DetectionResult resumed, List<Chapter> chapters, CancellationToken ct)
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
                $"{FormatWrittenCount(resumed, chapters, "partial mark(s)")}" +
                $"{await DryRunExportAsync(ctx, chapters, ct)}" +
                $"{WouldRenameNote(retarget, "re-tag as")}:" +
                $"{Environment.NewLine}{FormatChapterListing(chapters)}",
                important: true);
            return null;
        }
        var (finalPath, backupNote) = await CommitChaptersAsync(ctx, chapters, retarget, complete: false, ct);
        _progress.FinishWithSummary(ctx.Work,
            $"{ctx.Name}: WARNING - resume incomplete, still missing: {stillMissing}; wrote " +
            $"{FormatWrittenCount(resumed, chapters, "partial mark(s)")}" +
            $"{RenameNote(retarget, finalPath, "re-tagged as")}{backupNote}"
            + FormatProcessingTime(ctx.Work.Elapsed, ctx.Info.DurationSeconds), important: true);
        return SamePath(finalPath, ctx.File) ? null : finalPath;
    }

    /// <summary>Commits a resume that closed every gap: the full chapter set is written and the
    /// ".missing-marks" tag dropped from the file name again.</summary>
    /// <param name="ctx">The file's context.</param>
    /// <param name="resumed">The resume's detection result.</param>
    /// <param name="chapters">The titled chapters to write.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The original name the file was restored to, or null under --dry-run.</returns>
    private async Task<string?> ReportCompleteResumeAsync(
        FileContext ctx, DetectionResult resumed, List<Chapter> chapters, CancellationToken ct)
    {
        var restored = MissingMarksTag.StripFrom(ctx.File);
        RecordLowConfidence(ctx, resumed, restored);
        // Through FormatWrittenCount rather than indexing the chapter list directly: a tagged file
        // whose marks have since been stripped by hand resumes with nothing to seed from and
        // nothing to find, and reaches here with an empty list rather than a completed sequence.
        var written = FormatWrittenCount(resumed, chapters, "mark(s) written");
        if (_options.DryRun)
        {
            _progress.FinishWithSummary(ctx.Work,
                $"{ctx.Name}: DRY RUN - resume complete, all chapters found; would write " +
                $"{FormatWrittenCount(resumed, chapters)} and rename to {Path.GetFileName(restored)}" +
                $"{await DryRunExportAsync(ctx, chapters, ct)}:" +
                $"{Environment.NewLine}{FormatChapterListing(chapters)}");
            return null;
        }
        var (finalPath, backupNote) = await CommitChaptersAsync(ctx, chapters, restored, complete: true, ct);
        _progress.FinishWithSummary(ctx.Work,
            $"{ctx.Name}: resume complete - {written}" +
            $"{RenameNote(restored, finalPath)}{backupNote}"
            + FormatProcessingTime(ctx.Work.Elapsed, ctx.Info.DurationSeconds));
        return SamePath(finalPath, ctx.File) ? null : finalPath;
    }

    /// <summary>
    /// Runs the detection this file's <see cref="FilePlan"/> calls for: a plain whole-file
    /// detection, or the --verify decision tree for a file whose existing marks are to be checked
    /// first.
    /// </summary>
    /// <remarks>
    /// The pre-existing marks are re-read here rather than carried over from the planning, so that
    /// a file a <c>--run-before</c> command rewrote is described by what it holds now.
    /// </remarks>
    /// <param name="ctx">The file's context.</param>
    /// <param name="detector">The detector borrowed for this file.</param>
    /// <param name="plan">What is to happen to this file. Never <see cref="FilePlan.Skip"/>, which
    /// is settled before either hook runs, nor <see cref="FilePlan.Resume"/>, which never reaches
    /// this far.</param>
    /// <param name="watch">Running stopwatch of this file, for the processing-time average - only
    /// wanted by the one branch that finishes a file here rather than handing a result back.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The detection result and the note describing what happened to any existing
    /// marks, or null when the file was left alone - in which case it has already been counted
    /// and reported.</returns>
    private async Task<(DetectionResult Result, DroppedMarks Dropped, string Note)?> DetectChaptersAsync(
        FileContext ctx, ChapterDetector detector, FilePlan plan, Stopwatch watch, CancellationToken ct)
    {
        if (plan is FilePlan.Verify)
            return await VerifyThenDetectAsync(ctx, detector, watch, ct);
        var (_, dropped) = EvaluateExistingChapters(ctx.Info);
        return (await detector.DetectAsync(ctx.File, ctx.Info, ctx.Work, ctx.Logs, ct), dropped, "");
    }

    /// <summary>
    /// Runs the <c>--run-before</c> command for one file.
    /// </summary>
    /// <param name="template">The command line to resolve and run.</param>
    /// <param name="ctx">The file's context, as the probe left it.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True when the file may go on to be processed; false when the command failed, in
    /// which case the file has already been counted, reported and finished. Skipping it is the
    /// conservative reading of a failure: the preparation this option exists for did not happen,
    /// and marking a file that is in an unknown state is worse than not marking it at all.</returns>
    private async Task<bool> RunBeforeHookAsync(
        CommandTemplate template, FileContext ctx, CancellationToken ct)
    {
        var result = await RunHookAsync(template, "--run-before", ctx.File, ctx.Name, ct);
        if (result.ExitCode == 0)
            return true;
        _warnings++;
        _outcomes.RecordSkipped(ctx.Name, $"--run-before failed (exit code {result.ExitCode})");
        _progress.FinishWithSummary(ctx.Work,
            $"{ctx.Name}: WARNING - --run-before exited with code {result.ExitCode}" +
            $"{DescribeHookFailure(result)}; file skipped", important: true);
        return false;
    }

    /// <summary>
    /// Runs the <c>--run-after</c> command for one finished file, where there is one to run and the
    /// file is in a state worth announcing as finished.
    /// </summary>
    /// <param name="file">The file's path as it stands now, which is not the one it arrived under
    /// if this run added or dropped a ".missing-marks" tag.</param>
    /// <param name="name">Its bare file name, for the console lines.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task RunAfterHookAsync(string file, string name, CancellationToken ct)
    {
        if (_options.RunAfter is not { } template)
            return;
        // The other half of the caller's completeness check, and not a duplicate of it: a file
        // whose marks are all there can still be sitting under a tag, where the rename that should
        // have taken it off found the name occupied. A later run picks such a file up by its name
        // alone, so it is not finished either.
        if (MissingMarksTag.IsTagged(file))
            return;
        var result = await RunHookAsync(template, "--run-after", file, name, ct);
        if (result.ExitCode == 0)
            return;
        _warnings++;
        // Announced rather than finishing the file's line: that line was printed the moment the
        // file was written, and this is news about what happened to it afterwards. Nothing is
        // withheld from the file itself - it is already written, and there is nothing left to
        // withhold.
        _progress.Announce(
            $"{name}: WARNING - --run-after exited with code {result.ExitCode}" +
            DescribeHookFailure(result));
    }

    /// <summary>
    /// Resolves a hook's placeholders for one file and runs it - or, under --dry-run, prints the
    /// command line it would have run and reports success, that mode's promise being that nothing
    /// on the machine is touched.
    /// </summary>
    /// <param name="template">The command line to resolve and run.</param>
    /// <param name="option">Which of the two hooks this is, for the log lines.</param>
    /// <param name="file">The file the hook is running for.</param>
    /// <param name="name">Its bare file name, which every line for it is prefixed with.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<HookRunner.HookResult> RunHookAsync(
        CommandTemplate template, string option, string file, string name, CancellationToken ct)
    {
        var command = template.Expand(file);
        if (_options.DryRun)
        {
            // The one hook line worth printing without --verbose: under --dry-run the command line
            // is the answer the user came for, not a note about how the run got there.
            var announcement = $"{name}: DRY RUN - {option} would run: {command}";
            if (_options.Quiet)
                _progress.Log(announcement);
            else
                _progress.Announce(announcement);
            return new HookRunner.HookResult(0, null);
        }
        _progress.Log($"{name}: {option}: {command}");
        return await HookRunner.RunAsync(command, line => _progress.Log($"{name}: {option}| {line}"), ct);
    }

    /// <summary>
    /// The command's own last word, appended to the warning line a failure produces. Worth carrying
    /// because the most common failure by far - a mistyped or missing program - explains itself in
    /// one line that would otherwise only be visible under --verbose.
    /// </summary>
    /// <param name="result">The failed hook run.</param>
    private static string DescribeHookFailure(HookRunner.HookResult result)
        => result.LastOutputLine is { } line ? $" ({line})" : "";

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
    /// The --verify decision tree for a file that would otherwise be skipped: marks that all
    /// check out leave the file alone; some of them wrong keeps the trusted ones and gap-recovers
    /// only around the unconfirmed ones; nearly all of them wrong warns and leaves the file
    /// completely alone (see <see cref="IsWholesaleFailure"/>).
    /// </summary>
    /// <param name="ctx">The file's context.</param>
    /// <param name="detector">The detector borrowed for this file.</param>
    /// <param name="watch">Running stopwatch of this file, for the processing-time average.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The detection result and its discard note, or null when the file was left
    /// unchanged - in which case it has already been counted and reported.</returns>
    private async Task<(DetectionResult Result, DroppedMarks Dropped, string Note)?> VerifyThenDetectAsync(
        FileContext ctx, ChapterDetector detector, Stopwatch watch, CancellationToken ct)
    {
        // --verify-only stops before the decision tree rather than inside it: what a file's marks
        // turned out to be is the whole of what that mode produces, and every branch below is about
        // acting on them.
        if (_options.VerifyOnly && ctx.Info.ChapterCount == 0)
        {
            ReportSkipped(ctx.Work, ctx.Name, "no chapter marks to verify", hint: "");
            return null;
        }

        var verify = await detector.VerifyExistingChaptersAsync(ctx.File, ctx.Info, ctx.Work, ctx.Logs, ct);
        if (_options.VerifyOnly)
        {
            ReportVerifyOnly(ctx, verify, watch);
            return null;
        }
        if (verify.Checked == 0 || verify.Passed)
        {
            // --fix may have found marks worth moving even where every one of them checked out;
            // that is the point of it, and the file then gets rewritten rather than skipped.
            if (verify.Outcomes.Any(m => m.CorrectedStartSeconds != null))
                return await ApplyMarkFixesAsync(ctx, verify, ct);
            // A pulling run has more to do than skip: the marks that just checked out may be the
            // server's and not yet in the file, or the file's and not yet on the server. Verifying
            // them was the "if present" half of --abs-pull --verify --abs-push; this is the rest.
            if (_options.UsesAbsPull)
            {
                if (verify.Checked > 0)
                    ctx.Logs.Write($"{verify.Checked} chapter mark(s) verified correct");
                await ReconcileMarksAsync(ctx, watch, ct);
                return null;
            }
            var verifyNote = verify.Checked > 0
                ? $"{verify.Checked} pre-existing chapter mark(s) verified correct"
                : $"has {ctx.Info.ChapterCount} chapter mark(s) (none had a checkable number)";
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
                $"mark(s){thresholdNote} - the file's existing marks left untouched",
                " (use --force without --verify to mark it from scratch)", important: true);
            return null;
        }

        // At least one mark is trusted, and they still outnumber the failures - only the gap(s)
        // around the unconfirmed one(s) get their own Probe (and, for a still-missing trailing
        // chapter, Scan); everything else in the file is left exactly as --verify found it.
        var trustedNote = $", {verify.ConfirmedChapters.Count} of {ctx.Info.ChapterCount} existing " +
                          $"mark(s) trusted, {verify.Failed} unconfirmed one(s) gap-recovered";
        return (await detector.DetectGapsAsync(ctx.File, ctx.Info, ctx.Work, ctx.Logs, verify, ct),
                default, trustedNote);
    }

    /// <summary>
    /// Reports one file under <c>--verify-only</c>: what its marks turned out to be, and nothing
    /// else. Counted as processed, because reading the whole file to answer that is the work this
    /// mode does.
    /// </summary>
    /// <remarks>
    /// The three groups are kept apart on the line for the reason they are kept apart everywhere
    /// else: a numbered mark that failed is a mark this run believes is wrong, a named one is the
    /// same belief with nothing that could act on it, and an unverifiable one is a question this
    /// run could not ask at all. Collapsing them into one count would tell a reader a book needs
    /// attention when all that happened is that they left a <c>--custom</c> mapping off the command
    /// line.
    /// </remarks>
    /// <param name="ctx">The file's context.</param>
    /// <param name="verify">What the verification found.</param>
    /// <param name="watch">Running stopwatch of this file, for the processing-time average.</param>
    private void ReportVerifyOnly(FileContext ctx, VerifyResult verify, Stopwatch watch)
    {
        RecordProcessed(watch);
        var failedNumbers = verify.Outcomes
            .Where(m => m is { ExpectedNumber: not null, Confirmed: false })
            .Select(m => m.ExpectedNumber!.Value).ToList();
        var named = verify.NamedOutcomes ?? [];
        var failedNamed = named.Where(m => m.Kind != null && !m.Confirmed).Select(m => m.Title).ToList();
        var unverifiable = named.Where(m => m.Kind == null).Select(m => m.Title).ToList();
        var namedConfirmed = named.Count(m => m.Confirmed);

        _outcomes.RecordVerifyFailures(ctx.Name, failedNumbers, failedNamed);
        _outcomes.RecordUnverifiable(ctx.Name, unverifiable);

        var parts = new List<string>
        {
            $"{verify.Checked - verify.Failed} of {verify.Checked} chapter mark(s) confirmed",
        };
        if (verify.Failed > 0)
            parts.Add($"{verify.Failed} not confirmed " +
                      $"(chapter {MissingMarksTag.FormatList(failedNumbers)})");
        if (namedConfirmed > 0)
            parts.Add($"{namedConfirmed} named mark(s) confirmed");
        if (failedNamed.Count > 0)
            parts.Add($"{failedNamed.Count} named mark(s) not confirmed");
        if (unverifiable.Count > 0)
            parts.Add($"{unverifiable.Count} mark(s) not checkable by this run");

        var bad = verify.Failed > 0 || failedNamed.Count > 0;
        if (bad)
            _warnings++;
        _progress.FinishWithSummary(
            ctx.Work, $"{ctx.Name}: {string.Join(", ", parts)}"
                      + FormatProcessingTime(ctx.Work.Elapsed, ctx.Info.DurationSeconds),
            important: bad);
    }

    /// <summary>
    /// The <c>--verify --fix</c> outcome for a file whose marks all check out but some of which
    /// sit away from their announcements: the file's existing mark list is written back with the
    /// corrected timestamps and nothing else touched.
    /// </summary>
    /// <remarks>
    /// Built from the marks themselves rather than through <see cref="BuildChapters"/>, which is
    /// what every detection path uses. That is deliberate: this mode's whole promise is that it
    /// moves marks and changes nothing else, so it must not be able to rename one, drop one it did
    /// not recognize, or invent an intro entry the file never had. Matching the corrections back by
    /// timestamp is exact - <see cref="ExistingMarkOutcome.StartSeconds"/> is a verbatim copy of
    /// the mark's own, not a recomputed figure.
    /// </remarks>
    /// <param name="ctx">The file's context.</param>
    /// <param name="verify">The verification result carrying the corrections.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Always null: the file is finished here, and the caller's detection path must not
    /// run for it.</returns>
    private async Task<(DetectionResult Result, DroppedMarks Dropped, string Note)?> ApplyMarkFixesAsync(
        FileContext ctx, VerifyResult verify, CancellationToken ct)
    {
        var corrections = verify.Outcomes
            .Where(m => m.CorrectedStartSeconds != null)
            .ToDictionary(m => m.StartSeconds, m => m.CorrectedStartSeconds!.Value);
        var chapters = ctx.Info.ExistingChapters
            .Select(c => corrections.TryGetValue(c.StartSeconds, out var fixedStart)
                ? c with { StartSeconds = fixedStart }
                : c)
            .OrderBy(c => c.StartSeconds)
            .ToList();
        var largest = corrections.Max(kv => Math.Abs(kv.Value - kv.Key));
        var what = $"{corrections.Count} of {verify.Checked} verified mark(s) nudged onto their " +
                   $"announcements (largest correction {largest:0.##} s)";

        _processed++;
        if (_options.DryRun)
        {
            _progress.FinishWithSummary(ctx.Work,
                $"{ctx.Name}: DRY RUN - would write {what}" +
                $"{await DryRunExportAsync(ctx, chapters, ct)}:" +
                $"{Environment.NewLine}{FormatChapterListing(chapters)}");
            return null;
        }
        var (_, backupNote) = await CommitChaptersAsync(ctx, chapters, null, complete: true, ct);
        _progress.FinishWithSummary(ctx.Work, $"{ctx.Name}: {what}{backupNote}"
            + FormatProcessingTime(ctx.Work.Elapsed, ctx.Info.DurationSeconds));
        return null;
    }

    /// <summary>
    /// Whether a file's marks failed verification <em>wholesale</em> rather than individually -
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
        var (chapters, _) = BuildChapters(result, _options.NamedMarkDistanceSeconds);
        var target = MissingMarksTag.PathFor(ctx.File, result.MissingNumbers);
        var missingList = MissingMarksTag.FormatList(result.MissingNumbers);
        RecordStillMissing(ctx, target, result.MissingNumbers);
        RecordLowConfidence(ctx, result, target);
        if (_options.DryRun)
        {
            _progress.FinishWithSummary(ctx.Work,
                $"{ctx.Name}: DRY RUN - unresolved chapter sequence gap (missing: {missingList}); " +
                $"would write {FormatWrittenCount(result, chapters, "partial mark(s)")}" +
                $"{await DryRunExportAsync(ctx, chapters, ct)}" +
                $"{WouldRenameNote(target)}:{Environment.NewLine}{FormatChapterListing(chapters)}",
                important: true);
            return null;
        }
        var (finalPath, backupNote) = await CommitChaptersAsync(ctx, chapters, target, complete: false, ct);
        _progress.FinishWithSummary(ctx.Work,
            $"{ctx.Name}: WARNING - unresolved chapter sequence gap (missing: {missingList}); " +
            $"wrote {FormatWrittenCount(result, chapters, "partial mark(s)")}" +
            $"{RenameNote(target, finalPath)}{backupNote}"
            + FormatProcessingTime(ctx.Work.Elapsed, ctx.Info.DurationSeconds), important: true);
        return SamePath(finalPath, ctx.File) ? null : finalPath;
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
            _options.DryRun || TagRenameSuppressed(taggedPath) ? ctx.Name : Path.GetFileName(taggedPath),
            missingNumbers);

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
        if (result.LowConfidenceChapters.Count == 0)
            return;
        // The profile is the one this file actually resolved to, which with --lang auto is a
        // per-file answer - so a batch mixing a bare-number book with ordinary ones earns the
        // block's footnote from the one book it applies to.
        _outcomes.RecordLowConfidence(
            _options.DryRun || finalPath == null || TagRenameSuppressed(finalPath)
                ? ctx.Name
                : Path.GetFileName(finalPath),
            result.LowConfidenceChapters, result.SequenceCount, result.Profile.BareNumberAnnouncements);
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
        var reason = DescribeNoChapters(result, ctx.Options);
        _outcomes.RecordNoChapters(ctx.Name, reason);
        var langHint = ctx.Options.AutoLanguage ? $" (language used: {result.Profile.Language})" : "";
        _progress.FinishWithSummary(ctx.Work, $"{ctx.Name}: {reason}; file unchanged{langHint}"
            + FormatProcessingTime(ctx.Work.Elapsed, ctx.Info.DurationSeconds));
    }

    /// <summary>Which of the three ways a detection can come back empty-handed this one was, as the
    /// fragment following the file name. One wording feeding both the file's own result line and
    /// its --summary entry, so the two can never end up disagreeing.</summary>
    /// <param name="result">The file's detection result.</param>
    /// <param name="options">The options this file was detected with, which a per-folder
    /// <c>.abchapterize-config</c> may have changed - so the message quotes the threshold that
    /// actually applied rather than the one on the command line.</param>
    private static string DescribeNoChapters(DetectionResult result, CliOptions options)
        => result.EarlyAborted
            ? "early-abort - no chapter found within the first " +
              $"{options.EarlyAbortMinutes:0.#} minute(s) of play time"
            : result.BelowExpectedStartNumber is { } foundNumber
                ? $"first chapter found ({foundNumber}) is below --expected-start-chapter " +
                  $"{options.ExpectedStartChapter}"
                : "no chapter phrases found";

    /// <summary>The normal, successful outcome: writes the detected chapters into the file (or,
    /// under --dry-run, lists what would be written), takes any ".missing-marks" tag back off the
    /// file name, and prints the file's summary line.</summary>
    /// <param name="ctx">The file's context.</param>
    /// <param name="result">The file's detection result.</param>
    /// <param name="dropped">The marks the file arrived with that this run threw away, stated
    /// before anything else on the line: what a file <em>had</em> and what it now has are different
    /// questions, and the second is unreadable while the first is buried among the notes.</param>
    /// <param name="note">Anything the path that produced this result wants said about the
    /// marks the file arrived with but did <em>not</em> lose - --verify's trusted ones. Empty
    /// for an ordinary run.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The original name the file was restored to when this run completed a previously
    /// tagged one, or null when it kept the name it came in under.</returns>
    private async Task<string?> WriteDetectedChaptersAsync(
        FileContext ctx, DetectionResult result, DroppedMarks dropped, string note,
        CancellationToken ct)
    {
        _runStats.AccumulateConfidence(result.Chapters);
        var (chapters, merged) = BuildChapters(result, _options.NamedMarkDistanceSeconds);
        var notes = note + FormatNamedMarksNote(result, merged) + FormatLowConfidenceNote(result) +
                    FormatSequenceRestartNote(result) + FormatUnverifiedNumbersNote(result) +
                    FormatLanguageNote(result);
        var what = FormatWrittenCount(result, chapters, "mark(s) written");
        // A low-confidence mark is worth surfacing above the progress bar; so is a book that gave up
        // chapters below its sequence, which is otherwise indistinguishable from one that simply
        // ends early; so is a number nothing could corroborate, whose whole point is that the output
        // looks clean; and so is a file that turned out to hold several parts, since that is what
        // decides whether its marks are titled "Chapter 1" or "Part 2 - Chapter 1".
        var important = result.LowConfidenceChapters.Count > 0 || result.SequenceRestartSkips > 0 ||
                        result.UnverifiedNumbers is { Count: > 0 } || result.SequenceCount > 1;
        // The tag is a to-do note left on the file name, and a run that reached here left nothing
        // to do: every chapter of the sequence is present. So the file gets its own name back.
        // A *numbered* tag normally takes the resume path instead and is untagged there; what
        // reaches this point is the unnumbered form, which no resume picks up, or either form under
        // --force, which redetects the whole file from scratch.
        var restored = MissingMarksTag.IsTagged(ctx.File) ? MissingMarksTag.StripFrom(ctx.File) : null;
        RecordLowConfidence(ctx, result, restored);

        if (_options.DryRun)
        {
            var wouldRename = WouldRenameNote(restored);
            _progress.FinishWithSummary(ctx.Work,
                $"{ctx.Name}: DRY RUN - would {DescribeDropped(dropped, prospective: true)}" +
                $"write {FormatWrittenCount(result, chapters)}{notes}" +
                $"{await DryRunExportAsync(ctx, chapters, ct)}{wouldRename}:" +
                $"{Environment.NewLine}{FormatChapterListing(chapters)}", important);
            return null;
        }
        var (finalPath, backupNote) = await CommitChaptersAsync(ctx, chapters, restored, complete: true, ct);
        _progress.FinishWithSummary(ctx.Work,
            $"{ctx.Name}: {DescribeDropped(dropped, prospective: false)}{what}" +
            $"{notes}{RenameNote(restored, finalPath)}{backupNote}"
            + FormatProcessingTime(ctx.Work.Elapsed, ctx.Info.DurationSeconds), important);
        return SamePath(finalPath, ctx.File) ? null : finalPath;
    }

    /// <summary>
    /// What the summary line calls the marks this file yielded: how many entries were written, and
    /// in brackets how that splits into numbered chapters (with their range) and named ones.
    /// <para>
    /// Counted off the built list rather than off the detection result, so the total is what the
    /// file actually receives: the intro counts as a named mark, and a named mark folded into a
    /// chapter's title (see <see cref="MergeCrowdedNamedMarks"/>) counts as nothing, being no entry
    /// of its own. A component that comes to zero is left out rather than printed - a book with no
    /// named marks should not have to say so, and under --ignore-chapter-numbers there are no
    /// numbered chapters to report at all.
    /// </para>
    /// </summary>
    /// <param name="result">The file's detection result, for the numbered chapters and their range.</param>
    /// <param name="written">The entries actually being written, intro and named marks included.</param>
    /// <param name="noun">What to call the entries - "mark(s)", or "partial mark(s)" where the
    /// sequence is known to be incomplete.</param>
    private static string FormatWrittenCount(
        DetectionResult result, List<Chapter> written, string noun = "mark(s)")
    {
        var numbered = result.Chapters.Count;
        var components = new List<string>();
        if (numbered > 0)
            components.Add($"{numbered} chapter(s) {FormatChapterRanges(result)}");
        if (written.Count - numbered > 0)
            components.Add($"{written.Count - numbered} named");
        return $"{written.Count} {noun}" +
               (components.Count > 0 ? $" ({string.Join(", ", components)})" : "");
    }

    /// <summary>
    /// The numbered chapters' range, or - for a file whose numbering restarts partway through - one
    /// range per part. Spelling it out here is the whole of what the summary line says about parts,
    /// and it is where a reader would look: "35 chapter(s) 1-9" is what a book of 1-15, 1-11 and 1-9
    /// would otherwise report, which is both wrong-looking and wrong.
    /// </summary>
    /// <param name="result">The file's detection result.</param>
    private static string FormatChapterRanges(DetectionResult result)
    {
        var ranges = GapPlanning.BySequence(result.Chapters)
            .Select(s => s[0].Number == s[^1].Number
                ? $"{s[0].Number}"
                : $"{s[0].Number}-{s[^1].Number}")
            .ToList();
        return result.SequenceCount > 1
            ? $"in {result.SequenceCount} parts ({string.Join(", ", ranges)})"
            : ranges[0];
    }

    /// <summary>Note about the named marks that are not entries of their own: the ones folded into
    /// a neighbouring chapter's title, and a file that hit the per-file --custom cap - marks the
    /// user asked for were dropped there, which would otherwise look exactly like a mapping that
    /// stopped matching halfway through the book. Empty when neither applies.
    /// <para>
    /// The named marks that <em>were</em> written are counted by <see cref="FormatWrittenCount"/>
    /// instead, which is the one place the file's marks are broken down.
    /// </para>
    /// </summary>
    /// <param name="result">The file's detection result.</param>
    /// <param name="merged">How many of the named marks were folded into a chapter's title rather
    /// than written as entries of their own (see <see cref="MergeCrowdedNamedMarks"/>). Reported
    /// because they are in neither number the summary line prints.</param>
    private static string FormatNamedMarksNote(DetectionResult result, int merged)
    {
        var limitNote = result.CustomMarkLimitHit
            ? $", custom-mark limit of {DetectionTuning.MaxCustomMarksPerFile} reached - further matches dropped"
            : "";
        var mergedNote = merged > 0
            ? $", {merged} named mark(s) merged into a neighbouring chapter's title"
            : "";
        return mergedNote + limitNote;
    }

    /// <summary>Note naming the marks Whisper was unsure about, so they can be spot-checked;
    /// empty when every mark was confident.</summary>
    /// <param name="result">The file's detection result.</param>
    private static string FormatLowConfidenceNote(DetectionResult result)
        => result.LowConfidenceChapters.Count > 0
            ? $", {result.LowConfidenceChapters.Count} low-confidence mark(s) " +
              $"({RunOutcomes.NameChapters(result.LowConfidenceChapters, result.SequenceCount)}; " +
              "see --verbose)"
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

    /// <summary>Note for the announcements this file lost below its chapter sequence without them
    /// ever adding up to a new part (see <see cref="RegionProber.SequenceRestartSkips"/>): they were
    /// heard and understood, and dropped only because their numbers had already been used. Without
    /// this the file just stops yielding chapters partway through, which reads as a detection
    /// failure. Empty for the ordinary book, and for one whose parts were all recognized - those are
    /// reported as parts by <see cref="FormatChapterRanges"/> instead.</summary>
    /// <param name="result">The file's detection result.</param>
    private static string FormatSequenceRestartNote(DetectionResult result)
        => result.SequenceRestartSkips > 0
            ? $", {result.SequenceRestartSkips} announcement(s) skipped for being below the chapter " +
              "sequence without enough consecutive ones to confirm a new part; try " +
              "--ignore-chapter-numbers"
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

    /// <summary>Writes the --export sidecar for a run that is only previewing what it would do.
    /// </summary>
    /// <param name="ctx">The file's context.</param>
    /// <param name="chapters">The chapters that would be written.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// Beside the name the file already has, because --dry-run renames nothing: the prospective
    /// name is what the summary line reports, not where anything lands. That is also what makes
    /// the pairing work - the sidecar this leaves behind is the one --import goes looking for.
    /// </remarks>
    private Task<string> DryRunExportAsync(FileContext ctx, List<Chapter> chapters, CancellationToken ct)
        => ExportSidecarAsync(ctx, chapters, ctx.File, ct);

    /// <summary>Writes the --export sidecar, if asked for, and returns the note announcing it.
    /// Runs regardless of --dry-run, so a run can be previewed and saved for hand-editing in one
    /// pass.</summary>
    /// <param name="ctx">The file's context.</param>
    /// <param name="chapters">The chapters to export.</param>
    /// <param name="audioFile">The audio file the sidecar is to sit beside and be named after -
    /// the path the file ended up under, which on three of the commit paths is not the one it
    /// arrived under.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// <para>
    /// The path is stated by the caller rather than taken from <paramref name="ctx"/> because a
    /// commit may rename the file as it writes it - a chapter-sequence gap tags it, a completed
    /// resume untags it - and <c>--import</c> looks for a sidecar beside the name the file has
    /// now. Built from the pre-rename path, it would sit beside a name that no longer exists.
    /// </para>
    /// <para>
    /// Overwrites whatever is there. The alternative is a run that reports an export and leaves the
    /// previous run's chapters on disk, which is the worse failure by some distance: a stale
    /// sidecar is indistinguishable from a fresh one until it is imported.
    /// </para>
    /// </remarks>
    private async Task<string> ExportSidecarAsync(
        FileContext ctx, List<Chapter> chapters, string audioFile, CancellationToken ct)
    {
        if (!_options.Export)
            return "";
        var sidecarPath = ChapterSidecar.PathFor(audioFile, _options.SimpleMetadata);
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
    /// <param name="complete">
    /// Whether the chapter sequence has no gaps left in it. Only <c>--abs-push</c> reads it, and
    /// only to decide whether a gapped set still has to earn its way to the server - see
    /// <see cref="WithholdPartialPush"/>. Stated by the caller rather than derived here because the
    /// two paths that reach this with a gap are exactly the two that re-tag the file as still
    /// missing marks, and that is a fact about the outcome each of them has already worked out.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Where the file ended up - see <see cref="RenameCommitted"/> - and the backup note
    /// for its summary line.</returns>
    private async Task<(string Path, string Note)> CommitChaptersAsync(
        FileContext ctx, List<Chapter> chapters, string? renameTo, bool complete, CancellationToken ct)
    {
        var writeNote = await WriteChaptersIfTheContainerHoldsThemAsync(ctx, chapters, ct);
        // The server is told here rather than at the six places that reach this method, and after
        // the write rather than before it: what goes to Audiobookshelf is then exactly what the
        // file received, including a partial write left by an unresolved gap - which is worth
        // having on the server, since the alternative is nothing at all. A file the run declined to
        // write never gets here and so never sends anything.
        var (pushNote, pushMismatch) = ctx.Abs is { } abs
            ? await _abs!.PushAsync(abs.Book, chapters, ctx.Info.DurationSeconds, ct)
            : _options.AbsPush
                ? await PushLocalFileAsync(ctx, chapters, complete, ct)
                : ("", false);
        // Counted here rather than at the six callers: a push that the server did not store as sent
        // is a warning about the run, not about the outcome any one of them was reporting, and every
        // one of them would have had to remember to ask.
        if (pushMismatch)
            _warnings++;
        // The one place a rename is performed, so the one place --no-rename has to be applied.
        var finalPath = RenameCommitted(ctx.File, TagRenameSuppressed(renameTo) ? null : renameTo);
        // The sidecar last of all, for the same reason the push is here at all: this is where the
        // file's final name is known, and a sidecar is looked up by the audio file's name. Written
        // from here rather than by the six callers, so a gap, a resume and a --verify --fix all
        // export what they wrote instead of only the run that had nothing left to find.
        return (finalPath, writeNote + pushNote + await ExportSidecarAsync(ctx, chapters, finalPath, ct));
    }

    /// <summary>
    /// The <c>--abs-push</c> half: finds the book this local file belongs to and sends it the
    /// marks the file has just been given.
    /// </summary>
    /// <param name="ctx">The file's context.</param>
    /// <param name="chapters">The chapters just written into the file.</param>
    /// <param name="complete">Whether the chapter sequence has no gaps left in it.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The clause the summary line closes with about the push, and whether the server was
    /// found afterwards to be holding something other than what was sent.</returns>
    /// <remarks>
    /// <para>
    /// A complete set always goes. A set with a gap still in it goes only when the server has
    /// <em>fewer</em> marks than this run is holding, which is the whole of the difference from ABS
    /// mode, where a partial list is sent unconditionally. There the server holds the only copy, so
    /// a partial list beats nothing at all; here the file holds them and can be resumed later, and
    /// the danger is replacing what the server already has with something worse and then leaving it
    /// that way - finishing the file afterwards would not push again by itself. Comparing counts is
    /// what separates the two: 34 marks with a hole at chapter 7 are unambiguously more than the
    /// nothing a freshly scanned book carries, so refusing them protected nobody, while a server
    /// list at least as long is one this run has no evidence it can improve on. See
    /// <see cref="WithholdPartialPush"/> for why the count is the whole of the test.
    /// </para>
    /// <para>
    /// Nothing here can fail the file. The marks are in the file by the time this runs, which is
    /// what the user asked for first; a book that cannot be matched, or a server that has gone
    /// away, is reported in the summary line and nowhere else. Failing a written file over a
    /// push that did not happen would be the one outcome nobody wants.
    /// </para>
    /// <para>
    /// A run that also pulls has done the looking already, and its answer is used rather than
    /// asked for again - which matters beyond saving a lookup, because the pull refuses a book
    /// whose play time is not this file's (see <see cref="ABChapterize.Abs.AbsChapterPull"/>) and
    /// re-matching here would walk straight past that refusal. It is also where the second half of
    /// the reconciliation lives: a list the server already has is not sent back to it.
    /// </para>
    /// </remarks>
    private async Task<(string Note, bool Mismatch)> PushLocalFileAsync(
        FileContext ctx, List<Chapter> chapters, bool complete, CancellationToken ct)
    {
        if (ctx.Pull is { } pull)
        {
            if (pull.Book == null)
                return ($", not sent to ABS ({pull.Note})", false);
            if (AbsChapterMerge.SameMarks(chapters, pull.FromServer))
                return (", not sent to ABS (it already has these marks)", false);
            // The list itself, fetched for this very file, rather than the catalogue's count.
            if (WithholdPartialPush(chapters, complete, pull.FromServer.Count) is { } held)
                return (held, false);
            return await _abs!.PushAsync(pull.Book, chapters, ctx.Info.DurationSeconds, ct);
        }

        // Asked for even when the set has a gap, unlike before: what the server already holds is
        // the thing that decides whether a gapped set may be sent, and the book is what carries it.
        var match = await _abs!.MatchAsync(ctx.File, ctx.Info, ct);
        if (match.Book == null)
            return ($", not sent to ABS ({match.Reason})", false);
        if (WithholdPartialPush(chapters, complete, match.Book.ChapterCount) is { } withheld)
            return (withheld, false);

        ctx.Logs.Write(match.Reason);
        return await _abs.PushAsync(match.Book, chapters, ctx.Info.DurationSeconds, ct);
    }

    /// <summary>
    /// Decides whether a chapter set with a gap still in it should be kept back from the server,
    /// and says why when it should.
    /// </summary>
    /// <param name="chapters">The marks this run is holding.</param>
    /// <param name="complete">Whether the chapter sequence has no gaps left in it.</param>
    /// <param name="onServer">How many marks Audiobookshelf currently has for the book.</param>
    /// <returns>The clause the summary line closes with when nothing is to be sent, or null when
    /// the push should go ahead.</returns>
    /// <remarks>
    /// <para>
    /// A complete set is never withheld, so this only ever speaks about the two outcomes that reach
    /// a commit with a hole in the numbering - an unresolved gap and an incomplete resume, both of
    /// which re-tag the file as still missing marks.
    /// </para>
    /// <para>
    /// <b>Counts, not contents, and deliberately so</b> (the user's call, 2026-08-26): somebody
    /// running <c>--abs-push</c> has already decided the file is the source of truth, so a shorter
    /// list on the server is not something to preserve on the chance that it was curated. What is
    /// left worth guarding against is the plain regression - a set of twelve replacing a set of
    /// thirty-four and then staying, since the file can be resumed but the push does not repeat
    /// itself - and a count is all that takes.
    /// </para>
    /// <para>
    /// Outside a pulling run the figure comes from the catalogue listing fetched once per run, so
    /// it is as fresh as the run's own start. The one thing that could stale it is a second local
    /// file matching the same book after the first has pushed to it, which a folder of distinct
    /// audiobooks cannot produce - and the cost if it ever did is one comparison against a count
    /// this run put there itself.
    /// </para>
    /// Internal (and pure) for unit testing.
    /// </remarks>
    internal static string? WithholdPartialPush(IReadOnlyList<Chapter> chapters, bool complete, int onServer)
    {
        if (complete || chapters.Count > onServer)
            return null;
        return onServer > 0
            ? $", not sent to ABS while chapters are missing (it already has {onServer})"
            : ", not sent to ABS while chapters are missing";
    }

    /// <summary>
    /// Writes the marks into the file, unless its container cannot carry them.
    /// </summary>
    /// <param name="ctx">The file's context.</param>
    /// <param name="chapters">The titled chapters to write.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The clause the summary line closes with about the write, empty when there was none.</returns>
    /// <remarks>
    /// <para>
    /// Only ABS mode can reach the second branch, and only because it is the one mode where the
    /// file is not the destination: local targets are refused at the command line and local
    /// enumeration never yields anything but a chapter-capable container, while a book off a
    /// server is fetched whatever its format because its marks are going into the server's
    /// database. Remuxing that temporary copy would cost a full pass over it to produce a file
    /// with no chapters in it - ffmpeg's muxers for those containers accept chapters and drop
    /// them - and then delete it.
    /// </para>
    /// <para>
    /// Keyed on the container alone rather than on ABS mode, so that a future path which does let
    /// such a file through says nothing was written instead of quietly writing nothing.
    /// </para>
    /// </remarks>
    private async Task<string> WriteChaptersIfTheContainerHoldsThemAsync(
        FileContext ctx, List<Chapter> chapters, CancellationToken ct)
    {
        if (!AudioFormats.CanHoldChapters(ctx.File))
        {
            ctx.Logs.Write($"{Path.GetExtension(ctx.File)} cannot hold chapter marks - the copy is "
                           + "left as it was downloaded and the marks go to the server only");
            return "";
        }

        var earlierBakKept = await ctx.Ffmpeg.WriteChaptersAsync(
            ctx.File, chapters, ctx.Info.DurationSeconds, _options.Backup,
            BeginFinishPhase(ctx.Work, ctx.Info), ct);
        return FormatBackupNote(_options.Backup, earlierBakKept);
    }

    /// <summary>
    /// Puts a freshly written file under the name its outcome calls for, and answers where it
    /// actually ended up.
    /// </summary>
    /// <param name="file">The file just written, under the name it came in with.</param>
    /// <param name="renameTo">The name the outcome calls for, or null when it keeps its own.</param>
    /// <returns>The path the file now has: <paramref name="renameTo"/> when the move happened,
    /// <paramref name="file"/> when there was nothing to do or the destination was taken.</returns>
    /// <remarks>
    /// <para>
    /// The move never overwrites, and that is the whole point of the method. The destination is an
    /// audiobook's name, and anything already sitting under it is a file this run did not write - a
    /// copy the user put back beside a tagged one, or an earlier run's tagged result. Replacing it
    /// would destroy the only copy of something to save the caller a warning, which is the trade
    /// <see cref="CleanupRunner"/> refuses at length and this path used to make in silence.
    /// </para>
    /// <para>
    /// Renaming a file onto its own name is not a rename and is skipped rather than refused: a
    /// resume that closes none of its gaps re-tags with the numbers it already carries, and a
    /// non-overwriting move would fail on the destination it is itself sitting in.
    /// </para>
    /// </remarks>
    /// <exception cref="AppError">The rename failed. Named rather than left to <c>File.Move</c>,
    /// whose exceptions carry no path: by this point the marks are in the file and only its name is
    /// wrong, so the message has to say which file that is for the run to be recoverable by
    /// hand.</exception>
    internal static string RenameCommitted(string file, string? renameTo)
    {
        if (renameTo == null || SamePath(file, renameTo) || File.Exists(renameTo))
            return file;
        try
        {
            File.Move(file, renameTo);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or NotSupportedException)
        {
            throw new AppError(
                $"Could not rename \"{file}\" to \"{renameTo}\": {ex.Message} " +
                "Its chapter marks are written; only the name is still the old one.");
        }
        return renameTo;
    }

    /// <summary>
    /// <see cref="RenameNote"/>'s <c>--dry-run</c> counterpart: what the line promises about the
    /// file's name. Empty where the run would not rename it - either because the outcome calls for
    /// no rename at all, or because <c>--no-rename</c> would hold it back - so that a dry run
    /// never announces a name the real run would not produce.
    /// </summary>
    /// <param name="wanted">The name the outcome calls for, or null when none does.</param>
    /// <param name="verb">How this path words the rename.</param>
    private string WouldRenameNote(string? wanted, string verb = "rename to")
        => wanted == null || TagRenameSuppressed(wanted)
            ? ""
            : $" and {verb} {Path.GetFileName(wanted)}";

    /// <summary>
    /// Whether <c>--no-rename</c> holds back the move to <paramref name="wanted"/>: it does for a
    /// name carrying a ".missing-marks" tag, and only for one.
    /// <para>
    /// The tag is a note this tool leaves on somebody's file name, and the option says not to leave
    /// any - so writing one is refused, while taking one off is not. That asymmetry is deliberate
    /// and it is what makes the option safe to switch on over a library that already has tags in
    /// it: the alternative leaves a completed file tagged for ever, and
    /// <see cref="PlanFor"/> sends a tagged file down the resume path <em>before</em> it can be
    /// skipped for the marks it already carries - so every later run would re-analyze a file with
    /// nothing left to find in it.
    /// </para>
    /// <para>
    /// Read by the step that performs the rename and by the ones that report or list the file
    /// afterwards, so that a name nobody is going to see is never announced as one.
    /// </para>
    /// </summary>
    /// <param name="wanted">The name the outcome called for, or null when none was.</param>
    /// <param name="noRename">Whether <c>--no-rename</c> was given. A parameter rather than a read
    /// of the run's options, so the rule can be tested without a processor. Internal for that
    /// reason.</param>
    internal static bool TagRenameSuppressed(string? wanted, bool noRename)
        => noRename && wanted != null && MissingMarksTag.IsTagged(wanted);

    /// <summary>This run's answer to <see cref="TagRenameSuppressed(string?, bool)"/>.</summary>
    /// <param name="wanted">The name the outcome called for, or null when none was.</param>
    private bool TagRenameSuppressed(string? wanted)
        => TagRenameSuppressed(wanted, _options.NoRename);

    /// <summary>Whether two paths name the same file, by the platform's own rules.</summary>
    /// <param name="left">One path.</param>
    /// <param name="right">The other.</param>
    private static bool SamePath(string left, string right)
        => CliOptions.PathComparer.Equals(
            CliOptions.NormalizePath(left), CliOptions.NormalizePath(right));

    /// <summary>
    /// The clause a summary line closes with about the file's name: what it was renamed to, or that
    /// it was not and why. Said out loud rather than quietly omitted, because the marks are written
    /// either way and a reader who goes looking for the new name has to be told why it is not there
    /// - which holds for <c>--no-rename</c> too, where the name that did not happen is the tool's
    /// only lasting record that this file is incomplete.
    /// <para>
    /// The two ways a wanted rename does not happen are worth telling apart: one the reader asked
    /// for, the other an obstacle they may want to clear.
    /// </para>
    /// </summary>
    /// <param name="wanted">The name the outcome called for, or null when none was.</param>
    /// <param name="final">Where the file actually ended up.</param>
    /// <param name="verb">How this path words a rename that did happen.</param>
    private string RenameNote(string? wanted, string final, string verb = "renamed to")
    {
        if (wanted == null)
            return "";
        if (SamePath(final, wanted))
            return $", {verb} {Path.GetFileName(wanted)}";
        return TagRenameSuppressed(wanted)
            ? ", not renamed (--no-rename)"
            : $", NOT {verb} {Path.GetFileName(wanted)} - a file of that name is already there";
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
    /// Applies the policy for pre-existing chapter marks, shared between normal
    /// detection and --import: without --force, a file with any marks is skipped;
    /// with --max-chapters, a mark count above the threshold is treated as bogus and
    /// discarded even without --force.
    /// </summary>
    /// <param name="info">Probed media info of the file being processed.</param>
    /// <returns>Whether the file should be skipped, and what it arrived carrying.</returns>
    private (bool Skip, DroppedMarks Dropped) EvaluateExistingChapters(MediaInfo info)
    {
        if (info.ChapterCount == 0)
            return (false, default);
        var bogus = _options.MaxChapters is { } max && info.ChapterCount > max;
        if (!_options.Force && !bogus)
            return (true, default);
        return (false, new DroppedMarks(info.ChapterCount, bogus && !_options.Force));
    }

    /// <summary>The marks a file arrived with that this run threw away, carried as numbers
    /// rather than as finished text so the same fact can be stated in either mood - a dry run has
    /// dropped nothing yet.</summary>
    /// <param name="Count">How many marks the file had; 0 for the ordinary unmarked file.</param>
    /// <param name="Bogus">Whether they were discarded for exceeding --max-chapters rather than
    /// because --force said to replace them.</param>
    private readonly record struct DroppedMarks(int Count, bool Bogus);

    /// <summary>
    /// How the summary line opens on a file that arrived already marked: the count first, before
    /// anything this run produced, because "23 marks" reads very differently on a file that had
    /// none and on one that had 40 thrown away to get there. Empty for a file that had none, which
    /// is the ordinary case and needs no words.
    /// </summary>
    /// <param name="dropped">What the file arrived carrying.</param>
    /// <param name="prospective">True under --dry-run, where nothing has been dropped yet and the
    /// caller supplies the "would" this fragment then continues.</param>
    private static string DescribeDropped(DroppedMarks dropped, bool prospective)
        => dropped.Count == 0
            ? ""
            : (prospective ? "drop " : "") +
              $"{dropped.Count} {(dropped.Bogus ? "bogus" : "existing")} mark(s)" +
              (dropped.Bogus ? " (> --max-chapters)" : "") +
              (prospective ? " and " : " dropped, ");

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
        _reached++;
        var work = new WorkTracker();
        _progress.Start(name, work);
        try
        {
            // The hook is asked of RunOutcomes for the same reason the detection path asks it (see
            // ProcessOneCoreAsync): both of this mode's refusals - no sidecar, marks already there
            // - are recorded as skips, and a file the run left alone runs neither hook.
            var skippedBefore = _outcomes.SkippedCount;
            await ImportOneCoreAsync(file, name, work, ffmpeg, ct);
            pending.Progress?.MarkDone(file, null);
            if (_outcomes.SkippedCount == skippedBefore)
                await RunAfterHookAsync(file, name, ct);
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
    /// pre-existing-mark policy detection uses, and write what it contains.</summary>
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

        var (skip, dropped) = EvaluateExistingChapters(info);
        if (skip)
        {
            ReportSkipped(work, name, $"has {info.ChapterCount} chapter mark(s)");
            return;
        }

        if (_options.RunBefore is { } before)
        {
            var probed = new FileContext(file, name, work, new DetectionLog(log, null), info, ffmpeg, _options);
            if (!await RunBeforeHookAsync(before, probed, ct))
                return;
            // Re-probed for the same reason the detection path re-probes: the command may have
            // rewritten the file, and its duration is what the chapters are about to be written
            // against. The mark policy is applied again with it, so a command that marked the file
            // itself is not marked over.
            if (!_options.DryRun)
            {
                info = await ProbeAndLogAsync(file, ffmpeg, log, ct);
                (skip, dropped) = EvaluateExistingChapters(info);
                if (skip)
                {
                    ReportSkipped(work, name, $"has {info.ChapterCount} chapter mark(s)");
                    return;
                }
            }
        }

        var text = await File.ReadAllTextAsync(sidecarPath, ct);
        var chapters = _options.SimpleMetadata
            ? ChapterSidecar.ParseSimple(text, sidecarPath)
            : ChapterSidecar.ParseFfMetadata(text, sidecarPath);
        RecordProcessed(watch);

        if (_options.DryRun)
        {
            _progress.FinishWithSummary(work,
                $"{name}: DRY RUN - would {DescribeDropped(dropped, prospective: true)}" +
                $"import {chapters.Count} chapter(s) from {Path.GetFileName(sidecarPath)}:" +
                $"{Environment.NewLine}{FormatChapterListing(chapters)}");
            return;
        }

        var (_, backupNote) = await CommitChaptersAsync(
            new FileContext(file, name, work, new DetectionLog(log, null), info, ffmpeg, _options),
            chapters, null, complete: true, ct);
        _progress.FinishWithSummary(work,
            $"{name}: {DescribeDropped(dropped, prospective: false)}{chapters.Count} chapter(s) " +
            $"imported from {Path.GetFileName(sidecarPath)}{backupNote}"
            + FormatProcessingTime(work.Elapsed, info.DurationSeconds));
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

    /// <summary>Applies the extension, temporary-file, backup, --filter and --newer-than rules to
    /// one target's candidates and sorts what survives into
    /// <see cref="NaturalPathComparer">natural</see> order.</summary>
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
            .Where(f => !f.Contains(FfmpegClient.ScratchInfix, StringComparison.OrdinalIgnoreCase))
            .Where(f => _options.Revert || !f.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
            .Where(f => _options.FilterRegex == null || _options.FilterRegex.IsMatch(f))
            .Where(IsRecentEnough)
            .OrderBy(f => f, NaturalPathComparer.Instance);
    }

    /// <summary>Whether a file passes <c>--newer-than</c>. Always true when the option was not
    /// given.</summary>
    /// <param name="file">Path of the candidate file.</param>
    /// <remarks>
    /// <para>
    /// Last-write time, which is the only "how old is this" a folder on disk answers the same way
    /// everywhere: creation time is not recorded at all on some file systems, and on Windows it is
    /// set to <em>now</em> by an ordinary copy, so a shelf moved between drives would read as
    /// brand new. Last-write time survives that copy where the tool doing it preserves timestamps,
    /// and where it does not, both would have been wrong anyway.
    /// </para>
    /// <para>
    /// A consequence worth knowing rather than working around: writing marks into a file rewrites
    /// it, so a book this run marks is younger afterwards than it was before, and the same
    /// <c>--newer-than</c> the next day still selects it. It is then skipped for having marks,
    /// which is the cheap outcome.
    /// </para>
    /// </remarks>
    private bool IsRecentEnough(string file)
        => _newerThanUtc is not { } cutoff || File.GetLastWriteTimeUtc(file) >= cutoff;

    /// <summary>What to say when the enumeration turned up nothing: the selection rules that were
    /// in play, or - where there were none - what a supported file even looks like.</summary>
    /// <remarks>
    /// Naming the rules rather than saying only that nothing was found, because with a filter in
    /// play "nothing" is nearly always the filter's doing and nearly never an empty folder, and
    /// which of two filters did it is the first thing worth knowing.
    /// </remarks>
    private string NothingFoundNote
    {
        get
        {
            List<string> rules = [];
            if (_options.FilterRegex != null || _options.FilterExtensions != null)
                rules.Add("--filter");
            if (_options.NewerThan != null)
                rules.Add("--newer-than");
            return rules.Count > 0
                ? $"No audio files matching {string.Join(" and ", rules)} found."
                : $"No supported audio files ({AudioFormats.ChapterCapableText}) found.";
        }
    }
}
