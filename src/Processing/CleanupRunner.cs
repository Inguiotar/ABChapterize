// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Audio;
using ABChapterize.Cli;
using ABChapterize.Errors;
using ABChapterize.Formatting;
using ABChapterize.Ui;

namespace ABChapterize.Processing;

/// <summary>
/// The <c>--cleanup</c> housekeeping mode: puts a folder back the way it was before this tool was
/// let loose on it, without transcribing anything. Leftover scratch files go, <c>--backup</c> files
/// go (or are restored, with <c>--revert</c>), <c>--debug</c> logs go, and files a run flagged
/// ".missing-marks" get their own names back.
/// </summary>
/// <remarks>
/// <para>
/// Two rules shape everything below, and both exist because this mode deletes files it did not
/// just create - which is a different risk from anything else in the tool.
/// </para>
/// <para>
/// <b>Nothing is deleted that could be an audiobook's only copy.</b> A ".bak" is only removed once
/// the file it backs up has been found next to it <em>and</em> probed to be of the same length
/// (<see cref="BackupToleranceSeconds"/>), because a ".bak" whose partner is gone or is a different
/// recording is the only copy of something. A parked original (see
/// <see cref="FfmpegClient.ParkedSuffix"/>) with no partner is put back rather than deleted, for the
/// same reason: it is the audiobook itself, left behind by a write that was killed in its last
/// second.
/// </para>
/// <para>
/// <b>Everything is decided before anything is done.</b> The scan builds the whole plan, the plan is
/// what the confirmation prompt describes, and only then is a single file touched. That is also what
/// makes the ordering below expressible at all: the untagging has to be planned before the ".bak"
/// decisions, since "Book.missing-marks-3.m4b" is what will be sitting next to "Book.m4b.bak" by the
/// time the backups are looked at.
/// </para>
/// </remarks>
public sealed class CleanupRunner
{
    private readonly CliOptions _options;
    private readonly ProgressRenderer _progress;

    /// <summary>
    /// How far a ".bak" file's play time may differ from that of the audio file next to it and
    /// still be taken for a backup <em>of</em> it. Two seconds because writing chapter markings
    /// remuxes the file, and a remux can shift the reported duration by a frame or two - see the
    /// identical tolerance <see cref="FfmpegClient.WriteChaptersAsync"/> verifies its own output
    /// against. Anything beyond that is a different recording (an abridged edition, a re-download,
    /// a file the user renamed by hand), and a different recording's only copy is not this tool's
    /// to delete.
    /// </summary>
    public const double BackupToleranceSeconds = 2.0;

    /// <summary>Located lazily: the only thing needing ffmpeg is the ".bak" length comparison, and
    /// a folder with no backups in it - or a <c>--revert</c>, which restores them unconditionally -
    /// should not fail on a machine that has no ffmpeg installed.</summary>
    private FfmpegClient? _ffmpeg;

    /// <summary>How a backup's play time is compared against the file beside it; see
    /// <see cref="CompareLengthsAsync"/> for the contract.</summary>
    private readonly Func<string, string, CancellationToken, Task<string?>> _compareLengths;

    /// <summary>Creates a cleanup for the given validated options.</summary>
    /// <param name="options">Validated command line options, with <c>--cleanup</c> set.</param>
    /// <param name="progress">Renderer the action lines and the summary go to.</param>
    /// <param name="compareLengths">Replaces the ffprobe-based backup check, which is the one part
    /// of this class that needs a real ffmpeg and real audiobooks to exercise; null uses the real
    /// one. Everything else here is file shuffling that a temp directory can answer for, so this
    /// seam is what keeps the rest of the logic testable.</param>
    public CleanupRunner(
        CliOptions options, ProgressRenderer progress,
        Func<string, string, CancellationToken, Task<string?>>? compareLengths = null)
    {
        _options = options;
        _progress = progress;
        _compareLengths = compareLengths ?? CompareLengthsAsync;
    }

