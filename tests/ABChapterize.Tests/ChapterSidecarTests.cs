// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Xunit;
using ABChapterize.Audio;
using ABChapterize.Detection;
using ABChapterize.Errors;

namespace ABChapterize.Tests;

/// <summary>Tests for the --export/--import sidecar reader/writer in <see cref="ChapterSidecar"/>.</summary>
public class ChapterSidecarTests
{
    [Theory]
    [InlineData(false, "book.m4b.chapters.ffmeta")]
    [InlineData(true, "book.m4b.chapters.txt")]
    public void PathFor_UsesFormatSpecificSuffix(bool simple, string expected)
    {
        Assert.Equal(expected, ChapterSidecar.PathFor("book.m4b", simple));
    }

    [Fact]
    public void BuildSimple_WritesOneLinePerChapter()
    {
        var chapters = new List<Chapter> { new(0, "Intro"), new(3661.5, "Chapter 1") };
        var text = ChapterSidecar.BuildSimple(chapters);
        Assert.Equal("0:00:00.000  Intro\n1:01:01.500  Chapter 1\n", text.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void BuildSimple_StripsEmbeddedNewlinesFromTitles()
    {
        var chapters = new List<Chapter> { new(0, "Line1\nLine2") };
        var text = ChapterSidecar.BuildSimple(chapters);
        Assert.Equal("0:00:00.000  Line1 Line2\n", text.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void ParseSimple_RoundTripsThroughBuildSimple()
    {
        var chapters = new List<Chapter> { new(0, "Intro"), new(3661.5, "Chapter 1") };
        var parsed = ChapterSidecar.ParseSimple(ChapterSidecar.BuildSimple(chapters), "test.txt");
        Assert.Equal(2, parsed.Count);
        Assert.Equal(0, parsed[0].StartSeconds, 3);
        Assert.Equal("Intro", parsed[0].Title);
        Assert.Equal(3661.5, parsed[1].StartSeconds, 3);
        Assert.Equal("Chapter 1", parsed[1].Title);
    }

    [Fact]
    public void ParseSimple_RoundTripsAMarkPastTwentyFourHours()
    {
        // The writer used to render hours as the component of a day, so a mark at 25:01:01.500
        // went out as "1:01:01.500" and came back a day early - silent corruption on an omnibus
        // long enough to need it. The reader always accepted unbounded hours.
        var chapters = new List<Chapter> { new(90061.5, "Chapter 40") };
        var text = ChapterSidecar.BuildSimple(chapters);
        Assert.Equal("25:01:01.500  Chapter 40\n", text.ReplaceLineEndings("\n"));
        Assert.Equal(90061.5, ChapterSidecar.ParseSimple(text, "test.txt")[0].StartSeconds, 3);
    }

    [Fact]
    public void ParseSimple_SkipsBlankAndCommentLines()
    {
        var text = "; comment\n\n# also a comment\n0:00:00.000  Intro\n";
        var parsed = ChapterSidecar.ParseSimple(text, "test.txt");
        Assert.Single(parsed);
        Assert.Equal("Intro", parsed[0].Title);
    }

    [Fact]
    public void ParseSimple_ThrowsOnMalformedLine()
    {
        var ex = Assert.Throws<AppError>(() => ChapterSidecar.ParseSimple("not a valid line\n", "test.txt"));
        Assert.Contains("test.txt", ex.Message);
        Assert.Contains("line 1", ex.Message);
    }

    [Fact]
    public void ParseSimple_ThrowsWhenNoChaptersResult()
    {
        Assert.Throws<AppError>(() => ChapterSidecar.ParseSimple("; only comments\n", "test.txt"));
    }

    [Fact]
    public void ParseFfMetadata_RoundTripsThroughBuildFfMetadata()
    {
        var chapters = new List<Chapter> { new(0, "Intro"), new(600.25, "Chapter 1") };
        var meta = FfmpegClient.BuildFfMetadata(chapters, 3600);
        var parsed = ChapterSidecar.ParseFfMetadata(meta, "test.ffmeta");

        Assert.Equal(2, parsed.Count);
        Assert.Equal(0, parsed[0].StartSeconds, 3);
        Assert.Equal("Intro", parsed[0].Title);
        Assert.Equal(600.25, parsed[1].StartSeconds, 3);
        Assert.Equal("Chapter 1", parsed[1].Title);
    }

    [Fact]
    public void ParseFfMetadata_UnescapesTitles()
    {
        var chapters = new List<Chapter> { new(0, @"A=B;C#D\E") };
        var meta = FfmpegClient.BuildFfMetadata(chapters, 60);
        var parsed = ChapterSidecar.ParseFfMetadata(meta, "test.ffmeta");
        Assert.Equal(@"A=B;C#D\E", parsed[0].Title);
    }

    [Fact]
    public void ParseFfMetadata_ThrowsWhenNoChaptersResult()
    {
        Assert.Throws<AppError>(() => ChapterSidecar.ParseFfMetadata(";FFMETADATA1\n", "test.ffmeta"));
    }
}
