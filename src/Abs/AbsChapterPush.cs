// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Audio;
using ABChapterize.Errors;

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
