// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Microsoft.ML.OnnxRuntime;
using System.Reflection;
using ABChapterize.Errors;
using ABChapterize.Onnx;

namespace ABChapterize.Vad;

/// <summary>
/// One inference stream over the Silero VAD model: an ONNX session of its own plus the recurrent
/// state and audio context carried from frame to frame. <see cref="SileroVadDetector"/> owns a pool
/// of these and hands one to each stretch of audio it works on.
/// <para>
/// The frame contract was confirmed by inspecting the bundled model file's own ONNX input/output
/// metadata, and cross-checked against the upstream silero-vad repository's Python reference
/// wrapper (not assumed - a first implementation based on the metadata alone, without the context
/// step, was verified against a synthetic silence/speech/tone test file to produce near-zero speech
/// probability throughout, even in the speech regions):
/// </para>
/// <list type="bullet">
/// <item>"input" [1, <see cref="ContextSamples"/> + <see cref="FrameSamples"/>] float32 - each
/// call's window is not just the new samples: the model expects the last
/// <see cref="ContextSamples"/> samples of the *previous* window prepended (zeros at the start of a
/// stream), carried like the recurrent state below.</item>
/// <item>"state" [2, 1, 128] float32, recurrent, carried between frames.</item>
/// <item>"sr" scalar int64, always 16000 here.</item>
/// <item>"output" [1, 1] float32 - the frame's speech probability.</item>
/// <item>"stateN" [2, 1, 128] float32 - state for the next frame.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// Every buffer and <see cref="OrtValue"/> is allocated once here and rewritten in place, so a frame
/// costs one <c>Run</c> and three <c>Array.Copy</c>s and nothing else. This is not a
/// micro-optimization for its own sake: the model runs 31.25 inferences per second of audio (512 new
/// samples at 16 kHz), so a 12-hour audiobook is ~1.35 million <c>Run</c> calls on a model of a
/// couple of megabytes, a shape where everything is per-call overhead and nothing is arithmetic.
/// Measured with <c>tools\vadbench</c> on 2026-07-31: pre-binding plus the single-threaded session
/// options below took the shipped per-frame cost from 116 to 95 us, bit-identically.
/// </para>
/// <para>
/// One session per worker, not one session shared by all of them. Sharing runs correctly, but
/// stopped scaling past four workers and then got *slower* with each one added (802x realtime at 4,
/// 360x at 24, same measurement date) - which is what a contended per-session allocator arena looks
/// like. A session apiece costs a few megabytes of duplicated weights and removes it.
/// </para>
/// </remarks>
internal sealed class SileroWorker : IDisposable
{
    /// <summary>Embedded resource name of the bundled model (see the csproj's EmbeddedResource item).</summary>
    private const string ResourceName = "ABChapterize.silero_vad.onnx";

    /// <summary>Sample rate the model expects; matches
    /// <see cref="ABChapterize.Audio.IAudioSource.DetectSilencesAndStreamPcmAsync"/>'s fixed 16 kHz
    /// PCM output, so no resampling is needed anywhere in this path.</summary>
    internal const long SampleRate = 16000;

    /// <summary>New samples per inference frame (32 ms at 16 kHz) - required exactly by this model;
    /// see the type doc comment.</summary>
    internal const int FrameSamples = 512;

    /// <summary>Samples of the previous frame's tail prepended to each new frame; see the type doc
    /// comment.</summary>
    private const int ContextSamples = 64;

    /// <summary>Hidden state size per recurrent layer.</summary>
    private const int StateSize = 128;

    /// <summary>Number of stacked recurrent state layers the model carries.</summary>
    private const int StateLayers = 2;

    private static readonly string[] InputNames = ["input", "state", "sr"];
    private static readonly string[] OutputNames = ["output", "stateN"];

    private readonly InferenceSession _session;
    private readonly RunOptions _runOptions = new();

