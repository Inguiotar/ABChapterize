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
    /// CPU threads given to this processor. Defaults to nearly all logical cores, which is
    /// right for the common case of one processor at a time; when several run concurrently
    /// (see <see cref="ABChapterize.Concurrency.ConcurrencyMonitor"/>) each instance is given a
    /// smaller share instead, so the total across all of them still roughly matches the core count.
    /// </param>
    /// <param name="forceCpu">Skips the GPU backends entirely and loads straight onto CPU
    /// (--cpu-only, see <see cref="ABChapterize.Cli.CliOptions.CpuOnly"/>), rather than relying
    /// on them being unavailable or failing to load.</param>
    public WhisperTranscriber(string modelPath, string language, int? threads = null, bool forceCpu = false)
    {
        // Prefer the fastest available backend; Whisper.net probes them in this order
        // and silently falls back to the next one.
        RuntimeOptions.RuntimeLibraryOrder = forceCpu
            ? [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx]
            : [RuntimeLibrary.Cuda, RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx];

        _factory = WhisperFactory.FromPath(modelPath);
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
