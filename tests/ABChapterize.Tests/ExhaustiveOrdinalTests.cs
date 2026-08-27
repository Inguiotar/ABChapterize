// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Xunit;
using ABChapterize.Language;

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

    /// <summary>
    /// Spanish ordinals reach 199 rather than 999 (see <c>SpanishNumberParser</c>), and are
    /// checked in all three spellings a narrator may pick: masculine, feminine, and the
    /// twenties' fused single word.
    /// </summary>
    [Fact]
    public void Spanish_AllOrdinals_GenderAndFusedVariants()
    {
        for (var n = 1; n <= 199; n++)
        {
            AssertRoundTrip(Spellers.SpanishOrdinal(n), "es", n);
            AssertRoundTrip(Spellers.SpanishOrdinal(n, feminine: true), "es", n);
            AssertRoundTrip(Spellers.SpanishOrdinal(n, fuse: true), "es", n);
            AssertRoundTrip(Spellers.SpanishOrdinal(n, feminine: true, fuse: true), "es", n);
            AssertRoundTrip(Deaccent(Spellers.SpanishOrdinal(n)), "es", n);
        }
    }

    /// <summary>Portuguese ordinals reach 199 rather than 999 (see <c>PortugueseNumberParser</c>).</summary>
    [Fact]
    public void Portuguese_AllOrdinals_GenderAndAccentVariants()
    {
        for (var n = 1; n <= 199; n++)
        {
            AssertRoundTrip(Spellers.PortugueseOrdinal(n), "pt", n);
            AssertRoundTrip(Spellers.PortugueseOrdinal(n, feminine: true), "pt", n);
            AssertRoundTrip(Deaccent(Spellers.PortugueseOrdinal(n)), "pt", n);
        }
    }

    /// <summary>Danish ordinals reach 100 (see <c>DanishNumberParser</c>), in both the formal
    /// "-indstyvende" tens and the short everyday ones.</summary>
    [Fact]
    public void Danish_AllOrdinals_FormalAndColloquialTens()
    {
        for (var n = 1; n <= 100; n++)
        {
            AssertRoundTrip(Spellers.DanishOrdinal(n), "da", n);
            AssertRoundTrip(Spellers.DanishOrdinal(n, colloquialTens: true), "da", n);
        }
    }

    /// <summary>
    /// Norwegian ordinals run the full range in both counting systems. The ASCII sweep skips
    /// <c>n % 100 == 8</c>: dropping the ring off "attende" (8th) spells "attende", which is 18th,
    /// and in that position - alone, or after a hundreds word - a teen is a real possibility, so
    /// there is nothing to tell the two apart. Inside a compound there is, and
    /// <see cref="Norwegian_AnEighthInsideACompound_SurvivesLosingItsRing"/> covers that half.
    /// </summary>
    [Fact]
    public void Norwegian_AllOrdinals_BothCountingSystemsAndAsciiVariants()
    {
        for (var n = 1; n <= 999; n++)
        {
            AssertRoundTrip(Spellers.NorwegianOrdinal(n), "no", n);
            AssertRoundTrip(Spellers.NorwegianOrdinal(n, conservative: true), "no", n);
            if (n % 100 == 8)
                continue;
            AssertRoundTrip(AsciifyNorwegian(Spellers.NorwegianOrdinal(n)), "no", n);
            AssertRoundTrip(
                AsciifyNorwegian(Spellers.NorwegianOrdinal(n, conservative: true)), "no", n);
        }
    }

    /// <summary>
    /// The half of the "attende"/"attende" collision that is recoverable: no Norwegian compound
    /// ends in a teen, so a tens word followed by one can only be an eighth that lost its ring.
    /// </summary>
    [Fact]
    public void Norwegian_AnEighthInsideACompound_SurvivesLosingItsRing()
    {
        for (var n = 1; n <= 999; n++)
        {
            if (n % 10 != 8 || n % 100 is < 20 or 8)
                continue;
            AssertRoundTrip(AsciifyNorwegian(Spellers.NorwegianOrdinal(n)), "no", n);
        }
    }

    /// <summary>Czech ordinals agree in gender with what they name, so both the feminine that
    /// "kapitola" wants and the masculine a narrator uses elsewhere must read back.</summary>
    [Fact]
    public void Czech_AllOrdinals_BothGendersAndAsciiVariants()
    {
        for (var n = 1; n <= 999; n++)
        {
            AssertRoundTrip(Spellers.CzechOrdinal(n), "cs", n);
            AssertRoundTrip(Spellers.CzechOrdinal(n, masculine: true), "cs", n);
            AssertRoundTrip(AsciifyCzech(Spellers.CzechOrdinal(n)), "cs", n);
        }
    }

    /// <summary>Strips the acute accents Spanish and Portuguese ordinals carry, which a
    /// transcript may or may not reproduce.</summary>
    private static string Deaccent(string s) => s
        .Replace('á', 'a').Replace('é', 'e').Replace('í', 'i').Replace('ó', 'o').Replace('ú', 'u');

    [Theory]
    // The two Spanish ordinals with a lexeme of their own, alongside the compositional
    // "décimo primero"/"décimo segundo" the exhaustive test already covers.
    [InlineData("undécimo", "es", 11)]
    [InlineData("undecima", "es", 11)]
    [InlineData("duodécimo", "es", 12)]
    [InlineData("duodécima", "es", 12)]
    // "nono" is the older 9th, alive inside "decimonono".
    [InlineData("decimonono", "es", 19)]
    // Portuguese European/Brazilian doublets in the ordinal tens.
    [InlineData("setuagésimo", "pt", 70)]
    [InlineData("oitogésimo", "pt", 80)]
    [InlineData("cinquagésimo", "pt", 50)]
    // Danish ordinal combined with a hundreds word ahead of it.
    [InlineData("et hundrede og femte", "da", 105)]
    [InlineData("hundrede og tyvende", "da", 120)]
    [InlineData("to hundrede og enogtyvende", "da", 221)]
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

    /// <summary>Replaces the Norwegian diacritics by their plain ASCII look-alikes.</summary>
    private static string AsciifyNorwegian(string s) => s
        .Replace('å', 'a').Replace('ø', 'o').Replace('æ', 'a');

    /// <summary>Replaces the Czech diacritics by their plain ASCII look-alikes.</summary>
    private static string AsciifyCzech(string s) => s
        .Replace('á', 'a').Replace('č', 'c').Replace('ď', 'd').Replace('é', 'e')
        .Replace('ě', 'e').Replace('í', 'i').Replace('ň', 'n').Replace('ó', 'o')
        .Replace('ř', 'r').Replace('š', 's').Replace('ť', 't').Replace('ú', 'u')
        .Replace('ů', 'u').Replace('ý', 'y').Replace('ž', 'z');
}
