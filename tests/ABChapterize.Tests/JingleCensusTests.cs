// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Audio;
using ABChapterize.Detection;
using ABChapterize.Vad;
using Xunit;

namespace ABChapterize.Tests;

/// <summary>
/// Tests for <see cref="JingleCensus"/>, the --verbose jingle tally: the stretches between two VAD
/// speech segments, with the file's stored silences cut out of them and vocal transients bridged.
/// The geometry below is the shape real chapter openings come in - a lead-in hush followed by a
/// sting, a sting a vocal blip breaks in two, an ordinary narration pause with nothing but silence
/// in it - rather than synthetic edge cases.
/// </summary>
public class JingleCensusTests
{
    // The ordinary chapter opening: the narration stops, a hush of a second or two, then the music,
    // then the announcement. Only the music half is the jingle.
    [Fact]
    public void Measure_GapWithALeadInHush_CountsOnlyTheMusicAfterIt()
    {
        var jingles = JingleCensus.Measure(
            [new(0, 100), new(112, 130)], [new(100, 102.5)]);

        var jingle = Assert.Single(jingles);
        Assert.Equal(102.5, jingle.StartSeconds, 3);
        Assert.Equal(112, jingle.EndSeconds, 3);
    }

    // A pause between two paragraphs is a gap between speech segments as much as a jingle is, and
    // the whole point of the census is that it is not one - silencedetect covers it end to end.
    [Fact]
    public void Measure_GapThatIsAllSilence_CountsNoJingle()
    {
        var jingles = JingleCensus.Measure(
            [new(180, 200), new(204, 220)], [new(199.8, 204.2)]);

        Assert.Empty(jingles);
    }

    // The transient bridge: Silero picks a vocal-like blip out of the music, and the sting on
    // either side of it is one jingle whose length includes the blip - not two, and not two minus
    // the blip.
    [Fact]
    public void Measure_MusicBrokenByAVocalTransient_CountsOneJingleAcrossIt()
    {
        var jingles = JingleCensus.Measure(
            [new(0, 100), new(104, 104.3), new(108, 120)], []);

        var jingle = Assert.Single(jingles);
        Assert.Equal(100, jingle.StartSeconds, 3);
        Assert.Equal(108, jingle.EndSeconds, 3);
        Assert.Equal(8, jingle.LengthSeconds, 3);
    }

    // The transient is counted, not just bridged: how often Silero picked a vocal out of the music
    // is what tells a plain instrumental sting apart from one with a voice in it.
    [Fact]
    public void Measure_BridgedTransients_AreCountedOnTheJingleTheyLieIn()
    {
        var jingles = JingleCensus.Measure(
            [new(0, 100), new(104, 104.3), new(108, 108.2), new(112, 130)], []);

        var jingle = Assert.Single(jingles);
        Assert.Equal(2, jingle.BridgedBlips);
    }

    // Where the speech behind a jingle resumes is not where the music stops: a hush in between
    // belongs to neither, and the announcement is what a window crossing the jingle has to reach.
    [Fact]
    public void Measure_HushBetweenTheMusicAndTheVoice_LeavesTheAnnouncementPastTheJingleEnd()
    {
        var jingles = JingleCensus.Measure(
            [new(0, 100), new(116, 130)], [new(112, 116)]);

        var jingle = Assert.Single(jingles);
        Assert.Equal(112, jingle.EndSeconds, 3);
        Assert.Equal(116, jingle.AnnouncementSeconds, 3);
    }

    // The case the region list cannot report at all, since it keeps a region only for its longest
    // *contiguous* run: two 1.5 s halves of one sting either side of a 0.3 s transient are 3.3 s of
    // music, and the census is the thing that says so.
    [Fact]
    public void Measure_TwoShortHalvesBridgedByATransient_ClearTheFloorTogether()
    {
        var jingles = JingleCensus.Measure(
            [new(0, 100), new(101.5, 101.8), new(103.3, 120)], []);

        var jingle = Assert.Single(jingles);
        Assert.Equal(3.3, jingle.LengthSeconds, 3);
    }

