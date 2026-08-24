// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Abs;
using ABChapterize.Audio;
using ABChapterize.Cli;
using ABChapterize.Ui;

namespace ABChapterize.Processing;

/// <summary>
/// What ABS mode adds to a run, one book at a time: choosing the books, fetching each to a
/// temporary copy, deciding what marks it already has, sending the finished ones back and throwing
/// the copy away.
/// </summary>
/// <remarks>
/// <para>
/// It exists so that <see cref="FileProcessor"/> does not have to. Everything below is bracketed
/// around the ordinary per-file pipeline rather than woven into it - a downloaded book is a file on
/// disk like any other, and detection neither knows nor needs to know where it came from. That is
/// what keeps ABS mode from being a second pipeline that could drift from the first.
/// </para>
/// <para>
/// One instance per run, and it holds the whole run's temporary folder, so disposing it is what
/// removes anything a Ctrl+C or a crash left behind.
/// </para>
/// </remarks>
internal sealed class AbsFileFlow : IDisposable
{
    private readonly CliOptions _options;
    private readonly ProgressRenderer _progress;
    private readonly AbsWorkspace _workspace;

    /// <summary>Every book on the server, fetched once and only when <c>--push-only</c> outside ABS
    /// mode actually needs it (see <see cref="MatchAsync"/>).</summary>
    private IReadOnlyList<AbsBook>? _everyBook;

    /// <summary>Opens the ABS side of a run. Nothing is sent until <see cref="ConnectAsync"/>.</summary>
    /// <param name="options">The run's validated options; its <see cref="CliOptions.AbsServer"/>
    /// must be set, which <see cref="CliOptions.UsesAbs"/> guarantees.</param>
    /// <param name="progress">The run's renderer, for the connection note and the download bar.</param>
    public AbsFileFlow(CliOptions options, ProgressRenderer progress)
    {
        _options = options;
        _progress = progress;
        _workspace = new AbsWorkspace(
            options.AbsServer!, options.AbsTemp,
            options.LoggingEnabled ? progress.Log : null);
    }

    /// <summary>
    /// Authenticates, and checks up front that the account may do what this run intends.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task ConnectAsync(CancellationToken ct)
    {
        // A --dry-run and a --no-op listing write nothing, so an account that may only read is
        // enough for them - and being able to preview a run from a read-only account is worth the
        // one extra branch.
        await _workspace.OpenAsync(needsUpdate: !_options.DryRun && !_options.NoOp, ct);
        if (!_options.Quiet)
            _progress.Announce($"Audiobookshelf: {_workspace.Describe}");
    }

    /// <summary>
    /// Resolves the command line's selectors into the books this run will work on, applying
    /// <c>--filter</c> to what comes back.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The selected books, in a stable order.</returns>
    public async Task<IReadOnlyList<AbsBook>> SelectAsync(CancellationToken ct)
    {
        var books = await _workspace.SelectAsync(_options.AbsSelectors, ct);
        if (_options.FilterRegex is not { } filter)
            return books;
        // The extension half of --filter is not applied here and cannot be: which extension a book
        // has is only known once its item detail has been fetched, which is a request per book.
        // It is applied at the fetch instead - see FetchAsync.
        var kept = books.Where(b => filter.IsMatch(b.FilterText)).ToList();
        if (kept.Count < books.Count && !_options.Quiet)
            _progress.Announce($"--filter left {kept.Count} of {books.Count} selected book(s).");
        return kept;
    }

    /// <summary>
    /// Fetches one book to a temporary copy, or explains why it is being passed over.
    /// </summary>
    /// <param name="book">The book to fetch.</param>
    /// <param name="work">Its progress tracker, already started, which the download bar runs in.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The local copy, or null and the reason it was refused.</returns>
    public async Task<(AbsLocalCopy? Copy, string Refusal)> FetchAsync(
        AbsBook book, WorkTracker work, CancellationToken ct)
    {
        if (!book.IsSingleFile)
            return (null, $"{book.AudioFileCount} audio files - ABS mode works on books that are one file");
        if (AlreadyMarked(book))
            return (null, $"has {book.ChapterCount} chapter mark(s) on the server (use --force to redo)");

        // No format restriction here, deliberately. What a run against local files can work on is
        // limited by which containers hold chapter marks; nothing on this path writes marks into a
        // file, so that limit does not apply - the marks go into Audiobookshelf's own database and
        // the copy is deleted. Detection only ever needs a decode, and a book listed as an audio
        // file by the server is one ffmpeg reads. A file it cannot read fails this one book with
        // ffprobe's own complaint, which says far more than a guess from the extension could.
        var source = await _workspace.DescribeFileAsync(book, ct);
        if (_options.FilterExtensions is { } extensions && !extensions.Contains(source.Extension))
            return (null, $"{source.Extension} does not match --filter");

        // The bar fills with bytes off the network rather than with play time, which is the one
        // phase of a run where those are not the same thing; see PhaseNames.Download.
        work.BeginPhase(PhaseNames.Download, source.SizeBytes);
        var copy = await _workspace.FetchAsync(book, source, work.SetPhaseProgress, ct);
        return (copy, "");
    }

