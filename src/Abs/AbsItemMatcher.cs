// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Audio;

namespace ABChapterize.Abs;

/// <summary>
/// The outcome of looking for the Audiobookshelf book a local file is a copy of.
/// </summary>
/// <param name="Book">The book, or null when none could be settled on.</param>
/// <param name="Reason">Why, in the words a skip line uses: how the book was recognized when one
/// was, and what stood in the way when none was.</param>
/// <param name="NoClueNamedABook">True only where every clue was tried and none of them named a
/// book at all - as against a clue that named one the play time then refused, a clue that named
/// several, or a mapping entry whose book could not be found. The distinction is what decides
/// whether it is worth asking the server for more evidence (see
/// <see cref="AbsItemMatcher.FindByServerFileName"/>): the other three outcomes are all a stop, and
/// looking further after one of them is the guess the clue ordering exists to refuse.</param>
public readonly record struct AbsMatch(AbsBook? Book, string Reason, bool NoClueNamedABook = false);

/// <summary>
/// A book together with the names Audiobookshelf holds its audio files under, which is what the
/// last-resort stage matches against.
/// </summary>
/// <param name="Book">The candidate book.</param>
/// <param name="FileNames">Its audio file names as the server reports them, possibly none. Bare
/// names rather than paths; the extension is dropped before comparing, so that an entry naming the
/// same book as the local file matches it exactly rather than merely overlapping it.</param>
public readonly record struct AbsBookFiles(AbsBook Book, IReadOnlyList<string> FileNames);

/// <summary>
/// Finds the Audiobookshelf book a local audio file belongs to, for the modes that work on local
/// files and talk to a server - the two push modes and the two pull modes outside ABS mode - where
/// the files are named on the command line and the server has to be searched for each.
/// </summary>
/// <remarks>
/// <para>
/// Four things about a local file could name the book, and they are tried in descending order of
/// how much they can be trusted: the album tag, the title tag, the folder the file sits in, and
/// last the file name. That order is the whole design. A tag was written by whoever produced the
/// audiobook and survives being copied about; a file name is whatever the last tool to touch it
/// decided, and this tool itself renames files, so a run that had left a book tagged
/// ".missing-marks-7-8" would otherwise stop recognizing it.
/// </para>
/// <para>
/// <b>Ahead of all four sits whatever the user said outright</b>, in an <c>--abs-map</c> entry (see
/// <see cref="AbsBookMap"/>). Nothing can be more trustworthy than that, so it is tried first - and
/// it is a clue rather than a bypass: a mapping goes to the same search, and the book it names has
/// to pass the same play-time test as one a tag named.
/// </para>
/// <para>
/// A stage that matches nothing hands on to the next; a stage that matches more than one book stops
/// the search rather than guessing, because the two failures need opposite answers from the user -
/// one is "this book is not on the server", the other "say which of these you meant".
/// </para>
/// <para>
/// <b>All four clues are names, and a name is not evidence that two things are the same recording</b>
/// - so the book a clue settles on has to pass <see cref="SameRecordingSeconds"/> before it is
/// handed back. That test lives here rather than at any one caller because everything this tool
/// does across the two sides goes through a match: pulling a chapter list into a local file,
/// sending one up from a local file, or both at once. A caller that had to remember to ask would
/// eventually be a caller that forgot, and the failure it forgot to prevent writes a whole book's
/// marks into one part of it.
/// </para>
/// <para>
/// <b>The same test breaks a tie</b> before a crowded stage gives up, because it is the one piece
/// of evidence here that is not a name: a clue naming several books drops the ones that cannot be
/// this recording, and a single survivor is the match. This lowers no bar - what it accepts is a
/// book that would have had to pass <see cref="SameRecordingSeconds"/> anyway had the clue named it
/// alone - so it is the same rule reaching further rather than a weaker rule for a crowded field.
/// Where several survive, or where none does, the stage still refuses.
/// </para>
/// <para>
/// Pure, and separate from the run for that reason: it is given the books and the probe result and
/// returns a verdict, which is what makes every branch of it testable without a server. The
/// last-resort stage is split in two along that line - <see cref="PossibleRecordings"/> says which
/// books are worth asking about and <see cref="FindByServerFileName"/> judges the answers, with the
/// asking itself left to the caller.
/// </para>
/// </remarks>
public static class AbsItemMatcher
{
    /// <summary>
    /// How far the local file's play time and the server's may differ and still be taken for the
    /// same recording, in seconds.
    /// </summary>
    /// <remarks>
    /// Generous rather than tight, because what it has to separate is not close: two encodes of one
    /// book differ by an encoder's padding and a trimmed tail, a second or two at most, while a
    /// part of a split book, an abridgement or a different edition differ by many minutes. A minute
    /// sits in the empty space between those, and it is also about the point past which a mark
    /// taken from the other timeline would be visibly in the wrong place anyway.
    /// </remarks>
    public const double SameRecordingSeconds = 60.0;

