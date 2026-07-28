// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ABChapterize.Language;
using ABChapterize.Transcription;

namespace ABChapterize.Detection;

/// <summary>Finds the chapter-announcement phrase (and its chapter number) inside a window's
/// transcribed segments.</summary>
internal static class PhraseMatching
{
    /// <summary>A phrase match inside a transcribed window.</summary>
    /// <param name="Number">Parsed chapter number.</param>
    /// <param name="PhraseStartSeconds">Phrase start relative to the window start.</param>
    /// <param name="PhraseEndSeconds">End of the transcript segment the phrase was found in,
    /// relative to the window start. Whisper can smear that segment across a whole jingle (its
    /// start pulled seconds before the words are spoken), so the span [start, end] - not the
    /// start alone - is what the smeared-phrase rescue in <see cref="JingleGeometry.ResolveJingleAnchor"/>
    /// matches against VAD regions.</param>
    /// <param name="Confidence">Whisper's probability for the segment the match was found in.</param>
    /// <param name="SpansMerge">True when the text actually used to find the phrase and parse its
    /// number straddles a Pass 2 overlap's cache/fresh boundary - see <see cref="FindPhraseMatches"/>'s
    /// <c>mergeBoundarySegIndex</c> parameter.</param>
    internal readonly record struct PhraseMatch(
        int Number, double PhraseStartSeconds, double PhraseEndSeconds, double Confidence,
        bool SpansMerge = false);

    /// <summary>A non-numbered announcement (prologue, epilogue or a <c>--custom</c> mapping) found
    /// inside a transcribed window.</summary>
    /// <param name="Phrase">The phrase that matched.</param>
    /// <param name="Title">The title this particular match writes: the phrase's own template with
    /// its capturing-group references expanded (see <see cref="NamedPhrase.ResolveTitle"/>).
    /// Resolved here, while the <see cref="Match"/> is still around, rather than carried along as a
    /// match object nothing downstream has any other use for.</param>
    /// <param name="PhraseStartSeconds">Phrase start, in the time base <paramref name="Phrase"/> was
    /// matched in (window-relative for a Pass 2 probe).</param>
    /// <param name="PhraseEndSeconds">End of the transcript segment the phrase was found in, in the
    /// same time base - the smeared-segment span <see cref="JingleGeometry.ResolveJingleAnchor"/>
    /// needs, exactly as for <see cref="PhraseMatch.PhraseEndSeconds"/>.</param>
    /// <param name="Confidence">Whisper's probability for the segment the match was found in.</param>
    internal readonly record struct NamedMatch(
        NamedPhrase Phrase, string Title,
        double PhraseStartSeconds, double PhraseEndSeconds, double Confidence);

    /// <summary>
    /// Searches the transcribed segments for the chapter phrase and parses the chapter number,
    /// either from the regexp capturing group or from the words following the phrase
    /// ("Chapter Seven"); when neither yields a number, the words directly preceding the
    /// phrase are tried ("Erstes Kapitel", "Birinci Bölüm").
    /// </summary>
    /// <param name="segments">The window's transcript segments, in window-relative time.</param>
    /// <param name="profile">Language profile supplying the chapter phrase and number parsing.</param>
    /// <param name="mergeBoundarySegIndex">For a window assembled by Pass 2's overlap reuse (see
    /// ProbeAsync), the index of the first segment that came from the fresh tail decode rather
    /// than the reused cache; null for a window that is entirely one or the other (a plain probe,
    /// a fully-reused window, a gap chunk, or a --verify window). Used only to flag
    /// <see cref="PhraseMatch.SpansMerge"/> - it does not affect which matches are found.</param>
    internal static IEnumerable<PhraseMatch> FindPhraseMatches(
        List<TranscriptSegment> segments, LanguageProfile profile, int? mergeBoundarySegIndex = null)
    {
        if (segments.Count == 0)
            yield break;

        var (text, segStartChar) = Flatten(segments);
        var mergeBoundaryChar = mergeBoundarySegIndex is { } idx && idx > 0 && idx < segments.Count
            ? segStartChar[idx] : (int?)null;

        foreach (Match m in profile.PhraseRegex.Matches(text))
        {
            if (!TryParseAnnouncedNumber(text, m, profile, out var number, out var consumedStart, out var consumedEnd))
                continue;

            var segIndex = SegmentIndexAt(segStartChar, m.Index);
            var spansMerge = mergeBoundaryChar is { } b && consumedStart < b && b < consumedEnd;
            yield return new PhraseMatch(
                number, segments[segIndex].StartSeconds, segments[segIndex].EndSeconds,
                segments[segIndex].Probability, spansMerge);
        }
    }

    /// <summary>
    /// Reads the chapter number belonging to one phrase match, from the regexp's own capturing group
    /// if it has one, else from the words following the phrase ("Chapter Seven"), else from those
    /// preceding it ("Erstes Kapitel", "Birinci Bölüm"). The 80-character head/tail slices bound how
    /// far a number may sit from the phrase and still count as announced with it, keeping an
    /// unrelated number later in the same segment out.
    /// </summary>
    /// <param name="text">The flattened window text the match came from.</param>
    /// <param name="m">The phrase match itself.</param>
    /// <param name="profile">Language profile supplying the number grammar and group convention.</param>
    /// <param name="number">The parsed chapter number, when one was found.</param>
    /// <param name="consumedStart">Start of the character range actually consulted - the match
    /// itself, extended backwards when a preceding slice supplied the number.</param>
    /// <param name="consumedEnd">End of that range, extended forwards when a following slice
    /// supplied it. Together the two tell <see cref="FindPhraseMatches"/> whether the detection drew
    /// on text from both sides of a Pass 2 overlap's cache/fresh boundary.</param>
    private static bool TryParseAnnouncedNumber(
        string text, Match m, LanguageProfile profile,
        out int number, out int consumedStart, out int consumedEnd)
    {
        consumedStart = m.Index;
        consumedEnd = m.Index + m.Length;
        if (profile.PhraseHasNumberGroup && m.Groups.Count > 1 && m.Groups[1].Success)
            return int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out number);

