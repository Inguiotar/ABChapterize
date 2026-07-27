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
        List<DetectedChapter> chapters, bool leadInHasSpeech = true, List<DetectedMark>? named = null)
        => new(chapters, named ?? [], false, [], [], _profile, null, 0,
            new DetectionStats(null, null, null, null, 0, 0), LeadInHasSpeech: leadInHasSpeech);

    [Fact]
    public void BuildChapters_InsertsIntro_WhenFirstChapterStartsPastZero()
    {
        var (chapters, note) = FileProcessor.BuildChapters(Result([new(1, 30)]));

        Assert.Equal([new Chapter(0, "Intro"), new Chapter(30, "Chapter 1")], chapters);
        Assert.Equal(" + intro", note);
    }

    [Fact]
    public void BuildChapters_MergesNamedMarksByTime()
    {
        var (chapters, note) = FileProcessor.BuildChapters(Result(
            [new(1, 300), new(2, 900)],
            named: [new("prologue", "Prologue", 30), new("epilogue", "Epilogue", 1500)]));

        Assert.Equal(
            [new Chapter(0, "Intro"), new Chapter(30, "Prologue"), new Chapter(300, "Chapter 1"),
             new Chapter(900, "Chapter 2"), new Chapter(1500, "Epilogue")],
            chapters);
        Assert.Equal(" + intro", note);
    }

    [Fact]
    public void BuildChapters_SortsANamedMarkAfterAChapterAtTheSameTime()
    {
        // Both announced in one breath: the numbered entry is the one a player scrubs by, so it
        // must not be pushed behind the prologue that shares its timestamp.
        var (chapters, _) = FileProcessor.BuildChapters(Result(
            [new(1, 300)], named: [new("prologue", "Prologue", 300)]));

        Assert.Equal(
            [new Chapter(0, "Intro"), new Chapter(300, "Chapter 1"), new Chapter(300, "Prologue")],
            chapters);
    }

    [Fact]
    public void BuildChapters_OmitsIntro_WhenANamedMarkAlreadyStartsAtZero()
    {
        var (chapters, note) = FileProcessor.BuildChapters(Result(
            [new(1, 300)], named: [new("prologue", "Prologue", 0)]));

        Assert.Equal([new Chapter(0, "Prologue"), new Chapter(300, "Chapter 1")], chapters);
        Assert.Equal("", note);
    }

    [Fact]
    public void BuildChapters_InsertsIntro_ForASubSecondGap()
    {
        // The old 1.0 s grace period is gone: any nonzero gap gets its own Intro entry now.
        var (chapters, note) = FileProcessor.BuildChapters(Result([new(1, 0.5)]));

        Assert.Equal([new Chapter(0, "Intro"), new Chapter(0.5, "Chapter 1")], chapters);
        Assert.Equal(" + intro", note);
    }

    [Fact]
    public void BuildChapters_OmitsIntro_WhenFirstChapterStartsExactlyAtZero()
    {
        var (chapters, note) = FileProcessor.BuildChapters(Result([new(1, 0)]));

        Assert.Equal([new Chapter(0, "Chapter 1")], chapters);
        Assert.Equal("", note);
    }

    [Fact]
    public void BuildChapters_OmitsIntro_WhenNoSpeechPrecedesTheFirstChapterPhrase()
    {
        // A jingle, music or silence-only lead-in with no actual spoken prelude: even several
        // minutes in, there is nothing to give its own "Intro" entry - the mp4 muxer's own
        // start-snap absorbs the lead-in into chapter 1 instead.
        var (chapters, note) = FileProcessor.BuildChapters(Result([new(1, 180)], leadInHasSpeech: false));

        Assert.Equal([new Chapter(180, "Chapter 1")], chapters);
        Assert.Equal("", note);
    }

    [Fact]
    public void BuildChapters_ReturnsEmpty_WhenNoChaptersWereFound()
    {
        var (chapters, note) = FileProcessor.BuildChapters(Result([]));

        Assert.Empty(chapters);
        Assert.Equal("", note);
    }
}
