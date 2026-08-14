// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Detection;
using ABChapterize.Errors;
using ABChapterize.Language;
using ABChapterize.Language.Phrases;
using ABChapterize.Transcription;
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

    /// <summary>English's own default, read out of the registry rather than written out again, so
    /// that the wordings these tests reason about are the ones a run really gets.</summary>
    private static PhrasePattern BuiltInChapterPhrase()
        => Chapter(LanguageRegistry.For("en").ChapterPhrase);

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
    /// A literal gets no third wording for the number-first order, unlike the built-in defaults.
    /// Matches are taken leftmost-first, so "() part" would claim "seventh part" before "part ()"
    /// could claim "part 5" and the chapter would come out as 7 - three of the reference corpus's
    /// 12,916 probe transcripts read exactly that way (2026-08-14). A default can afford it because
    /// every one of its wordings carries a "^", which is what makes the wrong reading visible and
    /// lets the sequence fall back on the one behind it; a literal carries no guards at all, and a
    /// user who wants that order can write the wording out.
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

    /// <summary>
    /// What a superseded wording is kept for: on words two wordings read differently, the loser is
    /// still a reading of the same announcement. Here the built-in chapter phrase over Whisper's
    /// "Two chapter three" - number-first claims "Two chapter" for its earlier start, number-behind
    /// reads the same words as chapter 3, and only the sequence can say which is right.
    /// </summary>
    [Fact]
    public void MatchGroups_KeepTheWordingsAWinnerSuperseded()
    {
        var groups = BuiltInChapterPhrase().MatchGroups("Two chapter three, and later chapter four")
            .ToList();
        Assert.Equal(2, groups.Count);
        // Leftmost wins the position: "Two chapter" starts before "chapter three" does.
        Assert.Equal(1, groups[0][0].Alternative.Index);
        Assert.Equal("Two chapter", groups[0][0].Match.Value);
        // ... and the two wordings it displaced are behind it, in the order they were written.
        Assert.Equal([0, 2], groups[0].Skip(1).Select(h => h.Alternative.Index));
        Assert.All(groups[0].Skip(1), h => Assert.StartsWith("chapter", h.Match.Value));
        // The second announcement is contested only by the bare word, which reads it the same way.
        Assert.Equal([0, 2], groups[1].Select(h => h.Alternative.Index));
    }

    /// <summary>The winner alone is what every caller but the accept loop sees, and it is the same
    /// answer one alternation would have given.</summary>
    [Fact]
    public void Matches_IsTheWinnerOfEachGroup()
    {
        const string text = "Two chapter three, and later chapter four";
        var pattern = BuiltInChapterPhrase();
        Assert.Equal(
            pattern.MatchGroups(text).Select(g => (g[0].Match.Index, g[0].Alternative.Index)),
            pattern.Matches(text).Select(h => (h.Match.Index, h.Alternative.Index)));
    }

    /// <summary>
    /// A number spoken alone is read segment by segment rather than matched, so it cannot be
    /// superseded at a position - but where an expression wording read the same segment, the two are
    /// readings of one announcement and the accept loop has to be able to fall back from one to the
    /// other. It comes last: "none" is "/^()$/", which asks for a pause on both sides, so it is the
    /// stricter reading of the two.
    /// </summary>
    [Fact]
    public void ABareNumberJoinsTheExpressionReadingOfItsOwnSegment()
    {
        var profile = Profile("default;none", "de");
        var readings = PhraseMatching.FindPhraseReadings(
            [new TranscriptSegment(10, 12, "3. Kapitel.", 0.9)], profile).ToList();

        var group = Assert.Single(readings);
        Assert.Equal([false, false, true], group.Select(r => r.IsBareNumber));
        Assert.All(group, r => Assert.Equal(3, r.Number));
    }

    /// <summary>...and stands alone where no expression wording read that segment, which is the
    /// ordinary shape of a book that announces its chapters by number and nothing else.</summary>
    [Fact]
    public void ABareNumberWithoutAnExpressionReading_IsItsOwnAnnouncement()
    {
        var profile = Profile("default;none", "de");
        var readings = PhraseMatching.FindPhraseReadings(
            [new TranscriptSegment(10, 12, "Kapitel 4.", 0.9),
             new TranscriptSegment(20, 22, "Und so begann es.", 0.9),
             new TranscriptSegment(30, 32, "5.", 0.9)],
            profile).ToList();

        Assert.Equal(2, readings.Count);
        Assert.All(readings[0], r => Assert.False(r.IsBareNumber));
        Assert.True(Assert.Single(readings[1]).IsBareNumber);
    }

    /// <summary>Resolves a chapter phrase into the profile the detection passes are handed. Only the
    /// chapter phrase matters here, so the rest of the profile is the language's own.</summary>
    /// <param name="phrase">The value as it would be written after <c>--chapter-phrase</c>.</param>
    /// <param name="language">Two-letter language code.</param>
    private static LanguageProfile Profile(string phrase, string language)
    {
        var built = LanguageRegistry.For(language);
        var bodies = PhraseSpec.Parse(phrase, "--chapter-phrase")
            .For(language, () => [built.ChapterPhrase]);
        var pattern = PhraseCompiler.Compile(bodies, language, PhraseKind.Chapter, "chapter phrase");
        return new LanguageProfile(
            language, pattern.Source, pattern, built.ChapterTitle, built.PartTitle,
            built.IntroTitle, []);
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
