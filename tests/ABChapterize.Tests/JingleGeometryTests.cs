// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Audio;
using ABChapterize.Detection;
using ABChapterize.Vad;
using Xunit;

namespace ABChapterize.Tests;

/// <summary>
/// Direct, non-integration tests for <see cref="JingleGeometry.ComputeMarkBeforeJingle"/> and its
/// <see cref="JingleGeometry.RetreatPastNonSpeech"/> backward-walk helper - the --mark-before-jingle
/// placement algorithm's five steps in isolation, without the full detection pipeline
/// <see cref="ChapterDetectorTests"/>'s scripted-jingle scenarios exercise them through.
/// </summary>
public class JingleGeometryTests
{
    // Step 2 (containment): real speech already covers the original mark - an ordinary
    // in-narration pause with no jingle at all - so it is returned unchanged.
    [Fact]
    public void ComputeMarkBeforeJingle_RealSpeechCoversTheMark_ReturnsItUnchanged()
    {
        var result = JingleGeometry.ComputeMarkBeforeJingle(
            100, [], [new(0, 150)]);

        Assert.Equal(100, result);
    }

    // Step 1 backs out of the containing silence to its own start, then step 2's adjacency
    // check (not containment - the silence's start sits just past where real speech ends)
    // recognises real narration ending essentially there and returns the original mark.
    [Fact]
    public void ComputeMarkBeforeJingle_MarkInsideASilence_StepsOutToItsStart_ThenFindsAdjacentSpeech()
    {
        var result = JingleGeometry.ComputeMarkBeforeJingle(
            105, [new(100.2, 110)], [new(0, 100), new(112, 200)]);

        Assert.Equal(105, result);
    }

    // Step 1 backs out of the containing silence; with no real speech at or near its start,
    // steps 3-4 keep retreating through the jingle (the gap between the silence and the last
    // real narration, both of which VAD sees as one continuous non-speech stretch) to the true
    // leading edge.
    [Fact]
    public void ComputeMarkBeforeJingle_MarkInsideASilence_WithNoSpeechAtItsStart_RetreatsToTheJingleStart()
    {
        var result = JingleGeometry.ComputeMarkBeforeJingle(
            105, [new(100, 110)], [new(0, 80), new(110.5, 200)]);

        Assert.Equal(80, result);
    }

    // Steps 3-4: VAD "speech" blips shorter than TransientSpeechFloorSeconds (musical/vocal
    // transients in the jingle's own music) do not stop the retreat - only the first
    // qualifying (>= the floor) segment does.
    [Fact]
    public void ComputeMarkBeforeJingle_IgnoresSubFloorTransients_WhileRetreatingThroughTheMusic()
    {
        var result = JingleGeometry.ComputeMarkBeforeJingle(
            105, [], [new(0, 80), new(90, 90.3), new(95, 95.35), new(110, 200)]);

        Assert.Equal(80, result);
    }

    // Step 5: the retreat runs out of VAD data before ever finding real preceding speech (a
    // jingle sitting at the very start of the file, before there was any narration) - the
    // reached position is backed off by the flat JingleLeadSeconds lead instead.
    [Fact]
    public void ComputeMarkBeforeJingle_NoPrecedingSpeechAtAll_BacksOffByTheFlatLead()
    {
        var result = JingleGeometry.ComputeMarkBeforeJingle(
            3.0, [], [new(10, 20)]);

        Assert.Equal(2.5, result);
    }

    // Step 5's flat lead must never push the mark negative.
    [Fact]
    public void ComputeMarkBeforeJingle_NoPrecedingSpeechAtAll_NeverGoesNegative()
    {
        var result = JingleGeometry.ComputeMarkBeforeJingle(
            0.3, [], []);

        Assert.Equal(0, result);
    }

    // Step 2's cross-detector adjacency tolerance (JingleWalkAdjacencyToleranceSeconds) absorbs
    // silencedetect/VAD boundary jitter right up to its own limit, but not a hair beyond it -
    // past the limit, the walk instead retreats all the way to where the speech actually ends.
    [Theory]
    [InlineData(101.5, 101.5)] // exactly at the tolerance: still "real speech right here"
    [InlineData(101.51, 100)] // just past it: falls through to the retreat, landing at 100
    public void ComputeMarkBeforeJingle_AdjacencyToleranceCoversJitter_ButNotBeyondIt(
        double originalMark, double expected)
    {
        var result = JingleGeometry.ComputeMarkBeforeJingle(
            originalMark, [], [new(0, 100)]);

        Assert.Equal(expected, result);
    }

    // RetreatPastNonSpeech itself: starting inside a qualifying segment never moves the
    // position further than necessary - it is returned unchanged.
    [Fact]
    public void RetreatPastNonSpeech_AlreadyInsideAQualifyingSegment_ReturnsFromUnchanged()
    {
        var (position, foundSpeech) = JingleGeometry.RetreatPastNonSpeech(
            50, [new(40, 60)], 0.4);

        Assert.Equal(50, position);
        Assert.True(foundSpeech);
    }

    // Chains backward through several too-short blips before accepting the first one that
    // meets the minimum length.
    [Fact]
    public void RetreatPastNonSpeech_SkipsShortBlips_ChainingBackToTheFirstQualifyingOne()
    {
        var (position, foundSpeech) = JingleGeometry.RetreatPastNonSpeech(
            100, [new(0, 80), new(85, 85.2), new(92, 92.1)], 0.4);

        Assert.Equal(80, position);
        Assert.True(foundSpeech);
    }

    // When the retreat runs out of speech data entirely, it still reports however far it got
    // (having skipped past any too-short blips along the way) rather than the original
    // starting position - the caller's own fallback (step 5) backs off from that.
    [Fact]
    public void RetreatPastNonSpeech_RunsOutOfData_ReturnsFalseWithTheFurthestPositionReached()
    {
        var (position, foundSpeech) = JingleGeometry.RetreatPastNonSpeech(
            10, [new(2, 2.1)], 0.4);

        Assert.Equal(2, position);
        Assert.False(foundSpeech);
    }
}
