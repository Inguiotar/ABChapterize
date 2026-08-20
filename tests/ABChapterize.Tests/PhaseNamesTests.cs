// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Xunit;
using ABChapterize.Ui;

namespace ABChapterize.Tests;

/// <summary>
/// Tests for <see cref="PhaseNames"/>, whose whole job is to be complete: a phase added without a
/// display wording would show up on the bar under its bare name, which is the sort of thing nobody
/// notices until it is in a release.
/// </summary>
public class PhaseNamesTests
{
    [Fact]
    public void EveryPhase_HasAWordingOfItsOwn()
    {
        foreach (var phase in PhaseNames.All)
        {
            var display = PhaseNames.Display(phase);
            Assert.EndsWith("ing...", display);
            Assert.NotEqual(phase, display);
        }
    }

    [Fact]
    public void EveryPhase_KeepsItsOwnPrefix()
    {
        // The prefixes are what tell the probing passes apart at a glance - "SD-" the descending
        // skim, "SF-" a sub-floor sweep - so a wording must not quietly drop one.
        Assert.Equal("SD-probing...", PhaseNames.Display(PhaseNames.DescendingProbe));
        Assert.Equal("SC-probing...", PhaseNames.Display(PhaseNames.ChronologicalProbe));
        Assert.Equal("SF-probing...", PhaseNames.Display(PhaseNames.SubFloorProbe));
        Assert.Equal("S-probing...", PhaseNames.Display(PhaseNames.SilenceProbe));
        Assert.Equal("J-probing...", PhaseNames.Display(PhaseNames.JingleProbe));
        Assert.Equal("Re-probing...", PhaseNames.Display(PhaseNames.Reprobe));
        Assert.Equal("Re-scanning...", PhaseNames.Display(PhaseNames.Rescan));
    }

    [Fact]
    public void NoTwoPhases_ShareAName()
    {
        Assert.Equal(PhaseNames.All.Count, PhaseNames.All.Distinct().Count());
        Assert.Equal(PhaseNames.All.Count, PhaseNames.All.Select(PhaseNames.Display).Distinct().Count());
    }

    [Fact]
    public void AnUnknownName_IsShownAsItIs()
    {
        // Better a bare name on the bar than an empty label. The empty string is the real case:
        // a tracker that has not begun a phase yet.
        Assert.Equal("", PhaseNames.Display(""));
        Assert.Equal("Something", PhaseNames.Display("Something"));
    }
}
