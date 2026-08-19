// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Audio;
using static ABChapterize.Detection.DetectionTuning;

namespace ABChapterize.Detection;

/// <summary>
/// Picks the level below which Analyze's silence scan should count audio as silence, by looking at
/// what the file actually sounds like instead of assuming <see cref="DefaultSilenceNoiseDb"/>.
/// </summary>
/// <remarks>
/// <para>
/// The threshold has one job: separate the pauses from the narration. Every audiobook has a wide
/// gap between the two - room tone somewhere below -45 dBFS, sustained speech somewhere above -25 -
/// and a fixed -35 dBFS lands in the middle of it on every normal master. What this exists for is
/// the master that is not normal: one recorded with audible hiss (nothing ever drops below -35, so
/// no silence is found and no chapter is ever probed for) or mastered so quietly that the narration
/// itself sits under -35 (every pause between words reads as silence, and Analyze returns thousands
/// of candidates). Both used to be a manual <c>--min-silence-length</c> fight that could not
/// actually win, the problem being the level rather than the length.
/// </para>
/// <para>
/// So this does not re-derive the threshold from scratch - it keeps the default and only moves it
/// when the default would fall outside the file's own gap. On the reference corpus that means every
/// book keeps exactly the default and the automatic mode changes nothing at all, which is the
/// property that made it safe to turn on by default. See <see cref="DefaultSilenceNoiseDb"/> for the
/// measurements and the two headroom constants.
/// <include file='../../notes/Detection/SilenceThresholdProbe.xml' path='doc/member[@name="SilenceThresholdProbe"]/*' />
/// </para>
/// </remarks>
internal static class SilenceThresholdProbe
{
    /// <summary>What a measurement concluded.</summary>
    /// <param name="ThresholdDb">The silence threshold to use, in dBFS.</param>
    /// <param name="FloorDb">The measured room tone, or negative infinity for a file whose quiet
    /// stretches are digitally silent.</param>
    /// <param name="SpeechDb">The measured level of sustained speech.</param>
    /// <param name="Adjusted">Whether the measurement moved the threshold off
    /// <see cref="DefaultSilenceNoiseDb"/>, which is the unusual case worth saying out loud.</param>
    internal readonly record struct Reading(
        double ThresholdDb, double FloorDb, double SpeechDb, bool Adjusted);

    /// <summary>The reading for a file nothing could be measured from, which keeps the default
    /// rather than guessing.</summary>
    internal static Reading Unmeasured { get; } =
        new(DefaultSilenceNoiseDb, double.NegativeInfinity, double.NegativeInfinity, Adjusted: false);

    /// <summary>
    /// Decodes <see cref="NoiseProbeExcerpts"/> short excerpts spread across the file and works out
    /// the threshold from their frame levels.
    /// </summary>
    /// <param name="audio">Audio source the excerpts are decoded from.</param>
    /// <param name="file">Path of the audio file.</param>
    /// <param name="durationSeconds">The file's total play time.</param>
    /// <param name="inputDecoder">Explicit input decoder to force, or null.</param>
    /// <param name="ct">Cancellation token.</param>
    internal static async Task<Reading> MeasureAsync(
        IAudioSource audio, string file, double durationSeconds, string? inputDecoder,
        CancellationToken ct)
    {
        var levels = new List<double>();
        foreach (var start in ExcerptStarts(durationSeconds))
        {
            var samples = await audio.DecodePcmAsync(
                file, start, NoiseProbeExcerptSeconds, inputDecoder, ct);
            AddFrameLevels(samples, levels);
        }
        return FromFrameLevels(levels);
    }

    /// <summary>
    /// Where the excerpts are taken from: evenly spaced between the 5% and 95% marks of the play
    /// time, so a label jingle at the front and the closing credits at the back - neither of which
    /// sounds like the book - stay out of the measurement. A file too short to hold them all
    /// collapses to however many distinct positions there are, down to a single one at 0.
    /// </summary>
    /// <param name="durationSeconds">The file's total play time.</param>
    internal static IEnumerable<double> ExcerptStarts(double durationSeconds)
    {
        var first = durationSeconds * 0.05;
        var span = Math.Max(0, durationSeconds * 0.90 - NoiseProbeExcerptSeconds);
        double? previous = null;
        for (var k = 0; k < NoiseProbeExcerpts; k++)
        {
            var start = Math.Max(0, first + span * k / Math.Max(1, NoiseProbeExcerpts - 1));
            // A file shorter than one excerpt yields the same position every time; handing ffmpeg
            // the same seek eight times would pay for eight identical decodes.
            if (previous is { } last && start - last < NoiseProbeFrameSeconds)
                continue;
            previous = start;
            yield return start;
        }
    }

