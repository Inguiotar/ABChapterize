using Whisper.net;
using Whisper.net.LibraryLoader;

namespace Chapterize;

/// <summary>A transcribed text segment with absolute timing.</summary>
/// <param name="StartSeconds">Segment start in seconds, relative to the decoded audio window.</param>
/// <param name="EndSeconds">Segment end in seconds, relative to the decoded audio window.</param>
/// <param name="Text">Recognized text.</param>
public readonly record struct TranscriptSegment(double StartSeconds, double EndSeconds, string Text);

/// <summary>
/// Maps the model names accepted on the command line to the bundled GGML model files.
/// </summary>
public static class ModelCatalog
{
    /// <summary>
    /// Returns the full path of the GGML file for a model selector (tiny, base, small,
    /// medium, turbo, large). Models are expected in the "models" folder next to the executable.
    /// </summary>
    /// <param name="model">Validated model selector from the command line.</param>
    /// <exception cref="AppError">Thrown when the model file does not exist.</exception>
    public static string GetModelPath(string model)
    {
        var fileName = model switch
        {
            "tiny" => "ggml-tiny.bin",
            "base" => "ggml-base.bin",
            "small" => "ggml-small.bin",
            "medium" => "ggml-medium.bin",
            "turbo" => "ggml-large-v3-turbo.bin",
            "large" => "ggml-large-v3.bin",
            _ => throw new AppError($"Unknown model \"{model}\"."),
        };
        var path = Path.Combine(AppContext.BaseDirectory, "models", fileName);
        if (!File.Exists(path))
            throw new AppError(
                $"Whisper model file not found: {path}\n" +
                "Hint: the \"models\" folder must reside next to chapterize.exe and contain " +
                "the GGML models (download from https://huggingface.co/ggerganov/whisper.cpp).");
        return path;
    }
}

/// <summary>
/// Wraps a Whisper.net processor for a single model, using the best available
/// hardware acceleration (CUDA, then Vulkan GPU, then CPU with AVX).
/// </summary>
public sealed class WhisperTranscriber : IAsyncDisposable
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
    public WhisperTranscriber(string modelPath, string language)
    {
        // Prefer the fastest available backend; Whisper.net probes them in this order
        // and silently falls back to the next one.
        RuntimeOptions.RuntimeLibraryOrder =
            [RuntimeLibrary.Cuda, RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx];

        _factory = WhisperFactory.FromPath(modelPath);
        _processor = _factory.CreateBuilder()
            .WithLanguage(language)
            .WithThreads(Math.Max(2, Environment.ProcessorCount - 1))
            .Build();
        RuntimeName = RuntimeOptions.LoadedLibrary?.ToString() ?? "unknown";
    }

    /// <summary>
    /// Transcribes a chunk of 16 kHz mono float PCM audio.
    /// </summary>
    /// <param name="samples">The audio samples.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Recognized segments in chronological order, timed relative to the chunk.</returns>
    public async Task<List<TranscriptSegment>> TranscribeAsync(float[] samples, CancellationToken ct)
    {
        var result = new List<TranscriptSegment>();
        if (samples.Length < FfmpegClient.SampleRate / 2)
            return result; // shorter than 0.5 s: nothing usable

        await foreach (var seg in _processor.ProcessAsync(samples, ct))
        {
            result.Add(new TranscriptSegment(
                seg.Start.TotalSeconds, seg.End.TotalSeconds, seg.Text));
        }
        return result;
    }

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
