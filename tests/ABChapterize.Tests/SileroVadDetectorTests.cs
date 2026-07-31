// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Xunit;
using ABChapterize.Vad;

namespace ABChapterize.Tests;

/// <summary>
/// Tests that <see cref="SileroVadDetector"/>'s block-parallel path agrees with its single-stream
/// path. These run the real ONNX model - the whole question is whether cutting the timeline into
/// independently warmed-up blocks changes what the model says, which no stand-in could answer.
/// </summary>
/// <remarks>
/// <para>
/// What these tests are not: the fidelity argument for the shipped 600 s / 60 s geometry. That rests
/// on <c>tools\vadbench</c> comparing every frame of a real 8.5 h audiobook (one frame in ~960000
/// differing, by 0.001, with zero segment differences - 2026-07-31). The signal here is synthetic,
/// which Silero is not calibrated for, so it is used to prove the plumbing: that blocks are cut on
/// the frame grid, that every frame of the file appears exactly once and in order, and that the
/// warm-up really does converge a mid-file block onto the state a continuous run carried there.
/// </para>
/// <para>
/// The block geometry is scaled down through the internal constructor so a signal of a couple of
/// minutes still spans several blocks. The warm-up is a much larger fraction of a block than the
/// shipped ratio because what has to be long enough is the warm-up in seconds, not relative to the
/// block - and a block's warm-up can never exceed one block's length, since it is the previous
/// block's tail.
/// </para>
/// </remarks>
public class SileroVadDetectorTests
{
    private const int SampleRate = 16000;
    private const int FrameSamples = 512;

    /// <summary>Blocks small enough that the test signal spans a good number of them.</summary>
    private const double TestBlockSeconds = 30;

    /// <summary>Warm-up per block. Generous relative to the block, for the reason the shipped
    /// constant is generous too: too short a warm-up does not decay, it diverges.</summary>
    private const double TestWarmupSeconds = 15;

    [Fact]
    public async Task BlockParallelAgreesWithSingleStream()
    {
        var audio = BuildSignal(seconds: 150);

        using var single = new SileroVadDetector(workers: 1, TestBlockSeconds, TestWarmupSeconds);
        using var parallel = new SileroVadDetector(workers: 4, TestBlockSeconds, TestWarmupSeconds);
        var reference = await single.DetectSpeechProbabilitiesAsync(Chunks(audio), CancellationToken.None);
        var blocked = await parallel.DetectSpeechProbabilitiesAsync(Chunks(audio), CancellationToken.None);

        // Same frames, in the same places: a block boundary off the 512-sample grid, or a warm-up
        // frame leaking into the output, would show up here before anything else does.
        Assert.Equal(reference.Count, blocked.Count);
        for (var i = 0; i < reference.Count; i++)
            Assert.Equal(reference[i].TimeSeconds, blocked[i].TimeSeconds);

        // The tolerance separates a converged warm-up from an unconverged one, which is the only
        // thing a synthetic signal can honestly be asked about. Measured while writing this test
        // (2026-07-31): this geometry gives a worst frame of 0.054, while dropping the warm-up to
        // 2 s - the length vadbench found insufficient on real audio too - gives 0.16. Anything at
        // or above 0.1 therefore means the warm-up has stopped doing its job. What it deliberately
        // does not claim is a fidelity figure: real narration converges far tighter (one frame in
        // ~960000 differing by 0.001), and this signal's 2-second stretches of near-silence are
        // nothing like a real noise floor.
        var worst = reference.Zip(blocked).Max(p => Math.Abs(p.First.Probability - p.Second.Probability));
        Assert.True(worst < 0.1, $"worst per-frame probability difference was {worst:0.####}");
    }

    /// <summary>The frames are the raw material; the segments are what detection actually consumes,
    /// so they are asserted separately and exactly.</summary>
    [Fact]
    public async Task BlockParallelProducesTheSameSpeechSegments()
    {
        var audio = BuildSignal(seconds: 150);

        using var single = new SileroVadDetector(workers: 1, TestBlockSeconds, TestWarmupSeconds);
        using var parallel = new SileroVadDetector(workers: 4, TestBlockSeconds, TestWarmupSeconds);
        var reference = await single.DetectSpeechAsync(Chunks(audio), CancellationToken.None);
        var blocked = await parallel.DetectSpeechAsync(Chunks(audio), CancellationToken.None);

        Assert.Equal(reference, blocked);
    }

