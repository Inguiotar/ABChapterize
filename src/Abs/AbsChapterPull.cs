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
/// none, and also when no book was settled on - see <see cref="AbsChapterPull"/>.</param>
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
/// <b>Whether the two are the same recording is settled before this runs</b>, by
/// <see cref="AbsItemMatcher"/>: a chapter list that describes different audio would put a book's
/// marks at the wrong words or past its end, and a match that has not earned its play time never
/// reaches here carrying a book. What is left for this to decide is what to do with the book that
/// did - which of the two lists the run may take, and which of them it has to keep hold of to
/// answer "was there anything to do at all" afterwards.
/// </para>
/// </remarks>
public static class AbsChapterPull
{
    /// <summary>
    /// Settles what one local file may take from the book it matched.
    /// </summary>
    /// <param name="match">What <see cref="AbsItemMatcher"/> made of the file.</param>
    /// <param name="fromServer">The chapter list Audiobookshelf holds for the matched book.</param>
    /// <param name="info">What ffprobe found in the local file.</param>
    /// <returns>The pull, with an empty server list where there is nothing to take.</returns>
    public static AbsPull Decide(AbsMatch match, IReadOnlyList<Chapter> fromServer, MediaInfo info)
        // No book means no list, whatever was read before the match failed: the server list belongs
        // to the book, so carrying one over without it is how marks from the wrong timeline would
        // get in. The file's own marks are carried through either way - they are what the
        // reconciliation afterwards compares against, and they are the file's whatever the server
        // turned out to hold.
        => match.Book is { } book
            ? new AbsPull(book, fromServer, info.ExistingChapters, match.Reason)
            : new AbsPull(null, [], info.ExistingChapters, match.Reason);
}
