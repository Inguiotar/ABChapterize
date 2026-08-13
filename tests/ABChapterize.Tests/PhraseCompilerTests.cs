// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Errors;
using ABChapterize.Language.Phrases;
using Xunit;

namespace ABChapterize.Tests;

/// <summary>
/// The phrase syntax itself: how an option value becomes a list of wordings, where the <c>^</c> and
/// <c>$</c> guards attach, and what <c>()</c> expands to.
/// <para>
/// The splitting is the part worth testing hardest. <c>^</c> and <c>$</c> are requests for a pause
/// rather than anchors, and a request belongs to the wording that made it - so <c>/^(?:a|b)$/</c>
/// and <c>/(?:^a$|b$)/</c> mean different things and both have to come out right.
/// </para>
/// </summary>
public class PhraseCompilerTests
{
    /// <summary>Compiles a chapter phrase the way <c>--chapter-phrase</c> does.</summary>
    /// <param name="phrase">One or more entries, as they would be written after the option.</param>
    /// <param name="language">Two-letter language code.</param>
    private static PhrasePattern Chapter(string phrase, string language = "en")
        => PhraseCompiler.Compile(
            PhraseSpec.Parse(phrase, "--chapter-phrase").Entries.Select(e => e.Body).ToList(),
            language, PhraseKind.Chapter, "chapter phrase");

    /// <summary>Compiles a named phrase the way a <c>--custom</c> mapping does. Several entries
    /// rather than one semicolon-separated value, because a mapping's phrase is one entry - the
    /// semicolon already separates mappings there.</summary>
    /// <param name="phrases">The wordings as they would be written before the colon.</param>
    private static PhrasePattern Named(params string[] phrases)
        => PhraseCompiler.Compile(phrases, "en", PhraseKind.Named, "custom 1 phrase");

    [Fact]
    public void TopLevelAlternation_BecomesSeparateWordings()
    {
        var pattern = Named("/alpha|beta|gamma/");
        Assert.Equal(3, pattern.Alternatives.Count);
        Assert.Equal([0, 1, 2], pattern.Alternatives.Select(a => a.Index));
    }

    /// <summary>A "|" inside a group or a character class belongs to that group, not to the phrase.</summary>
    [Theory]
    [InlineData("/(?:alpha|beta) now/")]
    [InlineData("/[a|b]lpha/")]
    [InlineData(@"/alpha\|beta/")]
    public void AlternationThatIsNotTopLevel_StaysOneWording(string phrase)
        => Assert.Single(Named(phrase).Alternatives);

    /// <summary>A group spanning the whole wording is descended into, which is what hands the outer
    /// anchors to every wording inside it.</summary>
    [Fact]
    public void AnchorsOutsideAWholeGroup_ReachEveryWordingInside()
    {
        var pattern = Named("/^(?:alpha|beta)$/");
        Assert.Equal(2, pattern.Alternatives.Count);
        Assert.All(pattern.Alternatives, a =>
        {
            Assert.True(a.RequiresLeadIn);
            Assert.True(a.RequiresLeadOut);
        });
    }

    /// <summary>...and anchors written inside stay with the wording that carries them.</summary>
    [Fact]
    public void AnchorsInsideAGroup_StayWithTheirOwnWording()
    {
        var pattern = Named("/(?:^alpha$|beta$)/");
        Assert.Equal(2, pattern.Alternatives.Count);
        Assert.True(pattern.Alternatives[0].RequiresLeadIn);
        Assert.True(pattern.Alternatives[0].RequiresLeadOut);
        Assert.False(pattern.Alternatives[1].RequiresLeadIn);
        Assert.True(pattern.Alternatives[1].RequiresLeadOut);
    }

    /// <summary>A group that is not the whole wording is left alone - unwrapping it would change
    /// what the quantifier applies to.</summary>
    [Fact]
    public void AQuantifiedOrTrailingGroup_IsNotDescendedInto()
    {
        Assert.Single(Named("/(?:alpha|beta)?gamma/").Alternatives);
        PhraseAssert.Matches(Named("/(?:alpha|beta)?gamma/"), "gamma");
    }

