// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Xunit;

namespace ABChapterize.Tests;

/// <summary>
/// Tests for the FFMETADATA1 document builder and its escaping rules in
/// <see cref="FfmpegClient"/>.
/// </summary>
public class FfMetadataTests
{
    [Fact]
    public void BuildFfMetadata_WritesChaptersWithMillisecondTimebase()
    {
        var chapters = new List<Chapter> { new(0, "Intro"), new(600.25, "Chapter 1") };
        var meta = FfmpegClient.BuildFfMetadata(chapters, 3600);

        Assert.Equal("""
            ;FFMETADATA1
            [CHAPTER]
            TIMEBASE=1/1000
            START=0
            END=600250
            title=Intro
            [CHAPTER]
            TIMEBASE=1/1000
            START=600250
            END=3600000
            title=Chapter 1

            """.ReplaceLineEndings("\n"), meta);
    }

    [Fact]
    public void BuildFfMetadata_KeepsChapterEndsAboveStarts()
    {
        // Two chapters at the same position must not produce a zero-length chapter,
        // which some muxers reject.
        var chapters = new List<Chapter> { new(10, "A"), new(10, "B") };
        var meta = FfmpegClient.BuildFfMetadata(chapters, 10);
        Assert.Contains("START=10000\nEND=10001\ntitle=A", meta);
    }

    [Fact]
    public void BuildFfMetadata_EscapesSpecialCharactersInTitles()
    {
        var chapters = new List<Chapter> { new(0, @"A=B;C#D\E") };
        var meta = FfmpegClient.BuildFfMetadata(chapters, 60);
        Assert.Contains(@"title=A\=B\;C\#D\\E", meta);
    }

    [Theory]
    [InlineData("plain title", "plain title")]
    [InlineData("a=b", @"a\=b")]
    [InlineData("a;b", @"a\;b")]
    [InlineData("a#b", @"a\#b")]
    [InlineData(@"a\b", @"a\\b")]
    [InlineData("a\nb", "a\\\nb")]
    public void EscapeMeta_EscapesExactlyTheReservedCharacters(string raw, string expected)
    {
        Assert.Equal(expected, FfmpegClient.EscapeMeta(raw));
    }
}
