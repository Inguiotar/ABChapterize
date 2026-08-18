// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Audio;
using ABChapterize.Detection;
using Xunit;

namespace ABChapterize.Tests;

/// <summary>
/// The rule that promotes a sub-threshold pause to a probe candidate when a probed pause follows it
/// closely enough for an announcement to sit between the two.
/// <para>
/// Both worked examples are real chapters that this rule exists because of, with their geometry
/// taken from the build-331 debug logs, so a change that breaks either breaks a chapter that was
/// once lost to exactly this.
/// </para>
/// </summary>
public class SandwichedSilenceTests
{
    /// <summary>The adaptive floor a run uses by default - the shortest pause it will entertain as a
    /// chapter break, and so the shortest this rule may promote.</summary>
    private const double Floor = 0.8;

    /// <summary>Runs the rule over a whole file's worth of silences. The stretch is wider than any
    /// audiobook so the region bounds never decide a case these tests are making.</summary>
    /// <param name="all">Every silence Analyze kept.</param>
    /// <param name="probed">The ones that already have a window.</param>
    private static List<Silence> Promote(Silence[] all, params Silence[] probed)
        => RegionProber.SandwichedSilences(all, probed, Floor, 0, 1_000_000).ToList();

    /// <summary>
    /// "De vandrande djäknarne" chapter 2, which this rule was written for: a 0.95 s pause, then
    /// 2.89 s of "Andra kapitlet Masugnen", then the 2.75 s pause that is the only candidate. The
    /// candidate's own window opens after the announcement has been spoken, so without promoting the
    /// 0.95 s pause nothing ever reads it - and the chapter was duly lost in build 331.
    /// </summary>
    [Fact]
    public void TheSwedishChapterTwoPause_IsPromoted()
    {
        var announcementPause = new Silence(801.26, 802.22);
        var candidate = new Silence(805.11, 807.86);
        var promoted = Promote([new Silence(791.56, 792.46), announcementPause, candidate], candidate);
        Assert.Equal([announcementPause], promoted);
    }

    /// <summary>
    /// "The Philosopher's Stone" chapter 11, the second case: 1.39 s, then 0.98 s of
    /// "CHAPTER XI QUIDDITCH", then a 1.78 s candidate. Shorter announcement, longer pause - the
    /// same shape.
    /// </summary>
    [Fact]
    public void ThePhilosophersStoneChapterElevenPause_IsPromoted()
    {
        var announcementPause = new Silence(17476.08, 17477.46);
        var candidate = new Silence(17478.44, 17480.22);
        var promoted = Promote([announcementPause, candidate], candidate);
        Assert.Equal([announcementPause], promoted);
    }

    /// <summary>A pause with nothing but narration behind it is a breath, not the front of an
    /// announcement, and promoting those is what would have cost 1.90x the corpus's candidates.</summary>
    [Fact]
    public void APauseWithNoCandidateBehindIt_IsNotPromoted()
    {
        var candidate = new Silence(900.0, 902.0);
        Assert.Empty(Promote([new Silence(100.0, 101.2), candidate], candidate));
    }

    /// <summary>Speech longer than an announcement between the two pauses means they bracket
    /// narration, not a heading.</summary>
    [Fact]
    public void APauseTooFarAheadOfTheCandidate_IsNotPromoted()
    {
        var candidate = new Silence(910.0, 912.0);
        var early = new Silence(900.0, 901.0);   // 9 s of speech before the candidate
        Assert.Empty(Promote([early, candidate], candidate));
    }

    /// <summary>Below the floor a pause is not a chapter break in any pass's opinion, so it is not
    /// promoted however well-placed it is.</summary>
    [Fact]
    public void APauseBelowTheAdaptiveFloor_IsNotPromoted()
    {
        var candidate = new Silence(805.11, 807.86);
        var tooShort = new Silence(801.60, 802.22);  // 0.62 s, under the 0.8 s floor
        Assert.Empty(Promote([tooShort, candidate], candidate));
    }

    /// <summary>A pause that already has a window of its own must not get a second one.</summary>
    [Fact]
    public void APauseThatIsAlreadyACandidate_IsNotPromotedAgain()
    {
        var first = new Silence(800.0, 802.0);
        var second = new Silence(803.0, 805.0);
        Assert.Empty(Promote([first, second], first, second));
    }
}