    /// <summary>
    /// How many books the last-resort stage may ask the server for the file names of.
    /// </summary>
    /// <remarks>
    /// A bound on a request loop over a network, not a tuning knob: <see cref="PossibleRecordings"/>
    /// narrows by play time first, and on a real library that leaves nought to a handful of books,
    /// so this never fires. What it is here for is the shape of library that would defeat that
    /// narrowing - a hundred lecture recordings cut to the same length, say - where the stage would
    /// otherwise spend a request per book to answer a question that is about to fail anyway.
    /// </remarks>
    public const int MaxServerFileNameLookups = 20;

    /// <summary>What a run says when nothing about a file named a book.</summary>
    /// <remarks>
    /// It names the escape hatch, in the same way the ambiguity note names <c>item:</c>. The two
    /// failures a user meets here both have an answer, and a note that stated only the problem
    /// would leave them looking for one.
    /// </remarks>
    private const string NothingMatched =
        "no book on the server matches this file; name one in an --abs-map file";

    /// <summary>
    /// Looks for the one book a local file is a copy of.
    /// </summary>
    /// <param name="books">Every book on the server this account can see.</param>
    /// <param name="localPath">Path of the local audio file.</param>
    /// <param name="info">What ffprobe found in it, for its container tags and its play time.</param>
    /// <param name="mappings">The <c>--abs-map</c> entries in force for this file, or null when
    /// there are none.</param>
    /// <returns>The book and how it was recognized, or no book and why not.</returns>
    /// <remarks>
    /// Notes: the Silber Edition 150/157 collision the play-time tie-break was written for, and the
    /// two name rules that would also have settled it but were not taken.
    /// <include file='../../notes/Abs/AbsItemMatcher.xml' path='doc/member[@name="Find"]/*' />
    /// </remarks>
    public static AbsMatch Find(
        IReadOnlyList<AbsBook> books, string localPath, MediaInfo info,
        IReadOnlyList<AbsBookMapping>? mappings = null)
    {
        if (mappings is { Count: > 0 } && AbsBookMap.Find(mappings, localPath) is { } mapped)
            return ByMapping(books, mapped, info);

        foreach (var (clue, source) in Clues(localPath, info))
            if (ByName(books, clue, source, info) is { } match)
                return match;
        return new AbsMatch(null, NothingMatched, NoClueNamedABook: true);
    }

    /// <summary>
    /// The books whose play time allows them to be the recording in this file, which is who the
    /// last-resort stage is worth asking the server about.
    /// </summary>
    /// <param name="books">Every book on the server this account can see.</param>
    /// <param name="info">What ffprobe found in the local file.</param>
    /// <returns>The plausible books, possibly none.</returns>
    /// <remarks>
    /// Stricter than <see cref="CouldBeThisRecording"/> in the one respect that matters here: a
    /// book the server reports <em>no</em> play time for is left out, where the ordinary test keeps
    /// it. The two are not in conflict - they ask different questions of the same silence. Deciding
    /// whether to refuse a book a name already picked out, no evidence has to mean "carry on"; here
    /// the play time is being used to pick the books out in the first place, and a book there is no
    /// evidence for is not one worth spending a request on.
    /// </remarks>
    public static IReadOnlyList<AbsBook> PossibleRecordings(
        IReadOnlyList<AbsBook> books, MediaInfo info)
        => [.. books.Where(b => b.DurationSeconds > 0 && CouldBeThisRecording(b, info))];

