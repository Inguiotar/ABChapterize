// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Cli;

namespace ABChapterize.Processing;

/// <summary>
/// The per-directory checkpoint that makes an interrupted batch run resumable: while a directory
/// given on the command line is being processed, every file finished in it is appended to an
/// ".abchapterize-progress" file inside that directory, and the whole file is deleted again the
/// moment the directory is done. A run that completes normally therefore leaves nothing behind,
/// while one cut short by Ctrl+C, a crash or a power loss leaves a record the next run over the
/// same directory skips past instead of scanning the library from the top again.
/// <para>
/// Only directories get one - a file named directly on the command line has no base directory to
/// put it in, and re-running a handful of named files is not the case this exists for. Runs that
/// change nothing (--dry-run, --no-op, --revert) never write one either.
/// </para>
/// <para>
/// The record is only ever applied to a run whose <see cref="CliOptions.RunFingerprint"/> matches
/// the one that wrote it: files finished under different options were processed by a different
/// command, so counting them as done would silently leave the new command's work undone. A
/// mismatch (like --ignore-progress) simply starts the directory over with a fresh file.
/// </para>
/// <para>
/// Bookkeeping failures are never fatal: an unwritable directory costs the resumability of that
/// directory and nothing else, so every file operation here degrades into a no-op rather than
/// taking the run down with it.
/// </para>
/// </summary>
internal sealed class BatchProgress
{
    /// <summary>Name of the progress file, created inside the directory it belongs to. Leading
    /// dot so it is hidden on Unix-like systems; on Windows the hidden attribute is set instead
    /// (see <see cref="WriteHeader"/>).</summary>
    public const string FileName = ".abchapterize-progress";

    /// <summary>Format marker on the file's first data line. Bump when the layout below changes;
    /// a file written by any other version is discarded rather than misread.</summary>
    private const string FormatVersion = "1";

    private readonly string _path;
    private readonly string _directory;
    private readonly Action<string> _warn;
    private readonly Lock _lock = new();

    /// <summary>Files still to be processed in this directory; the progress file is deleted when
    /// this reaches zero. Guarded by <see cref="_lock"/> along with every write.</summary>
    private int _remaining;

    /// <summary>Set once an I/O error has been reported, to stop the checkpoint from writing
    /// (and complaining) again for every remaining file of the directory.</summary>
    private bool _disabled;

    /// <summary>Relative paths of the files a previous, interrupted run finished in this
    /// directory - empty unless that run used the same options as this one.</summary>
    public IReadOnlySet<string> AlreadyDone { get; }

    private BatchProgress(string directory, IReadOnlySet<string> alreadyDone, Action<string> warn)
    {
        _directory = directory;
        _path = Path.Combine(directory, FileName);
        AlreadyDone = alreadyDone;
        _warn = warn;
    }

    /// <summary>
    /// Opens the checkpoint of one directory, reading whatever a previous run left behind and
    /// starting a fresh record if there is nothing usable (or if <paramref name="ignoreExisting"/>
    /// says to ignore it).
    /// </summary>
    /// <param name="directory">The directory given on the command line.</param>
    /// <param name="fingerprint">This run's <see cref="CliOptions.RunFingerprint"/>.</param>
    /// <param name="ignoreExisting">True for --ignore-progress: discard any existing record.</param>
    /// <param name="warn">Where the one-off bookkeeping warning goes (see <see cref="Guarded"/>).
    /// Never <see cref="Console"/> directly: this runs while the progress bars own the terminal,
    /// and they erase by cursor arithmetic, so an unsynchronized write leaves the next redraw
    /// clearing the wrong lines.</param>
    /// <returns>The checkpoint, with <see cref="AlreadyDone"/> filled in.</returns>
    public static BatchProgress Open(
        string directory, string fingerprint, bool ignoreExisting, Action<string> warn)
    {
        var path = Path.Combine(directory, FileName);
        var done = ignoreExisting ? [] : ReadDoneFiles(path, fingerprint);
        var progress = new BatchProgress(directory, done, warn);
        if (done.Count == 0)
            progress.WriteHeader(fingerprint);
        return progress;
    }

