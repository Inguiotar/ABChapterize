// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Whisper.net.LibraryLoader;
using Whisper.net;
using ABChapterize.Audio;

namespace ABChapterize.Transcription;

/// <summary>
/// Wraps a Whisper.net processor for a single model, using the best available
/// hardware acceleration (CUDA, then Vulkan GPU, then CPU with AVX).
/// </summary>
public sealed class WhisperTranscriber : ITranscriber, IAsyncDisposable
{
    private readonly WhisperFactory _factory;
    private readonly WhisperProcessor _processor;

    /// <summary>Name of the native runtime that was actually loaded (e.g. "Cuda", "Vulkan", "Cpu").</summary>
    public string RuntimeName { get; }

    /// <summary>
    /// Loads the given model and creates a processor with the given language hint.
    /// </summary>
    /// <param name="modelPath">Full path of the GGML model file.</param>
    /// <param name="language">Two-letter language hint for Whisper.</param>
    /// <param name="threads">
    /// CPU threads given to this processor. The tool always passes what --whisper-threads resolved
    /// to (see <see cref="ABChapterize.Cli.CliOptions.EffectiveWhisperThreads"/>, one per physical
    /// core by default); the fallback below exists only for the diagnostic harnesses under
    /// <c>tools\</c>, which construct a transcriber without a parsed command line.
    /// </param>
    /// <param name="forceCpu">Skips the GPU backends entirely and loads straight onto CPU
    /// (--cpu-only, see <see cref="ABChapterize.Cli.CliOptions.CpuOnly"/>), rather than relying
    /// on them being unavailable or failing to load.</param>
    /// <param name="gpuDevice">Index of the GPU to run on, resolved from a device *name* by
    /// <see cref="ABChapterize.Gpu.GpuSelector"/> (--use-gpu), or null to let the backend keep its
    /// own default of device 0. Ignored by the CPU backend, which has no devices to choose
    /// between.</param>
    public WhisperTranscriber(string modelPath, string language, int? threads = null, bool forceCpu = false,
        int? gpuDevice = null)
    {
        // Prefer the fastest available backend; Whisper.net probes them in this order
        // and silently falls back to the next one.
        //
        // "Cuda first" is an aspiration, not a promise, and the fallback is invisible from
        // the outside - only RuntimeName tells you what actually loaded. Whisper.net.Runtime.Cuda
        // 1.9.1's ggml-cuda-whisper.dll needs two things that an NVIDIA GPU alone does not give it
        // (both established on a GTX 1070 box, 2026-07-28, where the load failed with
        // ERROR_MOD_NOT_FOUND and Vulkan took over):
        //   - cublas64_13.dll, i.e. an installed CUDA 13 runtime. The package does not ship it.
        //   - a supported architecture. Its embedded kernels cover sm_86/sm_89/sm_120a/sm_121a
        //     (Ampere, Ada, Blackwell) with no older-arch PTX to JIT from, so Pascal-era cards
        //     cannot run it even once cuBLAS is present.
        // Vulkan covers both cases and is why the fallback order matters more than it looks.
        //
        // gpuDevice is what --use-gpu resolves to. It is an index because that is all whisper.cpp
        // accepts, but nothing outside this call ever handles one: the name is matched against a
        // fresh in-process enumeration, because the same box enumerated its two GPUs in opposite
        // order in a desktop session and over SSH (2026-07-28, confirmed from both ends - GPU load
        // monitoring there, VRAM going 1225 -> 3208 MiB at 100% utilization here). An index
        // written down anywhere outside one process's lifetime is a bug waiting to happen.
        RuntimeOptions.RuntimeLibraryOrder = forceCpu
            ? [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx]
            : [RuntimeLibrary.Cuda, RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx];

        // Only passed when a device was actually chosen: GpuDevice defaults to 0, so handing it
        // the default explicitly would be indistinguishable from choosing device 0 on purpose - and
        // on the CUDA backend, where our Vulkan-derived indices mean nothing, that distinction is
        // the difference between "leave it alone" and "pin it to an ordinal we made up".
        _factory = gpuDevice is { } device
            ? WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions { GpuDevice = device })
            : WhisperFactory.FromPath(modelPath);
        _processor = _factory.CreateBuilder()
            .WithLanguage(language)
            .WithThreads(threads ?? Math.Max(2, Environment.ProcessorCount - 1))
            .WithProbabilities()
            .Build();
        RuntimeName = RuntimeOptions.LoadedLibrary?.ToString() ?? "unknown";
    }

    /// <inheritdoc/>
    public async Task<List<TranscriptSegment>> TranscribeAsync(float[] samples, CancellationToken ct)
    {
        var result = new List<TranscriptSegment>();
        if (samples.Length < FfmpegClient.SampleRate / 2)
            return result; // shorter than 0.5 s: nothing usable

        await foreach (var seg in _processor.ProcessAsync(samples, ct))
        {
            result.Add(new TranscriptSegment(
                seg.Start.TotalSeconds, seg.End.TotalSeconds, seg.Text, seg.Probability));
        }
        return result;
    }

    /// <inheritdoc/>
    public Task<(string Language, float Probability)> DetectLanguageWithProbability(float[] samples, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.Run(() =>
        {
            var (language, probability) = _processor.DetectLanguageWithProbability(samples);
            return (language ?? "", probability);
        }, ct);
    }

    /// <inheritdoc/>
    public void ChangeLanguage(string language) => _processor.ChangeLanguage(language);

    /// <summary>
    /// Releases the native processor and model. Waits for an in-flight transcription to
    /// wind down first (after a cancellation the processor may still be processing, and
    /// its synchronous Dispose would throw).
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await _processor.DisposeAsync();
        _factory.Dispose();
    }
}