    /// <summary>
    /// Appends one excerpt's per-frame RMS levels, in dBFS, to <paramref name="levels"/>. A
    /// digitally silent frame has no finite level and contributes negative infinity, which the
    /// percentiles below handle without special-casing: it sorts where it belongs, at the bottom.
    /// </summary>
    /// <param name="samples">The excerpt's 16 kHz mono samples.</param>
    /// <param name="levels">The list being accumulated across excerpts.</param>
    internal static void AddFrameLevels(float[] samples, List<double> levels)
    {
        var frame = (int)(FfmpegClient.SampleRate * NoiseProbeFrameSeconds);
        for (var start = 0; start + frame <= samples.Length; start += frame)
        {
            // Accumulated in double regardless of the float samples, as MarkLoudness does and for
            // the same reason: hundreds of squares are enough for single-precision drift to show.
            var sumOfSquares = 0.0;
            for (var i = start; i < start + frame; i++)
                sumOfSquares += (double)samples[i] * samples[i];
            var rms = Math.Sqrt(sumOfSquares / frame);
            levels.Add(rms > 0 ? 20 * Math.Log10(rms) : double.NegativeInfinity);
        }
    }

    /// <summary>
    /// The decision itself, kept separate from the decoding so it can be exercised against a level
    /// histogram rather than against an audiobook: keep the default threshold unless it fails to
    /// clear the room tone by <see cref="NoiseFloorHeadroomDb"/> or to stay
    /// <see cref="SpeechHeadroomDb"/> under sustained speech, and move it just far enough to
    /// satisfy whichever bound it broke.
    /// </summary>
    /// <param name="levels">Every frame's level, in any order; sorted in place.</param>
    /// <remarks>
    /// A master with less than the two headrooms' worth of range between its room tone and its
    /// narration has no gap for either bound to be satisfied in. That is a real recording (heavy
    /// compression, or hiss riding right under the voice) rather than a measurement error, so
    /// rather than pick a side, the threshold goes halfway between the two - the point furthest
    /// from both mistakes.
    /// </remarks>
    internal static Reading FromFrameLevels(List<double> levels)
    {
        levels.Sort();
        var speech = Percentile(levels, NoiseProbeSpeechPercentile);
        // Nothing audible in any excerpt - a file of pure digital silence, or a decode that yielded
        // nothing. There is no gap to place a threshold in, so the default stands.
        if (!double.IsFinite(speech))
            return Unmeasured;
        var floor = Percentile(levels, NoiseProbeFloorPercentile);

        var lowest = floor + NoiseFloorHeadroomDb;
        var highest = speech - SpeechHeadroomDb;
        var threshold = lowest > highest
            ? (floor + speech) / 2
            : Math.Clamp(DefaultSilenceNoiseDb, lowest, highest);
        threshold = Math.Clamp(threshold, MinAutoSilenceNoiseDb, MaxAutoSilenceNoiseDb);
        return new Reading(threshold, floor, speech, threshold != DefaultSilenceNoiseDb);
    }

    /// <summary>Nearest-rank percentile of an already sorted list.</summary>
    /// <param name="sorted">The values, ascending.</param>
    /// <param name="percentile">Which percentile to take, 0 to 100.</param>
    /// <returns>The value, or negative infinity for an empty list - which
    /// <see cref="FromFrameLevels"/> reads as "nothing measurable here".</returns>
    internal static double Percentile(List<double> sorted, int percentile)
    {
        if (sorted.Count == 0)
            return double.NegativeInfinity;
        var index = (int)Math.Round(percentile / 100.0 * (sorted.Count - 1));
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }

    /// <summary>The one-line note Analyze logs about the threshold it is scanning with.</summary>
    /// <param name="reading">What <see cref="MeasureAsync"/> concluded, or null when
    /// <c>--noise-floor</c> named a level outright.</param>
    /// <param name="thresholdDb">The threshold actually in use.</param>
    internal static string Describe(Reading? reading, double thresholdDb)
    {
        if (reading is not { } measured)
            return $"silence threshold {thresholdDb:0.#} dBFS";
        var levels = double.IsFinite(measured.FloorDb)
            ? $"room tone {measured.FloorDb:0.#}"
            : "room tone silent";
        // Speech gets the same treatment as the floor rather than being printed raw: Unmeasured
        // carries negative infinity in both fields, and "speech -Infinity dBFS" is not a level.
        var speech = double.IsFinite(measured.SpeechDb)
            ? $"speech {measured.SpeechDb:0.#} dBFS"
            : "nothing audible";
        return $"silence threshold {thresholdDb:0.#} dBFS (auto: {levels}, {speech}" +
               (measured.Adjusted ? ", adjusted from the default" : "") + ")";
    }
}