    /// <summary>
    /// The last resort: matches this file against the names Audiobookshelf holds its books' own
    /// audio files under.
    /// </summary>
    /// <param name="candidates">The books from <see cref="PossibleRecordings"/>, each with its
    /// file names fetched.</param>
    /// <param name="localPath">Path of the local audio file.</param>
    /// <param name="info">What ffprobe found in it.</param>
    /// <returns>The book and how it was recognized, or no book and why not.</returns>
    /// <remarks>
    /// <para>
    /// A file name on the server is the one thing about a book that is not in the library listing -
    /// that arrives minified, carrying counts where the detail carries lists - so reaching it costs
    /// a request per book. Hence the shape: it runs only after every free clue has come back with
    /// nothing, over a field the play time has already thinned. A local copy usually <em>is</em> the
    /// server's file, so this is a cheap way to recognize a book whose title nobody wrote into the
    /// tags.
    /// </para>
    /// <para>
    /// <b>The play time narrows and the name still identifies.</b> Two books can share a length, so
    /// a unique survivor of <see cref="PossibleRecordings"/> is not by itself a match and must not
    /// become one - that would make duration the identifier and leave the tool matching books by a
    /// number that a re-encode moves. What this stage adds is a name to agree with, on a field
    /// small enough to ask about.
    /// </para>
    /// <para>
    /// The same clues in the same order, matched against a different field. Keeping one list of
    /// what a file says about itself is the point: a second, shorter list here would be a second
    /// answer to that question, and the two would drift.
    /// </para>
    /// Notes: which response carries a file name and which does not, how far a book's three names
    /// diverge on a real server, and why the field is narrowed rather than merely capped.
    /// <include file='../../notes/Abs/AbsItemMatcher.xml' path='doc/member[@name="FindByServerFileName"]/*' />
    /// </remarks>
    public static AbsMatch FindByServerFileName(
        IReadOnlyList<AbsBookFiles> candidates, string localPath, MediaInfo info)
    {
        var stems = candidates
            .Select(c => (c.Book, Names: c.FileNames.Select(Path.GetFileNameWithoutExtension).ToList()))
            .ToList();

        foreach (var (clue, source) in Clues(localPath, info))
        {
            var exact = stems.Where(c => c.Names.Any(n => AbsSelector.MatchesExactly(n!, clue))).ToList();
            var found = exact.Count > 0
                ? exact
                : [.. stems.Where(c => c.Names.Any(n => Overlaps(n!, clue)))];
            var against = $"{source} against the file name on the server";
            if (Decide([.. found.Select(c => c.Book)], clue, against, info) is { } match)
                return match;
        }
        return new AbsMatch(null, NothingMatched, NoClueNamedABook: true);
    }

    /// <summary>
    /// Settles a file the user has mapped to a book by hand.
    /// </summary>
    /// <param name="books">Every book on the server this account can see.</param>
    /// <param name="mapped">The entry that names this file.</param>
    /// <param name="info">What ffprobe found in the local file.</param>
    /// <returns>The book and the note saying where it came from, or no book and what went wrong.</returns>
    /// <remarks>
    /// <para>
    /// An entry stops the search whatever it leads to. A mapping that names a book which is not
    /// there is a statement that turned out to be wrong, and falling through to the tags afterwards
    /// would answer a question the user had already answered - so it is reported, in terms of the
    /// entry, rather than quietly worked around. The result carries
    /// <see cref="AbsMatch.NoClueNamedABook"/> false for the same reason: there is nothing left to
    /// look for.
    /// </para>
    /// <para>
    /// <c>item:</c> is looked up in the catalogue the run has already fetched rather than requested
    /// on its own. That costs nothing, and it turns an id the account cannot see into a plain "not
    /// one of your books" instead of an HTTP failure a page later.
    /// </para>
    /// </remarks>
    private static AbsMatch ByMapping(
        IReadOnlyList<AbsBook> books, AbsBookMapping mapped, MediaInfo info)
    {
        var source = $"the mapping in \"{mapped.Where}\"";
        if (mapped.Book is not { } selector)
            return new AbsMatch(null, $"{source} says this file has no book on the server");

        if (selector.Kind == AbsSelectorKind.Item)
            return books.FirstOrDefault(b => b.ItemId == selector.Value) is { } byId
                ? Settle(byId, source, info)
                : new AbsMatch(null,
                    $"{source} names item \"{selector.Value}\", which is not a book on the server "
                    + "this account can see");

        return ByName(books, selector.Value, source, info)
               ?? new AbsMatch(null, $"{source} names \"{selector.Value}\", which matches no book "
                                     + "on the server");
    }

