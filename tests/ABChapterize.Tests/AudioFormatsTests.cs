// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Audio;
using Xunit;

namespace ABChapterize.Tests;

/// <summary>
/// Covers the one question <see cref="AudioFormats"/> answers, and the two containers whose
/// absence from the list is the point of having a list at all.
/// </summary>
public sealed class AudioFormatsTests
{
    [Theory]
    [InlineData("book.m4b")]
    [InlineData("book.m4a")]
    [InlineData("book.mp3")]
    [InlineData("book.opus")]
    [InlineData("book.mka")]
    // The extension is read case-insensitively: what a file is called is the user's business.
    [InlineData("BOOK.M4B")]
    [InlineData(@"D:\Audiobooks\Some Book (2019).m4b")]
    public void ChapterCapableContainers_CanHoldChapters(string path) =>
        Assert.True(AudioFormats.CanHoldChapters(path));

    [Theory]
    // Both accept chapters through ffmpeg's muxer and silently drop them, which is why they are
    // named rather than attempted. A run that writes into files must not touch either.
    [InlineData("book.flac")]
    [InlineData("book.ogg")]
    [InlineData("book.wav")]
    [InlineData("book.mp4")]
    [InlineData("book")]
    public void EverythingElse_Cannot(string path) =>
        Assert.False(AudioFormats.CanHoldChapters(path));

    [Fact]
    public void TheListedText_NamesEveryExtension()
    {
        foreach (var extension in AudioFormats.ChapterCapable)
            Assert.Contains(extension, AudioFormats.ChapterCapableText);
    }
}
