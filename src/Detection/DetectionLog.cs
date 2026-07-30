// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

namespace ABChapterize.Detection;

/// <summary>
/// Where one file's detection writes what it has to say. Two sinks rather than one because the two
/// audiences differ in kind: <paramref name="Plain"/> is read by a person watching a run or skimming
/// a <c>--log-file</c> afterwards, while <paramref name="Debug"/> is read by whoever is holding a
/// misplaced mark and needs the raw material behind it.
/// <para>
/// The split exists so the second can be complete without making the first unreadable. A 13-hour
/// audiobook yields several thousand silences and tens of thousands of VAD speech segments; put
/// those in the ordinary stream and every other line in it is lost, which is precisely the outcome a
/// troubleshooting dump must not cause for the log people actually watch.
/// </para>
/// <para>
/// Almost every line goes to both, via <see cref="Fanout"/>: a call site says what happened once and
/// the debug file holds the union of both streams by construction rather than by anyone remembering
/// to keep them in step. Only two kinds of line address one sink alone - the bulk dumps, which only
/// <paramref name="Debug"/> can absorb, and the abbreviated form of a line whose full form the debug
/// file is getting anyway (see <see cref="ChapterDetector"/>'s transcript logging).
/// </para>
/// </summary>
/// <param name="Plain">The ordinary log stream - the console under <c>--verbose</c> and the
/// <c>--log-file</c>. Null when neither is listening.</param>
/// <param name="Debug">The <c>--debug</c> file. Null unless <c>--debug</c> was given.</param>
public readonly record struct DetectionLog(Action<string>? Plain, Action<string>? Debug)
{
    /// <summary>
    /// One delegate writing to every sink there is, or null when there is none - so the call sites
    /// that build a log message only when someone is listening keep their single null check.
    /// Build it once per file and hold on to it; each call allocates.
    /// </summary>
    public Action<string>? Fanout()
    {
        // Copied out of the struct because a lambda inside one cannot close over `this` (CS1673).
        var (plain, debug) = (Plain, Debug);
        return plain == null ? debug
            : debug == null ? plain
            : message => { plain(message); debug(message); };
    }

    /// <summary>Writes one line to every sink.</summary>
    /// <param name="message">The message, without timestamp.</param>
    public void Write(string message)
    {
        Plain?.Invoke(message);
        Debug?.Invoke(message);
    }
}
