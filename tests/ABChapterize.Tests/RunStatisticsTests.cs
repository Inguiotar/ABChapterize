// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Xunit;
using ABChapterize.Detection;
using ABChapterize.Processing;

namespace ABChapterize.Tests;

/// <summary>
/// Tests for <see cref="RunStatistics"/>: the per-file <c>stats -</c> line and the run-wide
/// <c>--summary</c> block it accumulates across files. These assert the finished text rather than
/// the running totals, because the text is the product - <see cref="SummaryHighlighter"/> colorizes
/// it by pattern afterwards, so a stray thousands separator or a value split from its unit would
/// reach the terminal miscolored as well as misprinted.
/// </summary>
public sealed class RunStatisticsTests
{
    /// <summary>A file's stats with the silence/jingle extremes filled in.</summary>
    private static DetectionStats Stats(
        double? silence = 1.52, double? interSilence = 1.84,
        double? jingle = 6.5, double? interJingle = 6.25,
        double whisperAudio = 300, double transcribeSeconds = 100)
        => new(silence, interSilence, jingle, interJingle, whisperAudio, transcribeSeconds);

    [Fact]
    public void ProcessingTime_ReportsBothTheAbsoluteFigureAndItsShareOfTheBook()
    {
        // Six minutes is fast for a fifteen-hour book and slow for a ten-minute one, so the
        // share is what makes the absolute figure mean anything to the reader.
        Assert.Equal("; took 5:00 (10.0% of run length)",
            RunStatistics.FormatProcessingTime(TimeSpan.FromMinutes(5), 3000));

        // Past an hour the absolute figure grows an hours field rather than counting minutes up.
        Assert.Equal("; took 1:30:00 (50.0% of run length)",
            RunStatistics.FormatProcessingTime(TimeSpan.FromMinutes(90), 10800));

        // A run length of zero is "not known", not "instantaneous": the share is left off
        // rather than printed as a division by nothing.
        Assert.Equal("; took 0:42", RunStatistics.FormatProcessingTime(TimeSpan.FromSeconds(42), 0));
    }

    [Fact]
    public void RunSummary_ReportsBothExtremesWithTheirInterChapterFigures()
    {
        var run = new RunStatistics();
        run.AccumulateStats(Stats(), runLengthSeconds: 8000);

        Assert.Contains(
            "Shortest silence before a chapter 1.52 s (inter-chapter 1.84 s), " +
            "longest jingle before a chapter 6.50 s (inter-chapter 6.25 s)",
            run.FormatRunSummaryLines());
    }

    [Fact]
    public void RunSummary_TakesTheExtremeAcrossFiles_NotTheLastOne()
    {
        var run = new RunStatistics();
        run.AccumulateStats(Stats(silence: 2.0, jingle: 6.5), runLengthSeconds: 8000);
        run.AccumulateStats(Stats(silence: 1.2, jingle: 4.0), runLengthSeconds: 8000);

        var extremes = Assert.Single(run.FormatRunSummaryLines(), l => l.StartsWith("Shortest"));
        Assert.StartsWith("Shortest silence before a chapter 1.20 s", extremes);
        Assert.Contains("longest jingle before a chapter 6.50 s", extremes);
    }

    [Fact]
    public void RunSummary_OmitsAnExtremeNoFileContributed()
    {
        // No VAD pre-pass ran, so no jingle was ever measured - the line must not claim one at
        // the infinity the running maximum still sits at.
        var run = new RunStatistics();
        run.AccumulateStats(Stats(jingle: null, interJingle: null), runLengthSeconds: 8000);

        var extremes = Assert.Single(run.FormatRunSummaryLines(), l => l.StartsWith("Shortest"));
        Assert.DoesNotContain("jingle", extremes);
    }

    [Fact]
    public void RunSummary_OmitsTheInterChapterFigure_WhenOnlyChapterOneContributed()
    {
        var run = new RunStatistics();
        run.AccumulateStats(Stats(interSilence: null, interJingle: null), runLengthSeconds: 8000);

        var extremes = Assert.Single(run.FormatRunSummaryLines(), l => l.StartsWith("Shortest"));
        Assert.DoesNotContain("inter-chapter", extremes);
    }

    [Fact]
    public void RunSummary_ReportsWhisperAudioAgainstTheRunLength()
    {
        var run = new RunStatistics();
        run.AccumulateStats(Stats(whisperAudio: 300, transcribeSeconds: 100), runLengthSeconds: 8000);

        Assert.Contains(
            "Whisper audio processed: 5:00 of 2:13:20 run length (3.8%), " +
            "transcription speed 300% of real-time",
            run.FormatRunSummaryLines());
    }

    [Fact]
    public void RunSummary_ReportsTheConfidenceSpreadOverEveryMarkWritten()
    {
        var run = new RunStatistics();
        run.AccumulateConfidence([new DetectedChapter(1, 0, 0.9), new DetectedChapter(2, 10, 0.5)]);
        run.AccumulateConfidence([new DetectedChapter(3, 20, 0.7)]);

        Assert.Contains(
            "Confidence of written chapter marks: min 0.50, max 0.90, avg 0.70",
            run.FormatRunSummaryLines());
    }

    [Fact]
    public void RunSummary_IsEmpty_WhenEveryFileWasSkipped()
    {
        // Nothing accumulated at all: a run that only skipped files reports counts and elapsed
        // time (FileProcessor's own lines) and none of these.
        Assert.Empty(new RunStatistics().FormatRunSummaryLines());
    }

    [Fact]
    public void FileStats_LeadsWithTheLowercaseHeader_UnlikeTheRunSummary()
    {
        // This one is a mid-sentence continuation of the "stats - " header, where the run-wide
        // line stands on its own and is capitalized to match the other --summary lines.
        var line = RunStatistics.FormatFileStats(Stats(), runLengthSeconds: 8000);

        Assert.StartsWith("stats - shortest silence before a chapter 1.52 s (inter-chapter 1.84 s)", line);
        Assert.Contains("Whisper audio 5:00 (3.8% of run length)", line);
    }
}
