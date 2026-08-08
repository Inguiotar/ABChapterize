// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Audio;
using static ABChapterize.Detection.DetectionTuning;

namespace ABChapterize.Detection;

/// <summary>One stretch of audible non-speech: sound the VAD did not call speech and
/// silencedetect did not call silence, which on an audiobook is music.</summary>
/// <param name="StartSeconds">Where the audible non-speech begins.</param>
/// <param name="EndSeconds">Where it ends.</param>
internal readonly record struct Jingle(double StartSeconds, double EndSeconds)
{
    /// <summary>How long the stretch runs, the figure <see cref="JingleCensus"/> reports on.</summary>
    internal double LengthSeconds => EndSeconds - StartSeconds;
}

/// <summary>
/// Counts and measures a file's jingles from Pass 1's two signals, for the --verbose log. Purely
/// diagnostic: nothing in detection or placement reads a census back - it answers the question
/// neither Pass 1 line answers on its own, namely how much of what VAD called non-speech is music
/// rather than plain silence, which is what decides whether a book's chapter openings need
/// --mark-before-jingle at all and roughly what --max-jingle-length they call for.
/// <para>
/// A <see cref="NonSpeechRegion"/> is only the raw material: an ordinary in-narration pause is one
/// too, and a real jingle's region usually opens with a lead-in hush before the music starts. So a
/// jingle here is what is left of a region once every silence inside it is cut out, kept when at
/// least <see cref="MinJingleObservationSeconds"/> of sound survives.
/// </para>
/// </summary>
internal static class JingleCensus
{
    /// <summary>
    /// Every audible stretch of at least <see cref="MinJingleObservationSeconds"/> inside the VAD's
    /// non-speech regions, in file order.
    /// <para>
    /// Two properties of the inputs are load-bearing and neither is an approximation worth
    /// "fixing". Silences below <see cref="MinStoredSilenceSeconds"/> were never recorded, so a
    /// beat of quiet inside a music sting does not split one jingle into two - which is what makes
    /// the measured lengths comparable to the ones --max-jingle-length reasons about. And a region
    /// carries the short speech blips <see cref="JingleGeometry.ComputeNonSpeechRegions"/> merged
    /// across, deliberately: those are VAD misfiring on a vocal or rhythmic transient in the music,
    /// so counting them as part of the jingle is the correct reading rather than a leak.
    /// </para>
    /// </summary>
    /// <param name="regions">The merged VAD non-speech regions; empty when the pre-pass did not run,
    /// which yields an empty census.</param>
    /// <param name="silences">Every silence Pass 1 stored, down to
    /// <see cref="MinStoredSilenceSeconds"/> - the whole list, not Pass 2's --min-silence-length
    /// subset, since a half-second hush is still not music.</param>
    internal static List<Jingle> Measure(List<NonSpeechRegion> regions, List<Silence> silences)
    {
        var jingles = new List<Jingle>();
        foreach (var region in regions)
        {
            // Walks the region left to right, emitting whatever lies between the silences that
            // overlap it. Ordering by start rather than trusting the scan's own order costs
            // nothing here and keeps the cursor monotonic; overlapping silences collapse into one
            // because the cursor only ever moves forward.
            var cursor = region.StartSeconds;
            foreach (var silence in silences
                         .Where(s => s.EndSeconds > region.StartSeconds && s.StartSeconds < region.EndSeconds)
                         .OrderBy(s => s.StartSeconds))
            {
                AddIfJingle(jingles, cursor, Math.Min(silence.StartSeconds, region.EndSeconds));
                cursor = Math.Max(cursor, silence.EndSeconds);
            }
            AddIfJingle(jingles, cursor, region.EndSeconds);
        }
        return jingles;
    }

    /// <summary>Renders a census as the one --verbose line it is worth: how many jingles the file
    /// has and how long they run. The spread matters as much as the count - a book whose jingles
    /// are all within a second of each other plays one sting, while a wide spread means the music
    /// varies per chapter and --max-jingle-length has to cover the longest.</summary>
    /// <param name="jingles">What <see cref="Measure"/> found.</param>
    internal static string Describe(List<Jingle> jingles)
        => $"{jingles.Count} jingle(s) of >= {MinJingleObservationSeconds:0.#} s found" +
           (jingles.Count > 0
               ? $" - shortest {jingles.Min(j => j.LengthSeconds):0.00} s, " +
                 $"longest {jingles.Max(j => j.LengthSeconds):0.00} s, " +
                 $"average {jingles.Average(j => j.LengthSeconds):0.00} s"
               : "");

    /// <summary>Adds one gap between silences to the census, unless it is too short to be a jingle
    /// or empty (a silence covering the rest of the region leaves a cursor past its end).</summary>
    /// <param name="jingles">The census being built.</param>
    /// <param name="from">Start of the audible stretch.</param>
    /// <param name="to">End of the audible stretch.</param>
    private static void AddIfJingle(List<Jingle> jingles, double from, double to)
    {
        if (to - from >= MinJingleObservationSeconds)
            jingles.Add(new Jingle(from, to));
    }
}
