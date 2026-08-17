// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Audio;
using Xunit;

namespace ABChapterize.Tests;

/// <summary>
/// The high-frequency measure that decides whether a file may be denoised at all.
/// <para>
/// It is a coarse instrument by design - see <see cref="AudioFidelity"/> for why the threshold sits
/// far from the books that motivated it rather than between them - so these tests check that it
/// separates a dull signal from a bright one and that its edges behave, not that it lands on any
/// particular value for real audio.
/// </para>
/// </summary>
public class AudioFidelityTests
{
    private const int SampleRate = 16000;
    private const int Length = SampleRate * 4;

    /// <summary>A tone at one frequency, which is all the measure looks at.</summary>
    /// <param name="hz">Frequency of the tone.</param>
    /// <param name="amplitude">Its amplitude.</param>
    private static float[] Tone(double hz, double amplitude = 0.3)
    {
        var signal = new float[Length];
        for (var i = 0; i < Length; i++)
            signal[i] = (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / SampleRate));
        return signal;
    }

    /// <summary>Sums two signals, for building one with energy in both bands.</summary>
    /// <param name="a">First signal.</param>
    /// <param name="b">Second signal, same length.</param>
    private static float[] Plus(float[] a, float[] b)
    {
        var sum = new float[a.Length];
        for (var i = 0; i < a.Length; i++)
            sum[i] = a[i] + b[i];
        return sum;
    }

    /// <summary>A signal with nothing above the split reads as dull - which is the shape of the
    /// books this whole rescue exists for.</summary>
    [Fact]
    public void AudioWithNoTreble_MeasuresFarBelowTheThreshold()
    {
        var measured = AudioFidelity.Measure(Tone(800), SampleRate);
        Assert.NotNull(measured);
        Assert.True(measured < AudioFidelity.Threshold, $"{measured}");
    }

    /// <summary>A signal carrying as much energy above the split as below reads as bright, and is
    /// refused the denoiser.</summary>
    [Fact]
    public void AudioWithPlentyOfTreble_MeasuresAboveTheThreshold()
    {
        var measured = AudioFidelity.Measure(Plus(Tone(800), Tone(6000)), SampleRate);
        Assert.NotNull(measured);
        Assert.True(measured > AudioFidelity.Threshold, $"{measured}");
    }

    /// <summary>
    /// Content above 8 kHz must not count: Whisper resamples to 16 kHz and never hears it, so a
    /// recording that keeps its very top end is not thereby easier to transcribe. Sampled at 32 kHz
    /// so there is a band above the ceiling to put anything in.
    /// </summary>
    [Fact]
    public void EnergyAboveWhisperSCeiling_DoesNotCount()
    {
        var dull = new float[SampleRate * 8];
        var withUltrasonics = new float[dull.Length];
        for (var i = 0; i < dull.Length; i++)
        {
            dull[i] = (float)(0.3 * Math.Sin(2 * Math.PI * 800 * i / 32000.0));
            withUltrasonics[i] = dull[i] + (float)(0.3 * Math.Sin(2 * Math.PI * 12000 * i / 32000.0));
        }
        Assert.Equal(
            AudioFidelity.Measure(dull, 32000)!.Value,
            AudioFidelity.Measure(withUltrasonics, 32000)!.Value, 3);
    }

    /// <summary>Digital silence says nothing about a file's fidelity, and must not be read as
    /// "dull" - a book whose excerpts all landed in pauses would otherwise be denoised on no
    /// evidence at all.</summary>
    [Fact]
    public void Silence_IsNotMeasurable()
        => Assert.Null(AudioFidelity.Measure(new float[Length], SampleRate));

    /// <summary>Too little audio to hold any frames is likewise no evidence.</summary>
    [Fact]
    public void AVeryShortExcerpt_IsNotMeasurable()
        => Assert.Null(AudioFidelity.Measure(new float[100], SampleRate));

    /// <summary>The file's figure is the median of its excerpts, which is what keeps one unusual
    /// stretch from deciding a book whose measure moves several-fold across itself.</summary>
    [Fact]
    public void TheFileSFigureIsTheMedianOfItsExcerpts()
    {
        Assert.Equal(0.03, AudioFidelity.Combine([0.01, 0.03, 0.90])!.Value, 6);
        Assert.Equal(0.02, AudioFidelity.Combine([0.01, 0.03])!.Value, 6);
    }

    /// <summary>Unmeasurable excerpts are ignored rather than counted as zero, which would drag a
    /// bright file below the threshold on the strength of its pauses.</summary>
    [Fact]
    public void UnmeasurableExcerptsAreIgnored()
    {
        Assert.Equal(0.5, AudioFidelity.Combine([null, 0.5, null])!.Value, 6);
        Assert.Null(AudioFidelity.Combine([null, null]));
    }
}
