// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Xunit;
using ABChapterize.Abs;
using ABChapterize.Errors;

namespace ABChapterize.Tests;

/// <summary>
/// Tests for <see cref="AbsBookMap"/>: the <c>file name = book</c> grammar, the two ways an entry
/// says a file has no book at all, and the rule deciding which entry a given file is named by.
/// </summary>
public sealed class AbsBookMapTests : IDisposable
{
    private readonly string _dir;

    /// <summary>A folder for the mapping files these tests write.</summary>
    public AbsBookMapTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"abchapterize-absmap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    /// <summary>Removes the folder.</summary>
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>Writes a mapping file and parses it.</summary>
    /// <param name="lines">Its lines.</param>
    private IReadOnlyList<AbsBookMapping> Parse(params string[] lines)
    {
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.txt");
        File.WriteAllLines(path, lines);
        return AbsBookMap.ParseFile(path);
    }

    /// <summary>Writes a mapping file and expects it to be refused.</summary>
    /// <param name="lines">Its lines.</param>
    private CliError Refused(params string[] lines)
        => Assert.Throws<CliError>(() => Parse(lines));

    [Fact]
    public void AnItemIdEntry_NamesThatItem()
    {
        var mapped = Assert.Single(Parse("Stalker.m4b = item:li_8gch9ve"));

        Assert.Equal("Stalker.m4b", mapped.FileName);
        Assert.Equal(AbsSelectorKind.Item, mapped.Book?.Kind);
        Assert.Equal("li_8gch9ve", mapped.Book?.Value);
    }

    /// <summary>
    /// The right-hand side is an ordinary selector, so a bare name is a title - and a library title
    /// keeps the colon it carries, which is the whole reason the separator here is "=" and not ":".
    /// </summary>
    [Fact]
    public void AnUnprefixedEntry_NamesATitleAndKeepsItsOwnColon()
    {
        var mapped = Assert.Single(Parse("x.m4b = Silber Edition 150: Stalker"));

        Assert.Equal(AbsSelectorKind.Title, mapped.Book?.Kind);
        Assert.Equal("Silber Edition 150: Stalker", mapped.Book?.Value);
    }

    /// <summary>An entry is as likely to be written by deleting a book as by typing a word, so
    /// both spellings have to mean the same thing.</summary>
    [Theory]
    [InlineData("x.m4b = none")]
    [InlineData("x.m4b = NONE")]
    [InlineData("x.m4b =")]
    [InlineData("x.m4b =    ")]
    public void NoneAndAnEmptyRightHandSide_BothMeanThereIsNoBook(string line)
        => Assert.Null(Assert.Single(Parse(line)).Book);

    /// <summary>A book really called "none" is still reachable, which is what keeps the shorthand
    /// from taking a name away.</summary>
    [Fact]
    public void ABookActuallyCalledNone_IsStillNameable()
        => Assert.Equal("none", Assert.Single(Parse("x.m4b = title:none")).Book?.Value);

    [Fact]
    public void BlankAndCommentLines_AreSkipped()
    {
        var mappings = Parse("# my shelf", "", "   ", "a.m4b = item:one", "b.m4b = item:two");

        Assert.Equal(2, mappings.Count);
    }

    /// <summary>The first "=" separates, so a file name holding one is quoted to say so.</summary>
    [Fact]
    public void AQuotedFileName_MayContainTheSeparator()
    {
        var mapped = Assert.Single(Parse("\"E=mc2 The Audiobook.m4b\" = item:x"));

        Assert.Equal("E=mc2 The Audiobook.m4b", mapped.FileName);
        Assert.Equal("x", mapped.Book?.Value);
    }

    [Theory]
    [InlineData("no separator here")]
    [InlineData("= item:x")]
    [InlineData("\"unterminated.m4b = item:x")]
    // A set of books, where an entry has to name one.
    [InlineData("x.m4b = library:Discworld")]
    [InlineData("x.m4b = series:Perry Rhodan")]
    [InlineData("x.m4b = all")]
    public void AMalformedEntry_IsRefused(string line) => Refused(line);

    /// <summary>The same rule <c>--custom-file</c> follows: a file that turned out to say nothing
    /// is more likely a mistake than an intention.</summary>
    [Fact]
    public void AMappingFileWithNoEntries_IsRefused() => Refused("# nothing but a comment");

    [Fact]
    public void AnUnreadableFile_IsRefusedAsACommandLineError()
        => Assert.Throws<CliError>(
            () => AbsBookMap.ParseFile(Path.Combine(_dir, "does-not-exist.txt")));

    /// <summary>An error names the line it is on, a mapping file being something people edit by
    /// hand.</summary>
    [Fact]
    public void AnErrorNamesTheLineItIsOn()
    {
        var error = Refused("a.m4b = item:x", "", "this line has no separator");

        Assert.Contains("line 3", error.Message);
    }

    [Theory]
    [InlineData("Mort.m4b", @"C:\books\Mort.m4b")]
    [InlineData("Mort", @"C:\books\Mort.m4b")]
    [InlineData("mort.M4B", @"C:\books\Mort.m4b")]
    public void AnEntry_NamesTheFileWithOrWithoutItsExtension(string written, string path)
        => Assert.NotNull(AbsBookMap.Find(Parse($"{written} = item:x"), path));

    /// <summary>
    /// This tool renames the files it cannot finish, so an entry that stopped working once a run
    /// had parked its book under a ".missing-marks" name would fail in exactly the situation it was
    /// written for.
    /// </summary>
    [Theory]
    [InlineData("Mort.m4b")]
    [InlineData("Mort")]
    public void AnEntry_StillNamesAFileARunHasTaggedAsMissingMarks(string written)
        => Assert.NotNull(AbsBookMap.Find(
            Parse($"{written} = item:x"), @"C:\books\Mort.missing-marks-7-8.m4b"));

    [Fact]
    public void AnEntryForAnotherFile_NamesNothing()
        => Assert.Null(AbsBookMap.Find(Parse("Mort.m4b = item:x"), @"C:\books\Maskerade.m4b"));

    /// <summary>
    /// Mapping files layer the way options do - outermost folder, inner folders, then the command
    /// line - so the nearer entry has to win. One rule for two files and for one, which is why a
    /// repeat within a single file is not an error either.
    /// </summary>
    [Fact]
    public void ALaterEntry_WinsOverAnEarlierOne()
    {
        var mappings = Parse("Mort.m4b = item:outer", "Mort.m4b = item:inner");

        Assert.Equal("inner", AbsBookMap.Find(mappings, @"C:\books\Mort.m4b")?.Book?.Value);
    }

    /// <summary>The note names the mapping file, not its whole path: the folder it sits in is the
    /// one the file being processed sits in too.</summary>
    [Fact]
    public void AnEntry_NamesTheFileItCameFromForAMessage()
    {
        var path = Path.Combine(_dir, ".abchapterize-abs");
        File.WriteAllLines(path, ["Mort.m4b = item:x"]);

        Assert.Equal(".abchapterize-abs", Assert.Single(AbsBookMap.ParseFile(path)).Where);
    }
}