        var tail = text[(m.Index + m.Length)..];
        if (tail.Length > 80)
            tail = tail[..80];
        if (NumberWordParser.TryExtractNumber(tail, profile.Language, out number))
        {
            consumedEnd += tail.Length;
            return true;
        }

        var head = text[..m.Index];
        if (head.Length > 80)
            head = head[^80..];
        if (!NumberWordParser.TryExtractNumberBefore(head, profile.Language, out number))
            return false;
        consumedStart -= head.Length;
        return true;
    }

    /// <summary>
    /// Searches the transcribed segments for chapter announcements without requiring any of them to
    /// carry a number - what <c>--ignore-chapter-numbers</c> runs instead of
    /// <see cref="FindPhraseMatches"/>. A number that is spoken still reaches the title, because
    /// "Kapitel 3" is what the listener expects to see even when nothing checks that a 2 came before
    /// it; one that is not simply leaves the bare title word, which is the whole point for a book
    /// that announces its chapters without numbering them.
    /// </summary>
    /// <param name="segments">The window's transcript segments, in the caller's time base.</param>
    /// <param name="profile">Language profile supplying the chapter phrase, title and number grammar.</param>
    internal static IEnumerable<NamedMatch> FindChapterAnnouncements(
        List<TranscriptSegment> segments, LanguageProfile profile)
    {
        if (segments.Count == 0)
            yield break;

        var (text, segStartChar) = Flatten(segments);
        foreach (Match m in profile.PhraseRegex.Matches(text))
        {
            var title = TryParseAnnouncedNumber(text, m, profile, out var number, out _, out _)
                ? $"{profile.Title} {number}"
                : profile.Title;
            var segment = segments[SegmentIndexAt(segStartChar, m.Index)];
            yield return new NamedMatch(
                profile.ChapterAnnouncement, title,
                segment.StartSeconds, segment.EndSeconds, segment.Probability);
        }
    }

    /// <summary>
    /// Searches the transcribed segments for every non-numbered announcement the profile knows
    /// (prologue, epilogue, <c>--custom</c>). No number is parsed and none is required, so - unlike
    /// <see cref="FindPhraseMatches"/> - a phrase that matches is always a match; whether it may
    /// become a mark is decided by its <see cref="NamedPhrase.Scope"/> at the call site, which is
    /// the only place that knows how many chapters have been found so far.
    /// </summary>
    /// <param name="segments">The window's transcript segments, in whatever time base the caller
    /// works in (this method neither reads nor rewrites the timings).</param>
    /// <param name="profile">Language profile supplying <see cref="LanguageProfile.NamedPhrases"/>.</param>
    internal static IEnumerable<NamedMatch> FindNamedMatches(
        List<TranscriptSegment> segments, LanguageProfile profile)
    {
        if (segments.Count == 0 || profile.NamedPhrases.Count == 0)
            yield break;

        var (text, segStartChar) = Flatten(segments);
        foreach (var phrase in profile.NamedPhrases)
        {
            foreach (Match m in phrase.Regex.Matches(text))
            {
                var segment = segments[SegmentIndexAt(segStartChar, m.Index)];
                yield return new NamedMatch(
                    phrase, phrase.ResolveTitle(m),
                    segment.StartSeconds, segment.EndSeconds, segment.Probability);
            }
        }
    }

    /// <summary>
    /// Concatenates all segment texts into the single string the regexes run against, remembering
    /// where each segment starts in it so a match position can be mapped back to a time. The
    /// trailing space after every segment is what keeps two segments from fusing into a spurious
    /// word across their join.
    /// </summary>
    /// <param name="segments">The transcript segments, in order.</param>
    private static (string Text, int[] SegStartChar) Flatten(List<TranscriptSegment> segments)
    {
        var sb = new StringBuilder();
        var segStartChar = new int[segments.Count];
        for (var i = 0; i < segments.Count; i++)
        {
            segStartChar[i] = sb.Length;
            sb.Append(segments[i].Text);
            sb.Append(' ');
        }
        return (sb.ToString(), segStartChar);
    }

    /// <summary>The index of the segment a character position in <see cref="Flatten"/>'s text
    /// belongs to: the last segment starting at or before it.</summary>
    /// <param name="segStartChar">Per-segment start offsets from <see cref="Flatten"/>.</param>
    /// <param name="charIndex">The character position to locate.</param>
    private static int SegmentIndexAt(int[] segStartChar, int charIndex)
    {
        var segIndex = 0;
        for (var i = 0; i < segStartChar.Length; i++)
        {
            if (segStartChar[i] <= charIndex)
                segIndex = i;
            else
                break;
        }
        return segIndex;
    }
}
