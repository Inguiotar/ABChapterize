// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.OnnxRuntime;
using System.Reflection;
using System.Runtime.InteropServices;
using ABChapterize.Audio;

namespace ABChapterize.Vad;

/// <summary>
/// Voice activity detection using the Silero VAD ONNX model (MIT-licensed, bundled with the
/// executable as an embedded resource - see THIRD-PARTY-NOTICES.md). Consumes an externally-fed
/// 16 kHz mono PCM stream - see <see cref="IAudioSource.DetectSilencesAndStreamPcmAsync"/>,
/// which decodes the file once for both the silence scan and this VAD pass - and feeds it
/// through the model <see cref="FrameSamples"/> new samples at a time. The frame size and the stateful
/// (recurrent) input/output tensor contract were confirmed by inspecting the bundled model
/// file's actual ONNX input/output metadata directly, and cross-checked against the upstream
/// silero-vad repository's own Python reference wrapper (not assumed - a first implementation
/// based on the metadata alone, without the context step below, was verified against a
/// synthetic silence/speech/tone test file to produce near-zero speech probability throughout,
/// even in the speech regions):
/// <list type="bullet">
/// <item>"input" [1, <see cref="ContextSamples"/> + <see cref="FrameSamples"/>] float32 - each
/// call's window is not just the new samples: the model expects the last <see
/// cref="ContextSamples"/> samples of the *previous* window prepended (zeros for the very
/// first window of a file), carried like the recurrent state below.</item>
/// <item>"state" [2, 1, 128] float32, recurrent, carried between frames.</item>
/// <item>"sr" scalar int64, always 16000 here.</item>
/// <item>"output" [1, 1] float32 - the frame's speech probability.</item>
/// <item>"stateN" [2, 1, 128] float32 - state for the next frame.</item>
/// </list>
/// Per-frame probabilities are turned into speech segments by <see cref="VadSegmenter"/>.
/// </summary>
/// <remarks>
/// One instance is shared across every file in a run, mirroring how
/// <see cref="ABChapterize.Processing.FileProcessor"/> already shares a single
/// <see cref="FfmpegClient"/> across concurrently processed files:
/// <see cref="InferenceSession.Run(IReadOnlyCollection{NamedOnnxValue})"/> is safe to call
/// concurrently from multiple threads as long as each call uses its own tensors, which is
/// always the case here since the recurrent state and context are local variables of <see
/// cref="DetectSpeechAsync"/>, never shared across files or calls.
/// </remarks>
public sealed class SileroVadDetector : IVoiceActivityDetector, IDisposable
{
    /// <summary>Embedded resource name of the bundled model (see the csproj's EmbeddedResource item).</summary>
    private const string ResourceName = "ABChapterize.silero_vad.onnx";

    /// <summary>Sample rate the model expects; matches <see
    /// cref="IAudioSource.DetectSilencesAndStreamPcmAsync"/>'s fixed 16 kHz PCM output, so no
    /// resampling is needed here.</summary>
    private const long SampleRate = 16000;

    /// <summary>New samples per inference frame (32 ms at 16 kHz) - required exactly by this
    /// model; see the type doc comment.</summary>
    private const int FrameSamples = 512;

    /// <summary>Samples of the previous frame's tail prepended to each new frame; see the type
    /// doc comment.</summary>
    private const int ContextSamples = 64;

    /// <summary>Hidden state size per recurrent layer.</summary>
    private const int StateSize = 128;

    /// <summary>Number of stacked recurrent state layers the model carries.</summary>
    private const int StateLayers = 2;

    private readonly InferenceSession _session;

    /// <summary>Redirects Microsoft.ML.OnnxRuntime's native "onnxruntime" P/Invoke lookup to
    /// <c>runtimes\&lt;rid&gt;\</c> next to the executable, matching where the deployment puts
    /// it (see the csproj's PruneForeignRuntimes target) instead of the flat publish-root
    /// layout the package's own default probing expects. Registered here, rather than at some
    /// process-wide startup site, because this is the only file in the codebase that touches
    /// OnnxRuntime - the CLR guarantees this runs before <see cref="InferenceSession"/> is first
    /// constructed below.</summary>
    static SileroVadDetector()
    {
        NativeLibrary.SetDllImportResolver(typeof(InferenceSession).Assembly, ResolveOnnxRuntimeNativeLibrary);
    }