    // Real speech ends a jingle even where it is brief, so a chapter announcement half a second
    // long is not swallowed into the music before it. The floor is the one --mark-before-jingle's
    // walk uses, and 0.5 s is above it.
    [Fact]
    public void Measure_SpeechAboveTheTransientFloor_EndsTheJingle()
    {
        var jingles = JingleCensus.Measure(
            [new(0, 100), new(104, 104.5), new(108, 120)], []);

        Assert.Equal(2, jingles.Count);
        Assert.Equal(4, jingles[0].LengthSeconds, 3);
        Assert.Equal(3.5, jingles[1].LengthSeconds, 3);
    }

    // Sound either side of a silence deep inside the gap: each side is measured on its own, and
    // only the side that clears the 2 s floor survives. A transient bridges, a silence does not.
    [Fact]
    public void Measure_SilenceInsideTheGap_MeasuresEachSideSeparately()
    {
        var jingles = JingleCensus.Measure(
            [new(280, 300), new(310, 330)], [new(301.2, 307)]);

        var jingle = Assert.Single(jingles);
        Assert.Equal(307, jingle.StartSeconds, 3);
        Assert.Equal(310, jingle.EndSeconds, 3);
    }

    // Silences are clipped to the gap rather than taken whole: a hush that starts before it (VAD
    // saw the narration stop a moment after the amplitude did) must not shorten the music behind
    // it, and one running past the end must not push the cursor into the next chapter's speech.
    [Fact]
    public void Measure_SilencesOverhangingTheGap_AreClippedToIt()
    {
        var jingles = JingleCensus.Measure(
            [new(380, 400), new(409, 430)], [new(398, 401), new(407, 412)]);

        var jingle = Assert.Single(jingles);
        Assert.Equal(401, jingle.StartSeconds, 3);
        Assert.Equal(407, jingle.EndSeconds, 3);
    }

    // Two silences overlapping each other inside one gap must not make the walk step backwards and
    // emit a negative-length "jingle" between them.
    [Fact]
    public void Measure_OverlappingSilences_CollapseInsteadOfEmittingAGapBetweenThem()
    {
        var jingles = JingleCensus.Measure(
            [new(480, 500), new(512, 530)], [new(500, 505), new(502, 508)]);

        var jingle = Assert.Single(jingles);
        Assert.Equal(508, jingle.StartSeconds, 3);
        Assert.Equal(512, jingle.EndSeconds, 3);
    }

    // Narration read in short words with breath pauses bridges into one long span exactly as a
    // transient-broken jingle does - and must still count nothing, because every pause in it is a
    // stored silence and what survives the cut is the words themselves.
    [Fact]
    public void Measure_ShortWordsSeparatedByBreathPauses_CountNoJingle()
    {
        var speech = new List<SpeechSegment>();
        var silences = new List<Silence>();
        for (var i = 0; i < 12; i++)
        {
            speech.Add(new SpeechSegment(i * 1.0, i * 1.0 + 0.3));
            silences.Add(new Silence(i * 1.0 + 0.3, i * 1.0 + 1.0));
        }

        Assert.Empty(JingleCensus.Measure(speech, silences));
    }

    // Non-speech before the first segment and after the last is out of scope: a file's head and
    // tail hold publisher idents and dead air, not chapter music.
    [Fact]
    public void Measure_NonSpeechAtTheFileEdges_IsNotCounted()
    {
        Assert.Empty(JingleCensus.Measure([new(30, 60)], []));
    }

    // Without the VAD pre-pass there are no speech segments at all, and the census is what the log
    // then has to be able to say nothing about.
    [Fact]
    public void Measure_NoSpeechSegments_YieldsAnEmptyCensus()
    {
        Assert.Empty(JingleCensus.Measure([], [new(10, 20)]));
    }

    // The summary line the --verbose log prints, with its three figures.
    [Fact]
    public void Describe_WithJingles_ReportsCountShortestLongestAndAverage()
    {
        var jingles = JingleCensus.Measure(
            [new(90, 100), new(110, 200), new(204, 300), new(307, 320)],
            [new(100, 100.5)]);

        Assert.Equal(
            "3 jingle(s) of >= 2 s found - shortest 4.00 s, longest 9.50 s, average 6.83 s",
            JingleCensus.Describe(jingles));
    }

    // Nothing found still prints, so a book with no music says so rather than leaving the reader
    // wondering whether the tally ran.
    [Fact]
    public void Describe_WithNoJingles_ReportsTheCountAlone()
    {
        Assert.Equal("0 jingle(s) of >= 2 s found", JingleCensus.Describe([]));
    }
}
