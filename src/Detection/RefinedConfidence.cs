// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Language;
using ABChapterize.Transcription;
using static ABChapterize.Detection.PhraseMatching;

namespace ABChapterize.Detection;

/// <summary>
/// Re-measures how sure the recognizer is of an announcement, from the transcripts
/// <see cref="PreciseMarkRefiner"/> produced while pinning the onset - the sibling of
/// <see cref="RefinedNumberVote"/>, which re-derives the <em>number</em> from the same material.
/// <para>
/// The window that first found an announcement is the worst-framed look at it anything in the run
/// gets: it runs to half a minute, and the segment holding the announcement routinely swallows the
/// jingle in front of it, so its probability describes a stretch of music far more than it
/// describes the words. The refinement's probes are aimed at the announcement and a few seconds
/// wide. Taking the confidence from those is not a second opinion, it is the first properly framed
/// one.
/// </para>
/// <para>
/// The median rather than the best of them, and rather than the window's own figure. The best would
/// make a mark look more certain the more probes it needed, which is backwards - a mark the search
/// had to hunt for would outscore one confirmed immediately. The median is what the readings agree
/// on, and it moves the figure only as far as the evidence does.
/// </para>
/// </summary>
/// <remarks>Notes: what the window figure was really measuring, the false-alarm rates that chose the median, and why the spread was rejected.
/// <include file='../../notes/Detection/RefinedConfidence.xml' path='doc/member[@name="RefinedConfidence"]/*' /></remarks>
internal static class RefinedConfidence
{
    /// <summary>
    /// The median probability the refinement's own readings give this announcement.
    /// </summary>
    /// <param name="readings">Every probe transcript the announcement was found in, from
    /// <see cref="PreciseMarkResult.PhraseReadings"/>.</param>
    /// <param name="profile">The file's language profile, for the phrase matcher.</param>
    /// <param name="findMatches">The detector's capped phrase matcher - the same delegate
    /// <see cref="RefinedNumberVote.Recount"/> is given, so both read these transcripts through
    /// exactly the recognizer-facing rules a window is read through.</param>
    /// <param name="number">The number this mark has settled on, which may be one
    /// <see cref="RefinedNumberVote"/> has just corrected. Readings that name a different chapter
    /// are measuring a different announcement and are passed over rather than averaged in.</param>
    /// <returns>The median, or null when no reading names this chapter - a mark whose refinement
    /// confirmed nothing has nothing better to offer than the window figure it already has.</returns>
    internal static double? Median(
        IReadOnlyList<List<TranscriptSegment>> readings, LanguageProfile profile,
        Func<List<TranscriptSegment>, LanguageProfile, int?, IEnumerable<PhraseMatch>> findMatches,
        int number)
    {
        var values = new List<double>();
        foreach (var reading in readings)
        {
            // One value per reading, not per match: the reading is the observation. A probe
            // transcript that happens to hold the announcement twice is still a single look at it,
            // and counting it twice would let the framing of one probe outvote the others.
            foreach (var match in findMatches(reading, profile, null))
            {
                if (match.Number != number)
                    continue;
                values.Add(match.Confidence);
                break;
            }
        }
        if (values.Count == 0)
            return null;

        values.Sort();
        var mid = values.Count / 2;
        return values.Count % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2;
    }
}