    /// <summary>
    /// Runs one clue against the books' titles and item folders.
    /// </summary>
    /// <param name="books">Every book on the server this account can see.</param>
    /// <param name="clue">What the local file said it was.</param>
    /// <param name="source">Which clue that was, in the words a note uses.</param>
    /// <param name="info">What ffprobe found in the local file.</param>
    /// <returns>The verdict, or null where this clue named no book and the next one should be
    /// tried.</returns>
    /// <remarks>
    /// Three tiers, narrowest first: a title that is exactly the clue, then an item folder that is,
    /// then either of them merely containing it. An exact match outranks a containment so that
    /// naming one book gets that book even where the library also holds "... and Other Stories".
    /// </remarks>
    private static AbsMatch? ByName(
        IReadOnlyList<AbsBook> books, string clue, string source, MediaInfo info)
    {
        var exact = books.Where(b => AbsSelector.MatchesExactly(b.Title, clue)).ToList();
        var byPath = exact.Count > 0 ? exact
            : [.. books.Where(b => AbsSelector.MatchesExactly(b.RelativePath, clue))];
        var matches = byPath.Count > 0 ? byPath : [.. books.Where(b => Overlaps(b.Title, clue))];
        return Decide(matches, clue, source, info);
    }

    /// <summary>
    /// What one clue's books amount to: a match, a refusal, or nothing to say.
    /// </summary>
    /// <param name="matches">The books this clue named, in any number.</param>
    /// <param name="clue">What the local file said it was.</param>
    /// <param name="source">Which clue that was, in the words a note uses.</param>
    /// <param name="info">What ffprobe found in the local file.</param>
    /// <returns>The verdict, or null where the clue named nothing at all.</returns>
    /// <remarks>
    /// One implementation for both stages, so that the tie-break, the two ways a crowded field is
    /// reported and the play-time test cannot come to work differently depending on which field the
    /// clue was matched against.
    /// </remarks>
    private static AbsMatch? Decide(
        IReadOnlyList<AbsBook> matches, string clue, string source, MediaInfo info)
    {
        if (matches.Count == 0)
            return null;
        if (matches.Count == 1)
            return Settle(matches[0], source, info);

        var plausible = matches.Where(b => CouldBeThisRecording(b, info)).ToList();
        if (plausible.Count == 1)
            return Settle(plausible[0], source, info);
        if (plausible.Count == 0)
            return NoneIsThisRecording(matches, clue, source, info);
        return Ambiguous(plausible, clue, source);
    }

    /// <summary>
    /// Reports a clue that still names more than one book after the play time has thinned the
    /// field, naming a few of them and how to say which.
    /// </summary>
    /// <param name="matches">The books still in the running, at least two.</param>
    /// <param name="clue">What the local file said it was.</param>
    /// <param name="source">Which clue that was, in the words a skip line uses.</param>
    /// <returns>A match holding no book and the ambiguity.</returns>
    /// <remarks>
    /// Only the survivors are named. A book the play time has already ruled out is not one of the
    /// answers to "which of these did you mean", and listing it would send the reader off to name a
    /// book that would then be refused for its length.
    /// </remarks>
    private static AbsMatch Ambiguous(IReadOnlyList<AbsBook> matches, string clue, string source)
        => new(null,
            $"{source} \"{clue}\" matches {matches.Count} books on the server "
            + $"({Titles(matches)}); name one in an --abs-map file, or use --abs with item:<id>");

    /// <summary>
    /// Reports a clue that names several books of which none is the recording in this file.
    /// </summary>
    /// <param name="matches">Every book the clue named, at least two.</param>
    /// <param name="clue">What the local file said it was.</param>
    /// <param name="source">Which clue that was, in the words a skip line uses.</param>
    /// <param name="info">What ffprobe found in the local file.</param>
    /// <returns>A match holding no book and the play time that ruled all of them out.</returns>
    /// <remarks>
    /// Its own message rather than the ambiguity's, because the two ask different things of the
    /// reader. "Say which one you meant" is unanswerable here - naming any of them by item id would
    /// only reach the same refusal one step later - where "this file is none of them" points at the
    /// real cause, most often one part of a split book that has not been joined yet.
    /// </remarks>
    private static AbsMatch NoneIsThisRecording(
        IReadOnlyList<AbsBook> matches, string clue, string source, MediaInfo info)
        => new(null,
            $"{source} \"{clue}\" matches {matches.Count} books on the server "
            + $"({Titles(matches)}), and none of them runs this file's "
            + $"{FormatMinutes(info.DurationSeconds)}, so it is not one of them - one part of a "
            + "split book, or a different edition");

    /// <summary>A few of the books by title, for a message that has to name them without running
    /// to the width of a library.</summary>
    /// <param name="matches">The books to name.</param>
    private static string Titles(IReadOnlyList<AbsBook> matches)
        => string.Join(", ", matches.Take(3).Select(m => $"\"{m.Title}\""))
           + (matches.Count > 3 ? ", ..." : "");

