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
    private static string Bar((WorkTracker Tracker, string Label) slot, int width = Width)
        => ConsoleColors.PlainText(ProgressRenderer.BuildBarSpans(slot.Tracker, width));

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
        Assert.Equal("Analyzing... | ch 6 | 0:00 | book.m4b", ConsoleColors.PlainText(block[1]));
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

        Assert.Equal("Finishing... | 0:00 | book.m4b", Status((t, "book.m4b")));
    }

    [Fact]
    public void BuildLine_SpellsEveryPhaseAsSomethingInProgress()
    {
        // A phase name is a label on something happening right now, not a heading.
        var t = new WorkTracker();
        t.BeginPhase(PhaseNames.Scan, 100);

        Assert.StartsWith("Scanning... |", Status((t, "book.m4b")));
    }

    [Fact]
    public void BuildLine_MarksThePhaseAsRevisiting_WhileItReReadsGroundItCovered()
    {
        // A sequence-gap re-probe runs inside Probe and walks backwards through candidates the
        // phase has already counted, so its percentage falls. The suffix is what says why.
        var t = new WorkTracker();
        t.BeginPhase(PhaseNames.Probe, 100);
        t.SetPhaseProgress(40);
        Assert.StartsWith("Probing... |", Status((t, "book.m4b")));

        t.PhaseRevisiting = true;
        Assert.StartsWith("Probing... (<<) |", Status((t, "book.m4b")));

        t.PhaseRevisiting = false;
        Assert.StartsWith("Probing... |", Status((t, "book.m4b")));
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
        Assert.StartsWith("SF-probing... |", Status((t, "book.m4b")));
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
        Assert.Equal("Analyzing... | ch 6 | 0:00 | book.m4b", ConsoleColors.PlainText(spans));
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
    /// A pass working one piece of the book gets that piece marked out on the bar, so a fill that
    /// stops short - or runs backwards, as a gap re-probe's does - can be read against the stretch
    /// it belongs to.
    /// </summary>
    [Fact]
    public void BuildBarSpans_HighlightTheRegionBeingWorked_InDarkCyan()
    {
        var (tracker, label) = Slot(50, 100);
        tracker.RegionSpan = (25, 75);

        var spans = ProgressRenderer.BuildBarSpans(tracker, Width);
        var bar = spans.SkipWhile(s => s.Text != "[").Skip(1).TakeWhile(s => s.Text != "]").ToList();

        // Cells 0-9 and 30-39 are outside the region and stay white; the ten filled and ten empty
        // cells between them are the region.
        Assert.Equal(
            [(new string('#', 10), ConsoleColor.White),
             (new string('#', 10) + new string('-', 10), ConsoleColor.DarkCyan),
             (new string('-', 10), ConsoleColor.White)],
            bar.Select(s => (s.Text, s.Color)));
    }

    /// <summary>A gap far shorter than one bar cell still has to show, or a percentage running
    /// backwards would have nothing on the line beside it to explain itself.</summary>
    [Fact]
    public void BuildBarSpans_HighlightAtLeastOneCell_ForARegionTooShortToFillOne()
    {
        var (tracker, _) = Slot(50, 100);
        tracker.RegionSpan = (50, 50);

        var region = ProgressRenderer.BuildBarSpans(tracker, Width)
            .Single(s => s.Color == ConsoleColor.DarkCyan);

        Assert.Equal(1, region.Text.Length);
    }

    /// <summary>The primary whole-file walk marks no region, and its bar must stay one plain white
    /// run - a bar tinted end to end from the first second of every run would say nothing.</summary>
    [Fact]
    public void BuildBarSpans_LeaveTheBarPlain_WhenNoRegionIsMarked()
    {
        var spans = ProgressRenderer.BuildBarSpans(Slot(50, 100).Tracker, Width);
        Assert.DoesNotContain(spans, s => s.Color == ConsoleColor.DarkCyan);
    }

    /// <summary>Scan is the case that does tint end to end: it only ever reads regions, so its bar
    /// really is one.</summary>
    [Fact]
    public void MarkRegion_CoveringTheWholeBar_TintsAllOfIt()
    {
        var t = new WorkTracker();
        t.BeginPhase(PhaseNames.Scan, 100);
        t.MarkRegion(100);

        var spans = ProgressRenderer.BuildBarSpans(t, Width);
        Assert.Equal(40, spans.Single(s => s.Color == ConsoleColor.DarkCyan).Text.Length);
    }

    /// <summary>A pass that books whole regions of work marks each one from where the phase already
    /// stands, so the second region's highlight sits behind the first's.</summary>
    [Fact]
    public void MarkRegion_StartsWhereThePhaseStands()
    {
        var t = new WorkTracker();
        t.BeginPhase(PhaseNames.Scan, 100);
        t.MarkRegion(40);
        Assert.Equal((0L, 40L), t.RegionSpan);

        t.Advance(40);
        t.MarkRegion(60);
        Assert.Equal((40L, 100L), t.RegionSpan);
    }

    /// <summary>Beginning a phase clears the highlight, so a pass abandoned part way through cannot
    /// leave the next phase pointing at a stretch nothing is working on.</summary>
    [Fact]
    public void BeginPhase_ClearsTheRegionHighlight()
    {
        var t = new WorkTracker();
        t.BeginPhase(PhaseNames.Scan, 100);
        t.MarkRegion(40);

        t.BeginPhase(PhaseNames.Rescan, 100);

        Assert.Null(t.RegionSpan);
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
