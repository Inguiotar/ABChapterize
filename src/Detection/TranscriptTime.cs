// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Transcription;

namespace ABChapterize.Detection;

/// <summary>Moves transcript segments between the two time bases the detection passes work in:
/// window-relative (what Whisper emits and <see cref="PhraseMatching.FindPhraseMatches"/> expects)
/// and absolute file time (how <see cref="RegionProber"/>'s overlap cache and every mark position
/// are expressed).</summary>
internal static class TranscriptTime
{
    /// <summary>
    /// Returns copies of <paramref name="segments"/> with every timestamp shifted by
    /// <paramref name="delta"/> seconds: a positive delta of the window start makes segments
    /// absolute, a negative delta of it makes them window-relative again.
    /// </summary>
    /// <param name="segments">The segments to shift.</param>
    /// <param name="delta">Seconds to add to each segment's start and end time.</param>
    internal static List<TranscriptSegment> ShiftSegments(IEnumerable<TranscriptSegment> segments, double delta) =>
        segments.Select(s => s with
        {
            StartSeconds = s.StartSeconds + delta,
            EndSeconds = s.EndSeconds + delta,
        }).ToList();
}