    /// <summary>What one planned step does to one file.</summary>
    /// <param name="Kind">Which artifact it acts on, for grouping the steps in the prompt.</param>
    /// <param name="Path">The file the step acts on.</param>
    /// <param name="MoveTo">Where it is moved to, or null when it is deleted.</param>
    /// <param name="Report">The line printed once the step has been carried out.</param>
    /// <param name="Overwrite">Whether the move may replace a file already at the destination.
    /// True only for a <c>--revert</c>, whose whole job is to put the backup back over the
    /// processed file; every other move was planned against a destination the scan found free, and
    /// silently overwriting there would mean the scan got it wrong.</param>
    private readonly record struct CleanupStep(
        CleanupArtifact Kind, string Path, string? MoveTo, string Report, bool Overwrite = false);

    /// <summary>The steps to carry out, and the notes about files deliberately left alone.</summary>
    private sealed class CleanupPlan
    {
        /// <summary>The steps, in the order they must be carried out.</summary>
        public List<CleanupStep> Steps { get; } = [];

        /// <summary>Lines explaining files this mode looked at and decided not to touch - always
        /// printed, <c>--quiet</c> included, because "why is that .bak still there" is a question
        /// silence answers badly.</summary>
        public List<string> Notes { get; } = [];
    }

    /// <summary>Runs the cleanup: scan, confirm, act, report.</summary>
    /// <param name="ct">Cancellation token bound to Ctrl+C.</param>
    /// <exception cref="AppError">Confirmation was needed and could not be obtained, or at least
    /// one step failed.</exception>
    public async Task RunAsync(CancellationToken ct)
    {
        var plan = await BuildPlanAsync(ct);
        foreach (var note in plan.Notes)
            _progress.Announce(note);
        if (plan.Steps.Count == 0)
        {
            _progress.Announce("Nothing to clean up.");
            return;
        }
        if (!Confirm(plan))
        {
            _progress.Announce("Cleanup cancelled; nothing was changed.");
            return;
        }
        Apply(plan, ct);
    }

    /// <summary>
    /// Works out everything the cleanup would do, in the order it has to happen: parked originals
    /// first (they can put a missing audio file back), then untagging (which decides what the
    /// backups will be sitting next to), then the backups themselves, then the pure leftovers.
    /// </summary>
    /// <param name="ct">Cancellation token bound to Ctrl+C.</param>
    private async Task<CleanupPlan> BuildPlanAsync(CancellationToken ct)
    {
        var plan = new CleanupPlan();
        var found = Collect();
        var audioFiles = new AudioFileView(found);

        PlanParkedOriginals(plan, found, audioFiles);
        PlanTemporaries(plan, found);
        PlanUntagging(plan, found, audioFiles);
        await PlanBackupsAsync(plan, found, audioFiles, ct);
        PlanDeletions(plan, found);
        return plan;
    }

    /// <summary>
    /// The audio files the later planning steps will find, as the earlier ones will have left
    /// them: keyed by the name a file <em>will</em> have, valued by the path it has <em>now</em>.
    /// <para>
    /// Both halves are load-bearing, and the difference between them is a bug this class had
    /// briefly: a decision has to be made against the folder as it will be (a book still tagged
    /// ".missing-marks" is about to become "Book.m4b", and that is what its ".bak" pairs with),
    /// while any probing has to be done against the folder as it is (there is no "Book.m4b" to
    /// read a play time from until the rename has actually happened).
    /// </para>
    /// </summary>
    private sealed class AudioFileView
    {
        private readonly Dictionary<string, string> _byFutureName;

        /// <summary>Starts from the audio files the scan found, each still under its own name.</summary>
        /// <param name="found">Everything the scan turned up.</param>
        public AudioFileView(List<FoundFile> found)
            => _byFutureName = found
                .Where(f => f.Match.Kind is CleanupArtifact.TaggedAudio or CleanupArtifact.None)
                .GroupBy(f => CliOptions.NormalizePath(f.Path), CliOptions.PathComparer)
                .ToDictionary(g => g.Key, g => g.First().Path, CliOptions.PathComparer);

        /// <summary>Whether a file will be sitting under this name.</summary>
        /// <param name="futureName">The name to look for.</param>
        public bool Has(string futureName) => _byFutureName.ContainsKey(CliOptions.NormalizePath(futureName));

