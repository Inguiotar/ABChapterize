// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Audio;
using ABChapterize.Errors;

namespace ABChapterize.Abs;

/// <summary>
/// One book downloaded to disk, and everything the run needs to know about where it came from.
/// </summary>
/// <param name="Book">The book on the server.</param>
/// <param name="Source">Its audio file and the server's own chapter list.</param>
/// <param name="Path">Where the temporary copy sits.</param>
/// <param name="Folder">The folder holding it, which is what <see cref="AbsWorkspace.Discard"/>
/// removes - the run may leave a backup, a rename or a log beside the file, and a folder each is
/// what keeps two books from tidying up after one another.</param>
public sealed record AbsLocalCopy(AbsBook Book, AbsBookFile Source, string Path, string Folder);

/// <summary>
/// The Audiobookshelf side of a run: the connection, the catalogue, the temporary copies and the
/// write-back. One instance for the whole run, held by
/// <see cref="ABChapterize.Processing.FileProcessor"/>.
/// </summary>
/// <remarks>
/// A facade over four smaller pieces rather than an implementation of them -
/// <see cref="AbsSession"/> owns the HTTP, <see cref="AbsCatalog"/> the selection,
/// <see cref="AbsChapterPush"/> the one write, <see cref="AbsChapterMerge"/> the merge rule. What
/// lives here is only what none of them owns: where a download lands and when it goes away again.
/// </remarks>
public sealed class AbsWorkspace : IDisposable
{
    private readonly AbsSession _session;
    private readonly AbsCatalog _catalog;

    /// <summary>Where notes go, or null when nothing is listening.</summary>
    private readonly Action<string>? _log;

    /// <summary>This run's own temporary folder, created on first use and removed on dispose.</summary>
    private readonly string _root;

    /// <summary>Whether <see cref="_root"/> has actually been created, so a run that never
    /// downloads anything - <c>--no-op</c>, a selection that matches only split books - leaves no
    /// folder behind.</summary>
    private bool _rootCreated;

    /// <summary>Counts the books fetched, so each gets a folder of its own even when two books
    /// have the same file name.</summary>
    private int _fetched;

    /// <summary>Environment variable naming the download folder, read when <c>--abs-temp</c> is
    /// absent. It lives here rather than beside the connection variables because what it settles is
    /// this class own business - where a book lands - and not who the server is.</summary>
    public const string TempVariable = "ABCHAPTERIZE_ABS_TEMP";

    /// <summary>
    /// The suffix a debug log carries, which is the one artefact rescued out of a book's folder
    /// before it is deleted.
    /// </summary>
    private const string DebugLogSuffix = ".debug.log";

    /// <summary>Creates the workspace. Nothing is sent and no folder is made until
    /// <see cref="OpenAsync"/> and the first fetch.</summary>
    /// <param name="connection">The resolved server and credentials.</param>
    /// <param name="temporaryRoot">Where downloads should go, or null for the system temporary
    /// folder.</param>
    /// <param name="log">Sink for notes, or null.</param>
    public AbsWorkspace(AbsConnection connection, string? temporaryRoot, Action<string>? log = null)
    {
        _log = log;
        _session = new AbsSession(connection, log);
        _catalog = new AbsCatalog(_session, log);
        // A folder per run rather than a shared one: two abchapterize processes against the same
        // server would otherwise clean up each other's downloads, and the guid is also what makes
        // Discard safe to implement as a recursive delete - it can only ever name a folder this
        // process created.
        _root = Path.Combine(
            temporaryRoot ?? Path.GetTempPath(), $"abchapterize-abs-{Guid.NewGuid():N}");
    }

    /// <summary>The server and account, redacted; see <see cref="AbsConnection.Describe"/>.</summary>
    public string Describe => _session.Describe;

    /// <inheritdoc cref="AbsSession.OpenAsync"/>
    /// <param name="needsUpdate">Whether the run intends to write chapters back.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task OpenAsync(bool needsUpdate, CancellationToken ct) => _session.OpenAsync(needsUpdate, ct);

