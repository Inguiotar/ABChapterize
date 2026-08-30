// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Xunit;
using ABChapterize.Abs;
using ABChapterize.Audio;

namespace ABChapterize.Tests;

/// <summary>
/// Tests for <see cref="AbsItemMatcher"/>: the order the four clues about a local file are tried
/// in, the three ways the search can come back empty-handed, and the play-time test that a book
/// named by any of them still has to pass - which is also what breaks a tie between several.
/// </summary>
public sealed class AbsItemMatcherTests
{
    /// <summary>A book with the given title, everything else being beside the point here.</summary>
    /// <param name="title">The library title.</param>
    /// <param name="relativePath">The item folder, which the folder-name clue matches against.</param>
    /// <param name="seconds">Play time as the library reports it. The default matches
    /// <see cref="Tagged"/>'s, so a test that is not about play times need not mention them.</param>
    /// <param name="audioFiles">How many files the item holds.</param>
    /// <param name="itemId">Its identifier, which an <c>item:</c> mapping names it by. The title
    /// by default, so a test that is not about identifiers need not invent one.</param>
    private static AbsBook Book(
        string title, string relativePath = "", double seconds = 3600, int audioFiles = 1,
        string? itemId = null)
        => new(itemId ?? title, title, null, relativePath, audioFiles, 0, seconds);

    /// <summary>A probe result carrying the given container tags.</summary>
    /// <param name="title">The title tag, or null.</param>
    /// <param name="album">The album tag, or null.</param>
    /// <param name="seconds">Play time of the local file.</param>
    private static MediaInfo Tagged(string? title = null, string? album = null, double seconds = 3600)
        => new(seconds, 1000, 0, "aac", "LC", TitleTag: title, AlbumTag: album);

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
    /// --abs-push-only sweep over the folder afterwards has to go on recognizing it.
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

    /// <summary>
    /// The collision the tie-break was written for: a Perry Rhodan file whose title tag "Stalker"
    /// names both "Silber Edition 150: Stalker" and "Silber Edition 157: Stalker gegen Stalker".
    /// The play time is the one clue here that is not a name, so it is what can separate them.
    /// </summary>
    [Fact]
    public void SeveralBooksMatching_AreSettledByTheOneThatCouldBeThisRecording()
    {
        var books = new[]
        {
            Book("Perry Rhodan Silber Edition 150: Stalker", seconds: 48474),
            Book("Perry Rhodan Silber Edition 157: Stalker gegen Stalker", seconds: 3282),
        };

        var match = AbsItemMatcher.Find(
            books, @"C:\books\Stalker.m4b", Tagged(title: "Stalker", seconds: 48474));

        Assert.Equal("Perry Rhodan Silber Edition 150: Stalker", match.Book?.Title);
        Assert.Contains("title tag", match.Reason);
    }

    /// <summary>
    /// The tie-break adds evidence; it does not lower the bar. Two books this file's play time fits
    /// equally well are still two books, and the user is still the one who says which.
    /// </summary>
    [Fact]
    public void SeveralBooksOfTheRightLength_AreStillReportedRatherThanGuessedAt()
    {
        var books = new[]
        {
            Book("Silber Edition 150: Stalker", seconds: 48474),
            Book("Silber Edition 157: Stalker gegen Stalker", seconds: 48474),
        };

        var match = AbsItemMatcher.Find(
            books, @"C:\books\x.m4b", Tagged(title: "Stalker", seconds: 48474));

        Assert.Null(match.Book);
        Assert.Contains("matches 2 books", match.Reason);
        Assert.Contains("item:", match.Reason);
    }

    /// <summary>
    /// No play time is no evidence, in a tie as much as in a lone match - so a book the server says
    /// nothing about stays in the running and the ambiguity survives. Refusing it here would let a
    /// server that reports play times patchily settle ties by silence.
    /// </summary>
    [Fact]
    public void ABookTheServerReportsNoPlayTimeFor_KeepsATieOpen()
    {
        var books = new[]
        {
            Book("Silber Edition 150: Stalker", seconds: 48474),
            Book("Silber Edition 157: Stalker gegen Stalker", seconds: 0),
        };

        var match = AbsItemMatcher.Find(
            books, @"C:\books\x.m4b", Tagged(title: "Stalker", seconds: 48474));

        Assert.Null(match.Book);
        Assert.Contains("matches 2 books", match.Reason);
    }