    private readonly float[] _window = new float[ContextSamples + FrameSamples];
    private readonly float[] _state = new float[StateLayers * StateSize];
    private readonly float[] _nextState = new float[StateLayers * StateSize];
    private readonly float[] _output = new float[1];
    private readonly long[] _sampleRate = [SampleRate];

    private readonly OrtValue[] _inputs;
    private readonly OrtValue[] _outputs;

    /// <summary>
    /// Points OnnxRuntime's native lookup at the published layout before anything on this type
    /// touches it. It has to be the static constructor rather than the instance one: the CLR runs a
    /// type initializer before any field initializer, and <see cref="_runOptions"/> above is a field
    /// initializer that loads the native library. Registering from the constructor body instead
    /// crashed a published build outright (0xC0000005 inside OnnxRuntime's own static init), because
    /// the field had already gone looking for onnxruntime.dll in the wrong place.
    /// </summary>
    static SileroWorker() => OnnxRuntimeNative.EnsureRegistered();

    /// <summary>Creates a worker with its own session over the bundled model.</summary>
    internal SileroWorker()
    {
        using var options = SessionOptionsForOneWorker();
        _session = new InferenceSession(LoadModelBytes(), options);
        // "state" and "stateN" must stay distinct buffers: ORT writes the output while the input is
        // still live, and aliasing them would feed a half-updated state back into the model.
        _inputs =
        [
            OrtValue.CreateTensorValueFromMemory(_window, [1, ContextSamples + FrameSamples]),
            OrtValue.CreateTensorValueFromMemory(_state, [StateLayers, 1, StateSize]),
            OrtValue.CreateTensorValueFromMemory(_sampleRate, []),
        ];
        _outputs =
        [
            OrtValue.CreateTensorValueFromMemory(_output, [1, 1]),
            OrtValue.CreateTensorValueFromMemory(_nextState, [StateLayers, 1, StateSize]),
        ];
    }

    /// <summary>
    /// One ORT thread per session, sequential execution. The parallelism in this path is supplied by
    /// <see cref="SileroVadDetector"/>'s block scheduler; letting each session open a pool of its own
    /// as well would oversubscribe the machine several times over. ORT's default is a pool sized to
    /// the machine's cores, and on a model this small the cost of waking ~24 threads per inference
    /// exceeds the work being parallelized.
    /// </summary>
    private static SessionOptions SessionOptionsForOneWorker()
        => new()
        {
            IntraOpNumThreads = 1,
            InterOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
        };

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

    /// <summary>
    /// Begins a fresh stream: zero recurrent state and zero leading context, which is Silero's
    /// documented starting point. Must be called before the first frame of every stretch of audio
    /// this worker is given, since workers are recycled across blocks and would otherwise carry the
    /// previous block's state into the next one.
    /// </summary>
    internal void Reset()
    {
        Array.Clear(_state);
        Array.Clear(_window, 0, ContextSamples);
    }

    /// <summary>Runs one inference frame, folding its outputs into the carried state.</summary>
    /// <param name="chunk">Buffer holding at least <see cref="FrameSamples"/> samples from
    /// <paramref name="offset"/>.</param>
    /// <param name="offset">Start of the new frame within <paramref name="chunk"/>.</param>
    /// <returns>Speech probability in [0, 1] for this frame.</returns>
    internal float RunFrame(float[] chunk, int offset)
    {
        Array.Copy(chunk, offset, _window, ContextSamples, FrameSamples);
        _session.Run(_runOptions, InputNames, _inputs, OutputNames, _outputs);
        Array.Copy(_nextState, _state, _nextState.Length);
        // The next frame's leading context is this frame's tail, which is already sitting in the
        // window - so the carry is one copy within the window rather than a detour through a
        // separate context buffer.
        Array.Copy(_window, FrameSamples, _window, 0, ContextSamples);
        return _output[0];
    }

    /// <summary>Releases the session and the pre-bound tensors.</summary>
    public void Dispose()
    {
        foreach (var value in _inputs)
            value.Dispose();
        foreach (var value in _outputs)
            value.Dispose();
        _runOptions.Dispose();
        _session.Dispose();
    }
}
