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
public readonly record struct AbsMatch(AbsBook? Book, string Reason);

/// <summary>
/// Finds the Audiobookshelf book a local audio file belongs to, for the two modes that work on
/// local files and talk to a server - <c>--abs-push</c> and <c>--abs-push-only</c> outside ABS
/// mode - where the files are named on the command line and the server has to be searched for each.
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
/// Pure, and separate from the run for that reason: it is given the books and the probe result and
/// returns a verdict, which is what makes every branch of it testable without a server.
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
    /// Looks for the one book a local file is a copy of.
    /// </summary>
    /// <param name="books">Every book on the server this account can see.</param>
    /// <param name="localPath">Path of the local audio file.</param>
    /// <param name="info">What ffprobe found in it, for its container tags and its play time.</param>
    /// <returns>The book and how it was recognized, or no book and why not.</returns>
    public static AbsMatch Find(IReadOnlyList<AbsBook> books, string localPath, MediaInfo info)
    {
        foreach (var (clue, source) in Clues(localPath, info))
        {
            var exact = books.Where(b => AbsSelector.MatchesExactly(b.Title, clue)).ToList();
            var byPath = exact.Count > 0 ? exact
                : [.. books.Where(b => AbsSelector.MatchesExactly(b.RelativePath, clue))];
            var matches = byPath.Count > 0 ? byPath : [.. books.Where(b => Overlaps(b, clue))];

            if (matches.Count == 1)
                return Settle(matches[0], source, info);
            if (matches.Count > 1)
                return new AbsMatch(null,
                    $"the {source} \"{clue}\" matches {matches.Count} books on the server "
                    + $"({string.Join(", ", matches.Take(3).Select(m => $"\"{m.Title}\""))}"
                    + $"{(matches.Count > 3 ? ", ..." : "")}); use --abs with item:<id> to name one");
        }
        return new AbsMatch(null, "no book on the server matches this file");
    }

    /// <summary>
    /// Accepts the one book a clue settled on, unless its play time says it is not this recording.
    /// </summary>
    /// <param name="book">The book the clue named.</param>
    /// <param name="source">What the clue was, for the note either way.</param>
    /// <param name="info">What ffprobe found in the local file.</param>
    /// <returns>The match, or no book and the play times that ruled it out.</returns>
    /// <remarks>
    /// <para>
    /// The search stops here rather than trying the next clue. A refusal is not "this clue found
    /// nothing" but "the most trustworthy clue still available named a book that is not this
    /// audio", and carrying on to a less trustworthy one after that is guessing - which is the
    /// thing the whole ordering exists to avoid.
    /// </para>
    /// <para>
    /// A book the server reports no play time for is kept. There is then no evidence either way,
    /// and refusing on its absence would turn a server that answers differently from the one this
    /// was measured against into a server nothing can be pushed to at all - a failure that looks
    /// exactly like a broken matcher.
    /// </para>
    /// </remarks>
    private static AbsMatch Settle(AbsBook book, string source, MediaInfo info)
    {
        var drift = Math.Abs(book.DurationSeconds - info.DurationSeconds);
        if (book.DurationSeconds > 0 && drift > SameRecordingSeconds)
            return new AbsMatch(null,
                $"\"{book.Title}\" on the server runs {FormatMinutes(book.DurationSeconds)} against "
                + $"this file's {FormatMinutes(info.DurationSeconds)}, so they are not the same "
                + "recording - one part of a split book, or a different edition");

        return new AbsMatch(book, $"matched \"{book.Title}\" by {source}");
    }

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
    private static IEnumerable<(string Clue, string Source)> Clues(string localPath, MediaInfo info)
    {
        if (info.AlbumTag is { } album)
            yield return (album, "album tag");
        if (info.TitleTag is { } title)
            yield return (title, "title tag");
        if (Path.GetDirectoryName(localPath) is { Length: > 0 } folder
            && Path.GetFileName(folder) is { Length: > 0 } folderName)
            yield return (folderName, "folder name");
        // Last, and stripped of anything this tool itself may have added to it: a book parked under
        // a ".missing-marks-7-8" name by an earlier run is the same book.
        var stem = Path.GetFileNameWithoutExtension(Processing.MissingMarksTag.StripFrom(localPath));
        if (stem.Length > 0)
            yield return (stem, "file name");
    }

    /// <summary>Whether a book's title and a clue name each other, one being contained in the
    /// other once both are normalized.</summary>
    /// <param name="book">The candidate book.</param>
    /// <param name="clue">What the local file says it is.</param>
    /// <remarks>
    /// Both directions, because neither side is reliably the longer one: a library title carries
    /// the series and its number that a file name often drops ("Silber Edition 087: Das Spiel des
    /// Laren" against "Das Spiel des Laren"), while a file name carries the narrator, the year or
    /// an "unabridged" the library title does not.
    /// </remarks>
    private static bool Overlaps(AbsBook book, string clue)
        => AbsSelector.Matches(book.Title, clue) || AbsSelector.Matches(clue, book.Title);
}
