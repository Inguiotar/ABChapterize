// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Microsoft.ML.OnnxRuntime;
using System.Reflection;
using ABChapterize.Errors;
using ABChapterize.Onnx;

namespace ABChapterize.Audio;

/// <summary>
/// LavaSR's speech denoiser over one stretch of 16 kHz mono audio, used to give the recognizer a
/// second, cleaner look at a probe window whose announcement it garbled.
/// <para>
/// What it is for, measured: on "De vandrande djäknarne" the chapter 1 announcement is a coin flip
/// for ggml-small, recovered by only <b>10 of 22</b> window framings 20 ms apart - the same audio
/// coming back as "1. Kapitlet – Gjäknupptåg", "1. KAPITLET", "1. KAPITLÄT", "1. KJÄKNUGTÅG" or
/// "1. Jäknuktåg" depending on where the window opened, at p=0.45-0.65 throughout. Denoised first,
/// every one of the 22 reads "1. Kapitlet", and the announcement lands in a segment of its own with
/// the chapter title split off - the clean segmentation only the far bigger models produced on the
/// raw audio (medium and large-v3-turbo, at 2-3x the decode cost of a denoise plus a re-read).
/// </para>
/// <para>
/// Only the denoiser is bundled, not LavaSR's bandwidth extension: that stage reconstructs content
/// above the merge cutoff, Whisper resamples to 16 kHz and hears nothing above 8 kHz, and
/// denoise-only was measured to give the identical 22 of 22 at better than twice the speed. It is
/// also the half that can be exported at all - the extension carries a complex ISTFT and an
/// FFT-domain merge, and ONNX has an STFT operator but no inverse.
/// </para>
/// <para>
/// The exported graph is the masking network alone (spectrogram in, masked spectrogram out), so the
/// transforms below are ours and must match the conventions the model was trained under. Verified
/// against the PyTorch original over a real 60 s excerpt: correlation 1.00000000, RMS difference
/// 2.2e-06 against a signal RMS of 4.8e-02, i.e. some 87 dB down.
/// </para>
/// </summary>
public sealed class SpeechDenoiser : IDisposable
{
    /// <summary>Resource name of the embedded model; see the csproj's EmbeddedResource entry.</summary>
    private const string ResourceName = "ABChapterize.lavasr_denoiser.onnx";

    /// <summary>The only sample rate the model accepts, and what the pipeline already decodes for
    /// Whisper and the VAD - so nothing resamples on this path.</summary>
    public const int SampleRate = 16000;

    /// <summary>Transform size, hop and window, fixed by how the model was trained.</summary>
    private const int FftSize = 512;
    private const int HopSize = 256;

    /// <summary>Spectrogram bins per frame, i.e. the one-sided transform's width.</summary>
    private const int Bins = FftSize / 2 + 1;

    /// <summary>Frames per inference. The exported graph fixes this ("fixed63" in its file name),
    /// which is what makes <see cref="ChunkSamples"/> the unit this class works in.</summary>
    private const int Frames = 63;

    /// <summary>Samples one inference covers: 15872, i.e. 0.992 s. Chosen by the graph rather than
    /// by us - <c>(Frames - 1) * HopSize</c> is exactly the length whose centred transform yields
    /// <see cref="Frames"/> frames.</summary>
    internal const int ChunkSamples = (Frames - 1) * HopSize;

    private readonly InferenceSession _session;

    /// <summary>Held for the session's lifetime rather than made per inference: it owns a native
    /// handle, and a denoiser rescue over one probe window runs twenty-odd inferences.
    /// <see cref="Vad.SileroWorker"/> does the same with its own, for the same reason.</summary>
    private readonly RunOptions _runOptions = new();

    private readonly string _inputName;
    private readonly float[] _window = Hann(FftSize);

    /// <summary>Points OnnxRuntime's native lookup at the published layout before anything here
    /// touches it. The static constructor rather than the instance one, for the ordering reason
    /// <see cref="Vad.SileroWorker"/>'s own records: a type initializer is the only thing guaranteed
    /// to run before a field initializer, and a field that reaches OnnxRuntime is one edit away.</summary>
    static SpeechDenoiser() => OnnxRuntimeNative.EnsureRegistered();

