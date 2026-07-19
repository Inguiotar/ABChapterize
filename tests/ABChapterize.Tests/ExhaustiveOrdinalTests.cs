// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Xunit;

namespace ABChapterize.Tests;

/// <summary>
/// Exhaustive ordinal round-trip tests: every number 1-999 is spelled as an ordinal by the
/// independent reference spellers and must parse back through both public entry points —
/// <see cref="NumberWordParser.TryExtractNumber"/> (number after the phrase) and
/// <see cref="NumberWordParser.TryExtractNumberBefore"/> (ordinal-first announcements like
/// "Erstes Kapitel" / "Birinci Bölüm", where the number must end exactly at the phrase).
/// </summary>
public class ExhaustiveOrdinalTests
{
    /// <summary>Asserts that an ordinal parses to its value from text following the phrase.</summary>
    private static void AssertParses(string text, string language, int expected)
    {
        var ok = NumberWordParser.TryExtractNumber(text, language, out var number);
        Assert.True(ok && number == expected,
            $"[{language}] \"{text}\" -> expected {expected}, got {(ok ? number.ToString() : "no parse")}");
    }

    /// <summary>
    /// Asserts that an ordinal parses to its value from text preceding the phrase, with
    /// unrelated leading prose that must not disturb the parse.
    /// </summary>
    private static void AssertParsesBefore(string text, string language, int expected)
    {
        var ok = NumberWordParser.TryExtractNumberBefore("bla blub " + text, language, out var number);
        Assert.True(ok && number == expected,
            $"[{language}] \"... {text}\" (before phrase) -> expected {expected}, got {(ok ? number.ToString() : "no parse")}");
    }

    /// <summary>Runs both directions for one spelled ordinal.</summary>
    private static void AssertRoundTrip(string text, string language, int expected)
    {
        AssertParses(text + ".", language, expected);
        AssertParsesBefore(text, language, expected);
    }

    [Fact]
    public void English_AllOrdinals_AllVariants()
    {
        for (var n = 1; n <= 999; n++)
        {
            AssertRoundTrip(Spellers.EnglishOrdinal(n, useAnd: false, hyphen: true), "en", n);
            AssertRoundTrip(Spellers.EnglishOrdinal(n, useAnd: false, hyphen: false), "en", n);
            AssertRoundTrip(Spellers.EnglishOrdinal(n, useAnd: true, hyphen: true), "en", n);
            AssertRoundTrip(Spellers.EnglishOrdinal(n, useAnd: true, hyphen: false), "en", n);
        }
    }

    [Fact]
    public void German_AllOrdinals_AllDeclensionsAndSpellings()
    {
        for (var n = 1; n <= 999; n++)
        {
            var word = Spellers.GermanOrdinal(n, einhundert: false);
            // All adjective declensions: "erste, erster, erstes, ersten, erstem Kapitel".
            foreach (var ending in new[] { "", "r", "s", "n", "m" })
                AssertRoundTrip(word + ending, "de", n);
            AssertRoundTrip(Transliterate(word), "de", n);
            AssertRoundTrip(Spellers.GermanOrdinal(n, einhundert: true), "de", n);
        }
    }

    [Fact]
    public void Dutch_AllOrdinals_TremaAndPlainVariants()
    {
        for (var n = 1; n <= 999; n++)
        {
            var word = Spellers.DutchOrdinal(n);
            AssertRoundTrip(word, "nl", n);
            AssertRoundTrip(word.Replace('ë', 'e'), "nl", n);
        }
    }

    [Fact]
    public void French_AllOrdinals_AccentedPlainAndHyphenated()
    {
        for (var n = 1; n <= 999; n++)
        {
            var word = Spellers.FrenchOrdinal(n);
            AssertRoundTrip(word, "fr", n);
            AssertRoundTrip(word.Replace('è', 'e').Replace('é', 'e'), "fr", n);
            AssertRoundTrip(word.Replace(' ', '-'), "fr", n);
        }
    }

    [Fact]
    public void Italian_AllOrdinals_PlainAndElidedVariants()
    {
        for (var n = 1; n <= 999; n++)
        {
            AssertRoundTrip(Spellers.ItalianOrdinal(n, elideCento: false), "it", n);
            AssertRoundTrip(Spellers.ItalianOrdinal(n, elideCento: true), "it", n);
        }
    }

    [Fact]
    public void Turkish_AllOrdinals_TurkishAndAsciiVariants()
    {
        for (var n = 1; n <= 999; n++)
        {
            var word = Spellers.TurkishOrdinal(n);
            AssertRoundTrip(word, "tr", n);
            AssertRoundTrip(AsciifyTurkish(word), "tr", n);
        }
    }

    [Fact]
    public void Polish_AllOrdinals_AccentedAndAsciiVariants()
    {
        for (var n = 1; n <= 999; n++)
        {
            var word = Spellers.PolishOrdinal(n);
            AssertRoundTrip(word, "pl", n);
            AssertRoundTrip(AsciifyPolish(word), "pl", n);
        }
    }

    [Fact]
    public void Swedish_AllOrdinals_AccentedAndAsciiVariants()
    {
        for (var n = 1; n <= 999; n++)
        {
            var word = Spellers.SwedishOrdinal(n);
            AssertRoundTrip(word, "sv", n);
            AssertRoundTrip(AsciifySwedish(word), "sv", n);
        }
    }