    /// <summary>
    /// A book already ruled out by its length is not one of the answers to "which did you mean", so
    /// naming it would only send the reader off to a refusal one step later.
    /// </summary>
    [Fact]
    public void AnAmbiguity_NamesOnlyTheBooksThatCouldBeThisRecording()
    {
        var books = new[]
        {
            Book("Silber Edition 150: Stalker", seconds: 48474),
            Book("Silber Edition 151: Stalker Again", seconds: 48474),
            Book("Silber Edition 157: Stalker gegen Stalker", seconds: 3282),
        };

        var match = AbsItemMatcher.Find(
            books, @"C:\books\x.m4b", Tagged(title: "Stalker", seconds: 48474));

        Assert.Null(match.Book);
        Assert.Contains("matches 2 books", match.Reason);
        Assert.DoesNotContain("157", match.Reason);
    }

    /// <summary>
    /// Where the clue names several and none of them is this recording, "say which one you meant"
    /// is unanswerable - every one of them would be refused for its length. The commonest cause is
    /// one part of a split book, so the note says that instead.
    /// </summary>
    [Fact]
    public void SeveralBooksMatchingAndNoneOfThemThisRecording_SaysSoRatherThanAskingWhich()
    {
        var books = new[]
        {
            Book("Silber Edition 150: Stalker", seconds: 48474),
            Book("Silber Edition 157: Stalker gegen Stalker", seconds: 40000),
        };

        var match = AbsItemMatcher.Find(
            books, @"C:\books\x.m4b", Tagged(title: "Stalker", seconds: 300));

        Assert.Null(match.Book);
        Assert.Contains("none of them runs this file's 5 min", match.Reason);
        Assert.DoesNotContain("item:", match.Reason);
    }

    [Fact]
    public void AnExactTitle_WinsOverBooksThatMerelyContainIt()
    {
        var books = new[] { Book("Mort"), Book("Mort and Other Stories") };

        var match = AbsItemMatcher.Find(books, @"C:\books\x.m4b", Tagged(album: "Mort"));

        Assert.Equal("Mort", match.Book?.Title);
    }

    /// <summary>
    /// The case the play-time test exists for. Every one of a split book's parts carries the same
    /// album tag as the whole, so the clues recognize each of them - and the item's chapter list
    /// describes the concatenated timeline, so a five-minute part would be given a whole book's
    /// marks, nearly all of them past its own end. It is refused for every mode alike: no book
    /// means nothing pulled into the file and nothing sent up from it.
    /// </summary>
    [Fact]
    public void OnePartOfASplitBook_IsNotMatchedToTheWholeBook()
    {
        var books = new[] { Book("DW35 - Wintersmith", seconds: 36000, audioFiles: 135) };

        var match = AbsItemMatcher.Find(
            books, @"C:\books\part003.m4b", Tagged(album: "DW35 - Wintersmith", seconds: 300));

        Assert.Null(match.Book);
        Assert.Contains("not the same recording", match.Reason);
    }

    /// <summary>
    /// Two encodes of one book differ by an encoder's padding and a trimmed tail, which is well
    /// inside the tolerance and must not stop a match.
    /// </summary>
    [Fact]
    public void ASecondOrTwoOfDifference_IsStillTheSameRecording()
    {
        var books = new[] { Book("Wintersmith", seconds: 36000) };

        var match = AbsItemMatcher.Find(
            books, @"C:\books\x.m4b", Tagged(album: "Wintersmith", seconds: 36002.5));

        Assert.Equal("Wintersmith", match.Book?.Title);
    }

    /// <summary>An abridgement, or a different edition of the same title: recognized by name, and
    /// hours apart. The note carries both play times, that being the whole of what a user needs to
    /// see which of their two copies this is.</summary>
    [Fact]
    public void ADifferentEditionOfTheSameTitle_IsRefusedToo()
    {
        var books = new[] { Book("Wintersmith", seconds: 36000) };

        var match = AbsItemMatcher.Find(
            books, @"C:\books\x.m4b", Tagged(title: "Wintersmith", seconds: 21600));

        Assert.Null(match.Book);
        Assert.Contains("600 min", match.Reason);
        Assert.Contains("360 min", match.Reason);
    }

