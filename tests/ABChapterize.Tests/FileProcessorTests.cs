// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Xunit;
using ABChapterize.Audio;
using ABChapterize.Cli;
using ABChapterize.Detection;
using ABChapterize.Language;
using ABChapterize.Processing;

namespace ABChapterize.Tests;

/// <summary>Tests for <see cref="FileProcessor.BuildChapters"/>: intro-chapter insertion and
/// its "silent lead-in" carve-out.</summary>
public sealed class FileProcessorTests : IDisposable
{
    private readonly string _dir;
    private readonly string _file;
    private readonly LanguageProfile _profile;

    /// <summary>Creates a temp .m4b file so <see cref="CliOptions.Parse"/> accepts the target,
    /// and resolves a real English <see cref="LanguageProfile"/> from it.</summary>
    public FileProcessorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"abchapterize-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "book.m4b");
        File.WriteAllText(_file, "x");
        _profile = CliOptions.Parse([_file])!.ResolveProfile("en");
    }

    /// <summary>Removes the temp directory.</summary>
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>Builds a minimal <see cref="DetectionResult"/> with the given chapters, named marks
    /// and <see cref="DetectionResult.LeadInHasSpeech"/>, everything else at its default/empty
    /// value.</summary>
    private DetectionResult Result(
        List<DetectedChapter> chapters, bool leadInHasSpeech = true, List<DetectedMark>? named = null,
        int sequenceCount = 1)
        => new(chapters, named ?? [], false, [], [], _profile, null, 0,
            new DetectionStats(null, null, null, null, 0, 0), LeadInHasSpeech: leadInHasSpeech,
            SequenceCount: sequenceCount);

    /// <summary>Builds the chapter list with the named-mark merge switched off, which is what every
    /// test about intro insertion and ordering wants - the merge has tests of its own below - and
    /// drops the merge count with it.</summary>
    /// <param name="result">The detection result to lay out.</param>
    /// <param name="namedMarkDistanceSeconds">--named-mark-distance, off by default here.</param>
    private static List<Chapter> Build(DetectionResult result, double namedMarkDistanceSeconds = 0)
        => FileProcessor.BuildChapters(result, namedMarkDistanceSeconds).Chapters;

    [Fact]
    public void BuildChapters_InsertsIntro_WhenFirstChapterStartsPastZero()
    {
        var chapters = Build(Result([new(1, 30)]));

        Assert.Equal([new Chapter(0, "Intro"), new Chapter(30, "Chapter 1")], chapters);
    }

    [Fact]
    public void BuildChapters_PrefixesEveryChapterWithItsPart_WhenTheFileHoldsSeveralSequences()
    {
        // Both parts are labelled, not just the second: an unlabelled first part reads as a book
        // that acquired parts halfway through. The prefix is only ever written for a file that
        // really holds more than one sequence - see the test below for the ordinary book.
        var chapters = Build(Result(
            [new(1, 30), new(2, 300), new(1, 900, Sequence: 1), new(2, 1500, Sequence: 1)],
            sequenceCount: 2));

        Assert.Equal(
            [new Chapter(0, "Intro"), new Chapter(30, "Part 1 - Chapter 1"),
             new Chapter(300, "Part 1 - Chapter 2"), new Chapter(900, "Part 2 - Chapter 1"),
             new Chapter(1500, "Part 2 - Chapter 2")],
            chapters);
    }

    [Fact]
    public void BuildChapters_WritesNoPartPrefix_ForAFileHoldingOneSequence()
    {
        // The ordinary book, unchanged: a lone "Part 1" in front of every chapter of a book that
        // has no part 2 is noise, and it would rewrite every title this tool has ever produced.
        var chapters = Build(Result([new(1, 30), new(2, 300)]));

        Assert.Equal(
            [new Chapter(0, "Intro"), new Chapter(30, "Chapter 1"), new Chapter(300, "Chapter 2")],
            chapters);
    }

    [Fact]
    public void BuildChapters_InterleavesNamedMarksByTime()
    {
        var chapters = Build(Result(
            [new(1, 300), new(2, 900)],
            named: [new("prologue", "Prologue", 30), new("epilogue", "Epilogue", 1500)]));

        Assert.Equal(
            [new Chapter(0, "Intro"), new Chapter(30, "Prologue"), new Chapter(300, "Chapter 1"),
             new Chapter(900, "Chapter 2"), new Chapter(1500, "Epilogue")],
            chapters);
    }

    [Fact]
    public void BuildChapters_SortsANamedMarkAfterAChapterAtTheSameTime()
    {
        // Both announced in one breath: the numbered entry is the one a player scrubs by, so it
        // must not be pushed behind the prologue that shares its timestamp.
        var chapters = Build(Result(
            [new(1, 300)], named: [new("prologue", "Prologue", 300)]));

        Assert.Equal(
            [new Chapter(0, "Intro"), new Chapter(300, "Chapter 1"), new Chapter(300, "Prologue")],
            chapters);
    }

    [Fact]
    public void BuildChapters_OmitsIntro_WhenANamedMarkAlreadyStartsAtZero()
    {
        var chapters = Build(Result(
            [new(1, 300)], named: [new("prologue", "Prologue", 0)]));

        Assert.Equal([new Chapter(0, "Prologue"), new Chapter(300, "Chapter 1")], chapters);
    }

    [Fact]
    public void BuildChapters_InsertsIntro_ForASubSecondGap()
    {
        // The old 1.0 s grace period is gone: any nonzero gap gets its own Intro entry now.
        var chapters = Build(Result([new(1, 0.5)]));

        Assert.Equal([new Chapter(0, "Intro"), new Chapter(0.5, "Chapter 1")], chapters);
    }

    [Fact]
    public void BuildChapters_OmitsIntro_WhenFirstChapterStartsExactlyAtZero()
    {
        var chapters = Build(Result([new(1, 0)]));

        Assert.Equal([new Chapter(0, "Chapter 1")], chapters);
    }

    [Fact]
    public void BuildChapters_OmitsIntro_WhenNoSpeechPrecedesTheFirstChapterPhrase()
    {
        // A jingle, music or silence-only lead-in with no actual spoken prelude: even several
        // minutes in, there is nothing to give its own "Intro" entry - the mp4 muxer's own
        // start-snap absorbs the lead-in into chapter 1 instead.
        var chapters = Build(Result([new(1, 180)], leadInHasSpeech: false));

        Assert.Equal([new Chapter(180, "Chapter 1")], chapters);
    }

    [Fact]
    public void BuildChapters_MergesACloseNamedMarkIntoTheChapterTitle()
    {
        var (chapters, merged) = FileProcessor.BuildChapters(
            Result([new(10, 900)], named: [new("custom 1", "Interlude", 895)]), 10);

        Assert.Equal([new Chapter(0, "Intro"), new Chapter(900, "Chapter 10 (Interlude)")], chapters);
        Assert.Equal(1, merged);
    }

    [Fact]
    public void BuildChapters_LeavesANamedMarkAtExactlyTheDistanceAlone()
    {
        // The option names the distance a mark may keep and stay its own entry, so the boundary
        // itself is far enough.
        var (chapters, merged) = FileProcessor.BuildChapters(
            Result([new(10, 900)], named: [new("custom 1", "Interlude", 890)]), 10);

        Assert.Equal(
            [new Chapter(0, "Intro"), new Chapter(890, "Interlude"), new Chapter(900, "Chapter 10")],
            chapters);
        Assert.Equal(0, merged);
    }

    [Fact]
    public void BuildChapters_MergesNothing_WhenTheDistanceIsZero()
    {
        var (chapters, merged) = FileProcessor.BuildChapters(
            Result([new(10, 900)], named: [new("custom 1", "Interlude", 899.9)]), 0);

        Assert.Equal(
            [new Chapter(0, "Intro"), new Chapter(899.9, "Interlude"), new Chapter(900, "Chapter 10")],
            chapters);
        Assert.Equal(0, merged);
    }

    [Fact]
    public void BuildChapters_MergesSeveralNamedMarksIntoOneTitle_InFileOrder()
    {
        var (chapters, merged) = FileProcessor.BuildChapters(
            Result([new(10, 900)],
                named: [new("custom 2", "Zeittafel", 903), new("custom 1", "Interlude", 897)]), 10);

        Assert.Equal(
            [new Chapter(0, "Intro"), new Chapter(900, "Chapter 10 (Interlude, Zeittafel)")],
            chapters);
        Assert.Equal(2, merged);
    }

    [Fact]
    public void BuildChapters_MergesIntoTheNearerOfTwoChapters()
    {
        var (chapters, _) = FileProcessor.BuildChapters(
            Result([new(10, 900), new(11, 906)], named: [new("custom 1", "Interlude", 904)]), 10);

        Assert.Equal(
            [new Chapter(0, "Intro"), new Chapter(900, "Chapter 10"),
             new Chapter(906, "Chapter 11 (Interlude)")],
            chapters);
    }

    [Fact]
    public void BuildChapters_MergesIntoAChapterAnnouncement_WhenNumbersAreIgnored()
    {
        // With --ignore-chapter-numbers the chapters are themselves named marks, and an interlude
        // beside one still belongs to it.
        var (chapters, merged) = FileProcessor.BuildChapters(
            Result([], named:
            [
                new(_profile.ChapterAnnouncement.Kind, "Chapter 10", 900),
                new("custom 1", "Interlude", 897),
            ]), 10);

        Assert.Equal([new Chapter(0, "Intro"), new Chapter(900, "Chapter 10 (Interlude)")], chapters);
        Assert.Equal(1, merged);
    }

    [Fact]
    public void BuildChapters_ReturnsEmpty_WhenNoChaptersWereFound()
    {
        var chapters = Build(Result([]));

        Assert.Empty(chapters);
    }

    /// <summary>Builds a <see cref="VerifyResult"/> with the given confirmed/failed counts, which is
    /// all <see cref="FileProcessor.IsWholesaleFailure"/> looks at.</summary>
    /// <param name="confirmed">How many marks were confirmed.</param>
    /// <param name="failed">How many were not.</param>
    private VerifyResult Verified(int confirmed, int failed)
        => new(failed == 0, confirmed + failed, failed,
               [.. Enumerable.Range(1, confirmed).Select(n => new DetectedChapter(n, n * 100))],
               [], _profile, null, 0, []);

    /// <summary>
    /// The default rule: a file keeps its gap-scoped recovery while the confirmed marks still
    /// outnumber the failures, and is left alone once they do not. The 0-confirmed row is the one
    /// that used to discard a whole mark set and redetect the file.
    /// </summary>
    /// <param name="confirmed">Confirmed marks.</param>
    /// <param name="failed">Unconfirmed marks.</param>
    /// <param name="wholesale">Whether this counts as a wholesale failure.</param>
    [Theory]
    [InlineData(0, 20, true)]
    [InlineData(2, 18, true)]
    [InlineData(9, 11, true)]
    [InlineData(10, 10, false)]
    [InlineData(19, 1, false)]
    public void IsWholesaleFailure_ComparesFailuresAgainstConfirmations(
        int confirmed, int failed, bool wholesale)
        => Assert.Equal(wholesale, FileProcessor.IsWholesaleFailure(Verified(confirmed, failed), null));

    /// <summary>
    /// An explicit --verify-threshold replaces the ratio in both directions - it can condemn a file
    /// the ratio would have recovered, and spare one the ratio would have condemned.
    /// </summary>
    [Fact]
    public void IsWholesaleFailure_ObeysAnExplicitThreshold()
    {
        Assert.True(FileProcessor.IsWholesaleFailure(Verified(confirmed: 19, failed: 1), 0));
        Assert.False(FileProcessor.IsWholesaleFailure(Verified(confirmed: 2, failed: 18), 20));
    }

    /// <summary>
    /// Nothing confirmed is wholesale however high the threshold is set: gap recovery has no anchor
    /// to work from, which is a structural fact rather than a policy the option may overrule.
    /// </summary>
    [Fact]
    public void IsWholesaleFailure_HoldsWithNothingConfirmed_WhateverTheThresholdSays()
        => Assert.True(FileProcessor.IsWholesaleFailure(Verified(confirmed: 0, failed: 3), 100));
}
