// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Language;
using ABChapterize.Transcription;
using static ABChapterize.Detection.DetectionFormatting;
using static ABChapterize.Detection.DetectionTuning;
using static ABChapterize.Detection.PhraseMatching;

namespace ABChapterize.Detection;

/// <summary>
/// Re-reads a chapter number out of the transcripts <see cref="PreciseMarkRefiner"/> produced while
/// pinning the same announcement's onset, and overrules the detecting window when they disagree
/// with it clearly enough.
/// <para>
/// The evidence is better than the reading it corrects, and the reason is geometric rather than
/// statistical. A probe window is tens of seconds long and the announcement can sit anywhere
/// in it, including at the very end where Whisper's segmentation is at its worst; the refinement's
/// probes are a few seconds long and framed on the announcement itself, because that is what the
/// onset search needs them to be. Nothing is re-decoded to learn this - the probes have already run
/// and their transcripts were being thrown away.
/// </para>
/// <para>
/// Two guards keep a free second opinion from becoming a free second chance to be wrong. The
/// readings must reach <see cref="DetectionTuning.RefinedNumberVoteMinimum"/> and the winner must
/// hold a strict majority of them, which is what a refinement that drifts across two or three
/// numbers cannot supply; and the winner must be a number the sequence can actually hold
/// (<see cref="NumberBounds.Admits"/>), the same rule <see cref="SuspectNumberMender"/> adopts by.
/// A vote that fails either guard changes nothing, so the caller is left doing exactly what it
/// would have done unaided.
/// </para>
/// <para>
/// <b>The second guard has one exception, and it is the shape it would otherwise be blind to.</b>
/// A winner the sequence cannot hold is normally a refinement drifting - except where that winner
/// is the number of the chapter already marked within
/// <see cref="DetectionTuning.CollidingChapterMarkSeconds"/> of this one. Then "outside the
/// sequence" is the symptom rather than the objection: one announcement has been read twice, and
/// the reading that repeats the neighbour is the correct one precisely because the neighbour is
/// that same announcement. Refusing it here is refusing the only reading that could ever be right,
/// since a duplicate's correct number is by construction not above the last accepted. Adopting it
/// turns a phantom chapter into a mark <see cref="ChapterDetector.SettleCollidingMarksAsync"/> and
/// <see cref="GapPlanning.Normalize"/> can recognize as the duplicate it is.
/// </para>
/// <para>
/// Nothing but the number is affected. The mark's position comes from the onset search and is
/// already settled by the time this runs.
/// </para>
/// </summary>
/// <remarks>Notes: the window framing that read one number and the ten refinement probes that read another; the duplicate the sequence guard had to make an exception for.
/// <include file='../../notes/Detection/RefinedNumberVote.xml' path='doc/member[@name="RefinedNumberVote"]/*' /></remarks>
internal static class RefinedNumberVote
{
    /// <summary>
    /// Counts the chapter numbers <paramref name="readings"/> carry and returns the one that should
    /// replace <paramref name="heard"/>, or null to keep it.
    /// </summary>
    /// <param name="readings">The refinement's own probe transcripts that contained the
    /// announcement (<see cref="PreciseMarkResult.PhraseReadings"/>). Each is counted on its own
    /// rather than concatenated: they come from overlapping decodes of one announcement, and joining
    /// them would let one probe's trailing words and the next probe's leading ones form a phrase
    /// neither of them heard.</param>
    /// <param name="profile">The resolved language profile, for reading numbers the way the
    /// detecting window read them.</param>
    /// <param name="findMatches">The detector's <c>--max-chapter-number</c>-capped phrase matcher,
    /// so a cap that ruled a number out during detection rules it out here too.</param>
    /// <param name="heard">The number the detecting window read.</param>
    /// <param name="bounds">Where in the chapter sequence this announcement sits.</param>
    /// <param name="collidingNumber">The number of the chapter already marked within
    /// <see cref="DetectionTuning.CollidingChapterMarkSeconds"/> of this mark, or null where no
    /// mark sits that close; see the class remarks for why it is the one number
    /// <paramref name="bounds"/> may be overruled for.</param>
    /// <param name="phraseAbs">Absolute position of the announcement, for the log line.</param>
    /// <param name="log">This file's log sink, or null when nothing is listening.</param>
    /// <returns>The number to use instead, or null when the readings agree, cannot muster a
    /// majority, or offer nothing the sequence can hold.</returns>
    internal static int? Recount(
        IReadOnlyList<List<TranscriptSegment>> readings, LanguageProfile profile,
        Func<List<TranscriptSegment>, LanguageProfile, int?, IEnumerable<PhraseMatch>> findMatches,
        int heard, NumberBounds bounds, int? collidingNumber, double phraseAbs, Action<string>? log)
    {
        var votes = new Dictionary<int, int>();
        foreach (var reading in readings)
        {
            foreach (var match in findMatches(reading, profile, null))
                votes[match.Number] = votes.GetValueOrDefault(match.Number) + 1;
        }

        var total = votes.Values.Sum();
        if (total < RefinedNumberVoteMinimum)
            return null;

        // Ordered by count and then by number, so a tie between two equally-supported readings is
        // resolved the same way on every machine rather than by dictionary iteration order. It
        // cannot become a majority either way - the majority test below rejects both.
        var (winner, count) = votes.OrderByDescending(v => v.Value).ThenBy(v => v.Key).First();
        if (winner == heard || count * 2 <= total)
            return null;

        var duplicate = winner == collidingNumber;
        if (!bounds.Admits(winner) && !duplicate)
        {
            log?.Invoke(
                $"refinement read chapter {heard} at {FormatTimestamp(phraseAbs)} as {winner} " +
                $"({count} of {total} readings), outside {bounds.Describe()} - keeping {heard}");
            return null;
        }

        log?.Invoke(
            $"refinement read chapter {heard} at {FormatTimestamp(phraseAbs)} as {winner} " +
            $"({count} of {total} readings, framed on the announcement) - number corrected, " +
            "mark unchanged" +
            (duplicate && !bounds.Admits(winner)
                ? $"; {winner} is the chapter already marked here, so this is one announcement " +
                  "read twice"
                : ""));
        return winner;
    }
}