    /// <summary>
    /// The file count is deliberately not the test. A book joined into one file locally but kept as
    /// several on the server is what the sister project produces, and the server's chapter list is
    /// exactly the right one for that file - which is why the play time decides and the file count
    /// does not.
    /// </summary>
    [Fact]
    public void ASplitBookJoinedLocally_IsMatched()
    {
        var books = new[] { Book("Wintersmith", seconds: 36000, audioFiles: 19) };

        var match = AbsItemMatcher.Find(
            books, @"C:\books\x.m4b", Tagged(album: "Wintersmith", seconds: 36000));

        Assert.Equal("Wintersmith", match.Book?.Title);
    }

    /// <summary>
    /// No play time on the server is no evidence, not evidence against: refusing on its absence
    /// would leave a server that answers differently from ours one nothing can be pushed to.
    /// </summary>
    [Fact]
    public void ABookTheServerReportsNoPlayTimeFor_IsStillMatched()
    {
        var books = new[] { Book("Wintersmith", seconds: 0) };

        var match = AbsItemMatcher.Find(
            books, @"C:\books\x.m4b", Tagged(album: "Wintersmith", seconds: 36000));

        Assert.Equal("Wintersmith", match.Book?.Title);
    }

    /// <summary>
    /// A refusal stops the search instead of falling through to the next clue. The album tag is the
    /// most trustworthy thing the file says about itself, and a file name that names a different
    /// book of the right length is not better evidence - it is the guess the clue order exists to
    /// refuse.
    /// </summary>
    [Fact]
    public void ARefusedBook_StopsTheSearchRatherThanTryingTheNextClue()
    {
        var books = new[]
        {
            Book("Wintersmith", seconds: 36000),
            Book("Some Other Book", seconds: 300),
        };

        var match = AbsItemMatcher.Find(
            books, @"C:\books\Some Other Book.m4b", Tagged(album: "Wintersmith", seconds: 300));

        Assert.Null(match.Book);
        Assert.Contains("not the same recording", match.Reason);
    }

    /// <summary>One mapping entry, as a folder's own mapping file would have produced it.</summary>
    /// <param name="fileName">The local file the entry names.</param>
    /// <param name="book">The book selector, or null for "this file has no book on the server".</param>
    private static AbsBookMapping Mapped(string fileName, string? book)
        => new(fileName, book == null ? null : AbsSelector.Parse(book),
               @"C:\books\.abchapterize-abs");

    /// <summary>
    /// Nothing about a file can be more trustworthy than the user saying which book it is, so a
    /// mapping is tried ahead of the tags rather than after them.
    /// </summary>
    [Fact]
    public void AMapping_OutranksEveryTag()
    {
        var books = new[] { Book("The Right Book"), Book("The Wrong Book") };

        var match = AbsItemMatcher.Find(
            books, @"C:\books\Mort.m4b", Tagged(album: "The Wrong Book", title: "The Wrong Book"),
            [Mapped("Mort.m4b", "The Right Book")]);

        Assert.Equal("The Right Book", match.Book?.Title);
        Assert.Contains("the mapping in \".abchapterize-abs\"", match.Reason);
    }

    /// <summary>
    /// The one clue that is not a name. Looked up in the catalogue the run already fetched, so an
    /// id this account cannot see is a plain "not one of your books" rather than a failure a
    /// request later.
    /// </summary>
    [Fact]
    public void AMappingByItemId_NamesThatBookExactly()
    {
        var books = new[]
        {
            Book("Stalker", itemId: "li_one"),
            Book("Stalker gegen Stalker", itemId: "li_two"),
        };

        var match = AbsItemMatcher.Find(
            books, @"C:\books\x.m4b", Tagged(title: "Stalker"), [Mapped("x.m4b", "item:li_two")]);

        Assert.Equal("Stalker gegen Stalker", match.Book?.Title);
    }

    [Fact]
    public void AMappingNamingAnItemThatIsNotThere_SaysSo()
    {
        var match = AbsItemMatcher.Find(
            [Book("Mort", itemId: "li_one")], @"C:\books\x.m4b", Tagged(),
            [Mapped("x.m4b", "item:li_nope")]);

        Assert.Null(match.Book);
        Assert.Contains("li_nope", match.Reason);
        Assert.Contains("not a book on the server", match.Reason);
    }