    /// <summary>Loads the bundled model into a session of its own.</summary>
    /// <exception cref="AppError">Thrown when the embedded resource is missing.</exception>
    public SpeechDenoiser()
    {
        // One thread, like the VAD workers: this runs inside a per-file pipeline that already has
        // the machine busy, and the model is far too small to repay waking a pool per inference.
        using var options = new SessionOptions
        {
            IntraOpNumThreads = 1,
            InterOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
        };
        _session = new InferenceSession(LoadModelBytes(), options);
        _inputName = _session.InputNames[0];
    }

    /// <summary>Reads the embedded denoiser model into memory.</summary>
    /// <exception cref="AppError">Thrown when the embedded resource is missing.</exception>
    private static byte[] LoadModelBytes()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(ResourceName)
            ?? throw new AppError($"Embedded speech denoiser model resource \"{ResourceName}\" not found.");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// The periodic Hann window the model was trained with. Periodic rather than symmetric on
    /// purpose: it is what torch's <c>hann_window</c> produces by default, and the symmetric variant
    /// most signal libraries default to is a half-bin different - a smear the network never saw.
    /// </summary>
    /// <param name="size">Window length in samples.</param>
    private static float[] Hann(int size)
    {
        var window = new float[size];
        for (var i = 0; i < size; i++)
            window[i] = (float)(0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / size));
        return window;
    }

    /// <summary>
    /// Denoises a stretch of 16 kHz mono audio, returning a buffer of exactly the same length.
    /// <para>
    /// Chunked to the graph's fixed width, with a short tail zero-padded and cut back afterwards.
    /// Chunks are processed independently, exactly as LavaSR's own batching does (at 1.28 s rather
    /// than this 0.992 s), so the seam behaviour is the upstream model's rather than something
    /// introduced here.
    /// </para>
    /// </summary>
    /// <param name="samples">Mono samples at <see cref="SampleRate"/>.</param>
    /// <returns>The denoised samples, same length as <paramref name="samples"/>.</returns>
    public float[] Denoise(ReadOnlySpan<float> samples)
    {
        var output = new float[samples.Length];
        var chunk = new float[ChunkSamples];
        var spectrogram = new float[2 * Frames * Bins];

        for (var offset = 0; offset < samples.Length; offset += ChunkSamples)
        {
            var take = Math.Min(ChunkSamples, samples.Length - offset);
            samples.Slice(offset, take).CopyTo(chunk);
            if (take < ChunkSamples)
                Array.Clear(chunk, take, ChunkSamples - take);

            Transform(chunk, spectrogram);
            using var input = OrtValue.CreateTensorValueFromMemory(spectrogram, [1, 2, Frames, Bins]);
            using var results = _session.Run(
                _runOptions, [_inputName], [input], _session.OutputNames);
            var enhanced = results[0].GetTensorDataAsSpan<float>();

            InverseTransform(enhanced, chunk);
            chunk.AsSpan(0, take).CopyTo(output.AsSpan(offset, take));
        }
        return output;
    }

    /// <summary>
    /// One chunk's centred one-sided STFT, written as the model expects it: real parts of every
    /// frame, then imaginary parts, each frame-major.
    /// <para>
    /// "Centred" means the signal is reflect-padded by half a transform at both ends, which is what
    /// makes the frame count come out at <see cref="Frames"/> and what torch does by default.
    /// </para>
    /// </summary>
    /// <param name="chunk">Exactly <see cref="ChunkSamples"/> samples.</param>
    /// <param name="destination">Receives 2 x <see cref="Frames"/> x <see cref="Bins"/> floats.</param>
    private void Transform(float[] chunk, float[] destination)
    {
        var padded = ReflectPad(chunk, FftSize / 2);
        var real = new double[FftSize];
        var imaginary = new double[FftSize];

        for (var frame = 0; frame < Frames; frame++)
        {
            for (var i = 0; i < FftSize; i++)
            {
                real[i] = padded[frame * HopSize + i] * _window[i];
                imaginary[i] = 0.0;
            }
            Fft(real, imaginary, inverse: false);
            for (var bin = 0; bin < Bins; bin++)
            {
                destination[frame * Bins + bin] = (float)real[bin];
                destination[Frames * Bins + frame * Bins + bin] = (float)imaginary[bin];
            }
        }
    }

    /// <summary>
    /// The inverse of <see cref="Transform"/>: overlap-add, divided by the overlap-added window
    /// energy so the windowing cancels, then the centring padding cut back off.
    /// </summary>
    /// <param name="spectrogram">The model's output, laid out as <see cref="Transform"/> writes it.</param>
    /// <param name="destination">Receives <see cref="ChunkSamples"/> samples.</param>
    private void InverseTransform(ReadOnlySpan<float> spectrogram, float[] destination)
    {
        var length = (Frames - 1) * HopSize + FftSize;
        var accumulated = new double[length];
        var weight = new double[length];
        var real = new double[FftSize];
        var imaginary = new double[FftSize];

        for (var frame = 0; frame < Frames; frame++)
        {
            // Rebuild the full spectrum from its one-sided half: bin k and N-k are conjugates.
            for (var bin = 0; bin < Bins; bin++)
            {
                real[bin] = spectrogram[frame * Bins + bin];
                imaginary[bin] = spectrogram[Frames * Bins + frame * Bins + bin];
            }
            for (var bin = Bins; bin < FftSize; bin++)
            {
                real[bin] = real[FftSize - bin];
                imaginary[bin] = -imaginary[FftSize - bin];
            }
            Fft(real, imaginary, inverse: true);

            for (var i = 0; i < FftSize; i++)
            {
                accumulated[frame * HopSize + i] += real[i] * _window[i];
                weight[frame * HopSize + i] += (double)_window[i] * _window[i];
            }
        }

        var start = FftSize / 2;
        for (var i = 0; i < ChunkSamples; i++)
        {
            var w = weight[start + i];
            // The taper at either end is the only place the window energy can vanish; torch guards
            // it the same way rather than dividing by something arbitrarily small.
            destination[i] = w > 1e-11 ? (float)(accumulated[start + i] / w) : 0f;
        }
    }

    /// <summary>Reflect-pads a signal at both ends, mirroring about the first and last sample
    /// without repeating them - the padding torch's centred STFT applies.</summary>
    /// <param name="signal">The signal to pad.</param>
    /// <param name="pad">How many samples to add at each end.</param>
    private static float[] ReflectPad(float[] signal, int pad)
    {
        var padded = new float[signal.Length + 2 * pad];
        signal.CopyTo(padded, pad);
        for (var i = 0; i < pad; i++)
        {
            padded[pad - 1 - i] = signal[i + 1];
            padded[pad + signal.Length + i] = signal[signal.Length - 2 - i];
        }
        return padded;
    }

    /// <summary>
    /// In-place radix-2 FFT over <see cref="FftSize"/> points, which is a power of two. Written out
    /// rather than taken from a package: it is thirty lines, this is the only transform in the
    /// codebase, and a dependency for it would have to be published alongside the executable.
    /// </summary>
    /// <param name="real">Real parts, overwritten with the transform's.</param>
    /// <param name="imaginary">Imaginary parts, overwritten with the transform's.</param>
    /// <param name="inverse">Whether to compute the inverse transform, scaling by 1/N.</param>
    private static void Fft(double[] real, double[] imaginary, bool inverse)
    {
        var n = real.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
                j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imaginary[i], imaginary[j]) = (imaginary[j], imaginary[i]);
            }
        }

        for (var length = 2; length <= n; length <<= 1)
        {
            var angle = 2 * Math.PI / length * (inverse ? 1 : -1);
            var stepReal = Math.Cos(angle);
            var stepImaginary = Math.Sin(angle);
            for (var i = 0; i < n; i += length)
            {
                double wReal = 1, wImaginary = 0;
                for (var j = 0; j < length / 2; j++)
                {
                    var uReal = real[i + j];
                    var uImaginary = imaginary[i + j];
                    var vReal = real[i + j + length / 2] * wReal - imaginary[i + j + length / 2] * wImaginary;
                    var vImaginary = real[i + j + length / 2] * wImaginary + imaginary[i + j + length / 2] * wReal;
                    real[i + j] = uReal + vReal;
                    imaginary[i + j] = uImaginary + vImaginary;
                    real[i + j + length / 2] = uReal - vReal;
                    imaginary[i + j + length / 2] = uImaginary - vImaginary;
                    (wReal, wImaginary) = (wReal * stepReal - wImaginary * stepImaginary,
                                           wReal * stepImaginary + wImaginary * stepReal);
                }
            }
        }

        if (!inverse)
            return;
        for (var i = 0; i < n; i++)
        {
            real[i] /= n;
            imaginary[i] /= n;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _session.Dispose();
        _runOptions.Dispose();
    }
}
