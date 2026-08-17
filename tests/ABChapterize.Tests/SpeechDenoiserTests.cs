// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Audio;
using Xunit;

namespace ABChapterize.Tests;

/// <summary>
/// The bundled speech denoiser, run for real rather than faked.
/// <para>
/// It has to be the real model: the exported graph is only the masking network, so the STFT and
/// ISTFT around it are this codebase's own and the question these tests exist to answer is whether
/// they match the conventions the model was trained under. A wrong window shape, a symmetric Hann
/// instead of a periodic one, or zero padding instead of reflect padding all leave the graph running
/// happily on input it has never seen, and only an inference shows it.
/// </para>
/// <para>
/// The expected values come from the reference implementation this port was written against - NumPy
/// transforms plus ONNX Runtime - which was itself verified against LavaSR's PyTorch original on a
/// real 60 s excerpt at correlation 1.00000000, RMS difference some 87 dB below the signal. If the
/// bundled model is ever replaced (see THIRD-PARTY-NOTICES.md, which asks for a monthly check of the
/// upstream release), regenerate these numbers rather than relaxing the tolerances.
/// </para>
/// </summary>
public class SpeechDenoiserTests
{
    /// <summary>Two whole inference chunks, so the test covers a chunk seam rather than one buffer.</summary>
    private const int Samples = 2 * SpeechDenoiser.ChunkSamples;

    /// <summary>
    /// The signal the reference values were produced from: three tones in closed form, so both
    /// implementations build bit-identical input without a fixture file. Tones are also a sharp
    /// probe - the model is trained to keep speech, so it suppresses them almost entirely, and
    /// getting the transforms wrong shows up as that suppression failing.
    /// </summary>
    private static float[] Tones()
    {
        var signal = new float[Samples];
        for (var i = 0; i < Samples; i++)
            signal[i] = (float)(0.30 * Math.Sin(2 * Math.PI * 220.0 * i / SpeechDenoiser.SampleRate)
                              + 0.20 * Math.Sin(2 * Math.PI * 1000.0 * i / SpeechDenoiser.SampleRate)
                              + 0.10 * Math.Sin(2 * Math.PI * 3500.0 * i / SpeechDenoiser.SampleRate + 1.0));
        return signal;
    }

    /// <summary>Root mean square of a buffer, the summary the reference reports.</summary>
    /// <param name="signal">The buffer to measure.</param>
    private static double Rms(float[] signal)
    {
        var total = 0.0;
        foreach (var sample in signal)
            total += (double)sample * sample;
        return Math.Sqrt(total / signal.Length);
    }

    /// <summary>The denoised output must match the reference implementation's, which is the whole
    /// claim this port makes. Tolerances are proportional and loose enough for float32 accumulation
    /// order to differ, far tighter than any transform mistake would survive.</summary>
    [Fact]
    public void DenoisingReproducesTheReferenceImplementation()
    {
        using var denoiser = new SpeechDenoiser();
        var input = Tones();
        var output = denoiser.Denoise(input);

        Assert.Equal(input.Length, output.Length);
        Assert.Equal(0.264575910, Rms(input), 6);

        // Reference: output rms 0.005020187 - the tones are almost entirely removed.
        Assert.Equal(0.005020187, Rms(output), 4);

        // The first sample of the second chunk, where the model's own edge behaviour makes the
        // largest excursion in this signal and so the most sensitive single value to compare.
        Assert.Equal(0.056257486, output[SpeechDenoiser.ChunkSamples], 3);

        // Well inside either chunk the suppression is essentially total.
        Assert.True(Math.Abs(output[1000]) < 1e-4, $"y[1000] = {output[1000]}");
        Assert.True(Math.Abs(output[20000]) < 1e-4, $"y[20000] = {output[20000]}");
    }

    /// <summary>A buffer that is not a whole number of chunks is padded internally and cut back, so
    /// callers never have to know the chunk width; a probe window is any length at all.</summary>
    [Fact]
    public void APartialChunkKeepsItsLength()
    {
        using var denoiser = new SpeechDenoiser();
        var odd = new float[SpeechDenoiser.ChunkSamples + 1234];
        Assert.Equal(odd.Length, denoiser.Denoise(odd).Length);
    }

    /// <summary>Digital silence must survive as digital silence: the refiner measures dBFS at a
    /// finished mark, and a denoiser that dithered the quiet would move every such reading.</summary>
    [Fact]
    public void SilenceStaysSilent()
    {
        using var denoiser = new SpeechDenoiser();
        var output = denoiser.Denoise(new float[Samples]);
        Assert.All(output, sample => Assert.True(Math.Abs(sample) < 1e-6, $"{sample}"));
    }
}