    /// <summary>
    /// The user's answer, wrong or right, is still an answer: falling through to the tags would
    /// re-open a question they had already closed, and would hide the typo that caused it.
    /// </summary>
    [Fact]
    public void AMappingNamingNoBook_StopsTheSearchRatherThanFallingBackToTheTags()
    {
        var books = new[] { Book("Maskerade") };

        var match = AbsItemMatcher.Find(
            books, @"C:\books\x.m4b", Tagged(album: "Maskerade"),
            [Mapped("x.m4b", "A Book Nobody Has")]);

        Assert.Null(match.Book);
        Assert.Contains("matches no book on the server", match.Reason);
        Assert.False(match.NoClueNamedABook);
    }

    /// <summary>
    /// "none" is how a shelf records that one of its files is not on the server at all, so a
    /// per-file note stops the run reporting the same unmatched book on every sweep.
    /// </summary>
    [Fact]
    public void AMappingSayingNone_ReportsThatWithoutSearching()
    {
        var match = AbsItemMatcher.Find(
            [Book("Mort")], @"C:\books\Mort.m4b", Tagged(album: "Mort"), [Mapped("Mort.m4b", null)]);

        Assert.Null(match.Book);
        Assert.Contains("has no book on the server", match.Reason);
        Assert.False(match.NoClueNamedABook);
    }

    /// <summary>
    /// The user's call, 2026-08-30, and the point of the whole design: a mapping supplies the one
    /// thing the matcher could not work out - which book this is - and nothing else. A hand-written
    /// line is still a name, and a typo pairing a file with the wrong book would put a whole book's
    /// marks past a part's end exactly as an album tag would.
    /// </summary>
    [Fact]
    public void AMappedBook_StillHasToBeTheSameRecording()
    {
        var books = new[] { Book("Wintersmith", seconds: 36000) };

        var match = AbsItemMatcher.Find(
            books, @"C:\books\part003.m4b", Tagged(seconds: 300),
            [Mapped("part003.m4b", "Wintersmith")]);

        Assert.Null(match.Book);
        Assert.Contains("not the same recording", match.Reason);
    }

    [Fact]
    public void AMappingForAnotherFile_LeavesTheOrdinaryCluesToDecide()
    {
        var books = new[] { Book("Mort"), Book("Maskerade") };

        var match = AbsItemMatcher.Find(
            books, @"C:\books\Mort.m4b", Tagged(album: "Mort"),
            [Mapped("Maskerade.m4b", "Maskerade")]);

        Assert.Equal("Mort", match.Book?.Title);
        Assert.Contains("album tag", match.Reason);
    }

    /// <summary>
    /// The flag that decides whether the server is worth a request; every other empty-handed
    /// outcome is a stop, and looking further after one is the guess the clue order refuses.
    /// </summary>
    [Fact]
    public void OnlyAClueThatNamedNothingAtAll_LeavesTheSearchOpen()
    {
        var books = new[] { Book("Discworld 1"), Book("Discworld 2") };

        Assert.True(AbsItemMatcher.Find(books, @"C:\books\x.m4b", Tagged()).NoClueNamedABook);
        Assert.False(AbsItemMatcher
            .Find(books, @"C:\books\x.m4b", Tagged(album: "Discworld")).NoClueNamedABook);
    }

    /// <summary>The note points at the way out, the way an ambiguity points at "item:".</summary>
    [Fact]
    public void NoBookAtAll_PointsAtTheMappingFile()
        => Assert.Contains(
            "--abs-map", AbsItemMatcher.Find([Book("Mort")], @"C:\books\x.m4b", Tagged()).Reason);

    /// <summary>
    /// The narrowing that decides who is worth a request. A book the server reports no play time
    /// for is left out here although the ordinary test keeps it: there the silence must not refuse a
    /// book a name has already picked out, here it is the only evidence there is for spending a
    /// request at all.
    /// </summary>
    [Fact]
    public void PossibleRecordings_KeepsOnlyTheBooksOfThisLength()
    {
        var books = new[]
        {
            Book("Right Length", seconds: 36000),
            Book("Wrong Length", seconds: 300),
            Book("No Play Time", seconds: 0),
        };

        var possible = AbsItemMatcher.PossibleRecordings(books, Tagged(seconds: 36002));

        Assert.Equal("Right Length", Assert.Single(possible).Title);
    }