    /// <summary>
    /// Reads the finished files out of an existing progress file. Anything unreadable,
    /// unrecognized or written under a different fingerprint yields an empty set, which makes the
    /// caller start the directory over.
    /// </summary>
    /// <param name="path">Full path of the progress file.</param>
    /// <param name="fingerprint">This run's <see cref="CliOptions.RunFingerprint"/>.</param>
    private static HashSet<string> ReadDoneFiles(string path, string fingerprint)
    {
        var done = new HashSet<string>(CliOptions.PathComparer);
        if (!File.Exists(path))
            return done;
        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (IOException)
        {
            return done;
        }
        catch (UnauthorizedAccessException)
        {
            return done;
        }

        var headerOk = false;
        foreach (var line in lines)
        {
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            if (line.StartsWith("abchapterize-progress ", StringComparison.Ordinal))
            {
                var parts = line.Split(' ');
                headerOk = parts.Length == 3 && parts[1] == FormatVersion && parts[2] == fingerprint;
                if (!headerOk)
                    return done;
                continue;
            }
            if (headerOk)
                done.Add(line);
        }
        return headerOk ? done : [];
    }

    /// <summary>
    /// Starts a fresh progress file, replacing any unusable one. On Windows the file is
    /// additionally marked hidden, where a leading dot means nothing.
    /// </summary>
    /// <param name="fingerprint">This run's <see cref="CliOptions.RunFingerprint"/>.</param>
    private void WriteHeader(string fingerprint)
        => Guarded(() =>
        {
            // A hidden file cannot be overwritten by a plain create on Windows, so the previous
            // one - which this very method may have marked hidden - has to go first.
            File.Delete(_path);
            File.WriteAllLines(_path, [
                "# ABChapterize batch progress: the files below are already finished.",
                "# Written while a directory is being processed and deleted as soon as it is done,",
                "# so a run interrupted halfway can pick up where it stopped. Safe to delete.",
                $"abchapterize-progress {FormatVersion} {fingerprint}",
            ]);
            if (OperatingSystem.IsWindows())
                File.SetAttributes(_path, FileAttributes.Hidden);
        });

    /// <summary>
    /// Announces how many files of this directory the run is actually going to process, after
    /// <see cref="AlreadyDone"/> has been subtracted. A directory with nothing left to do is
    /// finished right here.
    /// </summary>
    /// <param name="fileCount">Number of files still to be processed in this directory.</param>
    public void Begin(int fileCount)
    {
        lock (_lock)
        {
            _remaining = fileCount;
            if (_remaining == 0)
                Delete();
        }
    }

    /// <summary>
    /// Records one finished file and, when it was the directory's last one, deletes the progress
    /// file. Called for every outcome that leaves nothing to redo - written, skipped, unchanged -
    /// but never after an error, which leaves the record in place for the next run.
    /// </summary>
    /// <param name="file">The file as it was enumerated, before any renaming.</param>
    /// <param name="renamedTo">The name the file now has, when processing renamed it (a
    /// ".missing-marks" tag added or dropped), so the next run recognizes it under either name.</param>
    public void MarkDone(string file, string? renamedTo)
    {
        lock (_lock)
        {
            string[] entries = renamedTo == null
                ? [Relative(file)]
                : [Relative(file), Relative(renamedTo)];
            Guarded(() => File.AppendAllLines(_path, entries));
            if (--_remaining <= 0)
                Delete();
        }
    }

    /// <summary>True when the given file was already finished by a previous run of the same
    /// command and can be skipped.</summary>
    /// <param name="file">A file enumerated in this directory.</param>
    public bool IsDone(string file) => AlreadyDone.Contains(Relative(file));

    /// <summary>The path recorded for a file: relative to the directory the progress file lives
    /// in, so moving or renaming the whole library does not invalidate it.</summary>
    /// <param name="file">Full or command-line-relative path of the file.</param>
    private string Relative(string file) => Path.GetRelativePath(_directory, CliOptions.NormalizePath(file));

    /// <summary>Removes the progress file, the last step of a finished directory.</summary>
    private void Delete() => Guarded(() => File.Delete(_path));

    /// <summary>
    /// Runs one progress-file operation, turning any I/O failure into a one-off warning plus a
    /// permanently disabled checkpoint. Losing the ability to resume a directory is worth a note,
    /// never an aborted run.
    /// </summary>
    /// <param name="action">The file operation to attempt.</param>
    private void Guarded(Action action)
    {
        if (_disabled)
            return;
        try
        {
            action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _disabled = true;
            _warn($"Warning: cannot maintain {Path.Combine(_directory, FileName)} ({ex.Message}); " +
                  "an interrupted run will not be resumable for this directory.");
        }
    }
}
