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
    /// <param name="done">Work booked in the current phase.</param>
    /// <param name="total">The phase's total work.</param>
    /// <param name="highestChapter">Highest chapter number found so far.</param>
    /// <param name="missingChapters">Chapters below it still outstanding.</param>
    /// <param name="extraMarks">Prologue/epilogue/--custom marks found so far.</param>
    /// <param name="namedMarks">All named marks including chapter announcements; defaults to
    /// <paramref name="extraMarks"/>, which is what every mode but --ignore-chapter-numbers
    /// reports.</param>
    private static (WorkTracker Tracker, string Label) Slot(
        long done, long total, int highestChapter = 0, int missingChapters = 0,
        int extraMarks = 0, int? namedMarks = null)
    {
        var t = new WorkTracker();
        t.BeginPhase("Analyze", total);
        t.SetPhaseProgress(done);
        t.HighestChapter = highestChapter;
        t.MissingChapters = missingChapters;
        t.ExtraMarks = extraMarks;
        t.NamedMarks = namedMarks ?? extraMarks;
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
        // Before the first chapter is found (all of Analyze, the start of Probe) a chapter
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
        // count must also take part in redraw detection (a Scan gap fill changes only it).
        var withMissing = ProgressRenderer.BuildLine(Slot(50, 100, highestChapter: 6, missingChapters: 2));
        var complete = ProgressRenderer.BuildLine(Slot(50, 100, highestChapter: 6));

        Assert.Contains("| ch 6(-2) |", withMissing);
        Assert.Contains("| ch 6 |", complete);
        Assert.NotEqual(withMissing, complete);
    }

    [Fact]
    public void BuildLine_ShowsExtraMarks_AsAPositiveCount()
    {
        var withExtras = ProgressRenderer.BuildLine(Slot(50, 100, highestChapter: 5, extraMarks: 1));
        var plain = ProgressRenderer.BuildLine(Slot(50, 100, highestChapter: 5));

        Assert.Contains("| ch 5(+1) |", withExtras);
        Assert.Contains("| ch 5 |", plain);
        Assert.NotEqual(withExtras, plain);
    }

    [Fact]
    public void BuildLine_ShowsMissingChaptersBeforeExtraMarks_InOneBracket()
    {
        // Both counts share a single bracket pair, outstanding work first: "ch 5(-1+1)".
        var line = ProgressRenderer.BuildLine(
            Slot(50, 100, highestChapter: 5, missingChapters: 1, extraMarks: 1));
        Assert.Contains("| ch 5(-1+1) |", line);
    }

    [Fact]
    public void BuildLine_ShowsChapterZero_WhenOnlyExtraMarksAreFound()
    {
        // A prologue routinely arrives before chapter 1, and "----" would deny it exists. The
        // zero is worth printing here precisely because the bracket beside it is not empty.
        var line = ProgressRenderer.BuildLine(Slot(50, 100, extraMarks: 1));
        Assert.Contains("| ch 0(+1) |", line);
    }

    [Fact]
    public void BuildLine_KeepsThePlainMarkTotal_WhenChapterNumbersAreIgnored()
    {
        // --ignore-chapter-numbers files chapter announcements among the named marks, so the
        // extra count alone (here: one prologue) would understate a yield of twelve marks.
        var line = ProgressRenderer.BuildLine(Slot(50, 100, extraMarks: 1, namedMarks: 12));
        Assert.Contains("| mk 12 |", line);
    }

    [Fact]
    public void BuildLine_ShowsTheFinishLabelInsteadOfChapters_WhileTheFileIsWritten()
    {
        // The chapter list is already final by the time the file is written, so the phase slot
        // shows the label instead of a now-meaningless chapter count, with no separate phase
        // label after the bar (that would just repeat the same word).
        var t = new WorkTracker();
        t.BeginPhase(WorkTracker.FinishPhaseLabel, 100);
        t.SetPhaseProgress(50);
        t.HighestChapter = 6;
        var line = ProgressRenderer.BuildLine((t, "book.m4b"));

        Assert.Contains("| Finish | 0:00 | book.m4b", line);
        Assert.DoesNotContain("ch 6", line);
        Assert.DoesNotContain(" Finish 50%", line);
    }

    [Fact]
    public void BuildLine_MarksThePhaseAsRevisiting_WhileItReReadsGroundItCovered()
    {
        // A sequence-gap re-probe runs inside Probe and walks backwards through candidates the
        // phase has already counted, so its percentage falls. The suffix is what says why.
        var t = new WorkTracker();
        t.BeginPhase("Probe", 100);
        t.SetPhaseProgress(40);
        Assert.Contains(" Probe |", ProgressRenderer.BuildLine((t, "book.m4b")));

        t.PhaseRevisiting = true;
        Assert.Contains(" Probe<< |", ProgressRenderer.BuildLine((t, "book.m4b")));

        t.PhaseRevisiting = false;
        Assert.Contains(" Probe |", ProgressRenderer.BuildLine((t, "book.m4b")));
    }

    [Fact]
    public void BeginPhase_ClearsTheRevisitingMarker_SoItCannotLeakIntoTheNextPhase()
    {
        // The flag is cleared by whoever set it, but a re-probe abandoned by an exception would
        // otherwise leave every later phase labelled as if it were re-reading.
        var t = new WorkTracker();
        t.BeginPhase("Probe", 100);
        t.PhaseRevisiting = true;

        t.BeginPhase("Scan", 100);

        Assert.False(t.PhaseRevisiting);
        Assert.Equal("Scan", t.PhaseLabel);
    }

    [Fact]
    public void BuildLine_ShowsElapsedTimer_AsItsOwnSectionBeforeTheFileName()
    {
        // A freshly built tracker is always at "0:00" - real elapsed-time values are covered
        // directly by FormatElapsedTimer_* below, without needing to wait on a live Stopwatch.
        var line = ProgressRenderer.BuildLine(Slot(50, 100));
        Assert.Contains("| 0:00 | book.m4b", line);
    }

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(45, "0:45")]
    [InlineData(59, "0:59")]
    [InlineData(60, "1:00")]
    [InlineData(65, "1:05")]
    [InlineData(150, "2:30")]
    public void FormatElapsedTimer_UsesHourColonMinutesFormat(int minutes, string expected)
    {
        Assert.Equal(expected, ProgressRenderer.FormatElapsedTimer(TimeSpan.FromMinutes(minutes)));
    }

    [Fact]
    public void FormatElapsedTimer_TruncatesPartialMinutes()
    {
        // Granularity is whole minutes: 59.9s still reads "0:00", not rounded up to "0:01".
        Assert.Equal("0:00", ProgressRenderer.FormatElapsedTimer(TimeSpan.FromSeconds(59.9)));
    }

    [Fact]
    public void BuildSpans_JoinToExactlyTheLineTheyRender()
    {
        // Colors are applied at write time, so the spans must carry the visible text and nothing
        // else: the renderer measures and compares the joined string, and any discrepancy between
        // the two would show up as wrong truncation or a skipped redraw rather than as wrong color.
        var slot = Slot(50, 100, highestChapter: 6, missingChapters: 2);
        Assert.Equal(
            ProgressRenderer.BuildLine(slot),
            ConsoleColors.PlainText(ProgressRenderer.BuildSpans(slot)));
    }

    [Fact]
    public void BuildSpans_DrawTheBarWhite_BetweenDarkGrayBrackets()
    {
        var spans = ProgressRenderer.BuildSpans(Slot(50, 100));

        Assert.Equal("[", spans[0].Text);
        Assert.Equal(ConsoleColor.DarkGray, spans[0].Color);
        Assert.Equal(24, spans[1].Text.Length);
        Assert.Equal(ConsoleColor.White, spans[1].Color);
        Assert.Equal("]", spans[2].Text);
        Assert.Equal(ConsoleColor.DarkGray, spans[2].Color);
    }

    [Fact]
    public void BuildSpans_SeparateEverySectionWithADarkGrayPipe()
    {
        var spans = ProgressRenderer.BuildSpans(Slot(50, 100, highestChapter: 6));

        Assert.All(spans.Where(s => s.Text == " | "), s => Assert.Equal(ConsoleColor.DarkGray, s.Color));
        // The percentage leads, the phase follows it behind a pipe of its own, and the file name
        // closes the line.
        Assert.Equal(
            "[############------------]  50% | Analyze | ch 6 | 0:00 | book.m4b",
            ConsoleColors.PlainText(spans));
    }

    [Fact]
    public void BuildSpans_ColorThePercentageAndTheTimerAlike()
    {
        var spans = ProgressRenderer.BuildSpans(Slot(50, 100));
        var percent = spans.Single(s => s.Text == "  50%");
        var timer = spans.Single(s => s.Text == "0:00");

        Assert.Equal(ConsoleColor.Cyan, percent.Color);
        Assert.Equal(percent.Color, timer.Color);
    }

    [Fact]
    public void BuildSpans_MuteTheChapterSectionWhileItIsStillAPlaceholder()
    {
        // Nothing to read there yet, so "----" sits at the separators' dark grey rather than
        // claiming the chapter color for a count that does not exist.
        var placeholder = ProgressRenderer.BuildSpans(Slot(50, 100)).Single(s => s.Text == "----");
        Assert.Equal(ConsoleColor.DarkGray, placeholder.Color);

        var found = ProgressRenderer.BuildSpans(Slot(50, 100, highestChapter: 6)).Single(s => s.Text == "ch 6");
        Assert.Equal(ConsoleColor.DarkGreen, found.Color);
    }

    [Fact]
    public void BuildSpans_SplitTheMissingCountOffFromItsBrackets()
    {
        // The brackets are structure and stay grey; the negative number is the one thing on the
        // line reporting something outstanding, so it alone goes dark red.
        var spans = ProgressRenderer.BuildSpans(Slot(50, 100, highestChapter: 6, missingChapters: 2));
        var tail = spans.SkipWhile(s => s.Text != "ch 6").Take(4).ToList();

        Assert.Equal([("ch 6", ConsoleColor.DarkGreen), ("(", ConsoleColor.DarkGray),
                      ("-2", ConsoleColor.DarkRed), (")", ConsoleColor.DarkGray)],
            tail.Select(s => (s.Text, s.Color)));
    }

    [Fact]
    public void BuildSpans_ColorTheExtraMarkCount_LikeTheChapterCount()
    {
        // Extra marks are yield, not a problem, so they share the chapter count's green and only
        // the sign sets them apart from the missing count sitting right next to them.
        var spans = ProgressRenderer.BuildSpans(
            Slot(50, 100, highestChapter: 6, missingChapters: 2, extraMarks: 3));
        var tail = spans.SkipWhile(s => s.Text != "ch 6").Take(5).ToList();

        Assert.Equal([("ch 6", ConsoleColor.DarkGreen), ("(", ConsoleColor.DarkGray),
                      ("-2", ConsoleColor.DarkRed), ("+3", ConsoleColor.DarkGreen),
                      (")", ConsoleColor.DarkGray)],
            tail.Select(s => (s.Text, s.Color)));
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
