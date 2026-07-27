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

        // Concatenate all segment texts and remember which character belongs to which segment
        // so a match position can be mapped back to a time.
        var sb = new StringBuilder();
        var segStartChar = new int[segments.Count];
        for (var i = 0; i < segments.Count; i++)
        {
            segStartChar[i] = sb.Length;
            sb.Append(segments[i].Text);
            sb.Append(' ');
        }
        var text = sb.ToString();
        var mergeBoundaryChar = mergeBoundarySegIndex is { } idx && idx > 0 && idx < segments.Count
            ? segStartChar[idx] : (int?)null;

        foreach (Match m in profile.PhraseRegex.Matches(text))
        {
            int number;
            // The exact character range actually consulted to find the phrase and parse its
            // number - just the match itself unless a head/tail slice contributed too - used
            // below to tell whether this detection drew on text from both sides of a Pass 2
            // overlap's cache/fresh boundary.
            var consumedStart = m.Index;
            var consumedEnd = m.Index + m.Length;
            if (profile.PhraseHasNumberGroup && m.Groups.Count > 1 && m.Groups[1].Success)
            {
                if (!int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out number))
                    continue;
            }
            else
            {
                var tail = text[(m.Index + m.Length)..];
                if (tail.Length > 80)
                    tail = tail[..80];
                if (NumberWordParser.TryExtractNumber(tail, profile.Language, out number))
                {
                    consumedEnd += tail.Length;
                }
                else
                {
                    // No number after the phrase - try the ordinal-first announcement
                    // order ("Erstes Kapitel", "2. Kapitel", "Birinci Bölüm").
                    var head = text[..m.Index];
                    if (head.Length > 80)
                        head = head[^80..];
                    if (!NumberWordParser.TryExtractNumberBefore(head, profile.Language, out number))
                        continue;
                    consumedStart -= head.Length;
                }
            }

            var segIndex = 0;
            for (var i = 0; i < segments.Count; i++)
            {
                if (segStartChar[i] <= m.Index)
                    segIndex = i;
                else
                    break;
            }
            var spansMerge = mergeBoundaryChar is { } b && consumedStart < b && b < consumedEnd;
            yield return new PhraseMatch(
                number, segments[segIndex].StartSeconds, segments[segIndex].EndSeconds,
                segments[segIndex].Probability, spansMerge);
        }
    }
}
