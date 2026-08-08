// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Audio;
using ABChapterize.Detection;
using Xunit;

namespace ABChapterize.Tests;

/// <summary>
/// Tests for <see cref="JingleCensus"/>, the --verbose jingle tally: cutting a file's stored
/// silences out of the VAD's non-speech regions and keeping what is left when it is long enough to
/// be music. The geometry below is the shape real chapter openings come in - a lead-in hush
/// followed by a sting, an ordinary narration pause with nothing but silence in it - rather than
/// synthetic edge cases.
/// </summary>
public class JingleCensusTests
{
    // The ordinary chapter opening: VAD stops hearing speech, a hush of a second or two, then the
    // music, then the announcement. Only the music half is the jingle.
    [Fact]
    public void Measure_RegionWithALeadInHush_CountsOnlyTheMusicAfterIt()
    {
        var jingles = JingleCensus.Measure(
            [new(100, 112)], [new(100, 102.5)]);

        var jingle = Assert.Single(jingles);
        Assert.Equal(102.5, jingle.StartSeconds, 3);
        Assert.Equal(112, jingle.EndSeconds, 3);
    }

    // A pause between two paragraphs is a non-speech region as much as a jingle is, and the whole
    // point of the census is that it is not one - silencedetect covers it end to end.
    [Fact]
    public void Measure_RegionThatIsAllSilence_CountsNoJingle()
    {
        var jingles = JingleCensus.Measure(
            [new(200, 204)], [new(199.8, 204.2)]);

        Assert.Empty(jingles);
    }

    // Sound either side of a silence deep inside the region: each side is measured on its own, and
    // only the side that clears the 2 s floor survives. (Below the floor is a breath pause VAD
    // called non-speech, not a sting.)
    [Fact]
    public void Measure_SilenceInsideTheRegion_MeasuresEachSideSeparately()
    {
        var jingles = JingleCensus.Measure(
            [new(300, 310)], [new(301.2, 307)]);

        var jingle = Assert.Single(jingles);
        Assert.Equal(307, jingle.StartSeconds, 3);
        Assert.Equal(310, jingle.EndSeconds, 3);
    }

    // Silences are clipped to the region rather than taken whole: a hush that starts before the
    // region (VAD saw the narration stop a moment after the amplitude did) must not shorten the
    // music behind it, and one running past the region's end must not push the cursor into the
    // next chapter's speech.
    [Fact]
    public void Measure_SilencesOverhangingTheRegion_AreClippedToIt()
    {
        var jingles = JingleCensus.Measure(
            [new(400, 409)], [new(398, 401), new(407, 412)]);

        var jingle = Assert.Single(jingles);
        Assert.Equal(401, jingle.StartSeconds, 3);
        Assert.Equal(407, jingle.EndSeconds, 3);
    }

    // Two silences overlapping each other inside one region must not make the walk step backwards
    // and emit a negative-length "jingle" between them.
    [Fact]
    public void Measure_OverlappingSilences_CollapseInsteadOfEmittingAGapBetweenThem()
    {
        var jingles = JingleCensus.Measure(
            [new(500, 512)], [new(500, 505), new(502, 508)]);

        var jingle = Assert.Single(jingles);
        Assert.Equal(508, jingle.StartSeconds, 3);
        Assert.Equal(512, jingle.EndSeconds, 3);
    }

    // Without the VAD pre-pass there are no regions at all, and the census is what the log then
    // has to be able to say nothing about.
    [Fact]
    public void Measure_NoRegions_YieldsAnEmptyCensus()
    {
        Assert.Empty(JingleCensus.Measure([], [new(10, 20)]));
    }

    // The summary line the --verbose log prints, with its three figures.
    [Fact]
    public void Describe_WithJingles_ReportsCountShortestLongestAndAverage()
    {
        var jingles = JingleCensus.Measure(
            [new(100, 110), new(200, 204), new(300, 307)], [new(100, 100.5)]);

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