    /// <summary>Loads "onnxruntime" from <c>runtimes\&lt;rid&gt;\</c> when it's there (the
    /// published layout); otherwise returns <see cref="IntPtr.Zero"/> to fall back to the
    /// default search (an unpublished build output, where the native still sits flat next to
    /// the assembly). Any other library name also falls through unchanged -
    /// "onnxruntime_providers_shared", the only other native OnnxRuntime ships, is never
    /// P/Invoked from managed code; onnxruntime itself loads it from its own directory, so
    /// moving both together is enough without a second entry here.</summary>
    private static IntPtr ResolveOnnxRuntimeNativeLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != "onnxruntime")
            return IntPtr.Zero;
        var fileName = OperatingSystem.IsWindows() ? "onnxruntime.dll" : "libonnxruntime.so";
        var path = Path.Combine(AppContext.BaseDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, fileName);
        return File.Exists(path) ? NativeLibrary.Load(path) : IntPtr.Zero;
    }

    /// <summary>Creates a detector that loads the bundled model once, reused for every file
    /// processed through this instance.</summary>
    public SileroVadDetector()
    {
        _session = new InferenceSession(LoadModelBytes());
    }

    /// <summary>Reads the embedded Silero VAD model into memory.</summary>
    private static byte[] LoadModelBytes()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(ResourceName)
            ?? throw new AppError($"Embedded Silero VAD model resource \"{ResourceName}\" not found.");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    /// <inheritdoc/>
    public async Task<List<SpeechSegment>> DetectSpeechAsync(IAsyncEnumerable<float[]> pcm, CancellationToken ct)
        => VadSegmenter.Smooth(await DetectSpeechProbabilitiesAsync(pcm, ct));

    /// <summary>Runs VAD inference over the given PCM stream and returns each frame's raw speech
    /// probability, before <see cref="VadSegmenter.Smooth"/>'s thresholding/hysteresis turns them
    /// into segments. Exposed (rather than kept private inside <see cref="DetectSpeechAsync"/>)
    /// purely for diagnostic tooling that needs to re-smooth the same frames at several candidate
    /// thresholds without paying for repeated ONNX inference - production code only ever calls
    /// <see cref="DetectSpeechAsync"/>.</summary>
    public async Task<List<(double TimeSeconds, float Probability)>> DetectSpeechProbabilitiesAsync(
        IAsyncEnumerable<float[]> pcm, CancellationToken ct)
    {
        var frames = new List<(double TimeSeconds, float Probability)>();
        // Zero-initialized recurrent state and context are Silero's documented starting point
        // for a fresh stream. Local variables here (not fields), so concurrent calls for
        // different files never share or race on them - see the type's threading remarks.
        var state = new float[StateLayers * StateSize];
        var context = new float[ContextSamples];
        var frameIndex = 0L;
        var leftover = Array.Empty<float>();

        await foreach (var rawChunk in pcm)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = leftover.Length == 0 ? rawChunk : Concat(leftover, rawChunk);

            var offset = 0;
            while (offset + FrameSamples <= chunk.Length)
            {
                var probability = RunFrame(chunk, offset, state, context);
                frames.Add((frameIndex * (double)FrameSamples / SampleRate, probability));
                frameIndex++;
                offset += FrameSamples;
            }
            leftover = chunk[offset..];
        }
        // Any trailing partial frame (< 32 ms) at true EOF is dropped; far below the precision
        // chapter marks need.

        return frames;
    }

    /// <summary>Runs one inference frame and updates <paramref name="state"/> and <paramref
    /// name="context"/> in place with the model's returned next-frame state and this frame's
    /// trailing samples, respectively.</summary>
    /// <param name="chunk">Buffer containing at least <see cref="FrameSamples"/> new samples from <paramref name="offset"/>.</param>
    /// <param name="offset">Start of the new frame within <paramref name="chunk"/>.</param>
    /// <param name="state">Recurrent state, read as input and overwritten with the next state.</param>
    /// <param name="context">Previous frame's trailing <see cref="ContextSamples"/> samples,
    /// read as input and overwritten with this frame's trailing samples.</param>
    /// <returns>Speech probability in [0, 1] for this frame.</returns>
    private float RunFrame(float[] chunk, int offset, float[] state, float[] context)
    {
        var window = new float[ContextSamples + FrameSamples];
        Array.Copy(context, window, ContextSamples);
        Array.Copy(chunk, offset, window, ContextSamples, FrameSamples);

        var inputTensor = new DenseTensor<float>(window, [1, ContextSamples + FrameSamples]);
        var stateTensor = new DenseTensor<float>(state, [StateLayers, 1, StateSize]);
        var srTensor = new DenseTensor<long>(new[] { SampleRate }, []);

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input", inputTensor),
            NamedOnnxValue.CreateFromTensor("state", stateTensor),
            NamedOnnxValue.CreateFromTensor("sr", srTensor),
        };

        using var results = _session.Run(inputs);
        var output = results.First(r => r.Name == "output").AsTensor<float>();
        var newState = results.First(r => r.Name == "stateN").AsTensor<float>();

        var i = 0;
        foreach (var v in newState)
            state[i++] = v;
        Array.Copy(chunk, offset + FrameSamples - ContextSamples, context, 0, ContextSamples);

        return output.First();
    }

    /// <summary>Concatenates a small leftover buffer with a new chunk.</summary>
    private static float[] Concat(float[] leftover, float[] chunk)
    {
        var result = new float[leftover.Length + chunk.Length];
        Array.Copy(leftover, result, leftover.Length);
        Array.Copy(chunk, 0, result, leftover.Length, chunk.Length);
        return result;
    }

    /// <inheritdoc/>
    public void Dispose() => _session.Dispose();
}
