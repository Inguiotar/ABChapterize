// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Xunit;
using ABChapterize.Ui;

namespace ABChapterize.Tests;

/// <summary>
/// Tests for <see cref="ProgressRenderer"/>'s rendered-line content. The renderer skips its
/// per-tick redraw whenever the freshly built block is byte-for-byte identical to what is on
/// screen, so these assert that the quantized values a user watches - the percent number and
/// the chapter display - are all part of the built line and therefore always force a redraw when
/// they change, even when the fixed-width bar fill happens not to move.
/// </summary>
public class ProgressRendererTests
{
    /// <summary>Builds a slot whose tracker sits at <paramref name="done"/>/<paramref
    /// name="total"/> progress with the given chapter state, ready for <see
    /// cref="ProgressRenderer.BuildLine"/>.</summary>
    private static (WorkTracker Tracker, string Label) Slot(
        long done, long total, int highestChapter = 0, int missingChapters = 0)
    {
        var t = new WorkTracker();
        t.BeginPhase("Pass 1", total);
        t.SetPhaseProgress(done);
        t.HighestChapter = highestChapter;
        t.MissingChapters = missingChapters;
        return (t, "book.m4b");
    }

    [Fact]
    public void BuildLine_ChangesWhenPercentChanges_EvenWhenBarFillDoesNot()
    {
        // 50 % and 51 % both round to the same 24-cell bar fill (12 cells), so only the percent
        // *number* differs. The line must still change, or a redraw would be skipped and the
        // displayed percentage would freeze a point behind the real progress.
        var at50 = ProgressRenderer.BuildLine(Slot(50, 100));
        var at51 = ProgressRenderer.BuildLine(Slot(51, 100));

        Assert.Contains("50%", at50);
        Assert.Contains("51%", at51);
        Assert.NotEqual(at50, at51);
    }

    [Fact]
    public void BuildLine_ShowsPlaceholder_UntilTheFirstChapterIsFound()
    {
        // Before the first chapter is found (all of Pass 1, the start of Pass 2) a chapter
        // count of zero carries no information, so a "----" placeholder is shown instead.
        var line = ProgressRenderer.BuildLine(Slot(50, 100));
        Assert.Contains("| ---- |", line);
    }

    [Fact]
    public void BuildLine_ChangesWhenAChapterIsFound_AndShowsTheHighestNumber()
    {
        // Same progress, first chapter found: the line must change so the chapter display is
        // redrawn the moment a chapter is detected rather than at the next percent tick.
        var none = ProgressRenderer.BuildLine(Slot(50, 100));
        var one = ProgressRenderer.BuildLine(Slot(50, 100, highestChapter: 1));

        Assert.Contains("| ch 1 |", one);
        Assert.NotEqual(none, one);
    }

    [Fact]
    public void BuildLine_ShowsMissingChapters_AsANegativeCount()
    {
        // Chapter 6 found but two earlier chapters still undetected: "ch 6(-2)". The missing
        // count must also take part in redraw detection (a Pass-3 gap fill changes only it).
        var withMissing = ProgressRenderer.BuildLine(Slot(50, 100, highestChapter: 6, missingChapters: 2));
        var complete = ProgressRenderer.BuildLine(Slot(50, 100, highestChapter: 6));

        Assert.Contains("| ch 6(-2) |", withMissing);
        Assert.Contains("| ch 6 |", complete);
        Assert.NotEqual(withMissing, complete);
    }

    [Fact]
    public void BuildLine_ShowsMuxingInsteadOfChapters_DuringTheMuxingPhase()
    {
        // The chapter list is already final by the time muxing runs, so the phase slot shows
        // "Muxing..." instead of a now-meaningless chapter count, with no separate phase label
        // after the bar (that would just repeat the same word).
        var t = new WorkTracker();
        t.BeginPhase("Muxing", 100);
        t.SetPhaseProgress(50);
        t.HighestChapter = 6;
        var line = ProgressRenderer.BuildLine((t, "book.m4b"));

        Assert.Contains("| Muxing... | book.m4b", line);
        Assert.DoesNotContain("ch 6", line);
        Assert.DoesNotContain(" Muxing 50%", line);
    }

    [Fact]
    public void BuildLine_IsStableWhenNothingChanges()
    {
        // Identical state must produce an identical line - this is exactly what lets the renderer
        // skip the redraw (and the flicker) while nothing is moving.
        var a = ProgressRenderer.BuildLine(Slot(50, 100, highestChapter: 2));
        var b = ProgressRenderer.BuildLine(Slot(50, 100, highestChapter: 2));

        Assert.Equal(a, b);
    }
}
