// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using System.Collections.Concurrent;
using ABChapterize.Audio;
using ABChapterize.Concurrency;
using ABChapterize.Errors;

namespace ABChapterize.Vad;

/// <summary>
/// Voice activity detection using the Silero VAD ONNX model (MIT-licensed, bundled with the
/// executable as an embedded resource - see THIRD-PARTY-NOTICES.md). Consumes an externally-fed
/// 16 kHz mono PCM stream - see <see cref="IAudioSource.DetectSilencesAndStreamPcmAsync"/>, which
/// decodes the file once for both the silence scan and this VAD pass - and cuts it into blocks that
/// several <see cref="SileroWorker"/>s classify at once. Per-frame probabilities are turned into
/// speech segments by <see cref="VadSegmenter"/>.
/// </summary>
/// <remarks>
/// <para>
/// Why blocks at all. Pass 1 is VAD-bound rather than ffmpeg-bound: measured on 2026-07-31 with
/// <c>tools\vadbench</c>, ffmpeg's own decode plus silencedetect runs at ~1400x realtime while the
/// same run with a single-threaded VAD attached drops to 190-280x, so the model accounts for 82-86%
/// of the pass. The work cannot go to a GPU - DirectML measured 6.3x *slower* than the best CPU
/// configuration (585 vs 92 us/frame), because the model runs 31.25 tiny inferences per second of
/// audio and the run is therefore pure per-dispatch overhead with no arithmetic to amortize it
/// against. Blocks are the remaining direction.
/// </para>
/// <para>
/// What this implementation actually measured, against its own single-worker path over the whole of
/// Wintersmith.m4b (8:32:52), 12 workers on a Ryzen AI 9 HX 370 (12C/24T), 2026-07-31: Pass 1 end to
/// end 201.5 s single-stream against 49.7 s block-parallel, a 4.05x speed-up of a pass whose ffmpeg
/// floor is 29.2 s - so of the 172 s the single-stream VAD added on top of the decode, 152 s are
/// gone and VAD has dropped from 86% of the pass to 41%. Fidelity over those ~960000 frames: 178
/// frames differ by more than 0.001, the worst by 0.011, and <b>zero</b> speech segments differ.
/// Re-run <c>tools\vadbench pipeline &lt;file&gt; --only seq,live</c> against any change here; its
/// per-frame and per-segment comparison is what turns "faster" into "faster and identical".
/// </para>
/// <para>
/// Why blocks are allowed to be independent. Silero is recurrent, so frame N's answer needs the
/// state frame N-1 handed on - but that state is a leaky summary of recent audio rather than a
/// running total since the file began, so a stream started mid-file converges onto the state a
/// continuous run would have carried there. Each block is therefore handed the audio immediately
/// *preceding* it purely to converge its state; those warm-up frames' probabilities are discarded
/// and the block's own kept. The file's first block needs no warm-up and is bit-identical by
/// construction, since a fresh stream starts from zero state either way.
/// </para>
/// <para>
/// Memory: at most <c>workers + 1</c> blocks of audio are alive at once (one per busy worker, plus
/// the one the reader is filling), each <see cref="BlockSeconds"/> long, plus a
/// <see cref="WarmupSeconds"/> tail per block - about 483 MB at 12 workers. The slot semaphore that
/// bounds this is also what back-pressures ffmpeg's stdout pipe, exactly as a single-threaded read
/// would.
/// </para>
/// <para>
/// Threading: every worker is handed out through the same slot semaphore, so no two stretches of
/// audio can share one worker's recurrent state, and the total in flight stays bounded however many
/// callers there are. Overlapping calls on one instance are therefore safe - they simply share the
/// worker budget rather than each claiming it - which matters because that guarantee is what let the
/// pre-0.10.0 detector be shared across concurrently processed files. Nothing does that today (see
/// <see cref="ABChapterize.Processing.FileProcessor"/>), but a class whose safety depends on its
/// only caller staying single-threaded is a trap waiting for the caller to change.
/// </para>
/// </remarks>
public sealed class SileroVadDetector : IVoiceActivityDetector, IDisposable
{
    /// <summary>
    /// Audio per block, before its warm-up prefix. Long enough that the warm-up is a ~10% tax rather
    /// than the dominant cost, short enough that the in-flight audio stays bounded (see the memory
    /// note in the type's remarks). Fixed rather than adapted to the file's length: a multi-chapter
    /// audiobook is hardly ever short enough for the block count to matter, and a 20-minute file
    /// finishes fast enough on one or two workers that nothing needs adapting.
    /// </summary>
    internal const double BlockSeconds = 600;

