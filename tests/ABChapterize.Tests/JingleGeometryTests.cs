// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Audio;
using ABChapterize.Detection;
using ABChapterize.Transcription;
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
            100, [], [new(0, 150)], []);

        Assert.Equal(100, result);
    }

    // Step 1 backs out of the containing silence to its own start, then step 2's adjacency
    // check (not containment - the silence's start sits just past where real speech ends)
    // recognises real narration ending essentially there and returns the original mark.
    [Fact]
    public void ComputeMarkBeforeJingle_MarkInsideASilence_StepsOutToItsStart_ThenFindsAdjacentSpeech()
    {
        var result = JingleGeometry.ComputeMarkBeforeJingle(
            105, [new(100.2, 110)], [new(0, 100), new(112, 200)], []);

        Assert.Equal(105, result);
    }

    // Step 1 backs out of the containing silence to its own start; with no real speech at or
    // near that point and no other, already-passed silence behind it either, steps 3-4 keep
    // retreating through the jingle (the gap between the silence and the last real narration,
    // both of which VAD sees as one continuous non-speech stretch) to the true leading edge.
    [Fact]
    public void ComputeMarkBeforeJingle_MarkInsideASilence_WithNoSpeechAtItsStart_RetreatsToTheJingleStart()
    {
        var result = JingleGeometry.ComputeMarkBeforeJingle(
            105, [new(100, 110)], [new(0, 80), new(110.5, 200)], []);

        Assert.Equal(80, result);
    }

    // Steps 3-4: VAD "speech" blips shorter than TransientSpeechFloorSeconds (musical/vocal
    // transients in the jingle's own music) do not stop the retreat - only the first
    // qualifying (>= the floor) segment does.
    [Fact]
    public void ComputeMarkBeforeJingle_IgnoresSubFloorTransients_WhileRetreatingThroughTheMusic()
    {
        var result = JingleGeometry.ComputeMarkBeforeJingle(
            105, [], [new(0, 80), new(90, 90.3), new(95, 95.35), new(110, 200)], []);

        Assert.Equal(80, result);
    }

    // Confirmed on real audio (chapters 1, 8 and 10 of a test audiobook): a genuine, already-
    // stored silence encountered while retreating through a jingle's own music - one directly
    // preceded by the previous chapter's own trailing narration (here, speech ends exactly at
    // the silence's own start) - is a stop at its own end - the true jingle start, matching
    // LeadingSilence's own anchor rule elsewhere in this file - rather than something to be
    // walked straight through into the narration beyond. This is the "two jingles separated by
    // a real silence" shape: retreating past the second jingle's music must land at the silence
    // between the two, not sail on through it into the tail of the first.
    [Fact]
    public void ComputeMarkBeforeJingle_RetreatCrossesAGenuineSilence_StopsAtItsEndRatherThanThroughIt()
    {
        var result = JingleGeometry.ComputeMarkBeforeJingle(
            102.75, [new(95, 100)], [new(0, 95), new(103, 200)], []);

        Assert.Equal(100, result);
    }

    // Confirmed on real audio (chapter 5): the old, undirected adjacency check let a VAD blip
    // that only *starts* after the point under test - here, effectively the chapter
    // announcement's own trailing word, spoken just after the mark - masquerade as "preceding"
    // speech merely because its end fell close enough. The fix requires the blip to also start
    // at or before that point; with it excluded, the walk correctly keeps retreating past the
    // jingle to the real preceding narration instead of stopping dead just inside it.
    [Fact]
    public void ComputeMarkBeforeJingle_ABlipStartingAfterTheMark_NeverCountsAsPrecedingSpeech()
    {
        var result = JingleGeometry.ComputeMarkBeforeJingle(
            100, [], [new(50, 70), new(100.2, 101.0)], []);

        Assert.Equal(70, result);
    }

    // Confirmed on real audio (chapters 8 and 9): a musical/vocal transient inside jingle music
    // can run well past TransientSpeechFloorSeconds - clearing the duration floor even though it
    // carries no real words. When the transcript actually covers that stretch and transcribes
    // nothing there (Whisper does not silently skip genuine narration), the blip is recognised
    // as music and walked straight through, exactly like a too-short one - landing the mark on
    // the real narration that precedes it instead.
    // Transcript segment spans below are deliberately kept short relative to their text
    // (comfortably above MinPlausibleSpeechCharsPerSecond) - see the pace-based rejection test
    // further down for the case where that pace is exactly what disqualifies a segment.
    [Fact]
    public void ComputeMarkBeforeJingle_AnUncorroboratedBlip_IsTreatedAsMusicAndWalkedThrough()
    {
        var transcript = new List<TranscriptSegment>
        {
            new(0, 4, "Vorheriges Kapitel Text.", 1.0),
            new(60, 90, " ", 1.0), // covers the blip below but transcribes nothing over it
        };

        var result = JingleGeometry.ComputeMarkBeforeJingle(
            105, [], [new(0, 50), new(70, 71.0), new(110, 200)], transcript);

        Assert.Equal(50, result);
    }

    // The corroboration check is not a blanket rejection: a blip the transcript actually covers
    // with real, plausibly-paced words is genuine trailing narration, not music, and stops the
    // retreat right there - same shape as the previous test, but with the blip transcribed this
    // time.
    [Fact]
    public void ComputeMarkBeforeJingle_ACorroboratedBlip_StopsTheRetreatThere()
    {
        var transcript = new List<TranscriptSegment>
        {
            new(0, 4, "Vorheriges Kapitel Text.", 1.0),
            new(69.5, 71.5, "Ein Wort hier.", 1.0),
        };

        var result = JingleGeometry.ComputeMarkBeforeJingle(
            105, [], [new(0, 50), new(70, 71.0), new(110, 200)], transcript);

        Assert.Equal(71.0, result);
    }

    // Confirmed on real audio (chapters 8 and 10): a transcript segment can cover a blip with
    // real, non-blank text and still fail to corroborate it, when Whisper smeared genuine
    // narration together with a reverb-drenched or musically-stretched announcement into one
    // abnormally long segment - "Kapitel 8" spanning 35 s here mirrors the real case. Such a
    // segment's average pace falls far below any plausible spoken rate, so the blip it covers is
    // rejected exactly like an untranscribed one and walked through as music.
    [Fact]
    public void ComputeMarkBeforeJingle_ABlipCorroboratedOnlyByAnImplausiblyPacedSegment_IsWalkedThrough()
    {
        var transcript = new List<TranscriptSegment>
        {
            new(0, 4, "Vorheriges Kapitel Text.", 1.0),
            new(55, 90, "Kapitel 8", 1.0),
        };

        var result = JingleGeometry.ComputeMarkBeforeJingle(
            105, [], [new(0, 50), new(70, 71.0), new(110, 200)], transcript);

        Assert.Equal(50, result);
    }

    // A blip lying entirely outside the transcript's own covered span has nothing to
    // corroborate against - the window Whisper was actually asked to transcribe does not
    // necessarily reach as far back as the retreat walk does - so it falls back to trusting
    // VAD's duration alone, exactly as when no transcript is available at all.
    [Fact]
    public void ComputeMarkBeforeJingle_ABlipOutsideTheTranscriptsCoverage_FallsBackToTrustingVad()
    {
        var transcript = new List<TranscriptSegment> { new(200, 210, "Ganz woanders.", 1.0) };

        var result = JingleGeometry.ComputeMarkBeforeJingle(
            105, [], [new(0, 50), new(70, 71.0), new(110, 200)], transcript);

        Assert.Equal(71.0, result);
    }

    // Step 5: the retreat runs out of VAD data before ever finding real preceding speech (a
    // jingle sitting at the very start of the file, before there was any narration) - the
    // reached position is backed off by the flat JingleLeadSeconds lead instead.
    [Fact]
    public void ComputeMarkBeforeJingle_NoPrecedingSpeechAtAll_BacksOffByTheFlatLead()
    {
        var result = JingleGeometry.ComputeMarkBeforeJingle(
            3.0, [], [new(10, 20)], []);

        Assert.Equal(2.5, result);
    }

    // Step 5's flat lead must never push the mark negative.
    [Fact]
    public void ComputeMarkBeforeJingle_NoPrecedingSpeechAtAll_NeverGoesNegative()
    {
        var result = JingleGeometry.ComputeMarkBeforeJingle(
            0.3, [], [], []);

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
            originalMark, [], [new(0, 100)], []);

        Assert.Equal(expected, result);
    }

    // RetreatPastNonSpeech itself: starting inside a qualifying segment never moves the
    // position further than necessary - it is returned unchanged.
    [Fact]
    public void RetreatPastNonSpeech_AlreadyInsideAQualifyingSegment_ReturnsFromUnchanged()
    {
        var (position, foundBoundary) = JingleGeometry.RetreatPastNonSpeech(
            50, [new(40, 60)], [], [], 0.4);

        Assert.Equal(50, position);
        Assert.True(foundBoundary);
    }

    // The chapter-12 shape (2026-07-26): after a falsely-corroborated leading silence just past
    // it is correctly rejected (see the "skipped over" tests below), the retreat can land exactly
    // inside a sub-floor musical transient deep in the jingle. The straddling check above must
    // apply the same duration/corroboration gate the other blip-handling branch already has,
    // rather than accepting outright just because the position happens to fall inside some VAD
    // speech segment - otherwise a too-short transient like this one (0.384 s, under the 0.4 s
    // floor) is trusted as the true jingle edge, undershooting the walk by most of the jingle's
    // own length. Skipped over, the retreat continues back to the real narration further behind.
    [Fact]
    public void RetreatPastNonSpeech_StartingInsideASubFloorBlip_IsSkippedRatherThanAcceptedOutright()
    {
        var (position, foundBoundary) = JingleGeometry.RetreatPastNonSpeech(
            29.8, [new(0, 20), new(29.6, 29.984)], [], [], 0.4);

        Assert.Equal(20, position);
        Assert.True(foundBoundary);
    }

    // Chains backward through several too-short blips before accepting the first one that
    // meets the minimum length.
    [Fact]
    public void RetreatPastNonSpeech_SkipsShortBlips_ChainingBackToTheFirstQualifyingOne()
    {
        var (position, foundBoundary) = JingleGeometry.RetreatPastNonSpeech(
            100, [new(0, 80), new(85, 85.2), new(92, 92.1)], [], [], 0.4);

        Assert.Equal(80, position);
        Assert.True(foundBoundary);
    }

    // A genuine, already-passed stored silence nearer to the starting position than the nearest
    // qualifying speech blip is a stop at its own end, without ever needing to fall back to that
    // more distant blip - here because real narration ends essentially right at the silence's
    // own start, the genuine "trailing narration then leading silence" shape.
    [Fact]
    public void RetreatPastNonSpeech_AGenuineSilenceIsCloser_StopsAtItsEnd()
    {
        var (position, foundBoundary) = JingleGeometry.RetreatPastNonSpeech(
            70, [new(0, 48.5)], [new(50, 60)], [], 0.4);

        Assert.Equal(60, position);
        Assert.True(foundBoundary);
    }

    // Confirmed on real audio (chapter 1): a stored silence not backed by genuine speech ending
    // near its own start is an ordinary trailing pause - sitting between the announcement's own
    // end and the *next* chapter's real narration - rather than a genuine leading silence. It is
    // skipped over exactly like a too-short or uncorroborated speech blip, and the retreat
    // continues past it to where real speech actually ends further back - same setup as the
    // previous test, but with the gap between the silence and that speech now far beyond
    // JingleWalkAdjacencyToleranceSeconds.
    [Fact]
    public void RetreatPastNonSpeech_ASilenceNotBackedByNearbyTrailingNarration_IsSkippedOver()
    {
        var (position, foundBoundary) = JingleGeometry.RetreatPastNonSpeech(
            70, [new(0, 40)], [new(50, 60)], [], 0.4);

        Assert.Equal(40, position);
        Assert.True(foundBoundary);
    }

    // Confirmed on real audio (chapter 1): the "two silences" shape - an unbacked trailing pause
    // sitting closer to the starting position, with the true, narration-backed leading silence
    // further back behind it. The retreat must skip the first (per the previous test) and
    // continue past the intervening jingle music to find the second, rather than stopping at the
    // nearer, spurious one.
    [Fact]
    public void RetreatPastNonSpeech_SkipsAnUnbackedTrailingPause_ToReachTheGenuineLeadingSilenceBehindIt()
    {
        var (position, foundBoundary) = JingleGeometry.RetreatPastNonSpeech(
            100, [new(0, 50)], [new(50, 52), new(95, 97)], [], 0.4);

        Assert.Equal(52, position);
        Assert.True(foundBoundary);
    }

    // When the retreat runs out of speech data entirely, it still reports however far it got
    // (having skipped past any too-short blips along the way) rather than the original
    // starting position - the caller's own fallback (step 5) backs off from that.
    [Fact]
    public void RetreatPastNonSpeech_RunsOutOfData_ReturnsFalseWithTheFurthestPositionReached()
    {
        var (position, foundBoundary) = JingleGeometry.RetreatPastNonSpeech(
            10, [new(2, 2.1)], [], [], 0.4);

        Assert.Equal(2, position);
        Assert.False(foundBoundary);
    }
}
