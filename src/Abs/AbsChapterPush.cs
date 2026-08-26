// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Audio;
using ABChapterize.Errors;
using ABChapterize.Formatting;

namespace ABChapterize.Abs;

/// <summary>
/// Sends a finished chapter list to Audiobookshelf.
/// </summary>
/// <remarks>
/// <para>
/// The one place in this tool that changes anything on the server, and deliberately the only one:
/// <c>POST /api/items/{id}/chapters</c> touches the chapter list and nothing else, where the
/// general media-update endpoint would let a bug here overwrite a title, an author or a cover. No
/// other write endpoint is called anywhere in <c>src\Abs\</c>, and none should be added without a
/// reason that could not be met by this one.
/// </para>
/// <para>
/// The request replaces the whole list rather than merging into it, which is why
/// <see cref="PushAsync"/> refuses an empty one: an accidental empty push is indistinguishable from
/// "delete this book's chapters", and it is the one mistake here that destroys something the user
/// cannot get back from the audio file.
/// </para>
/// <para>
/// A push is also read back and checked (<see cref="Confirm"/>), because the server accepting the
/// request and the server storing what was in it are two different claims. In ABS mode the
/// database holds the only copy of the marks, so a list quietly stored as something else is a run
/// that produced nothing and said it had succeeded.
/// </para>
/// </remarks>
public static class AbsChapterPush
{
    /// <summary>
    /// Replaces one book's chapter list on the server.
    /// </summary>
    /// <param name="session">The authenticated session.</param>
    /// <param name="book">The book to update.</param>
    /// <param name="chapters">The marks to send, in start order; must not be empty.</param>
    /// <param name="durationSeconds">The book's play time, which the last chapter ends at.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="AppError">Thrown when the list is empty, or when the server refuses the
    /// update.</exception>
    public static async Task PushAsync(
        AbsSession session, AbsBook book, IReadOnlyList<Chapter> chapters, double durationSeconds,
        CancellationToken ct)
    {
        if (chapters.Count == 0)
            throw new AppError(
                $"Refusing to send an empty chapter list for \"{book.Title}\": that would delete the "
                + "chapters Audiobookshelf has rather than replace them.");

        await session.PostAsync(
            $"/api/items/{Uri.EscapeDataString(book.ItemId)}/chapters",
            new AbsWire.ChapterUpdate { Chapters = Build(chapters, durationSeconds) },
            ct);
    }

    /// <summary>
    /// How far a mark read back off the server may sit from the one that was sent and still count
    /// as the same mark, in seconds.
    /// </summary>
    /// <remarks>
    /// A hair's breadth, and it can afford to be: measured against 2.36.0 on 2026-08-26 by pushing
    /// starts of 123.45678901234567 and 1234.0000001 and reading the item straight back, every
    /// value returned bit-for-bit, titles included - Audiobookshelf stores the numbers it is given
    /// rather than rounding them to a display resolution. So this is not a tolerance for expected
    /// drift; it is only there so that a future version re-serializing a double through some
    /// intermediate form cannot make the check cry wolf. Anything this could hide is a millisecond,
    /// and anything worth reporting is a mark in the wrong place.
    /// </remarks>
    public const double ConfirmedMarkSeconds = 0.001;

    /// <summary>
    /// Compares the chapter list the server now holds against the one that was sent to it, and says
    /// how they differ.
    /// </summary>
    /// <param name="sent">The marks that were pushed, in start order.</param>
    /// <param name="durationSeconds">The play time they were pushed with.</param>
    /// <param name="stored">The list read back off the server afterwards, in start order.</param>
    /// <returns>The empty string when the server holds exactly what it was sent, otherwise a
    /// one-clause description of the first difference found.</returns>
    /// <remarks>
    /// <para>
    /// Rebuilds the wire list from <paramref name="sent"/> rather than being handed it, because
    /// <see cref="Build"/> is pure and its clamping is part of what the wire carried: a mark at a
    /// negative start is sent as zero, and comparing against the unclamped list would report the
    /// server's faithful copy as a mismatch.
    /// </para>
    /// <para>
    /// Starts and titles only, no ends. An end is not a fact of its own - it is the next mark's
    /// start, so a list whose starts and count both check out has ends that follow from them - and
    /// the read-back path is <see cref="AbsCatalog.LoadChaptersAsync"/>, which drops them for that
    /// same reason. What is left unchecked is therefore the final chapter's end alone, which is the
    /// book's play time and belongs to no mark.
    /// </para>
    /// <para>
    /// Reports one difference rather than all of them: this ends up in a summary line, and the
    /// answer the reader needs is "the server has something else" plus enough of a specimen to see
    /// what kind of something else. A list of forty divergent marks would be neither.
    /// </para>
    /// </remarks>
    public static string Confirm(
        IReadOnlyList<Chapter> sent, double durationSeconds, IReadOnlyList<Chapter> stored)
    {
        var wire = Build(sent, durationSeconds);
        if (wire.Count != stored.Count)
            return $"it holds {stored.Count} mark(s) against the {wire.Count} sent";

        for (var i = 0; i < wire.Count; i++)
        {
            if (Math.Abs(wire[i].Start - stored[i].StartSeconds) > ConfirmedMarkSeconds)
                return $"mark {i + 1} of {wire.Count} sits at {TimeFormat.Hms(stored[i].StartSeconds, 2)} "
                       + $"rather than {TimeFormat.Hms(wire[i].Start, 2)}";
            if (!string.Equals(wire[i].Title, stored[i].Title, StringComparison.Ordinal))
                return $"mark {i + 1} of {wire.Count} is titled \"{stored[i].Title}\" "
                       + $"rather than \"{wire[i].Title}\"";
        }
        return "";
    }

    /// <summary>
    /// Turns this tool's marks - a start and a title each - into the shape Audiobookshelf stores,
    /// which also wants an end and a position.
    /// </summary>
    /// <param name="chapters">The marks, in start order.</param>
    /// <param name="durationSeconds">The book's play time.</param>
    /// <returns>The wire chapter list.</returns>
    /// <remarks>
    /// A chapter ends where the next one starts, so the ends carry no information of their own and
    /// are derived rather than tracked - the same reason <see cref="Chapter"/> has never had one.
    /// The last chapter ends at the book's duration; a duration the probe could not establish
    /// leaves it ending at its own start, which Audiobookshelf renders as a zero-length final
    /// chapter rather than refusing the whole list.
    /// </remarks>
    internal static List<AbsWire.Chapter> Build(IReadOnlyList<Chapter> chapters, double durationSeconds)
    {
        var wire = new List<AbsWire.Chapter>(chapters.Count);
        for (var i = 0; i < chapters.Count; i++)
        {
            var start = Math.Max(0, chapters[i].StartSeconds);
            var end = i + 1 < chapters.Count
                ? Math.Max(start, chapters[i + 1].StartSeconds)
                : Math.Max(start, durationSeconds);
            wire.Add(new AbsWire.Chapter { Id = i, Start = start, End = end, Title = chapters[i].Title });
        }
        return wire;
    }
}