    /// <summary>Frame timestamps must be the exact multiples of the frame duration that
    /// <see cref="VadSegmenter"/> and every downstream consumer assume, with no gap or repeat where
    /// one block ends and the next begins.</summary>
    [Fact]
    public async Task FrameTimestampsAreContiguousAcrossBlockBoundaries()
    {
        var audio = BuildSignal(seconds: 150);

        using var parallel = new SileroVadDetector(workers: 3, TestBlockSeconds, TestWarmupSeconds);
        var frames = await parallel.DetectSpeechProbabilitiesAsync(Chunks(audio), CancellationToken.None);

        Assert.NotEmpty(frames);
        // Every whole frame of the signal is accounted for; only a trailing partial frame is dropped.
        Assert.Equal(audio.Length / FrameSamples, frames.Count);
        for (var i = 0; i < frames.Count; i++)
            Assert.Equal(i * (double)FrameSamples / SampleRate, frames[i].TimeSeconds);
    }

    /// <summary>A worker count above the number of blocks must not change anything either - it just
    /// leaves workers idle.</summary>
    [Fact]
    public async Task MoreWorkersThanBlocksIsHarmless()
    {
        var audio = BuildSignal(seconds: 100);

        using var single = new SileroVadDetector(workers: 1, TestBlockSeconds, TestWarmupSeconds);
        using var many = new SileroVadDetector(workers: 8, TestBlockSeconds, TestWarmupSeconds);
        var reference = await single.DetectSpeechAsync(Chunks(audio), CancellationToken.None);
        var blocked = await many.DetectSpeechAsync(Chunks(audio), CancellationToken.None);

        Assert.Equal(reference, blocked);
    }

    /// <summary>
    /// Builds a signal that alternates between something the model can plausibly call speech and
    /// something it cannot: 3-second stretches of an amplitude- and frequency-modulated tone around
    /// the pitch of a voice, separated by 2 seconds of near-silence. What matters is not that Silero
    /// classifies it the way it would classify a narrator, but that it produces varied,
    /// state-dependent output - a constant signal would agree across any two implementations,
    /// including a broken one.
    /// </summary>
    /// <param name="seconds">Length of the signal.</param>
    private static float[] BuildSignal(double seconds)
    {
        var samples = new float[(int)(seconds * SampleRate)];
        // Fixed seed: a flaky comparison would be impossible to reproduce otherwise, and the point
        // is the agreement between two runs over the same samples, not the samples themselves.
        var random = new Random(20260731);
        for (var i = 0; i < samples.Length; i++)
        {
            var t = i / (double)SampleRate;
            var voiced = t % 5 < 3;
            if (!voiced)
            {
                samples[i] = (float)(0.0005 * (random.NextDouble() - 0.5));
                continue;
            }
            var pitch = 130 + 25 * Math.Sin(2 * Math.PI * 3 * t);
            var envelope = 0.35 * (1 + 0.4 * Math.Sin(2 * Math.PI * 7 * t));
            var tone = Math.Sin(2 * Math.PI * pitch * t) + 0.5 * Math.Sin(2 * Math.PI * 2 * pitch * t)
                       + 0.25 * Math.Sin(2 * Math.PI * 3 * pitch * t);
            samples[i] = (float)(envelope * tone / 1.75 + 0.01 * (random.NextDouble() - 0.5));
        }
        return samples;
    }

    /// <summary>Feeds the signal the way ffmpeg does: in fixed-size chunks that are deliberately not
    /// a multiple of the frame size, so the leftover carry between chunks is exercised.</summary>
    /// <param name="samples">The whole signal.</param>
    private static async IAsyncEnumerable<float[]> Chunks(float[] samples)
    {
        const int chunkSize = 65536;
        for (var offset = 0; offset < samples.Length; offset += chunkSize)
        {
            yield return samples[offset..Math.Min(offset + chunkSize, samples.Length)];
            await Task.Yield();
        }
    }
}
