// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Xunit;
using ABChapterize.Language;

namespace ABChapterize.Tests;

/// <summary>
/// Tests for <see cref="RomanNumerals"/> and its use from <see cref="NumberWordParser"/>. The
/// parser exists because Whisper writes chapter announcements in Roman numerals whenever it falls
/// into its all-caps heading style, which it does unpredictably for the same audio - see
/// <see cref="RomanNumerals"/> for the case that uncovered it.
/// </summary>
public class RomanNumeralTests
{
    /// <summary>Every value the parser accepts must round-trip through its own canonical
    /// spelling - the property the whole "is this word a numeral?" test rests on.</summary>
    [Fact]
    public void EveryAcceptedValue_RoundTripsThroughItsCanonicalSpelling()
    {
        for (var value = 1; value <= 999; value++)
        {
            var text = RomanNumerals.Format(value);
            Assert.True(RomanNumerals.TryParse(text, out var parsed), $"{value} -> {text}");
            Assert.Equal(value, parsed);
        }
    }

    [Theory]
    [InlineData("I", 1)]
    [InlineData("IV", 4)]
    [InlineData("IX", 9)]
    [InlineData("XIII", 13)]
    [InlineData("XL", 40)]
    [InlineData("XCIX", 99)]
    [InlineData("CDXLIV", 444)]
    [InlineData("CMXCIX", 999)]
    public void CanonicalNumerals_Parse(string text, int expected)
    {
        Assert.True(RomanNumerals.TryParse(text, out var n));
        Assert.Equal(expected, n);
    }

    /// <summary>Whisper's own casing varies with the style it picks, so both must work.</summary>
    [Theory]
    [InlineData("xiii")]
    [InlineData("XiIi")]
    public void Casing_IsIgnored(string text)
    {
        Assert.True(RomanNumerals.TryParse(text, out var n));
        Assert.Equal(13, n);
    }

    /// <summary>
    /// Non-canonical spellings a lenient reader could still assign a value to. Rejecting them is
    /// what keeps ordinary Latin-lettered words from parsing as numbers at all.
    /// </summary>
    [Theory]
    [InlineData("IIII")]
    [InlineData("XXXX")]
    [InlineData("IC")]
    [InlineData("VX")]
    [InlineData("IIX")]
    public void NonCanonicalSpellings_AreRejected(string text)
        => Assert.False(RomanNumerals.TryParse(text, out _));

    /// <summary>The words this parser is most at risk of misreading, all of which fail either the
    /// canonical check or the 999 cap.</summary>
    [Theory]
    [InlineData("DIM")]
    [InlineData("LID")]
    [InlineData("CIVIC")]
    [InlineData("MILD")]
    [InlineData("MIX")]      // canonical, but 1009 - above the cap
    [InlineData("M")]        // 1000, likewise
    [InlineData("chapter")]
    [InlineData("")]
    public void OrdinaryWordsAndOutOfRangeValues_AreRejected(string text)
        => Assert.False(RomanNumerals.TryParse(text, out _));

    [Theory]
    [InlineData("XIII. The Shaking of the Sheets", "en", 13)]
    [InlineData("VII Songs in the Night", "en", 7)]
    [InlineData("XI. The Bonfire of the Witches", "en", 11)]
    [InlineData("xiv, in which much happens", "de", 14)]
    public void NumberWordParser_ReadsRomanNumeralsAfterThePhrase(string text, string language, int expected)
    {
        Assert.True(NumberWordParser.TryExtractNumber(text, language, out var n));
        Assert.Equal(expected, n);
    }

    /// <summary>The "XIII. Kapitel" announcement order, which German and Italian books use.</summary>
    [Theory]
    [InlineData("XIII.", "de", 13)]
    [InlineData("Und nun XVII.", "de", 17)]
    public void NumberWordParser_ReadsRomanNumeralsBeforeThePhrase(string text, string language, int expected)
    {
        Assert.True(NumberWordParser.TryExtractNumberBefore(text, language, out var n));
        Assert.Equal(expected, n);
    }

    /// <summary>
    /// A one-letter numeral counts only with the period a heading gives it. Without one it is far
    /// likelier to be an English pronoun or a Polish "and" than a chapter number.
    /// </summary>
    [Theory]
    [InlineData("V. The Mother of Tongues", "en", 5)]
    [InlineData("X. The Melting Girl", "en", 10)]
    [InlineData("I. The Beginning", "en", 1)]
    public void SingleLetterNumerals_CountWithATrailingPeriod(string text, string language, int expected)
    {
        Assert.True(NumberWordParser.TryExtractNumber(text, language, out var n));
        Assert.Equal(expected, n);
    }

    [Theory]
    [InlineData("I wrote that down later", "en")]
    [InlineData("i epilog", "pl")]
    [InlineData("C is for cookie", "en")]
    public void SingleLetterNumerals_AreIgnoredWithoutOne(string text, string language)
        => Assert.False(NumberWordParser.TryExtractNumber(text, language, out _));

    /// <summary>A period that a quote or bracket closes over still counts - it is the same
    /// heading period, just wrapped.</summary>
    [Theory]
    [InlineData("V.\" said the narrator", "en", 5)]
    [InlineData("X.) The Melting Girl", "en", 10)]
    public void SingleLetterNumerals_CountThroughClosingPunctuation(string text, string language, int expected)
    {
        Assert.True(NumberWordParser.TryExtractNumber(text, language, out var n));
        Assert.Equal(expected, n);
    }

    /// <summary>
    /// The collision that fixes the order the three notations are tried in: French "dix" is the
    /// spoken word for ten and, read as a Roman numeral, the canonical spelling of 509. The
    /// narrator said "ten", so the language's own word parser has to be consulted before the Roman
    /// one - which is why <see cref="NumberWordParser"/> tries Roman last rather than alongside
    /// digits. Caught by the exhaustive French round-trip when Roman was tried first.
    /// </summary>
    [Fact]
    public void SpokenWords_WinOverACollidingRomanReading()
    {
        Assert.True(NumberWordParser.TryExtractNumber("dix.", "fr", out var french));
        Assert.Equal(10, french);
        // Same letters, a language whose parser makes nothing of them: the Roman reading stands.
        Assert.True(NumberWordParser.TryExtractNumber("DIX. The Reckoning", "en", out var english));
        Assert.Equal(509, english);
    }

    /// <summary>Spelled-out and digit forms must keep working exactly as before - the Roman path
    /// is an addition, not a replacement, and it is tried after both.</summary>
    [Theory]
    [InlineData("13 The Shaking of the Sheets", "en", 13)]
    [InlineData("thirteen", "en", 13)]
    [InlineData("dreizehn", "de", 13)]
    public void ExistingForms_StillParse(string text, string language, int expected)
    {
        Assert.True(NumberWordParser.TryExtractNumber(text, language, out var n));
        Assert.Equal(expected, n);
    }
}