    /// <summary>
    /// The case the last-resort stage exists for: a local copy of the server's own file, whose tags
    /// name nothing and whose name is nothing like the library title.
    /// </summary>
    [Fact]
    public void AServerFileName_RecognizesAFileNoTagOrTitleNames()
    {
        var candidates = new[]
        {
            new AbsBookFiles(Book("Perry Rhodan Silber Edition 150: Stalker", seconds: 48474),
                             ["PR-SE-150.m4b"]),
            new AbsBookFiles(Book("Perry Rhodan Silber Edition 151: Anything", seconds: 48474),
                             ["PR-SE-151.m4b"]),
        };

        var match = AbsItemMatcher.FindByServerFileName(
            candidates, @"C:\books\PR-SE-150.m4b", Tagged(seconds: 48474));

        Assert.Equal("Perry Rhodan Silber Edition 150: Stalker", match.Book?.Title);
        Assert.Contains("against the file name on the server", match.Reason);
    }

    /// <summary>
    /// A book split on the server but joined into one file locally is a book this tool works on, so
    /// its file names have to be readable - the play time is what settles whether the joined file
    /// really is the whole book.
    /// </summary>
    [Fact]
    public void AServerFileName_IsMatchedWhicheverOfABooksFilesCarriesIt()
    {
        var candidates = new[]
        {
            new AbsBookFiles(Book("Wintersmith", seconds: 36000, audioFiles: 3),
                             ["part001.m4b", "wintersmith-full.m4b", "part003.m4b"]),
        };

        var match = AbsItemMatcher.FindByServerFileName(
            candidates, @"C:\books\wintersmith-full.m4b", Tagged(seconds: 36000));

        Assert.Equal("Wintersmith", match.Book?.Title);
    }

    /// <summary>
    /// The stage adds a name to agree with; it does not make the play time an identifier. Two books
    /// of one length whose files are named alike are still two books.
    /// </summary>
    [Fact]
    public void TwoServerFilesOfTheSameName_AreStillReportedRatherThanGuessedAt()
    {
        var candidates = new[]
        {
            new AbsBookFiles(Book("Volume One", seconds: 36000), ["audiobook.m4b"]),
            new AbsBookFiles(Book("Volume Two", seconds: 36000), ["audiobook.m4b"]),
        };

        var match = AbsItemMatcher.FindByServerFileName(
            candidates, @"C:\books\audiobook.m4b", Tagged(seconds: 36000));

        Assert.Null(match.Book);
        Assert.Contains("matches 2 books", match.Reason);
    }

    [Fact]
    public void ServerFileNamesThatNameNothing_LeaveTheOriginalOutcomeToStand()
    {
        var candidates = new[]
        {
            new AbsBookFiles(Book("Something Else", seconds: 36000), ["something-else.m4b"]),
        };

        var match = AbsItemMatcher.FindByServerFileName(
            candidates, @"C:\books\Mort.m4b", Tagged(seconds: 36000));

        Assert.Null(match.Book);
        Assert.True(match.NoClueNamedABook);
    }

    /// <summary>
    /// Every clue names itself with its article, because the fifth source is a phrase that already
    /// carries one. Found by a live run against the test server: a mapped title that came out
    /// ambiguous read "the the mapping in ...".
    /// </summary>
    [Fact]
    public void AMappedTitleThatIsAmbiguous_ReadsAsOneSentence()
    {
        // Neither is exactly "Stalker", so both survive to the containment tier and the mapping
        // comes out ambiguous rather than settled.
        var books = new[] { Book("Stalker Rises"), Book("Stalker Strikes Back") };

        var match = AbsItemMatcher.Find(
            books, @"C:\books\x.m4b", Tagged(), [Mapped("x.m4b", "Stalker")]);

        Assert.Null(match.Book);
        Assert.DoesNotContain("the the", match.Reason);
        Assert.Contains(
            "the mapping in \".abchapterize-abs\" \"Stalker\" matches 2 books", match.Reason);
    }
}
