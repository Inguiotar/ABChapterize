// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Xunit;
using ABChapterize.Abs;

namespace ABChapterize.Tests;

/// <summary>
/// Tests for <see cref="AbsBook"/> and the temporary-copy naming in <see cref="AbsWorkspace"/>:
/// the two response shapes a book can arrive in, and what a server-side file name has to survive
/// to become a local one.
/// </summary>
public sealed class AbsBookTests
{
    /// <summary>An item as a minified library listing sends it: counts, no lists.</summary>
    /// <param name="files">How many audio files it reports.</param>
    /// <param name="chapters">How many chapters it reports.</param>
    private static AbsWire.Item Minified(int files, int chapters) => new()
    {
        Id = "id",
        RelPath = "01 - Die Dritte Macht",
        Media = new AbsWire.Media
        {
            NumAudioFiles = files,
            NumChapters = chapters,
            Duration = 3600,
            Metadata = new AbsWire.Metadata { Title = "Die Dritte Macht", AuthorName = "K.H. Scheer" },
        },
    };

    /// <summary>The same item as a full response sends it: lists, and authors as objects.</summary>
    private static AbsWire.Item Full() => new()
    {
        Id = "id",
        RelPath = "01 - Die Dritte Macht",
        Media = new AbsWire.Media
        {
            AudioFiles = [new AbsWire.AudioFile { Ino = "6162" }],
            Chapters = [new AbsWire.Chapter { Id = 0, Start = 0, End = 60, Title = "Intro" }],
            Duration = 3600,
            Metadata = new AbsWire.Metadata
            {
                Title = "Die Dritte Macht",
                Authors = [new AbsWire.NamedEntity { Name = "K.H. Scheer" },
                           new AbsWire.NamedEntity { Name = "Clark Darlton" }],
            },
        },
    };

    [Fact]
    public void MinifiedItem_ReadsItsCounts()
    {
        var book = AbsBook.From(Minified(files: 1, chapters: 36))!;

        Assert.Equal(1, book.AudioFileCount);
        Assert.Equal(36, book.ChapterCount);
        Assert.Equal("K.H. Scheer", book.Author);
        Assert.True(book.IsSingleFile);
    }

    /// <summary>
    /// The full shape counts its lists instead, which is what keeps a book listed one way and
    /// fetched the other from looking like two different books. A full response carries no
    /// <c>numAudioFiles</c>, so trusting that field would read every one of them as empty.
    /// </summary>
    [Fact]
    public void FullItem_CountsItsListsInstead()
    {
        var book = AbsBook.From(Full())!;

        Assert.Equal(1, book.AudioFileCount);
        Assert.Equal(1, book.ChapterCount);
        Assert.True(book.IsSingleFile);
    }

    /// <summary>A minified response flattens the authors into one string, a full one leaves them
    /// as a list; both have to arrive at the same author line.</summary>
    [Fact]
    public void FullItem_JoinsTheAuthorList()
        => Assert.Equal("K.H. Scheer, Clark Darlton", AbsBook.From(Full())!.Author);

    [Fact]
    public void MultiFileItem_IsNotSingleFile()
        => Assert.False(AbsBook.From(Minified(files: 19, chapters: 19))!.IsSingleFile);

    [Fact]
    public void ItemWithoutMedia_IsNoBook()
        => Assert.Null(AbsBook.From(new AbsWire.Item { Id = "id" }));

    [Fact]
    public void MissingTitle_StillYieldsAUsableName()
    {
        var item = Minified(1, 0);
        item.Media!.Metadata!.Title = null;

        Assert.Equal("(untitled)", AbsBook.From(item)!.Title);
    }

    /// <summary>What <c>--filter</c> matches against in ABS mode, standing in for a path that does
    /// not exist yet: either half of what the user can see in their library selects.</summary>
    [Fact]
    public void FilterText_CoversTheFolderAndTheTitle()
    {
        var book = AbsBook.From(Minified(1, 0))!;

        Assert.Equal("01 - Die Dritte Macht/Die Dritte Macht", book.FilterText);
    }

    [Fact]
    public void FilterText_WithNoItemFolder_IsJustTheTitle()
    {
        var item = Minified(1, 0);
        item.RelPath = "";

        Assert.Equal("Die Dritte Macht", AbsBook.From(item)!.FilterText);
    }

    /// <summary>
    /// Audiobookshelf runs on Linux in the ordinary case, where a file name may hold characters
    /// Windows refuses outright - a colon above all, which every "Series 01: Title.m4b" carries.
    /// </summary>
    [Fact]
    public void SafeName_SubstitutesWhatTheLocalFileSystemRefuses()
    {
        var safe = AbsWorkspace.SafeName("Silber Edition 001: Die Dritte Macht.m4b");

        Assert.Equal(-1, safe.IndexOfAny(Path.GetInvalidFileNameChars()));
        Assert.Contains("Die Dritte Macht", safe);
    }

    [Fact]
    public void SafeName_NeverComesBackEmpty()
        => Assert.Equal("audiobook", AbsWorkspace.SafeName("   "));

    [Fact]
    public void SafeName_LeavesAnOrdinaryNameAlone()
        => Assert.Equal("Mort.m4b", AbsWorkspace.SafeName("Mort.m4b"));

    /// <summary>
    /// The name arrives over the network and is then joined onto a folder path, so it is a guard
    /// rather than a convenience: a server answering "..", or with a separator the local rules do
    /// not count as invalid, must not put the download outside the folder meant for it.
    /// </summary>
    [Theory]
    [InlineData("..", "audiobook")]
    [InlineData(".", "audiobook")]
    [InlineData("...", "audiobook")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData(@"..\..\Windows\System32\evil.dll", "evil.dll")]
    [InlineData("sub/dir/Mort.m4b", "Mort.m4b")]
    public void SafeName_CannotEscapeItsFolder(string name, string expected)
        => Assert.Equal(expected, AbsWorkspace.SafeName(name));
}
