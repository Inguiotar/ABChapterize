// Chapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Xunit;

namespace Chapterize.Tests;

/// <summary>
/// Exhaustive round-trip tests: every number 0-999 is spelled out by the independent
/// reference spellers in every supported variant and must parse back to itself through
/// the public <see cref="NumberWordParser"/> entry point (with trailing punctuation,
/// as Whisper transcripts have it).
/// </summary>
public class ExhaustiveNumberWordTests
{
    /// <summary>Asserts that a spelled number parses back to its value in one language.</summary>
    /// <param name="text">Spelled-out number, optionally with trailing text.</param>
    /// <param name="language">Language code passed to the parser.</param>
    /// <param name="expected">Expected parse result.</param>
    private static void AssertParses(string text, string language, int expected)
    {
        var ok = NumberWordParser.TryExtractNumber(text, language, out var number);
        Assert.True(ok && number == expected,
            $"[{language}] \"{text}\" -> expected {expected}, got {(ok ? number.ToString() : "no parse")}");
    }

    [Fact]
    public void English_AllNumbers_AllVariants()
    {
        for (var n = 0; n <= 999; n++)
        {
            AssertParses(Spellers.English(n, useAnd: false, hyphen: true) + ".", "en", n);
            AssertParses(Spellers.English(n, useAnd: false, hyphen: false) + ".", "en", n);
            AssertParses(Spellers.English(n, useAnd: true, hyphen: true) + ".", "en", n);
            AssertParses(Spellers.English(n, useAnd: true, hyphen: false) + ".", "en", n);
        }
    }

    [Fact]
    public void German_AllNumbers_UmlautAndTransliteratedVariants()
    {
        for (var n = 0; n <= 999; n++)
        {
            var word = Spellers.German(n, einhundert: false);
            AssertParses(word + ".", "de", n);
            // Whisper sometimes transliterates umlauts; the parser must accept that too.
            AssertParses(Transliterate(word) + ".", "de", n);
            AssertParses(Spellers.German(n, einhundert: true) + ".", "de", n);
        }
    }

    [Fact]
    public void Dutch_AllNumbers_TremaAndPlainVariants()
    {
        for (var n = 0; n <= 999; n++)
        {
            var word = Spellers.Dutch(n);
            AssertParses(word + ".", "nl", n);
            AssertParses(word.Replace('ë', 'e') + ".", "nl", n);
        }
    }

    [Fact]
    public void French_AllNumbers_SpacedAndFullyHyphenated()
    {
        for (var n = 0; n <= 999; n++)
        {
            var word = Spellers.French(n);
            AssertParses(word + ".", "fr", n);
            // Post-1990 orthography hyphenates everything: "deux-cent-trente-quatre".
            AssertParses(word.Replace(' ', '-') + ".", "fr", n);
        }
    }

    [Fact]
    public void Spanish_AllNumbers_AccentedAndPlainVariants()
    {
        for (var n = 0; n <= 999; n++)
        {
            var word = Spellers.Spanish(n);
            AssertParses(word + ".", "es", n);
            AssertParses(StripAccents(word) + ".", "es", n);
        }
    }

    [Fact]
    public void Italian_AllNumbers_PlainAndElidedVariants()
    {
        for (var n = 0; n <= 999; n++)
        {
            var word = Spellers.Italian(n, elideCento: false);
            AssertParses(word + ".", "it", n);
            AssertParses(word.Replace('é', 'e') + ".", "it", n); // "ventitre" without accent
            AssertParses(Spellers.Italian(n, elideCento: true) + ".", "it", n);
        }
    }

    [Fact]
    public void Turkish_AllNumbers_TurkishAndAsciiVariants()
    {
        for (var n = 0; n <= 999; n++)
        {
            var word = Spellers.Turkish(n);
            AssertParses(word + ".", "tr", n);
            AssertParses(AsciifyTurkish(word) + ".", "tr", n);
        }
    }

    [Fact]
    public void AllLanguages_TrailingProseDoesNotChangeTheNumber()
    {
        // A word following the number must end it, not corrupt it.
        for (var n = 0; n <= 999; n += 7)
        {
            AssertParses(Spellers.English(n, useAnd: true, hyphen: true) + " begins now", "en", n);
            AssertParses(Spellers.German(n, einhundert: false) + " beginnt jetzt", "de", n);
            AssertParses(Spellers.Dutch(n) + " begint nu", "nl", n);
            AssertParses(Spellers.French(n) + " commence maintenant", "fr", n);
            AssertParses(Spellers.Spanish(n) + " comienza ahora", "es", n);
            AssertParses(Spellers.Italian(n, elideCento: true) + " inizia adesso", "it", n);
            AssertParses(Spellers.Turkish(n) + " burada başlar", "tr", n);
        }
    }

    /// <summary>Replaces German umlauts/ß by their ASCII transliterations.</summary>
    private static string Transliterate(string s) => s
        .Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue").Replace("ß", "ss");

    /// <summary>Replaces the Turkish letters by their plain ASCII look-alikes.</summary>
    private static string AsciifyTurkish(string s) => s
        .Replace('ı', 'i').Replace('ü', 'u').Replace('ö', 'o')
        .Replace('ş', 's').Replace('ç', 'c').Replace('ğ', 'g');

    /// <summary>Removes the Spanish acute accents.</summary>
    private static string StripAccents(string s) => s
        .Replace('á', 'a').Replace('é', 'e').Replace('í', 'i')
        .Replace('ó', 'o').Replace('ú', 'u');
}
