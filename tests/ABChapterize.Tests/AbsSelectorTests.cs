// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Xunit;
using ABChapterize.Abs;
using ABChapterize.Errors;

namespace ABChapterize.Tests;

/// <summary>
/// Tests for <see cref="AbsSelector"/>: the prefix grammar, the rule that keeps a colon inside a
/// title from being read as one, and the loose name matching everything above it is built on.
/// </summary>
public sealed class AbsSelectorTests
{
    [Theory]
    [InlineData("library:Discworld", AbsSelectorKind.Library, "Discworld")]
    [InlineData("series:Zyklus 02", AbsSelectorKind.Series, "Zyklus 02")]
    [InlineData("collection:Favourites", AbsSelectorKind.Collection, "Favourites")]
    [InlineData("item:abc-123", AbsSelectorKind.Item, "abc-123")]
    [InlineData("title:Mort", AbsSelectorKind.Title, "Mort")]
    [InlineData("LIBRARY:Discworld", AbsSelectorKind.Library, "Discworld")]
    public void Prefixes_AreRecognized(string argument, AbsSelectorKind kind, string value)
    {
        var selector = AbsSelector.Parse(argument);
        Assert.Equal(kind, selector.Kind);
        Assert.Equal(value, selector.Value);
        Assert.Equal(argument, selector.Raw);
    }

    [Theory]
    [InlineData("all")]
    [InlineData("ALL")]
    [InlineData("*")]
    [InlineData("everything")]
    public void Everything_HasThreeSpellings(string argument)
        => Assert.Equal(AbsSelectorKind.All, AbsSelector.Parse(argument).Kind);

    [Fact]
    public void UnprefixedArgument_IsATitle()
    {
        var selector = AbsSelector.Parse("The Colour of Magic");
        Assert.Equal(AbsSelectorKind.Title, selector.Kind);
        Assert.Equal("The Colour of Magic", selector.Value);
    }

    /// <summary>
    /// The rule that makes the grammar usable at all: real library titles carry colons, so only a
    /// known keyword in front of one introduces a prefix.
    /// </summary>
    [Theory]
    [InlineData("Perry Rhodan Silber Edition 001: Die Dritte Macht")]
    [InlineData("Discworld: The Colour of Magic")]
    [InlineData("nonsense:something")]
    // The four shorthands removed 2026-08-28. "book:" is the one that mattered - it is an
    // ordinary word, so while it was a keyword a book really called "Book: A Novel" was read
    // as a request for a title called "A Novel". The other three go with it because a
    // grammar with an undocumented half is what made that possible; see AbsSelector.Prefixes.
    [InlineData("book:Mort")]
    [InlineData("Book: A Novel")]
    [InlineData("lib:Discworld")]
    [InlineData("coll:Favourites")]
    [InlineData("id:abc-123")]
    public void AColonThatIsNotAKnownPrefix_StaysPartOfTheTitle(string argument)
    {
        var selector = AbsSelector.Parse(argument);
        Assert.Equal(AbsSelectorKind.Title, selector.Kind);
        Assert.Equal(argument, selector.Value);
    }

    [Theory]
    [InlineData("library:")]
    [InlineData("series:   ")]
    [InlineData("")]
    public void APrefixWithNothingAfterIt_Refuses(string argument)
        => Assert.Throws<CliError>(() => AbsSelector.Parse(argument));

    [Theory]
    [InlineData("DW01 - The Colour of Magic", "dw01 the colour of magic")]
    [InlineData("Silber Edition 001: Die Dritte Macht", "silber edition 001 die dritte macht")]
    [InlineData("  spaced   out  ", "spaced out")]
    [InlineData("---", "")]
    public void Normalize_KeepsOnlyLettersDigitsAndSingleSpaces(string text, string expected)
        => Assert.Equal(expected, AbsSelector.Normalize(text));

    /// <summary>Casing is full Unicode despite <c>InvariantGlobalization</c>, which .NET 5 moved
    /// off ICU - so a non-Latin title normalizes as well as a Latin one.</summary>
    [Fact]
    public void Normalize_LowerCasesOutsideTheLatinAlphabet()
        => Assert.Equal("глава", AbsSelector.Normalize("ГЛАВА"));

    [Theory]
    [InlineData("Perry Rhodan Silber Edition 003: Der Unsterbliche", "Der Unsterbliche", true)]
    [InlineData("DW01 - The Colour of Magic", "colour of magic", true)]
    [InlineData("DW01 - The Colour of Magic", "Mort", false)]
    // A wanted name that normalizes away matches nothing, rather than matching everything.
    [InlineData("DW01 - The Colour of Magic", "---", false)]
    public void Matches_IsContainmentOnTheNormalizedForm(string candidate, string wanted, bool expected)
        => Assert.Equal(expected, AbsSelector.Matches(candidate, wanted));

    [Fact]
    public void MatchesExactly_IgnoresPunctuationAndCaseButNothingElse()
    {
        Assert.True(AbsSelector.MatchesExactly("DW01 - The Colour of Magic", "dw01 the colour of magic"));
        Assert.False(AbsSelector.MatchesExactly("DW01 - The Colour of Magic", "colour of magic"));
    }
}