        /// <summary>Where the file that will be called <paramref name="futureName"/> is right now,
        /// or null when there will be none.</summary>
        /// <param name="futureName">The name to look for.</param>
        public string? CurrentPathOf(string futureName)
            => _byFutureName.GetValueOrDefault(CliOptions.NormalizePath(futureName));

        /// <summary>Records a planned rename or restore, unless the destination is already taken.</summary>
        /// <param name="futureName">The name the file will have.</param>
        /// <param name="currentPath">Where it is until the step runs.</param>
        /// <returns>False when something is already going to be called that.</returns>
        public bool Claim(string futureName, string currentPath)
            => _byFutureName.TryAdd(CliOptions.NormalizePath(futureName), currentPath);

        /// <summary>Forgets a name a planned rename is about to vacate.</summary>
        /// <param name="futureName">The name being vacated.</param>
        public void Release(string futureName) => _byFutureName.Remove(CliOptions.NormalizePath(futureName));
    }

    /// <summary>One file found by the scan, with what it turned out to be.</summary>
    /// <param name="Path">Full path of the file.</param>
    /// <param name="Match">What <see cref="CleanupMatch.Of"/> made of it.</param>
    private readonly record struct FoundFile(string Path, CleanupMatch Match);

    /// <summary>
    /// Every file under the command line targets that this mode has anything to say about, plus the
    /// plain audio files, which are not acted on but are what the ".bak" and parked-original
    /// decisions are made against.
    /// </summary>
    private List<FoundFile> Collect()
    {
        var seen = new HashSet<string>(CliOptions.PathComparer);
        return [.. _options.Targets
            .SelectMany(CandidatesUnder)
            .Where(path => seen.Add(CliOptions.NormalizePath(path)))
            .Select(path => new FoundFile(path, CleanupMatch.Of(path)))
            .Where(InScope)
            .OrderBy(f => f.Path, NaturalPathComparer.Instance)];
    }

    /// <summary>The files one command line target puts up for consideration: everything in a
    /// directory (honoring <c>--recurse</c>), or - for a file named directly - everything beside it
    /// that could belong to it.</summary>
    /// <param name="target">The file or directory as given on the command line.</param>
    private IEnumerable<string> CandidatesUnder(CliOptions.Target target)
    {
        if (target.IsDirectory)
            return Directory.EnumerateFiles(target.Path, "*",
                _options.Recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
        var directory = Path.GetDirectoryName(CliOptions.NormalizePath(target.Path));
        return directory != null ? Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly) : [];
    }

    /// <summary>
    /// Whether a found file is in this run's scope: recognized as one of ours (or an audio file the
    /// decisions need to see), of a selected extension, matching <c>--filter</c>, and - when a file
    /// rather than a directory was named - belonging to that file.
    /// </summary>
    /// <param name="found">The candidate to judge.</param>
    private bool InScope(FoundFile found)
    {
        // A directory's checkpoint belongs to no book, so no book-selecting filter can speak for
        // it: with a --filter in play the cleanup is scoped to particular files, and throwing away
        // an interrupted batch's resume point is not one of them.
        if (found.Match.Kind == CleanupArtifact.BatchProgress)
            return _options.FilterRegex == null && _options.FilterExtensions == null && NamedDirectly(found);
        if (found.Match.Kind == CleanupArtifact.None)
            return IsSelectedAudioFile(found.Path);
        return IsSelectedAudioFile(found.Match.AudioPath) &&
               (_options.FilterRegex == null || _options.FilterRegex.IsMatch(found.Match.AudioPath)) &&
               NamedDirectly(found);
    }

    /// <summary>Whether a file has one of the extensions this run works on.</summary>
    /// <param name="audioPath">Path of the audio file an artifact belongs to.</param>
    private bool IsSelectedAudioFile(string audioPath)
        => _options.EffectiveExtensions.Contains(
            Path.GetExtension(audioPath).ToLowerInvariant());

