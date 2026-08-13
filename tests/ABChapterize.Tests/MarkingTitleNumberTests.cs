// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Cli;
using ABChapterize.Detection;
using ABChapterize.Language;
using Xunit;

namespace ABChapterize.Tests;

/// <summary>
/// Tests for <see cref="MarkingTitleNumber"/>, which reads the chapter number out of a pre-existing
/// marking's title - what <c>--verify</c> and both resume paths run on.
/// <para>
/// The exhaustive half is deliberately built the same way <see cref="ExhaustiveNumberWordTests"/> is,
/// out of the independent reference spellers, and for a reason the bug it covers makes plain: the
/// number parsers were already tested exhaustively and were never at fault. What went unnoticed for
/// as long as it did was a caller handing them the wrong <em>shape</em> of string - a whole title
/// where the contract asks for the text following a phrase - so the test that catches it has to
/// exercise the caller, at the same coverage.
/// </para>
/// </summary>
public sealed class MarkingTitleNumberTests : IDisposable
{
    private readonly string _dir;
    private readonly string _file;

    /// <summary>Creates a temp directory with one audio file, so options can be parsed against it.</summary>
    public MarkingTitleNumberTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"abchapterize-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "book.m4b");
        File.WriteAllText(_file, "x");
    }

    /// <summary>Removes the temp directory.</summary>
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>The profile a file resolved to <paramref name="language"/> would be verified with.</summary>
    /// <param name="language">Two-letter code.</param>
    /// <param name="options">Extra command line options, e.g. an explicit --chapter-title.</param>
    private LanguageProfile Profile(string language, params string[] options)
        => CliOptions.Parse([.. options, _file])!.ResolveProfile(language);

    /// <summary>Asserts that a title yields the expected number under one language profile.</summary>
    /// <param name="title">The marking title to read.</param>
    /// <param name="profile">The profile to read it with.</param>
    /// <param name="expected">The number the title announces.</param>
    private static void AssertReads(string title, LanguageProfile profile, int expected)
    {
        var ok = MarkingTitleNumber.TryParse(title, profile, out var number);
        Assert.True(ok && number == expected,
            $"[{profile.Language}] \"{title}\" -> expected {expected}, got {(ok ? number.ToString() : "no number")}");
    }

    /// <summary>Asserts that a title yields no number at all.</summary>
    /// <param name="title">The marking title to read.</param>
    /// <param name="profile">The profile to read it with.</param>
    private static void AssertReadsNothing(string title, LanguageProfile profile)
    {
        var ok = MarkingTitleNumber.TryParse(title, profile, out var number);
        Assert.False(ok, $"[{profile.Language}] \"{title}\" -> expected no number, got {number}");
    }

    /// <summary>
    /// One language's reference spellings, with the range each parser actually covers - the same
    /// limits <see cref="ExhaustiveOrdinalTests"/> documents (Spanish and Portuguese ordinals stop
    /// at 199, Danish at 100).
    /// </summary>
    /// <param name="Code">Two-letter language code.</param>
    /// <param name="Cardinal">Reference cardinal speller.</param>
    /// <param name="Ordinal">Reference ordinal speller, or null where the language has none here.</param>
    /// <param name="MaxOrdinal">Highest ordinal the parser covers.</param>
    private sealed record Spelling(
        string Code, Func<int, string> Cardinal, Func<int, string>? Ordinal, int MaxOrdinal);

    /// <summary>Every language, its reference spellers and its ordinal range.</summary>
    private static readonly Spelling[] Spellings =
    [
        new("en", n => Spellers.English(n, useAnd: false, hyphen: true),
            n => Spellers.EnglishOrdinal(n, useAnd: false, hyphen: true), 999),
        new("de", n => Spellers.German(n, einhundert: false),
            n => Spellers.GermanOrdinal(n, einhundert: false) + "s", 999),
        new("fr", Spellers.French, Spellers.FrenchOrdinal, 999),
        new("nl", Spellers.Dutch, Spellers.DutchOrdinal, 999),
        new("it", n => Spellers.Italian(n, elideCento: false),
            n => Spellers.ItalianOrdinal(n, elideCento: false), 999),
        new("tr", Spellers.Turkish, Spellers.TurkishOrdinal, 999),
        new("pl", Spellers.Polish, Spellers.PolishOrdinal, 999),
        new("sv", Spellers.Swedish, Spellers.SwedishOrdinal, 999),
        new("es", Spellers.Spanish, n => Spellers.SpanishOrdinal(n), 199),
        new("pt", Spellers.Portuguese, n => Spellers.PortugueseOrdinal(n), 199),
        new("da", Spellers.Danish, n => Spellers.DanishOrdinal(n), 100),
    ];

    /// <summary>Every language's spelling table entry, as xUnit theory data.</summary>
    public static TheoryData<string> Languages()
    {
        var data = new TheoryData<string>();
        foreach (var s in Spellings)
            data.Add(s.Code);
        return data;
    }

    /// <summary>
    /// The ordinary written form in every language: the chapter word followed by the spelled-out
    /// number ("Chapter Five", "Kapitel Fünf", "Capitolo cinque"). This is the shape that used to
    /// fail everywhere, because the number is never the title's first token.
    /// </summary>
    /// <param name="code">Language under test.</param>
    [Theory]
    [MemberData(nameof(Languages))]
    public void TitleWordThenCardinal_ReadsTheNumber_InEveryLanguage(string code)
    {
        var spelling = Spellings.First(s => s.Code == code);
        var profile = Profile(code);
        var word = LanguageRegistry.For(code).ChapterTitle;
        for (var n = 0; n <= 999; n++)
            AssertReads($"{word} {spelling.Cardinal(n)}", profile, n);
    }

    /// <summary>
    /// The other word order, which a pre-posed ordinal gives every language that uses it
    /// ("Fünftes Kapitel", "Primo capitolo", "Enogtyvende kapitel").
    /// </summary>
    /// <param name="code">Language under test.</param>
    [Theory]
    [MemberData(nameof(Languages))]
    public void OrdinalThenTitleWord_ReadsTheNumber_InEveryLanguage(string code)
    {
        var spelling = Spellings.First(s => s.Code == code);
        if (spelling.Ordinal is not { } ordinal)
            return;
        var profile = Profile(code);
        var word = LanguageRegistry.For(code).ChapterTitle;
        for (var n = 1; n <= spelling.MaxOrdinal; n++)
            AssertReads($"{ordinal(n)} {word}", profile, n);
    }

    /// <summary>
    /// Digit forms, which worked before this class existed and must keep working: whatever tool
    /// tagged the file may write the number in front, behind, padded, or with an ordinal suffix.
    /// </summary>
    /// <param name="title">The marking title.</param>
    /// <param name="expected">The number it announces.</param>
    [Theory]
    [InlineData("Chapter 12", 12)]
    [InlineData("Chapter 05", 5)]
    [InlineData("05 - The Melting Girl", 5)]
    [InlineData("12. Chapter", 12)]
    [InlineData("3rd chapter", 3)]
    [InlineData("Five", 5)]
    [InlineData("Chapter Thirty-Seven", 37)]
    public void DigitAndPlainForms_StillRead(string title, int expected)
        => AssertReads(title, Profile("en"), expected);

    /// <summary>
    /// Roman numerals in a title - the notation Whisper falls into whenever it settles on a
    /// book-heading style, and which 0.9.1's Roman support never reached here because only the
    /// title's first token was ever tried.
    /// </summary>
    /// <param name="title">The marking title.</param>
    /// <param name="expected">The number it announces.</param>
    [Theory]
    [InlineData("Chapter XIII", 13)]
    [InlineData("XIII. The Shaking of the Sheets", 13)]
    [InlineData("Chapter V. The Mother of Tongues", 5)]
    [InlineData("The Melting Girl X.", 10)]
    public void RomanNumerals_AreRead(string title, int expected)
        => AssertReads(title, Profile("en"), expected);

    /// <summary>
    /// The one-letter guard survives the move to titles: a pronoun or an initial is not a chapter
    /// number, and neither is a bare letter without the period a heading gives it.
    /// </summary>
    /// <param name="title">The marking title.</param>
    [Theory]
    [InlineData("What I Did Next")]
    [InlineData("C for Cookie")]
    public void OneLetterRomanNumerals_WithoutAHeadingPeriod_AreNotRead(string title)
        => AssertReadsNothing(title, Profile("en"));

    /// <summary>
    /// The number is read from what directly follows the chapter word, so a heading behind it is
    /// simply never reached - the property that makes anchoring better than a free scan.
    /// </summary>
    /// <param name="title">The marking title.</param>
    /// <param name="code">Language under test.</param>
    /// <param name="expected">The number it announces.</param>
    [Theory]
    [InlineData("Capitolo uno - Il ritorno dei tre", "it", 1)]
    [InlineData("Kapitel Fünf - Die drei Sonnen", "de", 5)]
    [InlineData("Chapter Two: Seven Days Later", "en", 2)]
    public void AHeadingBehindTheNumber_IsNotMistakenForIt(string title, string code, int expected)
        => AssertReads(title, Profile(code), expected);

    /// <summary>
    /// The year hazard, and the reason the loose digit scan sits at the very bottom of the ladder
    /// and refuses four-digit runs: "Capitolo uno - Anno 1984" used to yield 1984, which --verify
    /// then hunted for, failed to find, and booked against the file - enough of those and a whole
    /// marking set was discarded and redetected.
    /// </summary>
    [Fact]
    public void AYearInAHeading_IsNotReadAsTheChapterNumber()
    {
        AssertReads("Capitolo uno - Anno 1984", Profile("it"), 1);
        AssertReads("1984 - Kapitel Fünf", Profile("de"), 5);
        AssertReadsNothing("Anno 1984", Profile("it"));
    }

    /// <summary>
    /// A title written in a language other than the one the audio resolved to. Both halves of the
    /// original defect are here: an inconclusive auto-detection falls back to English and then meets
    /// a German title, and a tagger writes English titles onto a book in any language at all.
    /// </summary>
    /// <param name="title">The marking title.</param>
    /// <param name="code">The language the <em>audio</em> resolved to.</param>
    /// <param name="expected">The number the title announces.</param>
    [Theory]
    [InlineData("Kapitel Fünf", "en", 5)]
    [InlineData("Capitolo due", "en", 2)]
    [InlineData("Chapter Five", "de", 5)]
    [InlineData("Chapitre Cinq", "de", 5)]
    [InlineData("Enogtyvende kapitel", "en", 21)]
    public void ATitleInAnotherLanguage_IsStillRead(string title, string code, int expected)
        => AssertReads(title, Profile(code), expected);

    /// <summary>
    /// The guard that keeps the cross-language net off ordinary prose: another language is only ever
    /// consulted when its own chapter word is literally in the title. Without it Danish "to" (two)
    /// would turn an English title into chapter 2.
    /// </summary>
    [Fact]
    public void AnotherLanguagesNumberWord_WithoutItsChapterWord_IsNotConsulted()
        => AssertReadsNothing("Chapter to the End", Profile("en"));

    /// <summary>
    /// A file this tool marked under an explicit --chapter-title is read back by that word, not by the
    /// phrase it listens for - the two are separate options and a --verify run has to survive both
    /// being set.
    /// </summary>
    [Fact]
    public void AnExplicitTitleWord_IsAnAnchorOfItsOwn()
        => AssertReads("Section Seven", Profile("en", "--chapter-title", "Section"), 7);

    /// <summary>
    /// Titles with no chapter identity at all stay unreadable, which is what keeps them out of
    /// --verify's checked count and lets the resume paths recognize them as named marks rather than
    /// chapters. The prologue case is the important one: it goes through this same test as a
    /// <em>negative</em>, and a false number there would reclassify a real chapter.
    /// </summary>
    /// <param name="title">The marking title.</param>
    [Theory]
    [InlineData("Prologue")]
    [InlineData("Prolog")]
    [InlineData("Epilogue")]
    [InlineData("Intro")]
    [InlineData("Zeittafel")]
    [InlineData("The Shaking of the Sheets")]
    public void TitlesWithoutANumber_YieldNothing(string title)
        => AssertReadsNothing(title, Profile("en"));

    /// <summary>
    /// The round trip that makes parts survive a resume: every title this tool writes for a file in
    /// parts has to give both numbers back. Built through
    /// <see cref="LanguageProfile.ChapterTitleFor"/> rather than from literals, so a change to the
    /// spelling fails here rather than silently costing a resumed run its committed marks.
    /// </summary>
    /// <param name="language">Two-letter code.</param>
    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("fr")]
    [InlineData("it")]
    [InlineData("nl")]
    [InlineData("sv")]
    [InlineData("da")]
    [InlineData("pl")]
    [InlineData("tr")]
    [InlineData("es")]
    [InlineData("pt")]
    public void APartPrefixedTitle_GivesBackBothItsNumbers(string language)
    {
        var profile = Profile(language);
        for (var part = 1; part <= 3; part++)
        for (var chapter = 1; chapter <= 12; chapter++)
        {
            var title = profile.ChapterTitleFor(chapter, part);
            AssertReads(title, profile, chapter);
            Assert.True(MarkingTitleNumber.TryParsePart(title, profile, out var read),
                $"[{language}] \"{title}\" -> no part read");
            Assert.Equal(part, read);
        }
    }

    /// <summary>
    /// An ordinary title carries no part, which is what makes the sequence default to 0 for every
    /// book that has one sequence and for every file some other tool marked.
    /// </summary>
    /// <param name="title">The marking title.</param>
    [Theory]
    [InlineData("Chapter 7")]
    [InlineData("Prologue")]
    [InlineData("Participation - Chapter 7")]
    [InlineData("Parting Words")]
    public void ATitleWithoutAPartPrefix_YieldsNoPart(string title)
        => Assert.False(MarkingTitleNumber.TryParsePart(title, Profile("en"), out _));

    /// <summary>
    /// The Scandinavian trap the strict rule exists for: "Del" is Swedish and Danish for "part" and
    /// also the head of ordinary words, so the prefix is only recognized when a non-letter follows
    /// it. Without that, "Delen 3" would read as part one - Danish "en" being the number.
    /// </summary>
    /// <param name="language">Two-letter code of a language whose part word is "Del".</param>
    [Theory]
    [InlineData("sv")]
    [InlineData("da")]
    public void APartWordThatOpensALongerWord_IsNotAPrefix(string language)
        => Assert.False(MarkingTitleNumber.TryParsePart("Delen 3 - Kapitel 1", Profile(language), out _));
}