    /// <summary>
    /// Audio prepended to each block purely to converge its recurrent state before the block's own
    /// frames start counting.
    /// <para>
    /// Calibrated on 2026-07-31 with <c>tools\vadbench</c>, which compares every frame's raw
    /// probability and every smoothed segment against a single-threaded run of the same model. The
    /// figures below come from the calibration sweep that chose this value, before the block
    /// scheduler moved into this class; see the type's remarks for what the shipped implementation
    /// then measured at the winning setting.
    /// </para>
    /// <list type="bullet">
    /// <item>600 s blocks / 60 s warm-up: one frame in ~960000 differs, by 0.001; zero segment
    /// differences. Inference 1653x realtime against 276x.</item>
    /// <item>300 s / 30 s: zero segment differences, worst frame delta 0.05.</item>
    /// <item>300 s / 2 s: 12 segment boundaries moved, worst by 32 ms (one frame).</item>
    /// <item>60 s / 2 s: ~3300 disagreeing frames scattered across the whole file, 17 boundaries
    /// moved by up to 96 ms.</item>
    /// </list>
    /// <para>
    /// The last two lines are the reason this value is not shaved to save its overhead: too short a
    /// warm-up does not produce a transient that fades, it produces a divergence that never
    /// re-converges and shows up anywhere in the file. A moved boundary matters here even when the
    /// probability delta could not have moved one, because these segments feed
    /// <see cref="ABChapterize.Detection.JingleGeometry"/>'s non-speech regions and therefore mark
    /// placement.
    /// </para>
    /// </summary>
    internal const double WarmupSeconds = 60;

    private readonly int _workers;
    private readonly int _blockSamples;
    private readonly int _warmupSamples;
    private readonly SileroWorker[] _pool;

    /// <summary>Workers not currently in use. Sized to <see cref="_workers"/> and guarded by
    /// <see cref="_slots"/>, so a taker never comes away empty.</summary>
    private readonly ConcurrentBag<SileroWorker> _free = [];

    /// <summary>
    /// Bounds how many workers are busy at once, which is also what bounds how much decoded audio is
    /// in flight. An instance field rather than one per call, so that the budget holds across
    /// overlapping calls too: the two together are what make "a worker is always available when a
    /// slot was granted" an invariant of the object rather than of one code path.
    /// </summary>
    private readonly SemaphoreSlim _slots;

    /// <summary>Creates a detector that loads the bundled model once per worker, reused for every
    /// file processed through this instance.</summary>
    /// <param name="workers">How many blocks may be classified at once; null (the default) means one
    /// per physical CPU core. Scaling peaks at the physical core count and degrades past it - SMT
    /// buys nothing for this workload.</param>
    public SileroVadDetector(int? workers = null)
        : this(workers ?? ProcessorTopology.PhysicalCoreCount, BlockSeconds, WarmupSeconds)
    {
    }

    /// <summary>Creates a detector with an explicit block geometry. Internal for unit testing, which
    /// needs many blocks out of a short synthetic signal.</summary>
    /// <param name="workers">How many blocks may be classified at once. A value of 1 selects the
    /// single-stream path instead, which ignores the two block parameters entirely - see
    /// <see cref="DetectSpeechProbabilitiesAsync"/>.</param>
    /// <param name="blockSeconds">Audio per block, before the warm-up prefix.</param>
    /// <param name="warmupSeconds">Audio prepended to each block to converge its recurrent state.</param>
    internal SileroVadDetector(int workers, double blockSeconds, double warmupSeconds)
    {
        _workers = Math.Max(1, workers);
        _blockSamples = AlignToFrames(blockSeconds);
        _warmupSamples = AlignToFrames(warmupSeconds);
        _pool = new SileroWorker[_workers];
        for (var i = 0; i < _workers; i++)
        {
            _pool[i] = new SileroWorker();
            _free.Add(_pool[i]);
        }
        _slots = new SemaphoreSlim(_workers);
    }

    /// <summary>How many blocks this detector classifies at once (read for the --verbose note that
    /// records what the run was configured with).</summary>
    public int Workers => _workers;

    /// <summary>Rounds a duration down onto the model's frame grid, so every block boundary falls on
    /// a frame boundary and the parallel run's frames line up one-for-one with a single-stream
    /// run's. Without this nothing about the two runs would be comparable.</summary>
    /// <param name="seconds">The duration to align.</param>
    private static int AlignToFrames(double seconds)
        => Math.Max(1, (int)(seconds * SileroWorker.SampleRate / SileroWorker.FrameSamples))
           * SileroWorker.FrameSamples;

    /// <summary>Start time of a frame, by its index from the beginning of the file.</summary>
    /// <param name="frameIndex">Zero-based frame index.</param>
    private static double FrameTime(long frameIndex)
        => frameIndex * (double)SileroWorker.FrameSamples / SileroWorker.SampleRate;

    /// <inheritdoc/>
    public async Task<List<SpeechSegment>> DetectSpeechAsync(IAsyncEnumerable<float[]> pcm, CancellationToken ct)
        => VadSegmenter.Smooth(await DetectSpeechProbabilitiesAsync(pcm, ct));

