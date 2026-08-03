// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Xunit;
using ABChapterize.Processing;
using ABChapterize.Ui;

namespace ABChapterize.Tests;

/// <summary>Tests for <see cref="RunOutcomes"/>: the three per-file listings closing a
/// <c>--summary</c> run, and the file names in them being marked as titles rather than left to the
/// highlighter's pattern rules.</summary>
public class RunOutcomesTests
{
    /// <summary>Renders the listings as plain lines, the way a <c>--log-file</c> receives them.</summary>
    /// <param name="outcomes">The roster to format.</param>
    private static List<string> Lines(RunOutcomes outcomes)
        => [.. outcomes.FormatListings()
            .Select(line => ConsoleColors.PlainText(SummaryHighlighter.HighlightSegments(line)))];

    [Fact]
    public void FormatListings_IsEmpty_WhenNothingWasSkippedOrLeftIncomplete()
        => Assert.Empty(Lines(new RunOutcomes()));

    [Fact]
    public void FormatListings_NamesEverySkippedFileAndItsReason()
    {
        var outcomes = new RunOutcomes();
        outcomes.RecordSkipped("Stalker.m4b", "has 24 chapter marking(s)");
        outcomes.RecordSkipped("Wintersmith.m4b", "12 pre-existing chapter marking(s) verified correct");

        Assert.Equal(
            ["Skipped 2 file(s):",
             "  Stalker.m4b: has 24 chapter marking(s)",
             "  Wintersmith.m4b: 12 pre-existing chapter marking(s) verified correct"],
            Lines(outcomes));
    }

    [Fact]
    public void SkippedCount_TracksTheListing()
    {
        // The count --summary's first line quotes comes from the listing itself, so the two cannot
        // report different numbers of skipped files.
        var outcomes = new RunOutcomes();
        Assert.Equal(0, outcomes.SkippedCount);
        outcomes.RecordSkipped("Stalker.m4b", "has 24 chapter marking(s)");
        Assert.Equal(1, outcomes.SkippedCount);
    }

    [Fact]
    public void FormatListings_NamesEveryFileNothingWasFoundIn()
    {
        var outcomes = new RunOutcomes();
        outcomes.RecordNoChapters("Interview.mp3", "no chapter phrases found");
        outcomes.RecordNoChapters("Lecture.m4b",
            "early-abort - no chapter found within the first 20 minute(s) of play time");

        Assert.Equal(
            ["No chapters found in 2 file(s):",
             "  Interview.mp3: no chapter phrases found",
             "  Lecture.m4b: early-abort - no chapter found within the first 20 minute(s) of play time"],
            Lines(outcomes));
    }

    [Fact]
    public void NoChaptersCount_TracksTheListing()
    {
        var outcomes = new RunOutcomes();
        Assert.Equal(0, outcomes.NoChaptersCount);
        outcomes.RecordNoChapters("Interview.mp3", "no chapter phrases found");
        Assert.Equal(1, outcomes.NoChaptersCount);
    }

    [Fact]
    public void FormatListings_CountsAndNamesTheStillMissingChapters()
    {
        var outcomes = new RunOutcomes();
        outcomes.RecordMissingMarks("Die Dritte Macht.missing-marks-3-7.m4b", [3, 7]);

        Assert.Equal(
            ["Still missing chapter marks in 1 file(s):",
             "  Die Dritte Macht.missing-marks-3-7.m4b: 2 mark(s) missing (chapter 3, 7)"],
            Lines(outcomes));
    }

    [Fact]
    public void FormatListings_SummarizesAVeryLongMissingList()
    {
        // Past the cut-off the file name itself stops spelling the numbers out, and so does this:
        // the count is the part that still means something.
        var outcomes = new RunOutcomes();
        outcomes.RecordMissingMarks("Omnibus.missing-marks.m4b", [.. Enumerable.Range(1, 14)]);

        Assert.Equal(
            "  Omnibus.missing-marks.m4b: 14 mark(s) missing " +
            "(chapter 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 and 4 more)",
            Lines(outcomes)[1]);
    }

    [Fact]
    public void FormatListings_OrdersTheBlocksByHowFarEachFileGot()
    {
        // Recorded back to front, printed not-started / empty-handed / incomplete.
        var outcomes = new RunOutcomes();
        outcomes.RecordMissingMarks("Book.missing-marks-5.m4b", [5]);
        outcomes.RecordNoChapters("Interview.mp3", "no chapter phrases found");
        outcomes.RecordSkipped("Other.m4b", "has 3 chapter marking(s)");

        Assert.Equal(
            ["Skipped 1 file(s):",
             "  Other.m4b: has 3 chapter marking(s)",
             "No chapters found in 1 file(s):",
             "  Interview.mp3: no chapter phrases found",
             "Still missing chapter marks in 1 file(s):",
             "  Book.missing-marks-5.m4b: 1 mark(s) missing (chapter 5)"],
            Lines(outcomes));
    }

    [Fact]
    public void FormatListings_MarksTheFileNameAsATitle()
    {
        var outcomes = new RunOutcomes();
        outcomes.RecordSkipped("Der Fall (Teil 2).m4b", "has 3 chapter marking(s)");

        var entry = outcomes.FormatListings()[1];
        Assert.Contains(entry, s => s == SummarySegment.Title("Der Fall (Teil 2).m4b"));
    }
}
