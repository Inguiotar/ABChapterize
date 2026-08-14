// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using System.Text.RegularExpressions;
using Xunit;
using ABChapterize.Language;
using ABChapterize.Language.Phrases;

namespace ABChapterize.Tests;

/// <summary>
/// Exhaustive coverage tests for the <c>()</c> expansion: every spelling the independent reference
/// spellers produce for 0-999, cardinal and ordinal, must be matched <em>whole</em> by its
/// language's number pattern.
/// <para>
/// The direction is deliberate. A pattern that admits too much costs nothing - whatever it captures
/// is handed to <see cref="NumberWordParser"/>, which is the authority on the value - while a
/// spelling it misses is an announcement the phrase never matches at all, i.e. a lost chapter. So
/// these tests are exhaustive about coverage and only spot-check tightness, which is the opposite
/// weighting from the parser round-trip tests next door.
/// </para>
/// </summary>
public class NumberWordPatternTests
{
    /// <summary>Per-language patterns, anchored so that a partial match counts as a failure -
    /// matching only the head of "einundzwanzig" is exactly the defect these tests exist for.</summary>
    private static readonly Dictionary<string, Regex> Anchored = LanguageRegistry.Languages
        .ToDictionary(
            l => l.Code,
            l => new Regex($"^(?:{NumberPattern.For(l.Code)})$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));

    /// <summary>Asserts that one spelling is matched in full by its language's number pattern.</summary>
    /// <param name="text">The spelling as a narrator might say it.</param>
    /// <param name="language">Two-letter language code.</param>
    private static void AssertMatches(string text, string language)
        => Assert.True(Anchored[language].IsMatch(text),
            $"[{language}] \"{text}\" is not matched by the () number pattern");

    /// <summary>Asserts that a piece of ordinary prose is <em>not</em> taken for a number.</summary>
    /// <param name="text">The text that must not match.</param>
    /// <param name="language">Two-letter language code.</param>
    private static void AssertRejects(string text, string language)
        => Assert.False(Anchored[language].IsMatch(text),
            $"[{language}] \"{text}\" is wrongly matched by the () number pattern");

    [Fact]
    public void English_EveryCardinalAndOrdinal()
    {
        for (var n = 0; n <= 999; n++)
            foreach (var useAnd in new[] { false, true })
                foreach (var hyphen in new[] { false, true })
                {
                    AssertMatches(Spellers.English(n, useAnd, hyphen), "en");
                    if (n > 0)
                        AssertMatches(Spellers.EnglishOrdinal(n, useAnd, hyphen), "en");
                }
    }

    [Fact]
    public void German_EveryCardinalAndOrdinal_WithDeclensionsAndTransliteration()
    {
        for (var n = 0; n <= 999; n++)
            foreach (var einhundert in new[] { false, true })
            {
                var cardinal = Spellers.German(n, einhundert);
                AssertMatches(cardinal, "de");
                AssertMatches(Transliterate(cardinal), "de");
                if (n == 0)
                    continue;
                var ordinal = Spellers.GermanOrdinal(n, einhundert);
                foreach (var ending in new[] { "", "r", "s", "n", "m" })
                    AssertMatches(ordinal + ending, "de");
                AssertMatches(Transliterate(ordinal), "de");
            }
    }

    [Fact]
    public void Dutch_EveryCardinalAndOrdinal_TremaAndPlain()
    {
        for (var n = 0; n <= 999; n++)
        {
            AssertMatches(Spellers.Dutch(n), "nl");
            AssertMatches(Spellers.Dutch(n).Replace('ë', 'e'), "nl");
            if (n == 0)
                continue;
            AssertMatches(Spellers.DutchOrdinal(n), "nl");
            AssertMatches(Spellers.DutchOrdinal(n).Replace('ë', 'e'), "nl");
        }
    }

    [Fact]
    public void French_EveryCardinalAndOrdinal_AccentedPlainAndHyphenated()
    {
        for (var n = 0; n <= 999; n++)
            foreach (var word in Variants(Spellers.French(n))
                         .Concat(n > 0 ? Variants(Spellers.FrenchOrdinal(n)) : []))
                AssertMatches(word, "fr");

        static IEnumerable<string> Variants(string word) =>
            [word, word.Replace('è', 'e').Replace('é', 'e'), word.Replace(' ', '-')];
    }

    [Fact]
    public void Italian_EveryCardinalAndOrdinal_PlainAndElided()
    {
        for (var n = 0; n <= 999; n++)
            foreach (var elide in new[] { false, true })
            {
                AssertMatches(Spellers.Italian(n, elide), "it");
                if (n > 0)
                    AssertMatches(Spellers.ItalianOrdinal(n, elide), "it");
            }
    }

    [Fact]
    public void Turkish_EveryCardinalAndOrdinal_TurkishAndAscii()
    {
        for (var n = 0; n <= 999; n++)
        {
            AssertMatches(Spellers.Turkish(n), "tr");
            AssertMatches(AsciifyTurkish(Spellers.Turkish(n)), "tr");
            if (n == 0)
                continue;
            AssertMatches(Spellers.TurkishOrdinal(n), "tr");
            AssertMatches(AsciifyTurkish(Spellers.TurkishOrdinal(n)), "tr");
        }
    }

    [Fact]
    public void Polish_EveryCardinalAndOrdinal_AccentedAndAscii()
    {
        for (var n = 0; n <= 999; n++)
        {
            AssertMatches(Spellers.Polish(n), "pl");
            AssertMatches(AsciifyPolish(Spellers.Polish(n)), "pl");
            if (n == 0)
                continue;
            AssertMatches(Spellers.PolishOrdinal(n), "pl");
            AssertMatches(AsciifyPolish(Spellers.PolishOrdinal(n)), "pl");
        }
    }

    [Fact]
    public void Swedish_EveryCardinalAndOrdinal_AccentedAndAscii()
    {
        for (var n = 0; n <= 999; n++)
        {
            AssertMatches(Spellers.Swedish(n), "sv");
            AssertMatches(AsciifySwedish(Spellers.Swedish(n)), "sv");
            if (n == 0)
                continue;
            AssertMatches(Spellers.SwedishOrdinal(n), "sv");
            AssertMatches(AsciifySwedish(Spellers.SwedishOrdinal(n)), "sv");
        }
    }

    [Fact]
    public void Danish_EveryCardinal_AndOrdinalsToOneHundred()
    {
        for (var n = 0; n <= 999; n++)
            AssertMatches(Spellers.Danish(n), "da");
        // Danish ordinals stop at 100th, exactly as the parser's own exhaustive test does.
        for (var n = 1; n <= 100; n++)
            foreach (var colloquial in new[] { false, true })
                AssertMatches(Spellers.DanishOrdinal(n, colloquial), "da");
    }

    [Fact]
    public void Spanish_EveryCardinal_AndOrdinalsToOneNinetyNine()
    {
        for (var n = 0; n <= 999; n++)
            AssertMatches(Spellers.Spanish(n), "es");
        // Spanish ordinals stop at 199th (the scale words end at "centésimo").
        for (var n = 1; n <= 199; n++)
            foreach (var feminine in new[] { false, true })
                foreach (var fuse in new[] { false, true })
                {
                    AssertMatches(Spellers.SpanishOrdinal(n, feminine, fuse), "es");
                    AssertMatches(Deaccent(Spellers.SpanishOrdinal(n, feminine, fuse)), "es");
                }
    }

    [Fact]
    public void Portuguese_EveryCardinal_AndOrdinalsToOneNinetyNine()
    {
        for (var n = 0; n <= 999; n++)
            AssertMatches(Spellers.Portuguese(n), "pt");
        for (var n = 1; n <= 199; n++)
            foreach (var feminine in new[] { false, true })
            {
                AssertMatches(Spellers.PortugueseOrdinal(n, feminine), "pt");
                AssertMatches(Deaccent(Spellers.PortugueseOrdinal(n, feminine)), "pt");
            }
    }

    [Theory]
    // Digits, digit ordinals and Roman numerals are the same in every language.
    [InlineData("13", "en")]
    [InlineData("13.", "de")]
    [InlineData("21st", "en")]
    [InlineData("1er", "fr")]
    [InlineData("5'inci", "tr")]
    [InlineData("1:a", "sv")]
    [InlineData("XIII", "en")]
    [InlineData("XIII.", "it")]
    [InlineData("V.", "en")]
    public void NotationsSharedByEveryLanguage_Match(string text, string language)
        => AssertMatches(text, language);

    /// <summary>
    /// A number may not start in the middle of a word, and Roman numerals are what makes that a
    /// real hazard rather than a theoretical one: they are ordinary letters, so without the guard
    /// the <em>tail</em> of a word reads as one. All four of these were found in corpus transcripts
    /// by a phrase written <c>/() chapter/</c> (2026-08-14) - "Kaskal." as 50, "AD" as 500 and
    /// "Parti" as 1, each of them then displacing the real chapter number that followed.
    /// </summary>
    [Theory]
    [InlineData("Kaskal.", "de")]
    [InlineData("AD", "en")]
    [InlineData("Parti", "fr")]
    [InlineData("Livia", "en")]
    public void ANumberMayNotStartInsideAWord(string text, string language)
    {
        var pattern = new Regex(NumberPattern.For(language),
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        Assert.False(pattern.IsMatch(text), $"[{language}] \"{text}\" holds no number");
    }

    [Theory]
    // Ordinary words must not pass for numbers, or every phrase match would carry one.
    [InlineData("Mildred", "en")]
    [InlineData("chapter", "en")]
    [InlineData("Kapitel", "de")]
    [InlineData("capitolo", "it")]
    // A five-digit run is a year or a serial, not a chapter.
    [InlineData("20661", "en")]
    // Half a compound is not a number either: the pattern must take "einundzwanzig" whole.
    [InlineData("einundzwanzigsten Kapitel", "de")]
    public void OrdinaryWords_DoNotMatch(string text, string language)
        => AssertRejects(text, language);

    /// <summary>Replaces the German umlauts by their ue/oe/ae/ss transliterations, which Whisper
    /// emits about as readily as the umlauts themselves.</summary>
    private static string Transliterate(string s) => s
        .Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue").Replace("ß", "ss");

    /// <summary>Replaces the Turkish letters by their plain ASCII look-alikes.</summary>
    private static string AsciifyTurkish(string s) => s
        .Replace('ı', 'i').Replace('ü', 'u').Replace('ö', 'o')
        .Replace('ş', 's').Replace('ç', 'c').Replace('ğ', 'g');

    /// <summary>Replaces the Polish diacritics by their plain ASCII look-alikes.</summary>
    private static string AsciifyPolish(string s) => s
        .Replace('ą', 'a').Replace('ć', 'c').Replace('ę', 'e').Replace('ł', 'l')
        .Replace('ń', 'n').Replace('ó', 'o').Replace('ś', 's').Replace('ź', 'z').Replace('ż', 'z');

    /// <summary>Replaces the Swedish diacritics by their plain ASCII look-alikes.</summary>
    private static string AsciifySwedish(string s) => s
        .Replace('å', 'a').Replace('ä', 'a').Replace('ö', 'o');

    /// <summary>Strips the acute accents Spanish and Portuguese carry.</summary>
    private static string Deaccent(string s) => s
        .Replace('á', 'a').Replace('é', 'e').Replace('í', 'i').Replace('ó', 'o').Replace('ú', 'u');
}