    [Theory]
    // Portuguese irregular ordinals 1st-10th ("capítulo primeiro").
    [InlineData("primeiro", "pt", 1)]
    [InlineData("primeira", "pt", 1)]
    [InlineData("segundo", "pt", 2)]
    [InlineData("terceiro", "pt", 3)]
    [InlineData("quarto", "pt", 4)]
    [InlineData("quinto", "pt", 5)]
    [InlineData("sexto", "pt", 6)]
    [InlineData("sétimo", "pt", 7)]
    [InlineData("oitavo", "pt", 8)]
    [InlineData("nono", "pt", 9)]
    [InlineData("décimo", "pt", 10)]
    // Danish ordinals 1st-20th ("Første kapitel"); compound ordinals beyond that are out
    // of scope for this parser (see DanishNumberParser class docs).
    [InlineData("første", "da", 1)]
    [InlineData("anden", "da", 2)]
    [InlineData("andet", "da", 2)]
    [InlineData("tredje", "da", 3)]
    [InlineData("fjerde", "da", 4)]
    [InlineData("femte", "da", 5)]
    [InlineData("sjette", "da", 6)]
    [InlineData("syvende", "da", 7)]
    [InlineData("ottende", "da", 8)]
    [InlineData("niende", "da", 9)]
    [InlineData("tiende", "da", 10)]
    [InlineData("tyvende", "da", 20)]
    // Danish ordinal combined with a hundreds word ahead of it.
    [InlineData("et hundrede og femte", "da", 105)]
    [InlineData("hundrede og tyvende", "da", 120)]
    public void OrdinalVariants_ParseTargeted(string text, string language, int expected)
        => AssertParses(text + ".", language, expected);

    [Theory]
    // English digit ordinals.
    [InlineData("1st", "en", 1)]
    [InlineData("2nd", "en", 2)]
    [InlineData("3rd", "en", 3)]
    [InlineData("21st", "en", 21)]
    [InlineData("112th", "en", 112)]
    // French digit ordinals.
    [InlineData("1er", "fr", 1)]
    [InlineData("1re", "fr", 1)]
    [InlineData("2e", "fr", 2)]
    [InlineData("2ème", "fr", 2)]
    [InlineData("2eme", "fr", 2)]
    [InlineData("3ième", "fr", 3)]
    // German/Dutch digit ordinals ("2." loses its dot to punctuation trimming).
    [InlineData("2.", "de", 2)]
    [InlineData("17.", "de", 17)]
    [InlineData("2te", "de", 2)]
    [InlineData("2de", "nl", 2)]
    [InlineData("8ste", "nl", 8)]
    // Spanish/Italian masculine and feminine markers.
    [InlineData("2º", "es", 2)]
    [InlineData("2ª", "es", 2)]
    [InlineData("2°", "it", 2)]
    // Turkish digit ordinals, with and without the apostrophe.
    [InlineData("5'inci", "tr", 5)]
    [InlineData("5inci", "tr", 5)]
    [InlineData("4üncü", "tr", 4)]
    // Portuguese masculine/feminine markers (shared with Spanish/Italian).
    [InlineData("2º", "pt", 2)]
    [InlineData("2ª", "pt", 2)]
    // Polish and Danish use a plain trailing dot, like German/Dutch.
    [InlineData("2.", "pl", 2)]
    [InlineData("21.", "da", 21)]
    // Swedish uses a colon before its suffix, "a" for 1st/2nd and "e" for the rest.
    [InlineData("1:a", "sv", 1)]
    [InlineData("2:a", "sv", 2)]
    [InlineData("3:e", "sv", 3)]
    [InlineData("21:a", "sv", 21)]
    public void DigitOrdinals_ParseInBothPositions(string text, string language, int expected)
    {
        AssertParses(text, language, expected);
        AssertParsesBefore(text, language, expected);
    }

    [Theory]
    // Feminine and declined word-ordinal forms not covered by the exhaustive spellers.
    [InlineData("première", "fr", 1)]
    [InlineData("seconde", "fr", 2)]
    [InlineData("ventesima", "it", 20)]
    [InlineData("centesima", "it", 100)]
    [InlineData("undicesima", "it", 11)]
    public void OrdinalVariants_Parse(string text, string language, int expected)
        => AssertParses(text, language, expected);

    [Fact]
    public void NumberBeforePhrase_MustEndAtThePhrase()
    {
        // A number that merely occurs earlier in the sentence must not count.
        Assert.False(NumberWordParser.TryExtractNumberBefore("drei sagte er", "de", out _));
        Assert.False(NumberWordParser.TryExtractNumberBefore("three said he", "en", out _));
        // But a cardinal directly before the phrase does ("2. Kapitel" style with words).
        Assert.True(NumberWordParser.TryExtractNumberBefore("und nun drei", "de", out var n) && n == 3);
        Assert.True(NumberWordParser.TryExtractNumberBefore("now twenty one", "en", out n) && n == 21);
    }

    [Fact]
    public void NumberBeforePhrase_EmptyOrProseOnly_DoesNotParse()
    {
        Assert.False(NumberWordParser.TryExtractNumberBefore("", "de", out _));
        Assert.False(NumberWordParser.TryExtractNumberBefore("und jetzt zum nächsten", "de", out _));
    }

    /// <summary>Replaces German umlauts/ß by their ASCII transliterations.</summary>
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
}