    /// <summary>
    /// Accepts the one book a clue settled on, unless its play time says it is not this recording.
    /// </summary>
    /// <param name="book">The book the clue named.</param>
    /// <param name="source">What the clue was, for the note either way.</param>
    /// <param name="info">What ffprobe found in the local file.</param>
    /// <returns>The match, or no book and the play times that ruled it out.</returns>
    /// <remarks>
    /// The search stops here rather than trying the next clue. A refusal is not "this clue found
    /// nothing" but "the most trustworthy clue still available named a book that is not this audio",
    /// and carrying on to a less trustworthy one after that is guessing - which is the thing the
    /// whole ordering exists to avoid. A mapping entry reaches this by the same road as a tag, and
    /// is refused on the same evidence: the user saying which book it is settles the identity, not
    /// whether the two are the same recording.
    /// </remarks>
    private static AbsMatch Settle(AbsBook book, string source, MediaInfo info)
    {
        if (!CouldBeThisRecording(book, info))
            return new AbsMatch(null,
                $"\"{book.Title}\" on the server runs {FormatMinutes(book.DurationSeconds)} against "
                + $"this file's {FormatMinutes(info.DurationSeconds)}, so they are not the same "
                + "recording - one part of a split book, or a different edition");

        return new AbsMatch(book, $"matched \"{book.Title}\" by {source}");
    }

    /// <summary>
    /// Whether a book's play time allows it to be the recording in this file.
    /// </summary>
    /// <param name="book">The candidate book.</param>
    /// <param name="info">What ffprobe found in the local file.</param>
    /// <returns>True when the two agree to within <see cref="SameRecordingSeconds"/>, or when the
    /// server reports no play time at all.</returns>
    /// <remarks>
    /// A book the server reports no play time for passes. There is then no evidence either way, and
    /// refusing on its absence would turn a server that answers differently from the one this was
    /// measured against into a server nothing can be pushed to at all - a failure that looks exactly
    /// like a broken matcher. The same silence keeps such a book in the running when this decides
    /// between several, which is why a tie-break can still end in a refusal.
    /// </remarks>
    private static bool CouldBeThisRecording(AbsBook book, MediaInfo info)
        => book.DurationSeconds <= 0
           || Math.Abs(book.DurationSeconds - info.DurationSeconds) <= SameRecordingSeconds;

    /// <summary>A play time as whole minutes, which is the resolution this comparison works at.
    /// </summary>
    /// <param name="seconds">The play time.</param>
    private static string FormatMinutes(double seconds) => $"{seconds / 60:0} min";

    /// <summary>
    /// What this file says it is, most trustworthy first; see the type remarks for why that order.
    /// </summary>
    /// <param name="localPath">Path of the local audio file.</param>
    /// <param name="info">What ffprobe found in it.</param>
    /// <returns>Each clue and the words a message should call it by.</returns>
    /// <remarks>
    /// Each name carries its own article, so that a message can drop one in without prefixing it.
    /// The fifth source is not in this list and is what forces that: a mapping calls itself "the
    /// mapping in ...", and a template supplying its own "the" turned that into "the the mapping
    /// in ..." the first time a mapped title came out ambiguous.
    /// </remarks>
    private static IEnumerable<(string Clue, string Source)> Clues(string localPath, MediaInfo info)
    {
        if (info.AlbumTag is { } album)
            yield return (album, "the album tag");
        if (info.TitleTag is { } title)
            yield return (title, "the title tag");
        if (Path.GetDirectoryName(localPath) is { Length: > 0 } folder
            && Path.GetFileName(folder) is { Length: > 0 } folderName)
            yield return (folderName, "the folder name");
        // Last, and stripped of anything this tool itself may have added to it: a book parked under
        // a ".missing-marks-7-8" name by an earlier run is the same book.
        var stem = Path.GetFileNameWithoutExtension(Processing.MissingMarksTag.StripFrom(localPath));
        if (stem.Length > 0)
            yield return (stem, "the file name");
    }

    /// <summary>Whether a name and a clue name each other, one being contained in the other once
    /// both are normalized.</summary>
    /// <param name="name">The candidate name: a book's title, or one of its file names.</param>
    /// <param name="clue">What the local file says it is.</param>
    /// <remarks>
    /// Both directions, because neither side is reliably the longer one: a library title carries
    /// the series and its number that a file name often drops ("Silber Edition 087: Das Spiel des
    /// Laren" against "Das Spiel des Laren"), while a file name carries the narrator, the year or
    /// an "unabridged" the library title does not.
    /// </remarks>
    private static bool Overlaps(string name, string clue)
        => AbsSelector.Matches(name, clue) || AbsSelector.Matches(clue, name);
}
