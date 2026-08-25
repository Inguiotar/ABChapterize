// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Audio;

namespace ABChapterize.Abs;

/// <summary>
/// What <c>--abs-pull</c> found for one local file: the book it belongs to, and the two chapter
/// lists the run has to reconcile.
/// </summary>
/// <param name="Book">The book on the server, or null when none could be settled on - in which
/// case there is nothing to pull and nothing to push either.</param>
/// <param name="FromServer">The chapter list Audiobookshelf holds for it. Empty when the server has
/// none, and also when the pull was refused - see <see cref="AbsChapterPull"/>.</param>
/// <param name="FromFile">The chapter list the local file carries, as the probe found it and
/// before the merge replaced it. Kept because "has this file already got these marks" is a
/// question only the pre-merge list can answer, and it is what decides whether the file needs
/// writing at all.</param>
/// <param name="Note">How the book was recognized, or why none was, in the words a log line or a
/// skip line uses.</param>
public readonly record struct AbsPull(
    AbsBook? Book,
    IReadOnlyList<Chapter> FromServer,
    IReadOnlyList<Chapter> FromFile,
    string Note);

/// <summary>
/// Decides what a local file may take from the Audiobookshelf book it was matched to.
/// </summary>
/// <remarks>
/// <para>
/// Pure, and separate from the fetching for the same reason <see cref="AbsItemMatcher"/> is: it is
/// handed the two sides and returns a verdict, so every branch of it is testable without a server.
/// </para>
/// <para>
/// <b>The one thing it exists to refuse is a chapter list that describes different audio.</b>
/// Audiobookshelf's marks are positions on the item's own timeline, and this tool is about to write
/// them into a file on the user's disk - so if the two are not the same recording, the result is a
/// book whose marks point at the wrong words, or past its end. The matcher cannot see this: it
/// recognizes a book by its title and tags, and every one of a hundred and thirty-five parts of a
/// split book carries the same album tag as the whole. Comparing play times is what tells them
/// apart, and it is the only evidence either side has in common.
/// </para>
/// </remarks>
public static class AbsChapterPull
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
    /// pulled from the other timeline would be visibly in the wrong place anyway.
    /// </remarks>
    public const double SameRecordingSeconds = 60.0;

    /// <summary>
    /// Settles what one local file may take from the book it matched.
    /// </summary>
    /// <param name="match">What <see cref="AbsItemMatcher"/> made of the file.</param>
    /// <param name="fromServer">The chapter list Audiobookshelf holds for the matched book.</param>
    /// <param name="info">What ffprobe found in the local file.</param>
    /// <returns>The pull, with an empty server list where there is nothing to take.</returns>
    public static AbsPull Decide(AbsMatch match, IReadOnlyList<Chapter> fromServer, MediaInfo info)
    {
        if (match.Book is not { } book)
            return new AbsPull(null, [], info.ExistingChapters, match.Reason);

        var drift = Math.Abs(book.DurationSeconds - info.DurationSeconds);
        if (drift > SameRecordingSeconds)
            // The book is dropped along with its chapters, not merely the chapters: a file whose
            // timeline is not this book's must not have marks pushed to it either, and leaving the
            // book here would let --abs-push do exactly that.
            return new AbsPull(null, [], info.ExistingChapters,
                $"\"{book.Title}\" on the server runs {FormatMinutes(book.DurationSeconds)} against "
                + $"this file's {FormatMinutes(info.DurationSeconds)}, so they are not the same "
                + "recording - one part of a split book, or a different edition");

        return new AbsPull(book, fromServer, info.ExistingChapters, match.Reason);
    }

    /// <summary>A play time as whole minutes, which is the resolution this comparison works at.
    /// </summary>
    /// <param name="seconds">The play time.</param>
    private static string FormatMinutes(double seconds) => $"{seconds / 60:0} min";
}
