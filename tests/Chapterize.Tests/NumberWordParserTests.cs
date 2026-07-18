// Chapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Xunit;

namespace Chapterize.Tests;

/// <summary>
/// Targeted tests for the <see cref="NumberWordParser"/> facade and per-language quirks
/// that the exhaustive round-trip tests do not cover: the digit fast path, punctuation
/// stripping, ordinals, rejection of non-numbers, and the English fallback.
/// </summary>
public class NumberWordParserTests
{
    [Theory]
    [InlineData("12.", "en", 12)]
    [InlineData("7,", "de", 7)]
    [InlineData("42", "fr", 42)]
    [InlineData("311!", "es", 311)]
    [InlineData("3: Der Aufbruch", "de", 3)]
    public void Digits_AlwaysWin_RegardlessOfLanguage(string text, string language, int expected)
    {
        Assert.True(NumberWordParser.TryExtractNumber(text, language, out var n));
        Assert.Equal(expected, n);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("the story continues")]
    [InlineData("and")]
    public void English_NonNumbers_AreRejected(string text)
    {
        Assert.False(NumberWordParser.TryExtractNumber(text, "en", out _));
    }

    [Theory]
    [InlineData("und zwanzig", "de")]
    [InlineData("die Geschichte", "de")]
    [InlineData("et", "fr")]
    [InlineData("et onze", "fr")]
    [InlineData("y", "es")]
    [InlineData("y nueve", "es")]
    [InlineData("het verhaal", "nl")]
    [InlineData("entwintig", "nl")]
    public void OtherLanguages_NonNumbers_AreRejected(string text, string language)
    {
        Assert.False(NumberWordParser.TryExtractNumber(text, language, out _));
    }

    [Theory]
    [InlineData("premier.", 1)]
    [InlineData("première!", 1)]
    [InlineData("deuxième,", 2)]
    [InlineData("second", 2)]
    [InlineData("dixième", 10)]
    public void French_Ordinals_AreUnderstood(string text, int expected)
    {
        Assert.True(NumberWordParser.TryExtractNumber(text, "fr", out var n));
        Assert.Equal(expected, n);
    }

    [Theory]
    [InlineData("primero.", 1)]
    [InlineData("primer capítulo", 1)]
    [InlineData("segundo", 2)]
    [InlineData("décimo", 10)]
    [InlineData("decimo", 10)]
    public void Spanish_Ordinals_AreUnderstood(string text, int expected)
    {
        Assert.True(NumberWordParser.TryExtractNumber(text, "es", out var n));
        Assert.Equal(expected, n);
    }

    [Theory]
    [InlineData("één.", 1)]
    [InlineData("Eenentwintig", 21)]
    [InlineData("TWEEËNTWINTIG", 22)]
    public void Dutch_AccentsAndCase_AreNormalized(string text, int expected)
    {
        Assert.True(NumberWordParser.TryExtractNumber(text, "nl", out var n));
        Assert.Equal(expected, n);
    }

    [Theory]
    [InlineData("hundertundeins", 101)]
    [InlineData("Einhundertundeins", 101)]
    [InlineData("fuenfundfuenfzig", 55)]
    [InlineData("Dreissig", 30)]
    [InlineData("eine", 1)]
    public void German_AlternateForms_AreUnderstood(string text, int expected)
    {
        Assert.True(NumberWordParser.TryExtractNumber(text, "de", out var n));
        Assert.Equal(expected, n);
    }

    [Theory]
    [InlineData("primo.", 1)]
    [InlineData("Prima", 1)]
    [InlineData("decimo", 10)]
    [InlineData("ventitré", 23)]
    [InlineData("ventitre", 23)]
    [InlineData("centotto", 108)]
    [InlineData("centootto", 108)]
    [InlineData("centottanta", 180)]
    [InlineData("duecentuno", 201)]
    public void Italian_OrdinalsAndElisions_AreUnderstood(string text, int expected)
    {
        Assert.True(NumberWordParser.TryExtractNumber(text, "it", out var n));
        Assert.Equal(expected, n);
    }

    [Theory]
    [InlineData("birinci bölüm", 1)]
    [InlineData("İkinci", 2)]
    [InlineData("üçüncü", 3)]
    [InlineData("ucuncu", 3)]
    [InlineData("dördüncü", 4)]
    [InlineData("onuncu", 10)]
    public void Turkish_Ordinals_AreUnderstood(string text, int expected)
    {
        Assert.True(NumberWordParser.TryExtractNumber(text, "tr", out var n));
        Assert.Equal(expected, n);
    }

    [Fact]
    public void UnknownLanguage_FallsBackToEnglish()
    {
        Assert.True(NumberWordParser.TryExtractNumber("twenty-one", "xx", out var n));
        Assert.Equal(21, n);
    }

    [Fact]
    public void SupportedLanguages_ListsAllParsers()
    {
        Assert.Equal(["de", "en", "es", "fr", "it", "nl", "tr"], NumberWordParser.SupportedLanguages);
    }

    [Theory]
    [InlineData("\"vingt et un\", dit-il", "fr", 21)]
    [InlineData("(siete)", "es", 7)]
    [InlineData("„Zwölf“", "de", 12)]
    [InlineData("twenty-one: The Reckoning", "en", 21)]
    public void SurroundingPunctuation_IsStripped(string text, string language, int expected)
    {
        Assert.True(NumberWordParser.TryExtractNumber(text, language, out var n));
        Assert.Equal(expected, n);
    }
}
