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
/// statistical. A Pass 2 probe window is tens of seconds long and the announcement can sit anywhere
/// in it, including at the very end where Whisper's segmentation is at its worst; the refinement's
/// probes are a few seconds long and framed on the announcement itself, because that is what the
/// onset search needs them to be. "Die Cyber-Brutzellen" (2026-08-01) is the case on record: the
/// announcement at 7:01:30 fell 27.3 to 43.0 s into a 44.2 s window and came out as a single
/// two-word segment reading "Kapitel 40" at p=0.59, while all ten refinement probes over the same
/// audio read "Kapitel 14", one of them at p=0.99. Nothing was re-decoded to learn that - the
/// probes had already run and their transcripts were being thrown away.
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
/// Nothing but the number is affected. The mark's position comes from the onset search and is
/// already settled by the time this runs.
/// </para>
/// </summary>
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
    /// <param name="phraseAbs">Absolute position of the announcement, for the log line.</param>
    /// <param name="log">This file's log sink, or null when nothing is listening.</param>
    /// <returns>The number to use instead, or null when the readings agree, cannot muster a
    /// majority, or offer nothing the sequence can hold.</returns>
    internal static int? Recount(
        IReadOnlyList<List<TranscriptSegment>> readings, LanguageProfile profile,
        Func<List<TranscriptSegment>, LanguageProfile, int?, IEnumerable<PhraseMatch>> findMatches,
        int heard, NumberBounds bounds, double phraseAbs, Action<string>? log)
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

        if (!bounds.Admits(winner))
        {
            log?.Invoke(
                $"the mark refinement read chapter {heard} at {FormatTimestamp(phraseAbs)} as " +
                $"{winner} ({count} of {total} probes), but that does not fit the sequence " +
                $"{bounds.Describe()} - leaving it at {heard}");
            return null;
        }

        log?.Invoke(
            $"the mark refinement read chapter {heard} at {FormatTimestamp(phraseAbs)} as {winner} " +
            $"({count} of {total} probes, from windows framed on the announcement) - " +
            "correcting the number, the mark stays where it is");
        return winner;
    }
}
