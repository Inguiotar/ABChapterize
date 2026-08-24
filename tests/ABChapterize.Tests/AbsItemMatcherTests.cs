// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Xunit;
using ABChapterize.Abs;
using ABChapterize.Audio;

namespace ABChapterize.Tests;

/// <summary>
/// Tests for <see cref="AbsItemMatcher"/>: the order the four clues about a local file are tried
/// in, and the two ways the search can come back empty-handed.
/// </summary>
public sealed class AbsItemMatcherTests
{
    /// <summary>A book with the given title, everything else being beside the point here.</summary>
    /// <param name="title">The library title.</param>
    /// <param name="relativePath">The item folder, which the folder-name clue matches against.</param>
    private static AbsBook Book(string title, string relativePath = "")
        => new(title, title, null, relativePath, 1, 0, 3600);

    /// <summary>A probe result carrying the given container tags.</summary>
    /// <param name="title">The title tag, or null.</param>
    /// <param name="album">The album tag, or null.</param>
    private static MediaInfo Tagged(string? title = null, string? album = null)
        => new(3600, 1000, 0, "aac", "LC", TitleTag: title, AlbumTag: album);

    [Fact]
    public void AlbumTag_IsTriedFirst()
    {
        var books = new[] { Book("DW35 - Wintersmith"), Book("Something Else") };

        var match = AbsItemMatcher.Find(books, @"C:\books\whatever.m4b", Tagged(album: "DW35 - Wintersmith"));

        Assert.Equal("DW35 - Wintersmith", match.Book?.Title);
        Assert.Contains("album tag", match.Reason);
    }

    /// <summary>
    /// The order is the design: a tag was written by whoever produced the audiobook, a file name by
    /// whichever tool last touched it. Where they point at different books, the tag wins.
    /// </summary>
    [Fact]
    public void AlbumTag_OutranksTheFileName()
    {
        var books = new[] { Book("The Right Book"), Book("The Wrong Book") };

        var match = AbsItemMatcher.Find(
            books, @"C:\books\The Wrong Book.m4b", Tagged(album: "The Right Book"));

        Assert.Equal("The Right Book", match.Book?.Title);
    }

    [Fact]
    public void TitleTag_IsUsedWhenTheAlbumTagMatchesNothing()
    {
        var books = new[] { Book("Maskerade") };

        var match = AbsItemMatcher.Find(
            books, @"C:\books\file.m4b", Tagged(title: "Maskerade", album: "A Compilation Nobody Has"));

        Assert.Equal("Maskerade", match.Book?.Title);
        Assert.Contains("title tag", match.Reason);
    }

    [Fact]
    public void FolderName_IsUsedWhenNoTagMatches()
    {
        var books = new[] { Book("Small Gods", relativePath: "DW13 - Small Gods") };

        var match = AbsItemMatcher.Find(books, @"C:\books\DW13 - Small Gods\part.m4b", Tagged());

        Assert.Equal("Small Gods", match.Book?.Title);
        Assert.Contains("folder name", match.Reason);
    }

    [Fact]
    public void FileName_IsTheLastResort()
    {
        var books = new[] { Book("Mort") };

        var match = AbsItemMatcher.Find(books, @"C:\books\Mort.m4b", Tagged());

        Assert.Equal("Mort", match.Book?.Title);
        Assert.Contains("file name", match.Reason);
    }

    /// <summary>
    /// A book this tool itself parked under a ".missing-marks" name is the same book, and a
    /// --push-only sweep over the folder afterwards has to go on recognizing it.
    /// </summary>
    [Fact]
    public void FileName_IgnoresAMissingMarksTag()
    {
        var books = new[] { Book("Mort") };

        var match = AbsItemMatcher.Find(books, @"C:\books\Mort.missing-marks-7-8.m4b", Tagged());

        Assert.Equal("Mort", match.Book?.Title);
    }

    /// <summary>Neither side is reliably the longer one, so containment counts both ways.</summary>
    [Theory]
    [InlineData("Perry Rhodan Silber Edition 087: Das Spiel des Laren", "Das Spiel des Laren")]
    [InlineData("Das Spiel des Laren", "Das Spiel des Laren (ungekuerzt, gelesen von Josef Tratnik)")]
    public void PartialTitles_MatchInEitherDirection(string libraryTitle, string localName)
    {
        var match = AbsItemMatcher.Find([Book(libraryTitle)], $@"C:\books\{localName}.m4b", Tagged());

        Assert.Equal(libraryTitle, match.Book?.Title);
    }

    [Fact]
    public void NoBookAtAll_IsReportedAsSuch()
    {
        var match = AbsItemMatcher.Find([Book("Mort")], @"C:\books\Guards! Guards!.m4b", Tagged());

        Assert.Null(match.Book);
        Assert.Contains("no book on the server matches", match.Reason);
    }

    /// <summary>
    /// The two failures need opposite answers from the user - "it is not there" against "say which
    /// one" - so an ambiguous clue stops the search rather than falling through to the next one and
    /// guessing from something weaker.
    /// </summary>
    [Fact]
    public void SeveralBooksMatching_IsReportedRatherThanGuessedAt()
    {
        var books = new[] { Book("Discworld 1"), Book("Discworld 2"), Book("Discworld 3") };

        var match = AbsItemMatcher.Find(books, @"C:\books\x.m4b", Tagged(album: "Discworld"));

        Assert.Null(match.Book);
        Assert.Contains("matches 3 books", match.Reason);
        Assert.Contains("item:", match.Reason);
    }

    [Fact]
    public void AnExactTitle_WinsOverBooksThatMerelyContainIt()
    {
        var books = new[] { Book("Mort"), Book("Mort and Other Stories") };

        var match = AbsItemMatcher.Find(books, @"C:\books\x.m4b", Tagged(album: "Mort"));

        Assert.Equal("Mort", match.Book?.Title);
    }
}