    /// <summary>The anchors are stripped rather than compiled, so they cannot also act as the regex
    /// anchors they look like - against a flattened multi-segment window those would match almost
    /// nowhere.</summary>
    [Fact]
    public void AnchorsAreGuardsRatherThanAnchors()
    {
        var pattern = Named("/^alpha$/");
        PhraseAssert.Matches(pattern, "and then alpha came along");
        Assert.True(Assert.Single(pattern.Alternatives).RequiresLeadIn);
    }

    /// <summary>An escaped dollar is a dollar sign; only an unescaped one at the very end asks for
    /// a pause.</summary>
    [Fact]
    public void AnEscapedDollarIsNotAGuard()
    {
        var pattern = Named(@"/costs \$/");
        Assert.False(Assert.Single(pattern.Alternatives).RequiresLeadOut);
        PhraseAssert.Matches(pattern, "it costs $");
    }

    [Fact]
    public void SemicolonSeparatedEntries_BecomeWordings()
    {
        var pattern = Chapter("/section ()/;/part ()/");
        Assert.Equal(2, pattern.Alternatives.Count);
        PhraseAssert.Matches(pattern, "section three");
        PhraseAssert.Matches(pattern, "part three");
    }

    /// <summary>A literal chapter phrase is the same two wordings a built-in default has: the word
    /// with the number behind it, then the bare word. Both announcement orders come out of them -
    /// "Teil sieben" from the first, "Siebter Teil" from the second, whose number is read off the
    /// words around the match rather than captured.</summary>
    [Fact]
    public void ALiteralChapterPhrase_HasTheSameTwoWordingsAsADefault()
    {
        var pattern = Chapter("part");
        Assert.Equal(2, pattern.Alternatives.Count);
        Assert.True(pattern.Alternatives[0].HasNumberGroup);
        Assert.False(pattern.Alternatives[1].HasNumberGroup);
        Assert.Equal("seven", PhraseAssert.Captured(pattern, "part seven"));
        PhraseAssert.Matches(pattern, "the seventh part of it");
    }

    /// <summary>
    /// A third wording for the number-first order would be wrong, not merely redundant: matches are
    /// taken leftmost-first, so "() part" would claim "seventh part" before "part ()" could claim
    /// "part 5" and the chapter would come out as 7. Six announcements across five books of the
    /// reference corpus have that shape (2026-08-13), which is why neither a literal phrase nor any
    /// built-in default carries such a wording.
    /// </summary>
    [Fact]
    public void ALiteralChapterPhrase_TakesTheNumberBehindTheWordFirst()
        => Assert.Equal("5", PhraseAssert.Captured(Chapter("part"), "the seventh part 5 begins"));

    /// <summary>A literal named phrase is exactly the word: nothing parses a number there.</summary>
    [Fact]
    public void ALiteralNamedPhrase_IsJustTheWord()
    {
        var pattern = Named("interlude");
        Assert.False(Assert.Single(pattern.Alternatives).HasNumberGroup);
        PhraseAssert.Matches(pattern, "and now an interlude");
    }

    /// <summary>"none" is shorthand for "/^()$/" and means it literally - same wording, same
    /// guards.</summary>
    [Fact]
    public void NoneIsShorthandForABareNumberWording()
    {
        foreach (var spelling in new[] { "none", "/^()$/" })
        {
            var wording = Assert.Single(Chapter(spelling).Alternatives);
            Assert.True(wording.IsBareNumber);
            Assert.True(wording.HasNumberGroup);
            Assert.True(wording.RequiresLeadIn);
            Assert.True(wording.RequiresLeadOut);
        }
    }

    /// <summary>Only the chapter phrase reads "none" as a spelling of its own; elsewhere it is the
    /// English word, and a mapping catching "none of it" has to keep working.</summary>
    [Fact]
    public void NoneIsAnOrdinaryWordForANamedPhrase()
    {
        var pattern = Named("none");
        Assert.False(Assert.Single(pattern.Alternatives).IsBareNumber);
        PhraseAssert.Matches(pattern, "and none of it mattered");
    }

