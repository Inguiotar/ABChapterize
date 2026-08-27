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

    /// <summary>Every book on the server, fetched once and only when a local file actually has to be
    /// matched against it - <c>--abs-push</c>, or <c>--abs-push-only</c> outside ABS mode (see
    /// <see cref="MatchAsync"/>).</summary>
    private IReadOnlyList<AbsBook>? _everyBook;

    /// <summary>The instant a book must have joined its library after to pass
    /// <c>--newer-than</c>, or null when the option was not given. Fixed once for the run, for the
    /// same reason <see cref="FileProcessor"/>'s is.</summary>
    private readonly DateTime? _newerThanUtc;

    /// <summary>Opens the ABS side of a run. Nothing is sent until <see cref="ConnectAsync"/>.</summary>
    /// <param name="options">The run's validated options; its <see cref="CliOptions.AbsServer"/>
    /// must be set, which <see cref="CliOptions.UsesAbs"/> guarantees.</param>
    /// <param name="progress">The run's renderer, for the connection note and the download bar.</param>
    public AbsFileFlow(CliOptions options, ProgressRenderer progress)
    {
        _options = options;
        _progress = progress;
        _newerThanUtc = options.NewerThan is { } age ? DateTime.UtcNow - age : null;
        _workspace = new AbsWorkspace(
            options.AbsServer!, options.AbsTemp, AbsRetryPolicy.For(options.AbsRetryMinutes),
            options.LoggingEnabled ? progress.Log : null,
            // Not gated on --quiet, unlike the connection note below: what goes down this sink is a
            // wait of a minute or more, and a quiet run that stalls without a word looks broken.
            progress.Announce);
    }

    /// <summary>
    /// Authenticates, and checks up front that the account may do what this run intends.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task ConnectAsync(CancellationToken ct)
    {
        // Each right is asked for only where this run would use it. A --dry-run and a --no-op
        // listing write nothing, so an account that may only read is enough for them; and of the
        // five modes only --abs ever fetches audio, so a pull or a push from an account without
        // download rights is a perfectly good run and must not be turned away at the door.
        await _workspace.OpenAsync(
            needsDownload: _options.Abs,
            needsUpdate: _options.SendsMarksToAbs && !_options.DryRun && !_options.NoOp,
            ct);
        if (!_options.Quiet)
            _progress.Announce($"Audiobookshelf: {_workspace.Describe}");
    }

    /// <summary>
    /// Resolves the command line's selectors into the books this run will work on, applying
    /// <c>--filter</c> and <c>--newer-than</c> to what comes back.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The selected books, in a stable order.</returns>
    public async Task<IReadOnlyList<AbsBook>> SelectAsync(CancellationToken ct)
    {
        var books = await _workspace.SelectAsync(_options.AbsSelectors, ct);
        // The extension half of --filter is not applied here and cannot be: which extension a book
        // has is only known once its item detail has been fetched, which is a request per book.
        // It is applied at the fetch instead - see FetchAsync.
        var kept = books;
        if (_options.FilterRegex is { } filter)
            kept = Narrow(kept, b => filter.IsMatch(b.FilterText), "--filter");
        if (_newerThanUtc != null)
            kept = Narrow(kept, IsRecentEnough, "--newer-than");
        return kept;
    }

    /// <summary>Applies one selection rule and says how much of the selection it left.</summary>
    /// <param name="books">The books to narrow.</param>
    /// <param name="keep">The rule.</param>
    /// <param name="option">The option to name in the note, when there is one to make.</param>
    /// <remarks>
    /// The note matters more here than it would over a folder: what the selectors picked is not
    /// something the user can see for themselves, so a run that quietly works on nine books out of
    /// two hundred looks like a broken server rather than a filter doing its job.
    /// </remarks>
    private IReadOnlyList<AbsBook> Narrow(
        IReadOnlyList<AbsBook> books, Func<AbsBook, bool> keep, string option)
    {
        var kept = books.Where(keep).ToList();
        if (kept.Count < books.Count && !_options.Quiet)
            _progress.Announce($"{option} left {kept.Count} of {books.Count} selected book(s).");
        return kept;
    }

    /// <summary>Whether a book passes <c>--newer-than</c>.</summary>
    /// <param name="book">The selected book.</param>
    /// <remarks>
    /// The date the server says the book joined its library, which is the only age a server-side
    /// selection has to go on - and the one that matches what somebody asking for "what arrived
    /// this week" means. A book the server gave no date for is kept: a filter that silently
    /// discarded it would be indistinguishable from a library that had nothing new in it.
    /// </remarks>
    private bool IsRecentEnough(AbsBook book)
        => _newerThanUtc is not { } cutoff || book.AddedUtc is not { } added || added >= cutoff;

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
    /// refuse too: <c>--verify</c> and a mark ceiling both decide with the audio in hand, so a book
    /// either of them has an opinion about is fetched and left to the ordinary decision.
    /// What the server reports is also its own chapter list rather than the file - which is exactly
    /// the list the merge rule would have gone on to prefer anyway (see
    /// <see cref="ABChapterize.Abs.AbsChapterMerge"/>).
    /// </para>
    /// <para>
    /// The ceiling asked for is <see cref="CliOptions.EffectiveMaxChapters"/> and not
    /// <c>--max-chapters</c> itself, which is what keeps the subset property true now that a stated
    /// bound on chapter numbers can imply one: a book this skipped while the file-level policy would
    /// have written its list off is a book silently passed over. The cost is that
    /// <c>--max-chapter-number</c> under <c>--abs</c> now fetches the already-marked books too,
    /// which is the same cost <c>--max-chapters</c> has always had and is paid for the same reason.
    /// </para>
    /// </remarks>
    private bool AlreadyMarked(AbsBook book)
        => book.ChapterCount > 0
           && !_options.Force && !_options.Verify && !_options.AbsPushOnly
           && _options.EffectiveMaxChapters == null;

    /// <summary>
    /// Applies the merge rule to a freshly probed copy, so the rest of the run sees the marks the
    /// book really has.
    /// </summary>
    /// <param name="info">What ffprobe found in the downloaded file.</param>
    /// <param name="copy">The local copy and where it came from.</param>
    /// <returns>The media info to carry on with, and the note for <c>--verbose</c>.</returns>
    /// <remarks>
    /// Deliberately skipped for <c>--abs-push-only</c>, which exists to send the server what the
    /// <em>file</em> carries: merging first would replace the file's marks with the server's own and
    /// send them straight back, turning the mode into an expensive no-op.
    /// </remarks>
    public (MediaInfo Info, string Note) Merge(MediaInfo info, AbsLocalCopy copy)
        => _options.AbsPushOnly ? (info, "") : AbsChapterMerge.Apply(info, copy.Source.Chapters);

    /// <summary>
    /// The <c>--abs-pull</c> half: finds the book a local file belongs to and reads the chapter
    /// list the server holds for it.
    /// </summary>
    /// <param name="localPath">Path of the local audio file.</param>
    /// <param name="info">What ffprobe found in it, before any merge.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>What may be taken from the server, and the note describing it.</returns>
    /// <remarks>
    /// One request per file on top of the matching, and only for the files a pulling run reaches -
    /// the whole-catalogue listing behind <see cref="MatchAsync"/> is fetched once and reused,
    /// but a book's own chapter list is not in it (a library listing arrives minified, carrying
    /// counts rather than lists).
    /// </remarks>
    public async Task<AbsPull> PullAsync(string localPath, MediaInfo info, CancellationToken ct)
    {
        var match = await MatchAsync(localPath, info, ct);
        var fromServer = match.Book is { } book
            ? await _workspace.ChaptersOfAsync(book, ct)
            : [];
        return AbsChapterPull.Decide(match, fromServer, info);
    }

    /// <summary>
    /// Sends a book's finished marks to the server.
    /// </summary>
    /// <param name="book">The book to update.</param>
    /// <param name="chapters">The marks, in start order.</param>
    /// <param name="durationSeconds">The book's play time, which the last chapter ends at.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The clause the file's summary line closes with, and whether the read-back found the
    /// server holding something other than what was sent.</returns>
    /// <remarks>
    /// <para>
    /// The clause names the book it went to, because under <c>--abs-push</c> the summary line is
    /// headed by the local file name and the server may well call the same book something else -
    /// "I Shall Wear Midnight.m4b" against "DW38 - I Shall Wear Midnight". Saying which book
    /// received the marks is the difference between a line that reports a push and one that can
    /// be checked.
    /// </para>
    /// <para>
    /// A failed read-back is announced as well as reported in the clause, and the announcement is
    /// what makes it visible: a summary line is held back by <c>--quiet</c>, and this is the one
    /// outcome where a quiet run has been told its marks are on the server when they are not.
    /// </para>
    /// </remarks>
    public async Task<(string Note, bool Mismatch)> PushAsync(
        AbsBook book, IReadOnlyList<Chapter> chapters, double durationSeconds, CancellationToken ct)
    {
        if (_options.DryRun)
            return ($", would send {chapters.Count} chapter(s) to ABS ({book.Title})", false);

        var mismatch = await _workspace.PushAsync(book, chapters, durationSeconds, ct);
        var sent = $", {chapters.Count} chapter(s) sent to ABS ({book.Title})";
        if (mismatch.Length == 0)
            return (sent, false);

        _progress.Announce(
            $"WARNING: Audiobookshelf did not store the marks sent for \"{book.Title}\" as they "
            + $"were sent - {mismatch}.");
        return ($"{sent} - WARNING: the server's copy differs ({mismatch})", true);
    }

    /// <summary>
    /// Finds the book a local file belongs to, for <c>--abs-push</c> and for <c>--abs-push-only</c>
    /// outside ABS mode.
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
