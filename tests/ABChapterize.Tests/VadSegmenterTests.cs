// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Xunit;
using ABChapterize.Vad;

namespace ABChapterize.Tests;

/// <summary>
/// Tests for <see cref="VadSegmenter"/>, the pure per-frame-probability-to-speech-segment
/// hysteresis helper behind <see cref="SileroVadDetector"/>. No ONNX involved: frame
/// probabilities are supplied directly.
/// </summary>
public sealed class VadSegmenterTests
{
    /// <summary>Builds a (time, probability) frame at the given time.</summary>
    private static (double TimeSeconds, float Probability) F(double t, float p) => (t, p);

    [Fact]
    public void ContinuousSpeech_ReturnsOneSegment_ExtendingToTheLastFrame()
    {
        var frames = new[] { F(0, 0.9f), F(0.2, 0.9f), F(0.4, 0.9f), F(0.6, 0.9f), F(0.8, 0.9f) };
        var segments = VadSegmenter.Smooth(frames);
        Assert.Equal([new SpeechSegment(0, 0.8)], segments);
    }

    [Fact]
    public void NonSpeechThroughout_ReturnsNoSegments()
    {
        var frames = new[] { F(0, 0.1f), F(0.2, 0.1f), F(0.4, 0.05f) };
        Assert.Empty(VadSegmenter.Smooth(frames));
    }

    [Fact]
    public void BriefDip_ShorterThanMinSilence_DoesNotFragmentTheRun()
    {
        // A one-frame dip below threshold (0.05 s, well under MinSilenceSeconds = 0.1 s) - a
        // consonant or breath mid-narration - must not split the passage into two segments.
        var frames = new[]
        {
            F(0, 0.9f), F(0.1, 0.9f), F(0.15, 0.1f) /* dip */, F(0.2, 0.9f), F(0.3, 0.9f),
        };
        var segments = VadSegmenter.Smooth(frames);
        Assert.Equal([new SpeechSegment(0, 0.3)], segments);
    }

    [Fact]
    public void ShortSpike_BelowMinSpeechDuration_IsDiscarded()
    {
        // A 0.2 s speech-probability spike, confirmed ended by 0.15 s of silence, is discarded
        // for being shorter than MinSpeechSeconds (0.25 s) - a spurious model spike, not a
        // real speech run.
        var frames = new[] { F(1.0, 0.9f), F(1.1, 0.9f), F(1.2, 0.1f), F(1.35, 0.1f) };
        Assert.Empty(VadSegmenter.Smooth(frames));
    }

    [Fact]
    public void ConfirmedSilence_EndsTheRunAtTheSilenceStart()
    {
        // Non-speech persisting >= MinSilenceSeconds ends the run; the segment's end is the
        // time the probability first dropped, not the time the gap was confirmed.
        var frames = new[]
        {
            F(0, 0.9f), F(0.1, 0.9f), F(0.2, 0.9f), F(0.3, 0.9f),
            F(0.4, 0.1f) /* silence starts here */, F(0.55, 0.1f) /* confirmed: 0.15 s >= 0.1 s */,
        };
        Assert.Equal([new SpeechSegment(0, 0.4)], VadSegmenter.Smooth(frames));
    }

    [Fact]
    public void MultipleSpeechRuns_AreAllReturned_SeparatedByConfirmedSilence()
    {
        var frames = new[]
        {
            F(0, 0.9f), F(0.1, 0.9f), F(0.2, 0.9f), F(0.3, 0.9f),
            F(0.4, 0.1f), F(0.55, 0.1f), // gap confirmed -> segment (0, 0.4)
            F(0.7, 0.9f), F(0.8, 0.9f), F(0.9, 0.9f), F(1.0, 0.9f),
            F(1.1, 0.1f), F(1.25, 0.1f), // gap confirmed -> segment (0.7, 1.1)
        };
        Assert.Equal(
            [new SpeechSegment(0, 0.4), new SpeechSegment(0.7, 1.1)],
            VadSegmenter.Smooth(frames));
    }
}