    /// <summary>
    /// Whether a target named as a single file covers this artifact. Compared with any
    /// ".missing-marks" tag stripped from both sides, so that naming either the tagged or the
    /// untagged name reaches the same set of leftovers - the user has no reason to know which of
    /// the two a given ".debug.log" was written under.
    /// </summary>
    /// <param name="found">The candidate to judge.</param>
    private bool NamedDirectly(FoundFile found)
        => _options.Targets.Any(t => t.IsDirectory
            ? IsUnder(found.Path, t.Path)
            : SameBook(found.Match.AudioPath, t.Path));

    /// <summary>Whether a file lies under a directory target, at any depth.</summary>
    /// <param name="path">The file's path.</param>
    /// <param name="directory">The directory target's path.</param>
    private static bool IsUnder(string path, string directory)
        => CliOptions.NormalizePath(path).StartsWith(
            CliOptions.NormalizePath(directory) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>Whether two paths name the same book, tagged or not.</summary>
    /// <param name="left">One path.</param>
    /// <param name="right">The other.</param>
    private static bool SameBook(string left, string right)
        => left.Length > 0 && CliOptions.PathComparer.Equals(
            CliOptions.NormalizePath(MissingMarksTag.StripFrom(left)),
            CliOptions.NormalizePath(MissingMarksTag.StripFrom(right)));

    /// <summary>
    /// Plans what to do with originals parked by a write that never finished. With the audio file
    /// back in place the parked copy is redundant and goes; without it, the parked copy <em>is</em>
    /// the audiobook and is moved back under its own name.
    /// </summary>
    /// <param name="plan">The plan being built.</param>
    /// <param name="found">Everything the scan turned up.</param>
    /// <param name="audioFiles">The audio files, updated by any restore planned here.</param>
    private static void PlanParkedOriginals(
        CleanupPlan plan, List<FoundFile> found, AudioFileView audioFiles)
    {
        foreach (var file in found.Where(f => f.Match.Kind == CleanupArtifact.Parked))
        {
            var audio = file.Match.AudioPath;
            if (audioFiles.Claim(audio, file.Path))
                plan.Steps.Add(new(CleanupArtifact.Parked, file.Path, audio,
                    $"{Path.GetFileName(file.Path)}: original restored as {Path.GetFileName(audio)}"));
            else
                plan.Steps.Add(new(CleanupArtifact.Parked, file.Path, null,
                    $"{Path.GetFileName(file.Path)}: leftover copy of the original deleted"));
        }
    }

    /// <summary>Plans the removal of remux targets, which are never anything's only copy.</summary>
    /// <param name="plan">The plan being built.</param>
    /// <param name="found">Everything the scan turned up.</param>
    private static void PlanTemporaries(CleanupPlan plan, List<FoundFile> found)
    {
        foreach (var file in found.Where(f => f.Match.Kind == CleanupArtifact.Temp))
            plan.Steps.Add(new(CleanupArtifact.Temp, file.Path, null,
                $"{Path.GetFileName(file.Path)}: temporary file deleted"));
    }

    /// <summary>
    /// Plans taking the ".missing-marks" tags back off. A name already taken is left alone with a
    /// note: the chapter markings written into the tagged file are real work, and overwriting an
    /// unrelated file with them is a far worse outcome than leaving a tag on.
    /// </summary>
    /// <param name="plan">The plan being built.</param>
    /// <param name="found">Everything the scan turned up.</param>
    /// <param name="audioFiles">The audio files, updated with every rename planned here.</param>
    private static void PlanUntagging(CleanupPlan plan, List<FoundFile> found, AudioFileView audioFiles)
    {
        foreach (var file in found.Where(f => f.Match.Kind == CleanupArtifact.TaggedAudio))
        {
            var restored = MissingMarksTag.StripFrom(file.Path);
            if (!audioFiles.Claim(restored, file.Path))
            {
                plan.Notes.Add($"{Path.GetFileName(file.Path)}: tag kept - " +
                               $"{Path.GetFileName(restored)} already exists");
                continue;
            }
            audioFiles.Release(file.Path);
            plan.Steps.Add(new(CleanupArtifact.TaggedAudio, file.Path, restored,
                $"{Path.GetFileName(file.Path)}: renamed to {Path.GetFileName(restored)}"));
        }
    }

    /// <summary>
    /// Plans what happens to the ".bak" files: restored over their originals under
    /// <c>--revert</c>, otherwise deleted - but only where the original is present and probes to
    /// the same length, the check that keeps this from throwing away the last copy of something.
    /// </summary>
    /// <param name="plan">The plan being built.</param>
    /// <param name="found">Everything the scan turned up.</param>
    /// <param name="audioFiles">The audio files as they will be once the earlier steps have run.</param>
    /// <param name="ct">Cancellation token bound to Ctrl+C.</param>
    private async Task PlanBackupsAsync(
        CleanupPlan plan, List<FoundFile> found, AudioFileView audioFiles, CancellationToken ct)
    {
        var claimed = new HashSet<string>(CliOptions.PathComparer);
        // Untagged spellings first. A book processed twice can leave both "Book.m4b.bak" and
        // "Book.missing-marks-3.m4b.bak" behind, and of the two it is the untagged one that
        // predates the tagging and is therefore the copy worth restoring under the original name.
        var backups = found
            .Where(f => f.Match.Kind == CleanupArtifact.Backup)
            .OrderBy(f => MissingMarksTag.IsTagged(f.Match.AudioPath) ? 1 : 0);
        foreach (var file in backups)
        {
            ct.ThrowIfCancellationRequested();
            if (_options.Revert)
                PlanRevert(plan, file, audioFiles, claimed);
            else
                await PlanBackupDeletionAsync(plan, file, audioFiles, ct);
        }
    }

    /// <summary>
    /// Plans restoring one backup. The destination is the book's <em>untagged</em> name, since a
    /// ".missing-marks" tag is something this tool added after the backup was taken and putting the
    /// original back under it would leave the folder still looking half-processed - except where
    /// <see cref="PlanUntagging"/> had to leave a tag on, in which case the untagged name belongs to
    /// somebody else and the tagged one is used instead.
    /// </summary>
    /// <param name="plan">The plan being built.</param>
    /// <param name="file">The backup to restore.</param>
    /// <param name="audioFiles">The audio files as they will be once the earlier steps have run.</param>
    /// <param name="claimed">Destinations already spoken for by an earlier backup.</param>
    private static void PlanRevert(
        CleanupPlan plan, FoundFile file, AudioFileView audioFiles, HashSet<string> claimed)
    {
        var audio = file.Match.AudioPath;
        var untagKept = MissingMarksTag.IsTagged(audio) && audioFiles.Has(audio);
        var destination = untagKept ? audio : MissingMarksTag.StripFrom(audio);
        if (!claimed.Add(CliOptions.NormalizePath(destination)))
        {
            plan.Notes.Add($"{Path.GetFileName(file.Path)}: backup kept - another backup is " +
                           $"already being restored as {Path.GetFileName(destination)}");
            return;
        }
        plan.Steps.Add(new(CleanupArtifact.Backup, file.Path, destination,
            $"{Path.GetFileName(destination)}: reverted from backup", Overwrite: true));
    }

    /// <summary>
    /// Plans deleting one backup, which happens only once the file it backs up has been found and
    /// probed to be the same recording. Both spellings of the partner are accepted: by the time the
    /// backups are looked at, a tagged book has usually just been renamed back to its plain name.
    /// </summary>
    /// <param name="plan">The plan being built.</param>
    /// <param name="file">The backup to consider.</param>
    /// <param name="audioFiles">The audio files as they will be once the earlier steps have run.</param>
    /// <param name="ct">Cancellation token bound to Ctrl+C.</param>
    private async Task PlanBackupDeletionAsync(
        CleanupPlan plan, FoundFile file, AudioFileView audioFiles, CancellationToken ct)
    {
        var audio = file.Match.AudioPath;
        if (FindPartner(audio, audioFiles) is not { } partner)
        {
            plan.Notes.Add($"{Path.GetFileName(file.Path)}: backup kept - " +
                           $"no {Path.GetFileName(MissingMarksTag.StripFrom(audio))} beside it " +
                           "(--cleanup --revert restores it)");
            return;
        }
        if (await _compareLengths(file.Path, partner, ct) is { } mismatch)
        {
            plan.Notes.Add($"{Path.GetFileName(file.Path)}: backup kept - {mismatch}");
            return;
        }
        plan.Steps.Add(new(CleanupArtifact.Backup, file.Path, null,
            $"{Path.GetFileName(file.Path)}: backup deleted"));
    }

    /// <summary>
    /// The audio file a backup belongs to, under either spelling of its name - and as the path it
    /// can be read from <em>now</em> rather than the one it will have, since the probe below has to
    /// happen before any rename does.
    /// </summary>
    /// <param name="audioPath">The name the backup was taken under.</param>
    /// <param name="audioFiles">The audio files as they will be once the earlier steps have run.</param>
    private static string? FindPartner(string audioPath, AudioFileView audioFiles)
        => audioFiles.CurrentPathOf(MissingMarksTag.StripFrom(audioPath))
           ?? audioFiles.CurrentPathOf(audioPath);

    /// <summary>
    /// Checks a backup against the audio file it claims to back up, by play time.
    /// </summary>
    /// <param name="backup">Path of the ".bak" file.</param>
    /// <param name="audio">Path of the audio file beside it.</param>
    /// <param name="ct">Cancellation token bound to Ctrl+C.</param>
    /// <returns>Null when the two match, otherwise the reason they do not - phrased to be printed
    /// after "backup kept - ".</returns>
    /// <remarks>
    /// A probe that fails counts as a mismatch rather than as an error: a ".bak" ffprobe cannot read
    /// is not a backup this tool wrote, and refusing to delete it is exactly the right response to
    /// not knowing what it is.
    /// </remarks>
    private async Task<string?> CompareLengthsAsync(string backup, string audio, CancellationToken ct)
    {
        _ffmpeg ??= Locate();
        try
        {
            var backupInfo = await _ffmpeg.ProbeAsync(backup, ct);
            var audioInfo = await _ffmpeg.ProbeAsync(audio, ct);
            var difference = Math.Abs(backupInfo.DurationSeconds - audioInfo.DurationSeconds);
            return difference <= BackupToleranceSeconds
                ? null
                : $"{Path.GetFileName(audio)} runs {TimeFormat.Hms(audioInfo.DurationSeconds)}, " +
                  $"the backup {TimeFormat.Hms(backupInfo.DurationSeconds)}";
        }
        catch (AppError)
        {
            return "its play time could not be determined";
        }
    }

    /// <summary>Locates ffmpeg for the backup comparison.</summary>
    private static FfmpegClient Locate()
    {
        var (ffmpeg, ffprobe) = FfmpegLocator.Locate();
        return new FfmpegClient(ffmpeg, ffprobe);
    }

    /// <summary>Plans the removal of the files that carry no audio at all: debug logs and batch
    /// checkpoints.</summary>
    /// <param name="plan">The plan being built.</param>
    /// <param name="found">Everything the scan turned up.</param>
    private static void PlanDeletions(CleanupPlan plan, List<FoundFile> found)
    {
        foreach (var file in found.Where(f => f.Match.Kind == CleanupArtifact.DebugLog))
            plan.Steps.Add(new(CleanupArtifact.DebugLog, file.Path, null,
                $"{Path.GetFileName(file.Path)}: debug log deleted"));
        foreach (var file in found.Where(f => f.Match.Kind == CleanupArtifact.BatchProgress))
            plan.Steps.Add(new(CleanupArtifact.BatchProgress, file.Path, null,
                $"{file.Path}: batch progress file deleted"));
    }

    /// <summary>
    /// Obtains the go-ahead: <c>--yes</c>, or a typed confirmation at an interactive console.
    /// Skipped entirely under <c>--revert</c>, where nothing is thrown away - every ".bak" is put
    /// back rather than deleted, which is what plain <c>--revert</c> already does unasked.
    /// </summary>
    /// <param name="plan">The plan the user is being asked about.</param>
    /// <returns>True to go ahead.</returns>
    /// <exception cref="AppError">Nothing can be asked and nothing was granted in advance.</exception>
    private bool Confirm(CleanupPlan plan)
    {
        if (_options.Revert || _options.AssumeYes)
            return true;
        if (Console.IsInputRedirected)
            throw new AppError(
                "--cleanup deletes files and cannot ask for confirmation here (no interactive " +
                "console). Add --yes to confirm in advance, or run it in a terminal.");

        // Straight to the console rather than through the renderer: this is a question being put to
        // whoever is sitting there, not a report of what a run did, and a --log-file copy of it
        // would be a copy of the question rather than of the answer.
        Console.WriteLine("--cleanup is about to:");
        foreach (var line in Describe(plan))
            Console.WriteLine($"  {line}");
        Console.Write("This cannot be undone. Are you sure? Type \"yes\" to proceed: ");
        return IsClearYes(Console.ReadLine());
    }

    /// <summary>Whether an answer to the confirmation prompt is a clear yes. Anything else - an
    /// empty line, a "maybe", a Ctrl+Z - is not.</summary>
    /// <param name="answer">What was typed, or null at end of input.</param>
    public static bool IsClearYes(string? answer)
        => answer?.Trim().ToLowerInvariant() is "yes" or "y";

    /// <summary>One line per kind of change, for the confirmation prompt and the
    /// <c>--summary</c> block.</summary>
    /// <param name="plan">The plan to describe.</param>
    private IEnumerable<string> Describe(CleanupPlan plan)
        => plan.Steps
            .GroupBy(s => s.Kind)
            .Select(g => $"{Describe(g.Key)}: {g.Count()} file(s)");

    /// <summary>What this cleanup does to one kind of artifact, as a phrase.</summary>
    /// <param name="kind">The kind of artifact.</param>
    private string Describe(CleanupArtifact kind) => kind switch
    {
        CleanupArtifact.Parked => "restore or delete originals left behind by an interrupted write",
        CleanupArtifact.Temp => "delete leftover temporary files",
        CleanupArtifact.TaggedAudio => "rename \".missing-marks\" files back to their original names",
        CleanupArtifact.Backup => _options.Revert
            ? "restore \".bak\" backups over the files beside them"
            : "delete \".bak\" backups whose file is present and of matching length",
        CleanupArtifact.DebugLog => "delete \".debug.log\" troubleshooting logs",
        CleanupArtifact.BatchProgress => "delete batch progress files of interrupted runs",
        _ => "act on files",
    };

    /// <summary>
    /// Carries the plan out, one step at a time, reporting each. A step that fails is reported and
    /// the rest still run - a single file held open by another program is no reason to leave the
    /// other ninety-nine as they are - but the run as a whole then ends in an error, so a script
    /// cannot mistake a partial cleanup for a complete one.
    /// </summary>
    /// <param name="plan">The plan to carry out.</param>
    /// <param name="ct">Cancellation token bound to Ctrl+C.</param>
    /// <exception cref="AppError">At least one step failed.</exception>
    private void Apply(CleanupPlan plan, CancellationToken ct)
    {
        var done = 0;
        var failed = 0;
        foreach (var step in plan.Steps)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (step.MoveTo is { } destination)
                    File.Move(step.Path, destination, step.Overwrite);
                else
                    File.Delete(step.Path);
                done++;
                if (!_options.Quiet)
                    _progress.Announce(step.Report);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed++;
                _progress.Announce($"{Path.GetFileName(step.Path)}: WARNING - {ex.Message}");
            }
        }

        if (_options.Summary)
            _progress.AnnounceSummary(
                $"Summary: {done} cleanup action(s) taken" +
                (failed > 0 ? $", {failed} failed" : "") +
                (plan.Notes.Count > 0 ? $", {plan.Notes.Count} file(s) left alone" : ""));
        if (failed > 0)
            throw new AppError($"{failed} cleanup action(s) failed; see the warnings above.");
    }
}