    /// <summary>
    /// Runs VAD inference over the given PCM stream and returns each frame's raw speech probability,
    /// before <see cref="VadSegmenter.Smooth"/>'s thresholding/hysteresis turns them into segments.
    /// Exposed (rather than kept private inside <see cref="DetectSpeechAsync"/>) purely for
    /// diagnostic tooling that needs to re-smooth the same frames at several candidate thresholds
    /// without paying for repeated ONNX inference - production code only ever calls
    /// <see cref="DetectSpeechAsync"/>.
    /// </summary>
    /// <param name="pcm">The audio as 16 kHz mono 32-bit float PCM chunks, in chronological order.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// A single worker takes the single-stream path rather than a block path with one block in
    /// flight: with nothing to overlap, the warm-up frames would be pure overhead, and the result is
    /// then exactly what a pre-block release produced, frame for frame.
    /// </remarks>
    public Task<List<(double TimeSeconds, float Probability)>> DetectSpeechProbabilitiesAsync(
        IAsyncEnumerable<float[]> pcm, CancellationToken ct)
        => _workers == 1 ? SingleStreamProbabilitiesAsync(pcm, ct) : BlockProbabilitiesAsync(pcm, ct);

    /// <summary>Classifies the whole stream as one continuous sequence of frames on one worker.</summary>
    /// <param name="pcm">The audio as 16 kHz mono 32-bit float PCM chunks, in chronological order.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<List<(double TimeSeconds, float Probability)>> SingleStreamProbabilitiesAsync(
        IAsyncEnumerable<float[]> pcm, CancellationToken ct)
    {
        var worker = await BorrowWorkerAsync(ct);
        try
        {
            worker.Reset();
            var frames = new List<(double TimeSeconds, float Probability)>();
            var frameIndex = 0L;
            var leftover = Array.Empty<float>();

            await foreach (var rawChunk in pcm)
            {
                ct.ThrowIfCancellationRequested();
                // The incoming chunk size is ffmpeg's, not a multiple of the frame size, so whatever
                // a chunk leaves over has to open the next one.
                var chunk = leftover.Length == 0 ? rawChunk : Concat(leftover, rawChunk);
                var offset = 0;
                while (offset + SileroWorker.FrameSamples <= chunk.Length)
                {
                    frames.Add((FrameTime(frameIndex), worker.RunFrame(chunk, offset)));
                    frameIndex++;
                    offset += SileroWorker.FrameSamples;
                }
                leftover = chunk[offset..];
            }
            // Any trailing partial frame (< 32 ms) at true EOF is dropped; far below the precision
            // chapter marks need.

            return frames;
        }
        finally
        {
            ReturnWorker(worker);
        }
    }

    /// <summary>Waits for a slot and takes the worker it entitles the caller to. Every worker in the
    /// pool is handed out through here, so no two streams can ever share one's recurrent state.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="AppError">A slot was granted but no worker was free, which
    /// <see cref="_slots"/> is supposed to make impossible.</exception>
    private async Task<SileroWorker> BorrowWorkerAsync(CancellationToken ct)
    {
        await _slots.WaitAsync(ct);
        if (_free.TryTake(out var worker))
            return worker;
        _slots.Release();
        throw new AppError("Internal error: no free VAD worker despite the pool's slot limit.");
    }

    /// <summary>Returns a worker to the pool and frees its slot.</summary>
    /// <param name="worker">The worker to give back.</param>
    private void ReturnWorker(SileroWorker worker)
    {
        _free.Add(worker);
        _slots.Release();
    }

    /// <summary>
    /// Cuts the stream into blocks and classifies several at once, reassembling their frames in
    /// chronological order.
    /// </summary>
    /// <param name="pcm">The audio as 16 kHz mono 32-bit float PCM chunks, in chronological order.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<List<(double TimeSeconds, float Probability)>> BlockProbabilitiesAsync(
        IAsyncEnumerable<float[]> pcm, CancellationToken ct)
    {
        var blocks = new List<Task<List<(double TimeSeconds, float Probability)>>>();
        // Filled directly from the incoming chunks and handed off whole, rather than accumulated in
        // a growing list and sliced off the front: this loop is the one part of the block path that
        // stays serial, so an O(n) copy-down per block in it visibly capped the achievable speed-up.
        var body = new float[_blockSamples];
        var filled = 0;
        var warmup = Array.Empty<float>();
        var blockStartSample = 0L;

        try
        {
            await foreach (var chunk in pcm.WithCancellation(ct))
            {
                var taken = 0;
                while (taken < chunk.Length)
                {
                    var count = Math.Min(_blockSamples - filled, chunk.Length - taken);
                    Array.Copy(chunk, taken, body, filled, count);
                    filled += count;
                    taken += count;
                    if (filled < _blockSamples)
                        continue;
                    blocks.Add(await DispatchAsync(warmup, body, blockStartSample, ct));
                    warmup = TailOf(body, _warmupSamples);
                    blockStartSample += _blockSamples;
                    body = new float[_blockSamples];
                    filled = 0;
                }
            }
            if (filled > 0)
                blocks.Add(await DispatchAsync(warmup, body[..filled], blockStartSample, ct));

            var frames = new List<(double TimeSeconds, float Probability)>();
            foreach (var block in blocks)
                frames.AddRange(await block);
            return frames;
        }
        catch
        {
            // Every dispatched block must be waited for before unwinding: each one releases its slot
            // on the semaphore disposed below, and an abandoned task would also leave its failure
            // unobserved. Their outcomes are discarded here - the original exception is the news.
            foreach (var block in blocks)
            {
                try { await block; } catch { /* the throw below carries the real failure */ }
            }
            throw;
        }
    }

    /// <summary>
    /// Takes a worker slot and starts one block on it, returning the task that will hold its frames.
    /// Awaits only the slot, not the work, so the caller goes straight back to reading the stream.
    /// </summary>
    /// <param name="warmup">Audio immediately preceding <paramref name="body"/>, classified and
    /// discarded to converge the worker's state; empty for the file's first block.</param>
    /// <param name="body">The block's own audio.</param>
    /// <param name="startSample">Sample offset of <paramref name="body"/> from the start of the file.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<Task<List<(double TimeSeconds, float Probability)>>> DispatchAsync(
        float[] warmup, float[] body, long startSample, CancellationToken ct)
    {
        var worker = await BorrowWorkerAsync(ct);
        // Deliberately not given the cancellation token: a token already cancelled would make
        // Task.Run skip the body entirely, and with it the hand-back below - losing a worker and its
        // slot for good. Cancellation is observed inside the frame loop instead.
        return Task.Run(() =>
        {
            try
            {
                return RunBlock(worker, warmup, body, startSample, ct);
            }
            finally
            {
                ReturnWorker(worker);
            }
        });
    }

    /// <summary>Classifies one block on a borrowed worker and returns its frames in absolute file
    /// time. The warm-up frames are run through the same worker without a reset in between, which is
    /// what carries their converged state into the block's own first frame.</summary>
    /// <param name="warmup">Audio immediately preceding <paramref name="body"/>; its frames are run
    /// for their effect on the state and their probabilities dropped.</param>
    /// <param name="body">The block's own audio.</param>
    /// <param name="worker">The worker borrowed for this block; handed back by the caller.</param>
    /// <param name="startSample">Sample offset of <paramref name="body"/> from the start of the file.</param>
    /// <param name="ct">Cancellation token.</param>
    private static List<(double TimeSeconds, float Probability)> RunBlock(
        SileroWorker worker, float[] warmup, float[] body, long startSample, CancellationToken ct)
    {
        worker.Reset();
        for (var offset = 0; offset + SileroWorker.FrameSamples <= warmup.Length;
             offset += SileroWorker.FrameSamples)
        {
            ct.ThrowIfCancellationRequested();
            worker.RunFrame(warmup, offset);
        }

        var firstFrame = startSample / SileroWorker.FrameSamples;
        var frames = new List<(double TimeSeconds, float Probability)>(
            body.Length / SileroWorker.FrameSamples);
        for (var offset = 0; offset + SileroWorker.FrameSamples <= body.Length;
             offset += SileroWorker.FrameSamples)
        {
            ct.ThrowIfCancellationRequested();
            // Timestamps are rewritten to absolute file time here rather than by the caller: a block
            // does not otherwise know where it sits, and the caller would have to recompute what the
            // loop already has.
            frames.Add((FrameTime(firstFrame + frames.Count), worker.RunFrame(body, offset)));
        }
        return frames;
    }

    /// <summary>Copies the last <paramref name="count"/> samples of a block, or all of it when it is
    /// shorter than that.</summary>
    /// <param name="block">The block to take the tail of.</param>
    /// <param name="count">How many samples are wanted.</param>
    private static float[] TailOf(float[] block, int count)
        => block[^Math.Min(count, block.Length)..];

    /// <summary>Concatenates a small leftover buffer with a new chunk.</summary>
    /// <param name="leftover">Samples left over from the previous chunk.</param>
    /// <param name="chunk">The newly arrived chunk.</param>
    private static float[] Concat(float[] leftover, float[] chunk)
    {
        var result = new float[leftover.Length + chunk.Length];
        Array.Copy(leftover, result, leftover.Length);
        Array.Copy(chunk, 0, result, leftover.Length, chunk.Length);
        return result;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var worker in _pool)
            worker.Dispose();
        _slots.Dispose();
    }
}
