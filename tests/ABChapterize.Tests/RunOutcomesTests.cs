// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Xunit;
using ABChapterize.Detection;
using ABChapterize.Processing;
using ABChapterize.Ui;

namespace ABChapterize.Tests;

/// <summary>Tests for <see cref="RunOutcomes"/>: the per-file listings closing a
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
        outcomes.RecordSkipped("Stalker.m4b", "has 24 chapter mark(s)");
        outcomes.RecordSkipped("Wintersmith.m4b", "12 pre-existing chapter mark(s) verified correct");

        Assert.Equal(
            ["Skipped 2 file(s):",
             "  Stalker.m4b: has 24 chapter mark(s)",
             "  Wintersmith.m4b: 12 pre-existing chapter mark(s) verified correct"],
            Lines(outcomes));
    }

    [Fact]
    public void SkippedCount_TracksTheListing()
    {
        // The count --summary's first line quotes comes from the listing itself, so the two cannot
        // report different numbers of skipped files.
        var outcomes = new RunOutcomes();
        Assert.Equal(0, outcomes.SkippedCount);
        outcomes.RecordSkipped("Stalker.m4b", "has 24 chapter mark(s)");
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

    /// <summary>Low-confidence chapters of a single-part book, which is the ordinary case:
    /// the listing reads their numbers and their part, and an ordinary book has one part.</summary>
    /// <param name="numbers">The chapter numbers, in file order.</param>
    private static DetectedChapter[] Ch(params int[] numbers)
        => [.. numbers.Select(n => new DetectedChapter(n, 0))];

    [Fact]
    public void FormatListings_OrdersTheBlocksByHowFarEachFileGot()
    {
        // Recorded back to front, printed not-started / empty-handed / incomplete / worth a look.
        var outcomes = new RunOutcomes();
        outcomes.RecordLowConfidence("Third.m4b", Ch([9]), sequenceCount: 1, bareNumbers: false);
        outcomes.RecordMissingMarks("Book.missing-marks-5.m4b", [5]);
        outcomes.RecordNoChapters("Interview.mp3", "no chapter phrases found");
        outcomes.RecordSkipped("Other.m4b", "has 3 chapter mark(s)");

        Assert.Equal(
            ["Skipped 1 file(s):",
             "  Other.m4b: has 3 chapter mark(s)",
             "No chapters found in 1 file(s):",
             "  Interview.mp3: no chapter phrases found",
             "Still missing chapter marks in 1 file(s):",
             "  Book.missing-marks-5.m4b: 1 mark(s) missing (chapter 5)",
             "Low-confidence chapter marks in 1 file(s) (below p=0.50, worth a manual check):",
             "  Third.m4b: 1 mark(s) (chapter 9)"],
            Lines(outcomes));
    }

    [Fact]
    public void FormatListings_CountsAndNamesTheLowConfidenceChapters()
    {
        var outcomes = new RunOutcomes();
        outcomes.RecordLowConfidence("Stalker.m4b", Ch([4, 17]), sequenceCount: 1, bareNumbers: false);
        outcomes.RecordLowConfidence("Wintersmith.m4b", Ch([2]), sequenceCount: 1, bareNumbers: false);

        Assert.Equal(
            ["Low-confidence chapter marks in 2 file(s) (below p=0.50, worth a manual check):",
             "  Stalker.m4b: 2 mark(s) (chapter 4, 17)",
             "  Wintersmith.m4b: 1 mark(s) (chapter 2)"],
            Lines(outcomes));
    }

    [Fact]
    public void FormatListings_NamesThePart_WhenTheBooksNumberingRestarts()
    {
        // "The Forever War" and its like: every part counts from one again, so a bare "chapter 4"
        // does not say which chapter 4 to go and listen to. Parts are numbered from one, matching
        // the mark titles this listing sends the reader to.
        var outcomes = new RunOutcomes();
        outcomes.RecordLowConfidence(
            "The Forever War.m4b",
            [new DetectedChapter(4, 100), new DetectedChapter(9, 200),
             new DetectedChapter(3, 300, Sequence: 1),
             new DetectedChapter(2, 400, Sequence: 2)],
            sequenceCount: 3, bareNumbers: false);

        Assert.Equal(
            ["Low-confidence chapter marks in 1 file(s) (below p=0.50, worth a manual check):",
             "  The Forever War.m4b: 4 mark(s) " +
             "(part 1 chapter 4, 9; part 2 chapter 3; part 3 chapter 2)"],
            Lines(outcomes));
    }

    [Fact]
    public void FormatListings_AppliesTheCutOffAcrossEveryPart_NotOncePerPart()
    {
        // Otherwise a book in five parts could name five times as many chapters as an ordinary
        // one and the cut-off would stop meaning anything.
        var outcomes = new RunOutcomes();
        outcomes.RecordLowConfidence(
            "Omnibus.m4b",
            [.. Enumerable.Range(1, 7).Select(n => new DetectedChapter(n, n)),
             .. Enumerable.Range(1, 7).Select(n => new DetectedChapter(n, 100 + n, Sequence: 1))],
            sequenceCount: 2, bareNumbers: false);

        Assert.Equal(
            ["Low-confidence chapter marks in 1 file(s) (below p=0.50, worth a manual check):",
             "  Omnibus.m4b: 14 mark(s) " +
             "(part 1 chapter 1, 2, 3, 4, 5, 6, 7; part 2 chapter 1, 2, 3 and 4 more)"],
            Lines(outcomes));
    }

    [Fact]
    public void FormatListings_StillNamesThePart_WhenEveryMarkFellInTheSameOne()
    {
        // The trigger is the book, not the marks that made the list: on a three-part file a bare
        // "chapter 3" is ambiguous however few parts the low-confidence marks happen to span.
        var outcomes = new RunOutcomes();
        outcomes.RecordLowConfidence(
            "The Forever War.m4b", [new DetectedChapter(3, 300, Sequence: 1)],
            sequenceCount: 3, bareNumbers: false);

        Assert.Equal(
            "  The Forever War.m4b: 1 mark(s) (part 2 chapter 3)",
            Lines(outcomes)[1]);
    }

    [Fact]
    public void FormatListings_SummarizesAVeryLongLowConfidenceList()
    {
        // Same cut-off the missing-marks note uses: a book unsure about forty chapters takes one
        // line, not forty numbers.
        var outcomes = new RunOutcomes();
        outcomes.RecordLowConfidence("Omnibus.m4b", Ch([.. Enumerable.Range(1, 14)]), sequenceCount: 1, bareNumbers: false);

        Assert.Equal(
            "  Omnibus.m4b: 14 mark(s) (chapter 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 and 4 more)",
            Lines(outcomes)[1]);
    }

    [Fact]
    public void FormatListings_FootnotesTheBlock_WhenABareNumberFileIsInIt()
    {
        // A bare number is often a one-token segment, so its confidence is volatile in a way a
        // phrase's is not - without the note the reader has no way to know the two are not
        // comparable. Earned by one file in the block, not by all of them: a mixed batch still
        // needs telling.
        var outcomes = new RunOutcomes();
        outcomes.RecordLowConfidence("Ordinary.m4b", Ch([3]), sequenceCount: 1, bareNumbers: false);
        outcomes.RecordLowConfidence("Corsa.m4b", Ch([44, 55, 58]), sequenceCount: 1, bareNumbers: true);

        Assert.Equal(
            ["Low-confidence chapter marks in 2 file(s) (below p=0.50, worth a manual check):",
             "  Ordinary.m4b: 1 mark(s) (chapter 3)",
             "  Corsa.m4b: 3 mark(s) (chapter 44, 55, 58)",
             "  (A bare number is often a segment of one token, so its confidence swings much wider " +
             "than a phrase's - a low value there is weaker evidence of a bad mark.)"],
            Lines(outcomes));
    }

    [Fact]
    public void FormatListings_LeavesTheFootnoteOut_WithoutABareNumberFile()
    {
        var outcomes = new RunOutcomes();
        outcomes.RecordLowConfidence("Ordinary.m4b", Ch([3]), sequenceCount: 1, bareNumbers: false);

        Assert.DoesNotContain(Lines(outcomes), l => l.Contains("bare number"));
    }

    [Fact]
    public void FormatListings_MarksALowConfidenceFileNameAsATitle()
    {
        var outcomes = new RunOutcomes();
        outcomes.RecordLowConfidence("Der Fall (Teil 2).m4b", Ch([3]), sequenceCount: 1, bareNumbers: true);

        var entry = outcomes.FormatListings()[1];
        Assert.Contains(entry, s => s == SummarySegment.Title("Der Fall (Teil 2).m4b"));
    }

    [Fact]
    public void FormatListings_MarksTheFileNameAsATitle()
    {
        var outcomes = new RunOutcomes();
        outcomes.RecordSkipped("Der Fall (Teil 2).m4b", "has 3 chapter mark(s)");

        var entry = outcomes.FormatListings()[1];
        Assert.Contains(entry, s => s == SummarySegment.Title("Der Fall (Teil 2).m4b"));
    }

    /// <summary>
    /// The question a pushing run leaves open: the marks are in the files, and the per-file lines
    /// saying which books never got them have long scrolled away.
    /// </summary>
    [Fact]
    public void FormatListings_NamesEveryFileTheServerDidNotGet()
    {
        var outcomes = new RunOutcomes();
        outcomes.RecordNotSentToAbs("Stalker.m4b",
            "the title tag \"Stalker\" matches 2 books on the server");
        outcomes.RecordNotSentToAbs("Atlan.missing-marks-7.m4b",
            "chapters are still missing and the server already has 34 mark(s)");

        Assert.Equal(
            ["Not sent to Audiobookshelf: 2 file(s):",
             "  Stalker.m4b: the title tag \"Stalker\" matches 2 books on the server",
             "  Atlan.missing-marks-7.m4b: chapters are still missing and the server already has " +
             "34 mark(s)"],
            Lines(outcomes));
    }

    /// <summary>
    /// It comes after the four listings about the file itself, being about the other side: these
    /// books were marked, and it is the server that did not get them.
    /// </summary>
    [Fact]
    public void FormatListings_PutsTheNotSentBlockAfterTheOnesAboutTheFile()
    {
        var outcomes = new RunOutcomes();
        outcomes.RecordSkipped("Mort.m4b", "has 24 chapter mark(s)");
        outcomes.RecordNotSentToAbs("Stalker.m4b", "no book on the server matches this file");

        Assert.Equal(
            ["Skipped 1 file(s):",
             "  Mort.m4b: has 24 chapter mark(s)",
             "Not sent to Audiobookshelf: 1 file(s):",
             "  Stalker.m4b: no book on the server matches this file"],
            Lines(outcomes));
    }

    [Fact]
    public void FormatListings_MarksANotSentFileNameAsATitle()
    {
        var outcomes = new RunOutcomes();
        outcomes.RecordNotSentToAbs("Der Fall (Teil 2).m4b", "no book on the server matches this file");

        var entry = outcomes.FormatListings()[1];
        Assert.Contains(entry, s => s == SummarySegment.Title("Der Fall (Teil 2).m4b"));
    }
}
