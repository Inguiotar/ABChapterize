// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Xunit;
using ABChapterize.Ui;

namespace ABChapterize.Tests;

/// <summary>
/// Tests for <see cref="ProgressRenderer"/>'s rendered-block content. The renderer skips its
/// per-tick redraw whenever the freshly built block is byte-for-byte identical to what is on
/// screen, so these assert that the quantized values a user watches - the percent number and
/// the chapter display - are all part of the built block and therefore always force a redraw when
/// they change, even when the bar fill happens not to move.
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
    /// <param name="sequences">One high per part, for a book whose numbering restarts.</param>
    private static (WorkTracker Tracker, string Label) Slot(
        long done, long total, int highestChapter = 0, int missingChapters = 0,
        int extraMarks = 0, int? namedMarks = null, int[]? sequences = null)
    {
        var t = new WorkTracker();
        t.BeginPhase(PhaseNames.Analyze, total);
        t.SetPhaseProgress(done);
        // The single-sequence spelling is the common one, so it stays the short parameter;
        // sequences takes the multi-part case, where 0 is not a value the detector can produce.
        t.HighestChapters = sequences ?? (highestChapter == 0 ? [] : [highestChapter]);
        t.MissingChapters = missingChapters;
        t.ExtraMarks = extraMarks;
        t.NamedMarks = namedMarks ?? extraMarks;
        return (t, "book.m4b");
    }

    /// <summary>A console width whose bar comes to a round 40 cells, so the expected strings below
    /// can be written out in full without counting.</summary>
    private const int Width = 49;

    /// <summary>The status line of a slot, i.e. the second of the block's two lines.</summary>
    /// <param name="slot">The tracker and label to render.</param>
    private static string Status((WorkTracker Tracker, string Label) slot)
        => ConsoleColors.PlainText(ProgressRenderer.BuildStatusSpans(slot));

    /// <summary>The bar line of a slot, i.e. the first of the block's two lines.</summary>
    /// <param name="slot">The tracker and label to render.</param>
    /// <param name="width">The console width to draw the bar to.</param>
    /// <param name="color">Whether to render it for a console that has color.</param>
    private static string Bar(
        (WorkTracker Tracker, string Label) slot, int width = Width, bool color = true)
        => ConsoleColors.PlainText(ProgressRenderer.BuildBarSpans(slot.Tracker, width, color));

    /// <summary>Just the cells between the bar's brackets, as text-and-color pairs, so a
    /// highlighting expectation can be written out without the line's other sections.</summary>
    /// <param name="tracker">The tracker to render a bar for.</param>
    private static List<(string Text, ConsoleColor? Color)> BarCells(WorkTracker tracker)
        => [.. ProgressRenderer.BuildBarSpans(tracker, Width)
                .SkipWhile(s => s.Text != "[").Skip(1).TakeWhile(s => s.Text != "]")
                .Select(s => (s.Text, s.Color))];

    [Fact]
    public void BuildLine_ChangesWhenPercentChanges_EvenWhenBarFillDoesNot()
    {
        // 50 % and 51 % both round to the same 40-cell bar fill (20 cells), so only the percent
        // *number* differs. The block must still change, or a redraw would be skipped and the
        // displayed percentage would freeze a point behind the real progress.
        var at50 = ProgressRenderer.BuildLine(Slot(50, 100), Width);
        var at51 = ProgressRenderer.BuildLine(Slot(51, 100), Width);

        Assert.Contains("50%", at50);
        Assert.Contains("51%", at51);
        Assert.NotEqual(at50, at51);
    }

    [Fact]
    public void BuildBlock_PutsTheBarAboveAndEverythingElseBelow()
    {
        // The bar is drawn console-wide, which leaves no room beside it - so the phase, the chapter
        // state, the timer and the file name get a line of their own underneath.
        var block = ProgressRenderer.BuildBlock(Slot(50, 100, highestChapter: 6), Width);

        Assert.Equal(ProgressRenderer.BlockLines, block.Count);
        Assert.Equal(
            " [####################--------------------]  50% ",
            ConsoleColors.PlainText(block[0]));
        Assert.Equal(" Analyzing... | ch 6 | 0:00 | book.m4b", ConsoleColors.PlainText(block[1]));
    }

    [Fact]
    public void BuildBarSpans_FillTheConsoleWidth_WhateverItIs()
    {
        // One space, the bracketed bar, " 100%" and one closing space: the bar takes every column
        // the rest of the line does not.
        foreach (var width in new[] { 40, 49, 80, 120, 200 })
            Assert.Equal(width, Bar(Slot(50, 100), width).Length);
    }

    [Fact]
    public void BuildBarSpans_KeepTheBarAtAUsableWidth_OnAVeryNarrowConsole()
    {
        // Below thirteen columns the arithmetic would start asking for a negative bar. The line
        // then overruns the console instead, which Render's own note calls out as deliberate.
        var bar = Bar(Slot(50, 100), 8);
        Assert.Contains("[##--]", bar);
    }

    [Fact]
    public void DrawnRows_AreTheBlockLines_WhileTheConsoleKeepsTheWidthItWasDrawnAt()
    {
        // The ordinary case, and the one ClearBar's cursor arithmetic was written for: every line
        // is shorter than the window, so each takes one row.
        Assert.Equal(
            ProgressRenderer.BlockLines, ProgressRenderer.DrawnRows([Width, 38], Width + 1));
    }

    [Fact]
    public void DrawnRows_CountTheExtraRows_WhenTheConsoleHasBeenNarrowedUnderTheBar()
    {
        // A 200-column bar line re-wraps into three rows at 80 columns, the short status line into
        // one: erasing only BlockLines of the four is what leaves a strip of old bar on screen.
        Assert.Equal(4, ProgressRenderer.DrawnRows([200, 38], 80));

        // Exactly as wide as the window still fits on one row - the console's newline is absorbed
        // by the pending wrap rather than opening a row of its own.
        Assert.Equal(2, ProgressRenderer.DrawnRows([80, 38], 80));
        Assert.Equal(3, ProgressRenderer.DrawnRows([81, 38], 80));
    }

    [Fact]
    public void DrawnRows_FallBackToTheBlockLines_WhenNothingIsKnownAboutWhatWasDrawn()
    {
        // A console that reports no width, and the state right after construction: neither says
        // anything about the screen, so the erase stays what it always was.
        Assert.Equal(ProgressRenderer.BlockLines, ProgressRenderer.DrawnRows([200, 38], 0));
        Assert.Equal(ProgressRenderer.BlockLines, ProgressRenderer.DrawnRows([], 80));
    }

    [Fact]
    public void BuildLine_ShowsPlaceholder_UntilTheFirstChapterIsFound()
    {
        // Before the first chapter is found (all of Analyze, the start of Probe) a chapter
        // count of zero carries no information, so a "----" placeholder is shown instead.
        Assert.Contains("| ---- |", Status(Slot(50, 100)));
    }

    [Fact]
    public void BuildLine_ChangesWhenAChapterIsFound_AndShowsTheHighestNumber()
    {
        // Same progress, first chapter found: the block must change so the chapter display is
        // redrawn the moment a chapter is detected rather than at the next percent tick.
        var none = ProgressRenderer.BuildLine(Slot(50, 100), Width);
        var one = ProgressRenderer.BuildLine(Slot(50, 100, highestChapter: 1), Width);

        Assert.Contains("| ch 1 |", one);
        Assert.NotEqual(none, one);
    }

    [Fact]
    public void BuildLine_ShowsMissingChapters_AsANegativeCount()
    {
        // Chapter 6 found but two earlier chapters still undetected: "ch 6(-2)". The missing
        // count must also take part in redraw detection (a Scan gap fill changes only it).
        var withMissing = ProgressRenderer.BuildLine(
            Slot(50, 100, highestChapter: 6, missingChapters: 2), Width);
        var complete = ProgressRenderer.BuildLine(Slot(50, 100, highestChapter: 6), Width);

        Assert.Contains("| ch 6(-2) |", withMissing);
        Assert.Contains("| ch 6 |", complete);
        Assert.NotEqual(withMissing, complete);
    }

    [Fact]
    public void BuildLine_ShowsExtraMarks_AsAPositiveCount()
    {
        Assert.Contains("| ch 5(+1) |", Status(Slot(50, 100, highestChapter: 5, extraMarks: 1)));
        Assert.Contains("| ch 5 |", Status(Slot(50, 100, highestChapter: 5)));
    }

    [Fact]
    public void BuildLine_ShowsMissingChaptersBeforeExtraMarks_InOneBracket()
    {
        // Both counts share a single bracket pair, outstanding work first: "ch 5(-1+1)".
        Assert.Contains("| ch 5(-1+1) |",
            Status(Slot(50, 100, highestChapter: 5, missingChapters: 1, extraMarks: 1)));
    }

    [Fact]
    public void BuildLine_ShowsChapterZero_WhenOnlyExtraMarksAreFound()
    {
        // A prologue routinely arrives before chapter 1, and "----" would deny it exists. The
        // zero is worth printing here precisely because the bracket beside it is not empty.
        Assert.Contains("| ch 0(+1) |", Status(Slot(50, 100, extraMarks: 1)));
    }

    [Fact]
    public void BuildLine_ShowsOneChapterNumberPerPart_WhenTheNumberingRestarts()
    {
        // A book in parts has no single "how far in": part 3's chapter 4 has not gone backwards
        // from part 1's chapter 11, and reporting the last part alone would say it had. Each
        // part's own high, in part order. The missing/extra bracket stays one total across them.
        Assert.Contains("| ch 11,15,4(+1) |",
            Status(Slot(50, 100, sequences: [11, 15, 4], extraMarks: 1)));
    }

    [Fact]
    public void BuildLine_KeepsTheSingleNumberSpelling_ForAnOrdinaryBook()
    {
        // One part is by far the common case and must read exactly as it always did - no comma,
        // no brackets around a single figure.
        Assert.Contains("| ch 6(-2+1) |",
            Status(Slot(50, 100, highestChapter: 6, missingChapters: 2, extraMarks: 1)));
    }

    [Fact]
    public void BuildLine_KeepsThePlainMarkTotal_WhenChapterNumbersAreIgnored()
    {
        // --ignore-chapter-numbers files chapter announcements among the named marks, so the
        // extra count alone (here: one prologue) would understate a yield of twelve marks.
        Assert.Contains("| mk 12 |", Status(Slot(50, 100, extraMarks: 1, namedMarks: 12)));
    }

    [Fact]
    public void BuildLine_DropsTheChapterCount_WhileTheFileIsWritten()
    {
        // The chapter list is already final by the time the file is written, so the status line
        // drops a count nothing can change any more and shows the phase alone.
        var t = new WorkTracker();
        t.BeginPhase(PhaseNames.Finish, 100);
        t.SetPhaseProgress(50);
        t.HighestChapters = [6];

        Assert.Equal(" Finishing... | 0:00 | book.m4b", Status((t, "book.m4b")));
    }

    [Fact]
    public void BuildLine_SpellsEveryPhaseAsSomethingInProgress()
    {
        // A phase name is a label on something happening right now, not a heading.
        var t = new WorkTracker();
        t.BeginPhase(PhaseNames.Scan, 100);

        Assert.StartsWith(" Scanning... |", Status((t, "book.m4b")));
    }

    [Fact]
    public void BuildLine_MarksThePhaseAsRevisiting_WhileItReReadsGroundItCovered()
    {
        // A sequence-gap re-probe runs inside Probe and walks backwards through candidates the
        // phase has already counted, so its percentage falls. The suffix is what says why.
        var t = new WorkTracker();
        t.BeginPhase(PhaseNames.Probe, 100);
        t.SetPhaseProgress(40);
        Assert.StartsWith(" Probing... |", Status((t, "book.m4b")));

        t.PhaseRevisiting = true;
        Assert.StartsWith(" Probing... (<<) |", Status((t, "book.m4b")));

        t.PhaseRevisiting = false;
        Assert.StartsWith(" Probing... |", Status((t, "book.m4b")));
    }

    [Fact]
    public void BeginPhase_ClearsTheRevisitingMarker_SoItCannotLeakIntoTheNextPhase()
    {
        // The flag is cleared by whoever set it, but a re-probe abandoned by an exception would
        // otherwise leave every later phase labelled as if it were re-reading.
        var t = new WorkTracker();
        t.BeginPhase(PhaseNames.Probe, 100);
        t.PhaseRevisiting = true;

        t.BeginPhase(PhaseNames.Scan, 100);

        Assert.False(t.PhaseRevisiting);
        Assert.Equal(PhaseNames.Scan, t.PhaseName);
    }

    [Fact]
    public void Relabel_RenamesTheRunningPhase_WithoutDisturbingItsBar()
    {
        // What the sub-floor sweep does: it runs inside Probe, over Probe's own bar, and is still
        // worth naming - so beginning a phase for it (and resetting the bar) is exactly wrong.
        var t = new WorkTracker();
        t.BeginPhase(PhaseNames.Probe, 100);
        t.SetPhaseProgress(40);
        t.RegionSpan = (10, 20);

        t.Relabel(PhaseNames.SubFloorProbe);

        Assert.Equal(PhaseNames.SubFloorProbe, t.PhaseName);
        Assert.StartsWith(" SF-probing... |", Status((t, "book.m4b")));
        Assert.Equal(0.4, t.Fraction, 6);
        Assert.Equal((10L, 20L), t.RegionSpan);
    }

    [Fact]
    public void BuildLine_ShowsElapsedTimer_AsItsOwnSectionBeforeTheFileName()
    {
        // A freshly built tracker is always at "0:00" - real elapsed-time values are covered
        // directly by FormatElapsedTimer_* below, without needing to wait on a live Stopwatch.
        Assert.Contains("| 0:00 | book.m4b", Status(Slot(50, 100)));
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
    public void BuildSpans_JoinToExactlyTheBlockTheyRender()
    {
        // Colors are applied at write time, so the spans must carry the visible text and nothing
        // else: the renderer measures and compares the joined string, and any discrepancy between
        // the two would show up as wrong truncation or a skipped redraw rather than as wrong color.
        var slot = Slot(50, 100, highestChapter: 6, missingChapters: 2);
        Assert.Equal(
            ProgressRenderer.BuildLine(slot, Width),
            string.Join('\n', ProgressRenderer.BuildBlock(slot, Width).Select(ConsoleColors.PlainText)));
    }

    [Fact]
    public void BuildSpans_DrawTheBarWhite_BetweenDarkGrayBrackets()
    {
        var spans = ProgressRenderer.BuildBarSpans(Slot(50, 100).Tracker, Width);

        Assert.Equal(" ", spans[0].Text);
        Assert.Equal("[", spans[1].Text);
        Assert.Equal(ConsoleColor.DarkGray, spans[1].Color);
        Assert.Equal(40, spans[2].Text.Length);
        Assert.Equal(ConsoleColor.White, spans[2].Color);
        Assert.Equal("]", spans[3].Text);
        Assert.Equal(ConsoleColor.DarkGray, spans[3].Color);
    }

    [Fact]
    public void BuildSpans_SeparateEverySectionWithADarkGrayPipe()
    {
        var spans = ProgressRenderer.BuildStatusSpans(Slot(50, 100, highestChapter: 6));

        Assert.All(spans.Where(s => s.Text == " | "), s => Assert.Equal(ConsoleColor.DarkGray, s.Color));
        // The phase leads the status line and the file name closes it.
        Assert.Equal(" Analyzing... | ch 6 | 0:00 | book.m4b", ConsoleColors.PlainText(spans));
    }

    [Fact]
    public void BuildSpans_ColorThePercentageAndTheTimerAlike()
    {
        var percent = ProgressRenderer.BuildBarSpans(Slot(50, 100).Tracker, Width)
            .Single(s => s.Text == "  50%");
        var timer = ProgressRenderer.BuildStatusSpans(Slot(50, 100)).Single(s => s.Text == "0:00");

        Assert.Equal(ConsoleColor.Cyan, percent.Color);
        Assert.Equal(percent.Color, timer.Color);
    }

    [Fact]
    public void BuildSpans_MuteTheChapterSectionWhileItIsStillAPlaceholder()
    {
        // Nothing to read there yet, so "----" sits at the separators' dark grey rather than
        // claiming the chapter color for a count that does not exist.
        var placeholder = ProgressRenderer.BuildStatusSpans(Slot(50, 100)).Single(s => s.Text == "----");
        Assert.Equal(ConsoleColor.DarkGray, placeholder.Color);

        var found = ProgressRenderer.BuildStatusSpans(Slot(50, 100, highestChapter: 6))
            .Single(s => s.Text == "ch 6");
        Assert.Equal(ConsoleColor.DarkGreen, found.Color);
    }

    [Fact]
    public void BuildSpans_SplitTheMissingCountOffFromItsBrackets()
    {
        // The brackets are structure and stay grey; the negative number is the one thing on the
        // line reporting something outstanding, so it alone goes dark red.
        var spans = ProgressRenderer.BuildStatusSpans(Slot(50, 100, highestChapter: 6, missingChapters: 2));
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
        var spans = ProgressRenderer.BuildStatusSpans(
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
        // Identical state must produce an identical block - this is exactly what lets the renderer
        // skip the redraw (and the flicker) while nothing is moving.
        Assert.Equal(
            ProgressRenderer.BuildLine(Slot(50, 100, highestChapter: 2), Width),
            ProgressRenderer.BuildLine(Slot(50, 100, highestChapter: 2), Width));
    }

    /// <summary>
    /// A pass working one piece of the book gets that piece marked out on the bar in the bright
    /// cyan, so a fill that jumps - or runs backwards, as a gap re-probe's does - can be read
    /// against the stretch it belongs to.
    /// </summary>
    [Fact]
    public void BuildBarSpans_HighlightTheRegionBeingWorked_InCyan()
    {
        var (tracker, _) = Slot(50, 100);
        tracker.RegionSpan = (25, 75);

        // Cells 0-9 and 30-39 are outside the region and stay white. Only the region's own cells
        // are ever drawn as done, so the ten white cells the fill has passed read "-" rather than
        // "#": that audio is not work this phase did, it is audio it skipped.
        Assert.Equal(
            [(new string('-', 10), ConsoleColor.White),
             (new string('#', 10) + new string('-', 10), ConsoleColor.Cyan),
             (new string('-', 10), ConsoleColor.White)],
            BarCells(tracker));
    }

    /// <summary>
    /// Every stretch the phase will work is marked out too, a shade darker, so the piece being read
    /// right now is legible as one of several rather than as all there is to do.
    /// </summary>
    [Fact]
    public void BuildBarSpans_HighlightEveryStretchThePhaseCovers_InDarkCyan()
    {
        var t = new WorkTracker();
        t.BeginPhase(PhaseNames.Scan, 100, [(10, 20), (60, 80)]);
        t.SetPhaseProgress(15);
        t.RegionSpan = (10, 20);

        Assert.Equal(
            [(new string('-', 4), ConsoleColor.White),
             (new string('#', 2) + new string('-', 2), ConsoleColor.Cyan),
             (new string('-', 16), ConsoleColor.White),
             (new string('-', 8), ConsoleColor.DarkCyan),
             (new string('-', 8), ConsoleColor.White)],
            BarCells(t));
    }

    /// <summary>
    /// The distinction between work done and audio skipped is carried by the character, not only by
    /// the color, so it survives a terminal with no color at all - which is what
    /// <see cref="ConsoleColors.PlainText(System.Collections.Generic.IEnumerable{ColoredSpan})"/>
    /// renders.
    /// </summary>
    [Fact]
    public void BuildBarSpans_MarkWorkDoneWithTheCharacter_NotOnlyWithTheColor()
    {
        var t = new WorkTracker();
        t.BeginPhase(PhaseNames.Scan, 100, [(0, 10), (20, 40), (70, 80)]);
        t.SetPhaseProgress(75);

        // Every "#" sits inside one of the three stretches and behind the reading head, which is at
        // cell 30 - part way through the last stretch, so that one is half drawn. Everything
        // between the stretches is audio this phase skips and stays "-" whether the head has passed
        // it or not. 35 of the 40 bytes of work are done, hence 87 % rather than the file's 75 %.
        Assert.Equal("[####----########------------##----------]  87%",
            Bar((t, "book.m4b")).Trim());
    }

    /// <summary>
    /// The other half of that statement - which stretches the pass is going to read at all - is
    /// carried only by the color, so a colorless console gets it as a character too: a marked cell
    /// still waiting to be read is "~". Without it the bar above and a stalled whole-file one are
    /// the same picture.
    /// </summary>
    [Fact]
    public void BuildBarSpans_MarkStretchesStillToRead_WithATilde_WhenThereIsNoColor()
    {
        var t = new WorkTracker();
        t.BeginPhase(PhaseNames.Scan, 100, [(0, 10), (20, 40), (70, 80)]);
        t.SetPhaseProgress(75);

        // The same bar as above with its two unread marked cells - the tail of the third stretch,
        // which the head is part way through - drawn "~". The eight cells after it are audio no
        // stretch covers and stay "-", which is what makes the two readable apart at all.
        Assert.Equal("[####----########------------##~~--------]  87%",
            Bar((t, "book.m4b"), color: false).Trim());
    }

    /// <summary>Both shades mean "a piece of the book", so the piece being read right now loses its
    /// color to the same substitution - it is the one a reader most needs to see.</summary>
    [Fact]
    public void BuildBarSpans_MarkTheRegionBeingWorked_WithATilde_WhenThereIsNoColor()
    {
        var (tracker, label) = Slot(50, 100);
        tracker.RegionSpan = (25, 75);

        // Half of the region's own length is behind the head, hence 50 % beside a bar whose fill
        // sits in the middle of the file for the same reason.
        Assert.Equal("[----------##########~~~~~~~~~~----------]  50%",
            Bar((tracker, label), color: false).Trim());
    }

    /// <summary>
    /// A phase that reads the book end to end marks nothing out, so there is no "still to read"
    /// to distinguish and the bar is the same with color and without. The substitution keys on the
    /// cell's color rather than on the option, which is what makes this fall out rather than need
    /// arranging.
    /// </summary>
    [Fact]
    public void BuildBarSpans_LeaveAWholeFilePhaseAlone_WhenThereIsNoColor()
    {
        var slot = Slot(50, 100);
        Assert.DoesNotContain('~', Bar(slot, color: false));
        Assert.Equal(Bar(slot), Bar(slot, color: false));
    }

    /// <summary>
    /// The percentage counts the work the phase actually has to do. A gap re-probe reading two
    /// short stretches of a nine-hour book is not 3 % done when it has finished the first of them,
    /// which is what a figure about the file rather than about the work used to say.
    /// </summary>
    [Fact]
    public void BuildBarSpans_PercentIsProgressThroughTheStretches_NotThroughTheFile()
    {
        var t = new WorkTracker();
        t.BeginPhase(PhaseNames.Scan, 1000, [(100, 200), (800, 900)]);
        t.SetPhaseProgress(200);

        // 200 bytes into a 1000-byte file is 20 % of it, but the first of two equal stretches is
        // half the work.
        Assert.Contains(" 50%", Bar((t, "book.m4b")));
    }

    /// <summary>The other end of the same rule: a phase that has finished its stretches reads
    /// 100 %, though its bar stops well short of the file's end.</summary>
    [Fact]
    public void BuildBarSpans_PercentReachesAHundred_WhenTheStretchesAreDone()
    {
        var t = new WorkTracker();
        t.BeginPhase(PhaseNames.Scan, 1000, [(100, 200), (300, 400)]);
        t.SetPhaseProgress(400);

        Assert.Contains(" 100%", Bar((t, "book.m4b")));
    }

    /// <summary>
    /// A stretch the reading head has passed is drawn done to its last cell. Its cells are rounded
    /// outwards so that a very short one still shows, while the fill rounds to nearest, so without
    /// this a phase reading 100 % could sit beside a stretch with a cell still empty.
    /// </summary>
    [Fact]
    public void BuildBarSpans_DrawAFinishedStretchToItsLastCell()
    {
        var t = new WorkTracker();
        // 4 cells of bar per 10 bytes here, and 25 rounds the fill to cell 10 while the stretch's
        // own cells are rounded out to 11 - so the last cell is only drawn by knowing it is done.
        t.BeginPhase(PhaseNames.Scan, 100, [(15, 26)]);
        t.SetPhaseProgress(26);

        var stretch = BarCells(t).Single(c => c.Color == ConsoleColor.DarkCyan);

        Assert.DoesNotContain('-', stretch.Text);
        Assert.Contains(" 100%", Bar((t, "book.m4b")));
    }

    /// <summary>Two stretches that overlap once rounded are merged before they are divided by, or
    /// the double-counted length would leave a finished phase reading short of 100 %.</summary>
    [Fact]
    public void BuildBarSpans_PercentDoesNotDoubleCountOverlappingStretches()
    {
        var t = new WorkTracker();
        t.BeginPhase(PhaseNames.Scan, 1000, [(100, 300), (200, 400)]);
        t.SetPhaseProgress(400);
        t.RegionSpan = (200, 400);

        Assert.Contains(" 100%", Bar((t, "book.m4b")));
    }

    /// <summary>A gap far shorter than one bar cell still has to show, or a fill jumping about the
    /// bar would have nothing beside it to explain itself.</summary>
    [Fact]
    public void BuildBarSpans_HighlightAtLeastOneCell_ForARegionTooShortToFillOne()
    {
        var (tracker, _) = Slot(50, 100);
        tracker.RegionSpan = (50, 50);

        var region = BarCells(tracker).Single(c => c.Color == ConsoleColor.Cyan);

        Assert.Equal(1, region.Text.Length);
    }

    /// <summary>Two stretches close enough to round onto the same cell must not produce overlapping
    /// spans - the bar is colored cell by cell for exactly this.</summary>
    [Fact]
    public void BuildBarSpans_KeepTheBarIntact_WhenTwoStretchesLandOnOneCell()
    {
        var t = new WorkTracker();
        t.BeginPhase(PhaseNames.Scan, 100, [(50, 51), (51, 52)]);

        Assert.Equal(40, BarCells(t).Sum(c => c.Text.Length));
    }

    /// <summary>The primary whole-file walk marks nothing, and its bar must stay one plain white
    /// run - a bar tinted end to end from the first second of every run would say nothing.</summary>
    [Fact]
    public void BuildBarSpans_LeaveTheBarPlain_WhenNoStretchIsMarked()
    {
        Assert.Equal(
            [(new string('#', 20) + new string('-', 20), ConsoleColor.White)],
            BarCells(Slot(50, 100).Tracker));
    }

    /// <summary>Beginning a phase clears both highlights, so a pass abandoned part way through
    /// cannot leave the next phase pointing at stretches nothing is working on.</summary>
    [Fact]
    public void BeginPhase_ClearsBothHighlights()
    {
        var t = new WorkTracker();
        t.BeginPhase(PhaseNames.Scan, 100, [(10, 40)]);
        t.RegionSpan = (10, 40);

        t.BeginPhase(PhaseNames.Rescan, 100);

        Assert.Null(t.RegionSpan);
        Assert.Null(t.PhaseSpans);
    }

    /// <summary>Every bar this tool draws spans the whole file, so play time converts to bar
    /// coordinates in one place and a stretch cannot be measured differently from the position
    /// reported inside it.</summary>
    [Fact]
    public void Span_MapsPlayTimeOntoTheBar_AtTheFilesOwnByteRate()
    {
        Assert.Equal((1000L, 4000L), WorkTracker.Span(10, 40, 100));
    }

    /// <summary>
    /// A phase with a position but no progress draws a marker rather than a fill, and counts what
    /// it has looked at rather than claiming a percentage of the file. The marker sits where the
    /// fill would have ended, so both read the same way round.
    /// </summary>
    [Fact]
    public void BuildLine_WhileExploring_DrawsAMarkerAndACount()
    {
        var (tracker, label) = Slot(50, 100);
        tracker.LocationsExplored = 17;

        var line = ProgressRenderer.BuildLine((tracker, label), Width);

        // Half of a 40-cell bar is 20 filled, so the marker sits on cell 20 (one back from the
        // count), with nothing but track either side of it.
        Assert.Contains("[" + new string('-', 19) + "X" + new string('-', 20) + "]", line);
        Assert.DoesNotContain("#", line);
        Assert.DoesNotContain("%", line);
        Assert.Contains("  17", line);
    }

    /// <summary>The count is what moves the block while exploring, the position being free to sit
    /// still or jump backwards - so a redraw must follow it.</summary>
    [Fact]
    public void BuildLine_WhileExploring_ChangesWithTheCountAlone()
    {
        var (first, label) = Slot(50, 100);
        first.LocationsExplored = 17;
        var (second, _) = Slot(50, 100);
        second.LocationsExplored = 18;

        Assert.NotEqual(
            ProgressRenderer.BuildLine((first, label), Width),
            ProgressRenderer.BuildLine((second, label), Width));
    }

    /// <summary>At the very start of the file an ordinary bar fills nothing at all; the marker has
    /// to go somewhere, and cell 0 is where the track begins.</summary>
    [Fact]
    public void BuildLine_WhileExploringAtTheFileStart_PutsTheMarkerOnTheFirstCell()
    {
        var (tracker, label) = Slot(0, 100);
        tracker.LocationsExplored = 1;

        Assert.Contains("[X" + new string('-', 39) + "]",
            ProgressRenderer.BuildLine((tracker, label), Width));
    }

    /// <summary>The count takes exactly the percentage's width, so a phase that stops exploring does
    /// not shuffle the bar's right edge sideways.</summary>
    [Fact]
    public void BuildLine_WhileExploring_KeepsTheLineWidth()
    {
        var (exploring, label) = Slot(50, 100);
        exploring.LocationsExplored = 17;

        Assert.Equal(Bar(Slot(50, 100)).Length, Bar((exploring, label)).Length);
    }

    /// <summary>Beginning a phase clears the marker, so a skim abandoned part way through cannot
    /// leave the next phase counting locations it is not exploring.</summary>
    [Fact]
    public void BeginPhase_ClearsTheExploringCount()
    {
        var (tracker, label) = Slot(50, 100);
        tracker.LocationsExplored = 17;

        tracker.BeginPhase(PhaseNames.Probe, 100);
        tracker.SetPhaseProgress(50);

        Assert.Null(tracker.LocationsExplored);
        Assert.Contains("50%", ProgressRenderer.BuildLine((tracker, label), Width));
    }
}
