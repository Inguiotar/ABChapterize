// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Xunit;

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

    /// <summary>Builds a minimal <see cref="DetectionResult"/> with the given chapters and
    /// <see cref="DetectionResult.LeadInHasSpeech"/>, everything else at its default/empty value.</summary>
    private DetectionResult Result(List<DetectedChapter> chapters, bool leadInHasSpeech = true)
        => new(chapters, false, [], [], _profile, null, 0,
            new DetectionStats(null, null, null, null, 0, 0), LeadInHasSpeech: leadInHasSpeech);

    [Fact]
    public void BuildChapters_InsertsIntro_WhenFirstChapterStartsPastZero()
    {
        var (chapters, note) = FileProcessor.BuildChapters(Result([new(1, 30)]));

        Assert.Equal([new Chapter(0, "Intro"), new Chapter(30, "Chapter 1")], chapters);
        Assert.Equal(" + intro", note);
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
