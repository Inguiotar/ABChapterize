using Xunit;

namespace Chapterize.Tests;

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
}