    /// <summary>
    /// Whether this run would work on the given book at all - what the <c>--no-op</c> listing
    /// reports, and the same question <see cref="FetchAsync"/> answers before fetching anything.
    /// </summary>
    /// <param name="book">The selected book.</param>
    /// <remarks>
    /// One rule, asked from both places. A listing that called a book skipped and a summary that
    /// counted it as processed would be worse than no summary at all, and two copies of the
    /// condition is how that happens.
    /// </remarks>
    public bool WouldProcess(AbsBook book) => book.IsSingleFile && !AlreadyMarked(book);

    /// <summary>
    /// Whether this book is one the run would only download in order to skip.
    /// </summary>
    /// <param name="book">The selected book, carrying the chapter count the server reported.</param>
    /// <remarks>
    /// <para>
    /// The same policy <see cref="FileProcessor"/> applies to a local file, asked one step earlier
    /// - and it has to be, because the step in between is a gigabyte off the network. A library of
    /// two hundred already-marked books would otherwise be fetched in full and then passed over
    /// book by book, which is the difference between a run that takes a minute and one that takes
    /// an afternoon.
    /// </para>
    /// <para>
    /// Deliberately conservative, and it can only ever refuse a book the file-level policy would
    /// refuse too: <c>--verify</c> and <c>--max-chapters</c> both decide with the audio in hand, so
    /// a book either of them has an opinion about is fetched and left to the ordinary decision.
    /// What the server reports is also its own chapter list rather than the file - which is exactly
    /// the list the merge rule would have gone on to prefer anyway (see
    /// <see cref="ABChapterize.Abs.AbsChapterMerge"/>).
    /// </para>
    /// </remarks>
    private bool AlreadyMarked(AbsBook book)
        => book.ChapterCount > 0
           && !_options.Force && !_options.Verify && !_options.PushOnly
           && _options.MaxChapters == null;

    /// <summary>
    /// Applies the merge rule to a freshly probed copy, so the rest of the run sees the marks the
    /// book really has.
    /// </summary>
    /// <param name="info">What ffprobe found in the downloaded file.</param>
    /// <param name="copy">The local copy and where it came from.</param>
    /// <returns>The media info to carry on with, and the note for <c>--verbose</c>.</returns>
    /// <remarks>
    /// Deliberately skipped for <c>--push-only</c>, which exists to send the server what the
    /// <em>file</em> carries: merging first would replace the file's marks with the server's own and
    /// send them straight back, turning the mode into an expensive no-op.
    /// </remarks>
    public (MediaInfo Info, string Note) Merge(MediaInfo info, AbsLocalCopy copy)
        => _options.PushOnly ? (info, "") : AbsChapterMerge.Apply(info, copy.Source);

    /// <summary>
    /// Sends a book's finished marks to the server.
    /// </summary>
    /// <param name="book">The book to update.</param>
    /// <param name="chapters">The marks, in start order.</param>
    /// <param name="durationSeconds">The book's play time, which the last chapter ends at.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The clause the file's summary line closes with.</returns>
    public async Task<string> PushAsync(
        AbsBook book, IReadOnlyList<Chapter> chapters, double durationSeconds, CancellationToken ct)
    {
        if (_options.DryRun)
            return $", would send {chapters.Count} chapter(s) to Audiobookshelf";
        await _workspace.PushAsync(book, chapters, durationSeconds, ct);
        return $", {chapters.Count} chapter(s) sent to Audiobookshelf";
    }

    /// <summary>
    /// Finds the book a local file belongs to, for <c>--push-only</c> outside ABS mode.
    /// </summary>
    /// <param name="localPath">Path of the local audio file.</param>
    /// <param name="info">What ffprobe found in it.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The book and how it was recognized, or no book and why not.</returns>
    public async Task<AbsMatch> MatchAsync(string localPath, MediaInfo info, CancellationToken ct)
    {
        _everyBook ??= await _workspace.EveryBookAsync(ct);
        return AbsItemMatcher.Find(_everyBook, localPath, info);
    }

    /// <summary>Removes a book's temporary copy once the run is finished with it.</summary>
    /// <param name="copy">The local copy to remove.</param>
    /// <remarks>
    /// A <c>--debug</c> log is kept: it is written beside the audio file, which here is a folder
    /// about to stop existing, and it is the one thing in there a second run could not produce
    /// again. It lands in the current directory instead.
    /// </remarks>
    public void Discard(AbsLocalCopy copy)
        => _workspace.Discard(copy, _options.Debug ? Directory.GetCurrentDirectory() : null);

    /// <summary>Closes the session and removes this run's temporary folder with anything left in
    /// it - an interrupted download, a book whose processing threw.</summary>
    public void Dispose() => _workspace.Dispose();
}
