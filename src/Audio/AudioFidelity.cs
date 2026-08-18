// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

namespace ABChapterize.Audio;

/// <summary>
/// How much high frequency a book's speech has kept, as a rough stand-in for how hard the recognizer
/// will find it. Used for one decision only: whether a file may be denoised at all.
/// <para>
/// The measure is the power above <see cref="SplitHz"/> over the power in the speech band below it,
/// taken over the loudest half of frames so it describes speech rather than the pauses between it,
/// and looking no higher than <see cref="CeilingHz"/> because that is the whole of what Whisper hears
/// after its own resample to 16 kHz.
/// </para>
/// <para>
/// What was ruled out first, all measured over the sixteen-book corpus: container bitrate and sample
/// rate say nothing (the darkest book sits mid-pack at 67 kbps while a 51 kbps one is fine), the
/// room-tone and speech levels Analyze already computes say nothing (the darkest book's -96.3/-22.1
/// is a twin of a perfectly clean book's -96.8/-25.1), and neither does Whisper's own confidence
/// (the corpus's lowest-confidence book is a well-behaved one). This measure does track a listener's
/// judgement - ranked against by-ear labels over the corpus it agrees at Spearman 0.90, and
/// reproduces the ordering of the middle group exactly.
/// </para>
/// <para>
/// It is nonetheless a coarse instrument, and the threshold reflects that. Within a single book the
/// value wanders by 3.2x to 24.1x between excerpts, and re-sampling a book at different positions
/// reshuffles the bottom of the corpus ranking - only membership of a broad "dark" group survives.
/// So <see cref="Threshold"/> sits far above the books that motivated the work rather than between
/// them, which is also the right way round for the risk: this gate only ever <em>permits</em> a
/// re-read that still has to be triggered by a garbled announcement, so admitting a clean book costs
/// nothing, while excluding a dark one silently loses a chapter.
/// </para>
/// </summary>
public static class AudioFidelity
{
    /// <summary>Below this ratio a file may be denoised. Chosen to exclude only the clearly bright
    /// books - on the corpus, the five whose speech carries the most treble - and to keep every book
    /// with any claim to being dark, the darkest of which measure around 0.001. A tighter cut-off
    /// was rejected: the nearest excluded book would have sat 1.3x away while the measure itself
    /// moves 5x inside one book.</summary>
    public const double Threshold = 0.02;

    /// <summary>Where the speech band is split. Lossy coding and a dull microphone both shed the
    /// treble above this first, and it is the frequency the corpus separates on.</summary>
    private const double SplitHz = 4000;

    /// <summary>Highest frequency considered, being all Whisper hears after resampling to 16 kHz - a
    /// lowpass above this is inaudible to it and must not count against a file.</summary>
    private const double CeilingHz = 8000;

    /// <summary>Lowest frequency considered, keeping rumble and DC out of the reference band.</summary>
    private const double FloorHz = 300;

    /// <summary>Transform size, a compromise between frequency resolution and having many frames to
    /// take a median over.</summary>
    private const int FrameSize = 512;

    /// <summary>
    /// Measures one excerpt, or null when it holds too little audio to say anything.
    /// </summary>
    /// <param name="samples">Mono samples at <paramref name="sampleRate"/>.</param>
    /// <param name="sampleRate">Sample rate of <paramref name="samples"/>.</param>
    public static double? Measure(ReadOnlySpan<float> samples, int sampleRate)
    {
        var frames = samples.Length / FrameSize;
        if (frames < 10)
            return null;

        // Frames are ranked by energy and only the louder half kept, so pauses cannot dominate a
        // measurement that is meant to describe the speech.
        var energies = new double[frames];
        for (var f = 0; f < frames; f++)
        {
            var total = 0.0;
            for (var i = 0; i < FrameSize; i++)
            {
                var sample = (double)samples[f * FrameSize + i];
                total += sample * sample;
            }
            energies[f] = total;
        }
        var ranked = (double[])energies.Clone();
        Array.Sort(ranked);
        var cutoff = ranked[frames / 2];
        if (cutoff <= 0)
            return null;

        var window = new double[FrameSize];
        for (var i = 0; i < FrameSize; i++)
            window[i] = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / (FrameSize - 1));

        var bins = FrameSize / 2 + 1;
        var spectrum = new double[bins];
        var counted = 0;
        var real = new double[FrameSize];
        var imaginary = new double[FrameSize];
        for (var f = 0; f < frames; f++)
        {
            if (energies[f] < cutoff)
                continue;
            for (var i = 0; i < FrameSize; i++)
            {
                real[i] = samples[f * FrameSize + i] * window[i];
                imaginary[i] = 0.0;
            }
            Dft(real, imaginary);
            for (var bin = 0; bin < bins; bin++)
                spectrum[bin] += Math.Sqrt(real[bin] * real[bin] + imaginary[bin] * imaginary[bin]);
            counted++;
        }
        if (counted == 0)
            return null;

        double high = 0, low = 0;
        for (var bin = 0; bin < bins; bin++)
        {
            var hz = (double)bin * sampleRate / FrameSize;
            var power = spectrum[bin] / counted;
            power *= power;
            if (hz >= SplitHz && hz <= CeilingHz)
                high += power;
            else if (hz >= FloorHz && hz < SplitHz)
                low += power;
        }
        return low > 0 ? high / low : null;
    }

    /// <summary>
    /// The value for a whole file from several excerpts: their median, which is what makes the
    /// figure usable at all given how far single excerpts of one book scatter. Null when no excerpt
    /// could be measured.
    /// </summary>
    /// <param name="excerpts">Per-excerpt measurements, nulls included and ignored.</param>
    public static double? Combine(IEnumerable<double?> excerpts)
    {
        var values = excerpts.Where(v => v.HasValue).Select(v => v!.Value).OrderBy(v => v).ToList();
        if (values.Count == 0)
            return null;
        return values.Count % 2 == 1
            ? values[values.Count / 2]
            : 0.5 * (values[values.Count / 2 - 1] + values[values.Count / 2]);
    }

    /// <summary>
    /// Plain O(n²) DFT over one frame. Deliberately not the FFT
    /// <see cref="SpeechDenoiser"/> carries, although this is not cheap: with
    /// <see cref="ABChapterize.Detection.DetectionTuning.FidelityExcerpts"/> excerpts of
    /// <see cref="ABChapterize.Detection.DetectionTuning.FidelityExcerptSeconds"/> at 16 kHz it is
    /// around 3,700 transforms per gated file - the louder half of each excerpt's 937 frames -
    /// which is the bulk of the
    /// ~8 s that measurement takes (build-339 corpus run, 2026-08-17). It stays here anyway because
    /// the price is paid at most once per file, and only by a file whose transcripts already
    /// suggest it needs denoising, while sharing the denoiser's transform would mean lifting it out
    /// and giving it a home of its own to serve a caller that gains nothing from it.
    /// </summary>
    /// <param name="real">Real parts, overwritten with the transform's.</param>
    /// <param name="imaginary">Imaginary parts, overwritten with the transform's.</param>
    private static void Dft(double[] real, double[] imaginary)
    {
        var n = real.Length;
        var inputReal = (double[])real.Clone();
        for (var k = 0; k <= n / 2; k++)
        {
            double sumReal = 0, sumImaginary = 0;
            for (var t = 0; t < n; t++)
            {
                var angle = -2.0 * Math.PI * k * t / n;
                sumReal += inputReal[t] * Math.Cos(angle);
                sumImaginary += inputReal[t] * Math.Sin(angle);
            }
            real[k] = sumReal;
            imaginary[k] = sumImaginary;
        }
    }
}
