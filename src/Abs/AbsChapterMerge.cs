// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Audio;

namespace ABChapterize.Abs;

/// <summary>
/// Settles what "the marks this book already has" means for a book fetched from Audiobookshelf,
/// which can have two answers: the list the server keeps in its database, and whatever is embedded
/// in the audio file it sent.
/// </summary>
/// <remarks>
/// <para>
/// <b>The server wins where it has an opinion; the file fills in where it has none.</b> The server
/// list is the one the user sees, edits and shares between devices, and it is also the one this run
/// is going to replace - so it is what a skip, a <c>--force</c> and a <c>--verify</c> all have to
/// reason about. A retail file's own marks are frequently absent, stale, or a grouping the retailer
/// invented, and none of those should quietly outrank a list somebody curated.
/// </para>
/// <para>
/// The two disagreeing is not an error and not rare: Audiobookshelf reads chapters at scan time and
/// keeps them independently ever after, so any edit made in its web interface puts the two out of
/// step by design. It is reported under <c>--verbose</c> and otherwise passed over.
/// </para>
/// </remarks>
public static class AbsChapterMerge
{
    /// <summary>
    /// How far apart two marks may sit and still be the same mark, when deciding whether the two
    /// lists agree. Only ever used for the <c>--verbose</c> note - nothing branches on the answer -
    /// so it is a readability threshold rather than a calibrated one: a second of drift is what
    /// re-muxing a file through a different tool costs, and reporting that as a disagreement would
    /// make the note fire on books nobody has touched.
    /// </summary>
    public const double SameMarkSeconds = 1.0;

    /// <summary>
    /// Applies the merge rule to a freshly probed file.
    /// </summary>
    /// <param name="info">What ffprobe found in the file - the temporary copy in ABS mode, the
    /// user's own file under <c>--abs-pull</c>.</param>
    /// <param name="fromServer">The chapter list Audiobookshelf holds for the book, possibly
    /// empty.</param>
    /// <returns>The media info the rest of the pipeline should see, and the one-line note for
    /// <c>--verbose</c>, or an empty note when there is nothing worth saying.</returns>
    /// <remarks>
    /// The same rule for both directions the marks can travel, and deliberately so: <c>--abs</c>
    /// and <c>--abs-pull</c> ask the identical question - which of the two lists is this book's -
    /// and an answer that depended on which mode was asking would be two rules wearing one name.
    /// </remarks>
    public static (MediaInfo Info, string Note) Apply(MediaInfo info, IReadOnlyList<Chapter> fromServer)
    {
        var fromFile = info.ExistingChapters;
        if (fromServer.Count == 0)
            return (info, fromFile.Count > 0
                ? $"Audiobookshelf has no chapters; using the {fromFile.Count} the file carries"
                : "");

        var merged = info with { ChapterCount = fromServer.Count, ExistingChapterList = fromServer };
        return (merged, Describe(fromFile, fromServer));
    }

    /// <summary>
    /// The note describing how the two lists stand to each other, once the server has been found to
    /// have one.
    /// </summary>
    /// <param name="fromFile">The marks embedded in the downloaded file.</param>
    /// <param name="fromServer">The marks Audiobookshelf holds.</param>
    /// <returns>A note, or the empty string when the two agree and there is nothing to report.</returns>
    private static string Describe(IReadOnlyList<Chapter> fromFile, IReadOnlyList<Chapter> fromServer)
    {
        if (fromFile.Count == 0)
            return $"using the {fromServer.Count} chapter(s) Audiobookshelf holds (the file carries none)";
        if (Agree(fromFile, fromServer))
            return "";
        return $"Audiobookshelf has {fromServer.Count} chapter(s), the file {fromFile.Count}"
               + " - the server's list is the one that counts";
    }

    /// <summary>Whether two chapter lists describe the same marks, titles aside.</summary>
    /// <param name="left">One list, in start order.</param>
    /// <param name="right">The other, in start order.</param>
    /// <remarks>
    /// Positions only. Titles differ between the two sides for reasons that say nothing about
    /// whether a book is marked - Audiobookshelf renumbers untitled chapters, and this tool writes
    /// its own <c>--chapter-title</c> wording - so comparing them would report a disagreement on
    /// books whose marks are identical.
    /// </remarks>
    private static bool Agree(IReadOnlyList<Chapter> left, IReadOnlyList<Chapter> right)
        => left.Count == right.Count
           && !left.Where((c, i) => Math.Abs(c.StartSeconds - right[i].StartSeconds) > SameMarkSeconds).Any();

    /// <summary>
    /// Whether one side already has exactly what the run settled on, titles included - the question
    /// <c>--abs-pull</c> asks before rewriting a file or sending anything to the server.
    /// </summary>
    /// <param name="left">One list, in start order.</param>
    /// <param name="right">The other, in start order.</param>
    /// <remarks>
    /// Stricter than <see cref="Agree"/>, and for the opposite reason. That one answers "are these
    /// the same marks", where a differing title is noise; this one answers "is there anything left
    /// to do", where it is not - a pull exists to give a file the list the server holds, titles and
    /// all, and stopping short of the titles would leave the two sides disagreeing for ever. It
    /// still converges: once the file has been written, its own list is what comes back off the
    /// next probe and the answer becomes yes.
    /// </remarks>
    public static bool SameMarks(IReadOnlyList<Chapter> left, IReadOnlyList<Chapter> right)
        => Agree(left, right)
           && !left.Where((c, i) => !string.Equals(c.Title, right[i].Title, StringComparison.Ordinal)).Any();
}