    /// <summary>A bare-number wording may sit beside ordinary ones - the whole reason bare numbers
    /// became a wording rather than a mode.</summary>
    [Fact]
    public void BareNumbersMayBeOneWordingAmongSeveral()
    {
        var pattern = Chapter("/chapter ()/;none");
        Assert.Equal(2, pattern.Alternatives.Count);
        Assert.True(pattern.HasBareNumberAlternative);
        Assert.True(pattern.HasRegexAlternative);
        PhraseAssert.Matches(pattern, "chapter twelve");
    }

    /// <summary>Several <c>()</c> in one phrase, one per wording - what a language whose
    /// announcement order varies needs.</summary>
    [Fact]
    public void SeveralNumberTokens_WorkInDifferentWordings()
    {
        var pattern = Chapter("/(?:(?:das )?() kapitel|kapitel ())/", "de");
        Assert.Equal(2, pattern.Alternatives.Count);
        Assert.All(pattern.Alternatives, a => Assert.True(a.HasNumberGroup));
        Assert.Equal("dritte", PhraseAssert.Captured(pattern, "das dritte kapitel"));
        Assert.Equal("drei", PhraseAssert.Captured(pattern, "kapitel drei"));
    }

    /// <summary>An unnamed capturing group is the number group, keeping the pre-0.12.0
    /// <c>"/part (\d+)/"</c> convention working while freeing every other group to be named.</summary>
    [Fact]
    public void AnUnnamedGroupIsTheNumberGroup()
    {
        var pattern = Chapter(@"/part (\d+)/");
        Assert.True(Assert.Single(pattern.Alternatives).HasNumberGroup);
        Assert.Equal("12", PhraseAssert.Captured(pattern, "part 12"));
    }

    [Fact]
    public void NamedGroupsAreKept()
    {
        var pattern = Named("/(?<kind>interlude|intermezzo) ()/");
        Assert.Contains("kind", pattern.GroupNames);
        Assert.Contains(PhraseAlternative.NumberGroup, pattern.GroupNames);
    }

    /// <summary>Lookarounds open with "(?" and are not capturing groups, whatever they look like.</summary>
    [Theory]
    [InlineData("/(?=alpha)beta/")]
    [InlineData("/(?<=alpha)beta/")]
    [InlineData("/(?<!alpha)beta/")]
    public void LookaroundsAreNotReadAsNumberGroups(string phrase)
        => Assert.False(Assert.Single(Named(phrase).Alternatives).HasNumberGroup);

    /// <summary>A "(" inside a character class is a character.</summary>
    [Fact]
    public void ABracketInsideACharacterClassIsNotAGroup()
        => Assert.False(Assert.Single(Named("/[(]alpha/").Alternatives).HasNumberGroup);

    [Fact]
    public void MatchesAreLeftmostAndNonOverlapping()
    {
        // Both wordings match at the same place; the first one written claims it, exactly as the
        // one alternation this used to be would have done.
        var pattern = Named("/interlude/", "/interlude the second/");
        var hits = pattern.Matches("an interlude the second, then another interlude").ToList();
        Assert.Equal(2, hits.Count);
        Assert.All(hits, h => Assert.Equal(0, h.Alternative.Index));
    }

    [Theory]
    [InlineData("/(unclosed/")]
    [InlineData("/a{2,1}/")]
    public void AnInvalidRegexp_IsAnError(string phrase)
        => Assert.Throws<CliError>(() => Named(phrase));

    /// <summary>A phrase that opens with a slash and does not close with one is a typo, not a word
    /// starting with a slash - saying so beats compiling it as a literal nobody will ever hear.</summary>
    [Theory]
    [InlineData("/chapter")]
    [InlineData("chapter/")]
    public void AHalfWrittenRegexp_IsAnError(string phrase)
        => Assert.Throws<CliError>(() => Named(phrase));
}
