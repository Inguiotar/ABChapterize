// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using System.Text.RegularExpressions;
using ABChapterize.Errors;
using ABChapterize.Language.Phrases;
using Xunit;

namespace ABChapterize.Tests;

/// <summary>
/// The replacement side: what a mark's title is allowed to say about the announcement that produced
/// it, and what it writes for one.
/// </summary>
public class TitleTemplateTests
{
    /// <summary>Resolves a template against a phrase, both written as a user would write them.</summary>
    /// <param name="phrase">The phrase, as it would stand before the colon of a --custom mapping.</param>
    /// <param name="title">The title template, as it would stand after it.</param>
    /// <param name="heard">The transcript text the phrase should match in.</param>
    /// <param name="language">Two-letter language code, for reading a captured number.</param>
    private static string Resolve(string phrase, string title, string heard, string language = "en")
    {
        var pattern = PhraseCompiler.Compile([phrase], language, PhraseKind.Named, "custom 1 phrase");
        var match = pattern.Matches(heard).FirstOrDefault().Match;
        return new TitleTemplate(title, "custom 1 title").Resolve(match, language);
    }

    [Fact]
    public void ATemplateWithoutReferences_IsItsOwnTitle()
        => Assert.Equal("Interlude", Resolve("interlude", "Interlude", "an interlude"));

    [Fact]
    public void ANamedGroup_IsWrittenAsItWasCaptured()
        => Assert.Equal("The intermezzo",
            Resolve("/(?<kind>interlude|intermezzo)/", "The ${kind}", "an intermezzo now"));

    /// <summary>
    /// <c>${number}</c> is the digits of whatever was captured, whatever notation the narrator used.
    /// Whisper writes one announcement as digits, as words and as a Roman numeral at different
    /// moments, and a title that inherited that would disagree with the chapter marks beside it.
    /// </summary>
    [Theory]
    [InlineData("interlude thirteen", "Interlude 13")]
    [InlineData("interlude XIII.", "Interlude 13")]
    [InlineData("interlude 13", "Interlude 13")]
    [InlineData("interlude one hundred and five", "Interlude 105")]
    public void TheNumberGroup_IsWrittenInDigits(string heard, string expected)
        => Assert.Equal(expected, Resolve("/interlude ()/", "Interlude ${number}", heard));

    [Theory]
    [InlineData("$roman{number}", "Interlude XIII")]
    [InlineData("$digits{number}", "Interlude 13")]
    [InlineData("${number}", "Interlude 13")]
    public void TheConversions_ApplyToTheNumber(string reference, string expected)
        => Assert.Equal(expected,
            Resolve("/interlude ()/", $"Interlude {reference}", "interlude thirteen"));

    [Theory]
    [InlineData("$upper{kind}", "INTERMEZZO")]
    [InlineData("$lower{kind}", "intermezzo")]
    [InlineData("$capital{kind}", "Intermezzo")]
    public void TheCaseConversions_ApplyToAnyGroup(string reference, string expected)
        => Assert.Equal(expected,
            Resolve("/(?<kind>interlude|intermezzo)/", reference, "an intermezzo now"));

    /// <summary>A conversion asked of a group that holds no number leaves the text alone: the
    /// request was a request, and refusing the whole mark over it would be out of proportion.</summary>
    [Fact]
    public void ARomanConversionOfSomethingUnnumbered_KeepsTheText()
        => Assert.Equal("The intermezzo",
            Resolve("/(?<kind>interlude|intermezzo)/", "The $roman{kind}", "an intermezzo now"));

    /// <summary>A group that belongs to a wording other than the one that matched resolves to
    /// nothing - the wordings are one phrase written several ways, and only one of them matched.</summary>
    [Fact]
    public void AGroupThatDidNotTakePart_ResolvesToNothing()
    {
        var pattern = PhraseCompiler.Compile(
            ["/interlude (?<kind>long|short)/", "/interlude/"], "en", PhraseKind.Named, "custom 1 phrase");
        var match = pattern.Matches("an interlude now").FirstOrDefault().Match;
        Assert.Equal("Interlude", new TitleTemplate("Interlude ${kind}", "t").Resolve(match, "en"));
    }

    /// <summary>The whole point of a bare-number announcement is that there is no expression behind
    /// it, so a title referencing one has nothing to resolve against and must not throw.</summary>
    [Fact]
    public void AMatchlessAnnouncement_ResolvesEveryReferenceToNothing()
        => Assert.Equal("Interlude", new TitleTemplate("Interlude ${number}", "t").Resolve(null, "en"));

    [Theory]
    [InlineData("Only $$5", "Only $5")]
    [InlineData("Costs $lots", "Costs $lots")]
    [InlineData("100$$", "100$")]
    public void ADollarThatIsNotAReference_SurvivesAsWritten(string template, string expected)
        => Assert.Equal(expected, Resolve("interlude", template, "an interlude"));

    /// <summary>Index references named a group until 0.12.0 and are refused rather than quietly read
    /// as text: a title that used to substitute and now does not is the one outcome nobody would
    /// notice until the file was written.</summary>
    [Theory]
    [InlineData("The $1")]
    [InlineData("Part $12 of it")]
    public void AnIndexReference_IsRejected(string template)
        => Assert.Throws<CliError>(() => new TitleTemplate(template, "custom 1 title"));

    /// <summary>A misspelled conversion is named rather than silently kept as text: the
    /// <c>$word{...}</c> shape is unmistakable, and "$romen{n}" in a chapter title helps nobody.</summary>
    [Theory]
    [InlineData("The $romen{kind}")]
    [InlineData("The $ROMAN2{kind}")]
    public void AnUnknownConversion_IsRejected(string template)
        => Assert.Throws<CliError>(() => new TitleTemplate(template, "custom 1 title"));

    [Fact]
    public void AReferenceNamingNoGroup_IsRejected()
        => Assert.Throws<CliError>(() => new TitleTemplate("The ${}", "custom 1 title"));

    [Fact]
    public void ReferencedGroups_AreReportedForValidation()
        => Assert.Equal(
            ["kind", "number"],
            new TitleTemplate("$upper{kind} ${number} $roman{number}", "t").ReferencedGroups.Order());

    /// <summary>
    /// What the resume and <c>--verify</c> carry-over matches a file's existing marks against: the
    /// literal text as written, with every reference standing in for whatever it expanded to.
    /// </summary>
    [Fact]
    public void TheMatcher_RecognizesTitlesThisTemplateWrote()
    {
        var matcher = new TitleTemplate("Interlude ${number}", "t").Matcher;
        Assert.Matches(matcher, "Interlude 13");
        Assert.Matches(matcher, "interlude 105");
        Assert.DoesNotMatch(matcher, "Interlude");
        Assert.DoesNotMatch(matcher, "An Interlude 13 of sorts");
    }

    /// <summary>A template with no references is matched literally, dollar sign and all - the
    /// escape is undone before the comparison, not after it.</summary>
    [Fact]
    public void TheMatcher_ComparesTheResolvedLiteral()
    {
        var matcher = new TitleTemplate("Only $$5", "t").Matcher;
        Assert.Matches(matcher, "Only $5");
        Assert.DoesNotMatch(matcher, "Only $$5");
    }

    /// <summary>The template as written survives for the run fingerprint and the debug log, which
    /// have to tell two option sets apart even when one file's marks come out the same.</summary>
    [Fact]
    public void RawKeepsWhatWasWritten()
        => Assert.Equal("Interlude ${number}", new TitleTemplate("Interlude ${number}", "t").Raw);
}