    /// <inheritdoc cref="AbsCatalog.SelectAsync"/>
    /// <param name="selectors">The selectors, in the order they were typed.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task<IReadOnlyList<AbsBook>> SelectAsync(
        IReadOnlyList<AbsSelector> selectors, CancellationToken ct)
        => _catalog.SelectAsync(selectors, ct);

    /// <inheritdoc cref="AbsCatalog.EveryBookAsync"/>
    /// <param name="ct">Cancellation token.</param>
    public Task<IReadOnlyList<AbsBook>> EveryBookAsync(CancellationToken ct)
        => _catalog.EveryBookAsync(ct);

    /// <inheritdoc cref="AbsCatalog.LoadFileAsync"/>
    /// <param name="book">The book to look at.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task<AbsBookFile> DescribeFileAsync(AbsBook book, CancellationToken ct)
        => _catalog.LoadFileAsync(book, ct);

    /// <summary>
    /// Downloads one book's audio file into a folder of its own.
    /// </summary>
    /// <param name="book">The book to fetch.</param>
    /// <param name="source">Its audio file, from <see cref="DescribeFileAsync"/>.</param>
    /// <param name="onProgress">Called with the running byte count, for the progress bar.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The local copy.</returns>
    public async Task<AbsLocalCopy> FetchAsync(
        AbsBook book, AbsBookFile source, Action<long>? onProgress, CancellationToken ct)
    {
        var folder = Path.Combine(EnsureRoot(), (++_fetched).ToString("D4"));
        Directory.CreateDirectory(folder);
        // The server's own file name, so every console line, log line and summary about this book
        // names something the user would recognize in their library rather than a temporary id.
        var path = Path.Combine(folder, SafeName(source.FileName));

        var written = await _session.DownloadAsync(
            $"/api/items/{Uri.EscapeDataString(book.ItemId)}/file/{Uri.EscapeDataString(source.Ino)}/download",
            path, onProgress, ct);
        _log?.Invoke($"downloaded {written:0} byte(s) to {path}");
        return new AbsLocalCopy(book, source, path, folder);
    }

    /// <summary>
    /// Sends a finished chapter list back to the server.
    /// </summary>
    /// <param name="book">The book to update.</param>
    /// <param name="chapters">The marks to send, in start order.</param>
    /// <param name="durationSeconds">The book's play time.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task PushAsync(
        AbsBook book, IReadOnlyList<Chapter> chapters, double durationSeconds, CancellationToken ct)
        => AbsChapterPush.PushAsync(_session, book, chapters, durationSeconds, ct);

    /// <summary>
    /// Removes a book's temporary folder, keeping any debug log it holds.
    /// </summary>
    /// <param name="copy">The local copy to remove.</param>
    /// <param name="keepLogsIn">Where to move a debug log to, or null to remove it with the rest.</param>
    /// <remarks>
    /// A <c>--debug</c> log is written beside the audio file, which in ABS mode is a folder about to
    /// stop existing - so the one thing a run cannot reproduce by fetching the book again is the one
    /// thing that would be deleted. It is moved out instead.
    /// </remarks>
    public void Discard(AbsLocalCopy copy, string? keepLogsIn)
    {
        if (keepLogsIn != null && Directory.Exists(copy.Folder))
            foreach (var log in Directory.EnumerateFiles(copy.Folder, "*" + DebugLogSuffix))
                Rescue(log, keepLogsIn);
        Remove(copy.Folder);
    }

    /// <summary>Moves one file out of a folder that is about to be deleted.</summary>
    /// <param name="file">The file to move.</param>
    /// <param name="destination">The folder to move it to.</param>
    private void Rescue(string file, string destination)
    {
        try
        {
            var target = Path.Combine(destination, Path.GetFileName(file));
            // Never overwrites: the destination is a folder the user chose, and a second run of the
            // same book is a reason to keep both logs rather than to lose the first.
            if (!File.Exists(target))
                File.Move(file, target);
            _log?.Invoke($"kept {Path.GetFileName(file)} in {destination}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"could not keep {Path.GetFileName(file)}: {ex.Message}");
        }
    }

    /// <summary>Creates this run's temporary folder if it is not there yet.</summary>
    /// <returns>The folder path.</returns>
    /// <exception cref="AppError">Thrown when it cannot be created, which is fatal: there is
    /// nowhere to put the books.</exception>
    private string EnsureRoot()
    {
        if (_rootCreated)
            return _root;
        try
        {
            Directory.CreateDirectory(_root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new AppError($"Cannot create the download folder {_root}: {ex.Message}");
        }
        _rootCreated = true;
        return _root;
    }

    /// <summary>
    /// Makes a server-side file name usable as a local one.
    /// </summary>
    /// <param name="name">The file name as the server reported it.</param>
    /// <returns>A name with nothing in it the local file system objects to.</returns>
    /// <remarks>
    /// <para>
    /// Audiobookshelf runs on Linux in the ordinary case, where a file name may hold characters
    /// Windows refuses outright - a colon above all, which every "Series 01: Title.m4b" carries.
    /// Substituted rather than stripped so the name stays readable and two books cannot collapse
    /// onto one.
    /// </para>
    /// <para>
    /// The relative-path names are the reason this is a guard and not a convenience. This string
    /// arrives over the network and is then joined onto a folder path, so a server answering with
    /// ".." - or with a name carrying a separator on a platform that does not count it as invalid -
    /// would put the download somewhere this run never meant to write. Neither survives: any
    /// directory part is taken off, and a name that is nothing but dots is replaced outright.
    /// </para>
    /// </remarks>
    internal static string SafeName(string name)
    {
        // The directory part comes off first, and against both separators rather than the local
        // one: the name was written by whatever the server runs on, and on Windows a separator is
        // itself an invalid file name character, so substituting first would turn "../x" into
        // ".._x" and leave nothing to split on.
        var bare = name.Split('/', '\\')[^1];
        var cleaned = new string([.. bare.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)]).Trim();
        return cleaned.Trim('.').Length == 0 ? "audiobook" : cleaned;
    }

    /// <summary>Deletes a folder this workspace created, ignoring a failure to.</summary>
    /// <param name="folder">The folder to remove.</param>
    /// <remarks>
    /// Failure is reported and passed over rather than thrown: the download is a copy, the marks
    /// are already on the server by the time this runs, and ending a finished run over a locked
    /// temporary file would turn a tidying problem into a lost result. Whoever holds the file - a
    /// virus scanner, an editor the user opened it in - releases it eventually, and the folder
    /// carries this run's guid, so nothing else will ever collide with what is left.
    /// </remarks>
    private void Remove(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"could not remove {folder}: {ex.Message}");
        }
    }

    /// <summary>Closes the session and removes this run's temporary folder with everything left
    /// in it.</summary>
    public void Dispose()
    {
        _session.Dispose();
        if (_rootCreated)
            Remove(_root);
    }
}
