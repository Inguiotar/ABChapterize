// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Xunit;

namespace ABChapterize.Tests;

/// <summary>
/// Tests for <see cref="ProgressRenderer"/>'s rendered-line content. The renderer skips its
/// per-tick redraw whenever the freshly built block is byte-for-byte identical to what is on
/// screen, so these assert that the two quantized values a user watches - the percent number and
/// the chapter count - are both part of the built line and therefore always force a redraw when
/// they change, even when the fixed-width bar fill happens not to move.
/// </summary>
public class ProgressRendererTests
{
    /// <summary>Builds a slot whose tracker sits at <paramref name="done"/>/<paramref
    /// name="total"/> progress with the given chapter count, ready for <see
    /// cref="ProgressRenderer.BuildLine"/>.</summary>
    private static (WorkTracker Tracker, string Label) Slot(long done, long total, int chapters)
    {
        var t = new WorkTracker();
        t.BeginPhase("Pass 1", total);
        t.SetPhaseProgress(done);
        t.ChaptersFound = chapters;
        return (t, "book.m4b");
    }

    [Fact]
    public void BuildLine_ChangesWhenPercentChanges_EvenWhenBarFillDoesNot()
    {
        // 50 % and 51 % both round to the same 24-cell bar fill (12 cells), so only the percent
        // *number* differs. The line must still change, or a redraw would be skipped and the
        // displayed percentage would freeze a point behind the real progress.
        var at50 = ProgressRenderer.BuildLine(Slot(50, 100, chapters: 0));
        var at51 = ProgressRenderer.BuildLine(Slot(51, 100, chapters: 0));

        Assert.Contains("50%", at50);
        Assert.Contains("51%", at51);
        Assert.NotEqual(at50, at51);
    }

    [Fact]
    public void BuildLine_ChangesWhenChapterCountChanges()
    {
        // Same progress, one more chapter found: the line must change so the "N ch" counter is
        // redrawn the moment a chapter is detected rather than at the next percent tick.
        var zero = ProgressRenderer.BuildLine(Slot(50, 100, chapters: 0));
        var one = ProgressRenderer.BuildLine(Slot(50, 100, chapters: 1));

        Assert.Contains("0 ch", zero);
        Assert.Contains("1 ch", one);
        Assert.NotEqual(zero, one);
    }

    [Fact]
    public void BuildLine_IsStableWhenNothingChanges()
    {
        // Identical state must produce an identical line - this is exactly what lets the renderer
        // skip the redraw (and the flicker) while nothing is moving.
        var a = ProgressRenderer.BuildLine(Slot(50, 100, chapters: 2));
        var b = ProgressRenderer.BuildLine(Slot(50, 100, chapters: 2));

        Assert.Equal(a, b);
    }
}
