// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Audio;
using static ABChapterize.Detection.DetectionTuning;

namespace ABChapterize.Detection;

/// <summary>
/// Measures how loud the audio actually is at a finished chapter mark, for the --verbose mark
/// log. Purely diagnostic: nothing in detection or placement reads this back, it exists so a
/// reported mark can be judged at a glance - a figure near digital silence says the mark landed in
/// a real pause, a loud one says it landed mid-word or inside music, which is exactly the symptom
/// that otherwise only shows up by listening to the finished file.
/// </summary>
internal static class MarkLoudness
{
    /// <summary>
    /// RMS level of the <see cref="MarkLoudnessWindowSeconds"/> of audio a player would hear first
    /// when seeking to <paramref name="at"/>, in dBFS (0 dBFS being a full-scale signal, so every
    /// real measurement is negative). Deliberately forward-looking rather than centered on the
    /// mark: what matters is how playback from here starts, not what preceded it.
    /// <para>
    /// Costs one small decode per mark, so callers should only ask when --verbose actually wants
    /// the figure. Digital silence (an all-zero window, as an unscripted test decode also yields)
    /// has no finite dBFS value and reports <see cref="double.NegativeInfinity"/>, which <see
    /// cref="Format"/> renders as "-inf".
    /// </para>
    /// </summary>
    /// <param name="audio">Audio source to decode with.</param>
    /// <param name="file">Path of the audio file.</param>
    /// <param name="at">Absolute position of the finished mark.</param>
    /// <param name="inputDecoder">Explicit input decoder to force, or null.</param>
    /// <param name="ct">Cancellation token.</param>
    internal static async Task<double> MeasureDbfsAsync(
        IAudioSource audio, string file, double at, string? inputDecoder, CancellationToken ct)
    {
        var samples = await audio.DecodePcmAsync(
            file, Math.Max(0, at), MarkLoudnessWindowSeconds, inputDecoder, ct);
        if (samples.Length == 0)
            return double.NegativeInfinity;

        // Accumulated in double regardless of the float samples: a window of 16 kHz audio is
        // thousands of squares, enough for single-precision rounding to drift visibly.
        var sumOfSquares = 0.0;
        foreach (var sample in samples)
            sumOfSquares += (double)sample * sample;
        var rms = Math.Sqrt(sumOfSquares / samples.Length);
        return rms > 0 ? 20 * Math.Log10(rms) : double.NegativeInfinity;
    }

    /// <summary>Renders a <see cref="MeasureDbfsAsync"/> reading for the log, with digital silence
    /// spelled "-inf" rather than as a formatted infinity.</summary>
    /// <param name="dbfs">The measured level.</param>
    internal static string Format(double dbfs)
        => double.IsNegativeInfinity(dbfs) ? "-inf dBFS" : $"{dbfs:0.0} dBFS";
}
