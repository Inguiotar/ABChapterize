// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Xunit;
using ABChapterize.Language;

namespace ABChapterize.Tests;

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
        Assert.Equal(
            ["da", "de", "en", "es", "fr", "it", "nl", "pl", "pt", "sv", "tr"],
            LanguageRegistry.SupportedCodes);
    }

    /// <summary>
    /// The registry is the single source of truth for a language, so a class that declares
    /// itself "de" must not be reachable under any other code - a copy-paste slip in
    /// <see cref="LanguageRegistry"/> would otherwise silently give one language another's
    /// number grammar.
    /// </summary>
    [Fact]
    public void EveryRegisteredLanguage_CarriesItsOwnNumberParser()
    {
        foreach (var language in LanguageRegistry.Languages)
            Assert.Equal(language.Code, language.NumberParser.LanguageCode);
    }

    /// <summary>What <c>--chapter-phrase none</c> accepts as an announcement: text that is a
    /// number and nothing else, in any of the notations a transcript writes one in.</summary>
    [Theory]
    [InlineData("Seventeen.", "en", 17)]
    [InlineData(" 17. ", "en", 17)]
    [InlineData("Twenty-one", "en", 21)]
    [InlineData("Einundzwanzig.", "de", 21)]
    [InlineData("\"Vingt et un.\"", "fr", 21)]
    [InlineData("XIII.", "en", 13)]
    [InlineData("2nd", "en", 2)]
    public void WholeText_AcceptsANumberStandingAlone(string text, string language, int expected)
    {
        Assert.True(NumberWordParser.TryParseWholeText(text, language, out var n));
        Assert.Equal(expected, n);
    }

    /// <summary>What this particular helper is strict about: the text must be nothing but the
    /// number. The mode built on it splits a segment into sentences first, so a number opening a
    /// sentence still counts - see <see cref="NumberWordParser.FindBareNumberAnnouncement"/>.</summary>
    [Theory]
    [InlineData("Seventeen men stood at the gate.", "en")]
    [InlineData("Chapter seventeen.", "en")]
    [InlineData("Siebzehn Jahre spater.", "de")]
    [InlineData("It was over.", "en")]
    [InlineData("", "en")]
    [InlineData("   ", "en")]
    public void WholeText_RejectsANumberInsideASentence(string text, string language)
        => Assert.False(NumberWordParser.TryParseWholeText(text, language, out _));

    /// <summary>The one-letter Roman guard applies here too: a lone "I" is a pronoun far more
    /// often than it is chapter one, and only the heading period settles it.</summary>
    [Fact]
    public void WholeText_KeepsTheOneLetterRomanGuard()
    {
        Assert.False(NumberWordParser.TryParseWholeText("I", "en", out _));
        Assert.True(NumberWordParser.TryParseWholeText("I.", "en", out var n));
        Assert.Equal(1, n);
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
    /// <summary>
    /// The reading that recovers a glued announcement: Whisper writes "45. Zhang Mingoua lanciò
    /// un'occhiata…" as one segment, and the number is still a sentence of its own inside it. Each
    /// of these is a real transcript line from "Corsa nello spazio" (build 244, 2026-08-05), where
    /// the old whole-segment rule threw ten chapters away.
    /// </summary>
    [Theory]
    [InlineData("45. Zhang Mingoua lanciò un'occhiata alla lettura della data.", "it", 45)]
    [InlineData("16. Becca tirò avanti tutta la notte a caffè.", "it", 16)]
    [InlineData("2. Era in ritardo. Terribilmente in ritardo.", "it", 2)]
    [InlineData("Two. He was late.", "en", 2)]
    [InlineData("Twenty-one. The reckoning began.", "en", 21)]
    [InlineData("XIII. The shaking of the sheets.", "en", 13)]
    [InlineData("53.", "it", 53)]
    [InlineData("Cinquantasette", "it", 57)]
    public void BareNumberAnnouncement_ReadsANumberThatOpensItsSegment(
        string text, string language, int expected)
    {
        var announced = NumberWordParser.FindBareNumberAnnouncement(
            text, language, NumberWordParser.BareNumberReading.SpokenAloneAtSegmentStart);
        Assert.NotNull(announced);
        Assert.Equal(expected, announced!.Value.Number);
        Assert.True(announced.Value.SpokenAlone);
    }

    /// <summary>
    /// What the two strict readings refuse, and have to: a number that merely begins a sentence
    /// without ending it. All four are real lines from the same book, and the first is the one that
    /// makes the point - chapter 1's announcement reads "1. 9 febbraio 2066…", so had the "1." been
    /// dropped the date behind it must not become chapter 9. Probe's forward scan sees only these
    /// readings, which is why it can walk a whole book unsupervised.
    /// </summary>
    [Theory]
    [InlineData("9 febbraio 2066. Da 10 km di distanza.", "it")]
    [InlineData("1000 km sopra le macchinazioni di Washington.", "it")]
    [InlineData("Seventeen men stood at the gate.", "en")]
    [InlineData("Siebzehn Jahre später kam er zurück.", "de")]
    public void BareNumberAnnouncement_RejectsANumberThatOnlyOpensASentence(string text, string language)
    {
        Assert.Null(NumberWordParser.FindBareNumberAnnouncement(
            text, language, NumberWordParser.BareNumberReading.SpokenAloneAtSegmentStart));
        Assert.Null(NumberWordParser.FindBareNumberAnnouncement(
            text, language, NumberWordParser.BareNumberReading.SpokenAloneAnywhere));
    }

    /// <summary>
    /// The most permissive reading does take those, deliberately - it is only ever used where the
    /// hole being filled says which numbers may appear and
    /// <see cref="AnnouncementIsolation"/> then has to vouch for the position. What it must never
    /// do is claim they stand on their own evidence, so every one comes back
    /// <c>SpokenAlone: false</c>, which is what denies it the guard's fallback.
    /// </summary>
    [Theory]
    [InlineData("9 febbraio 2066. Da 10 km di distanza.", "it", 9)]
    [InlineData("Seventeen men stood at the gate.", "en", 17)]
    public void BareNumberAnnouncement_WidestReadingTakesThemButFlagsThem(
        string text, string language, int expected)
    {
        var wide = NumberWordParser.FindBareNumberAnnouncement(
            text, language, NumberWordParser.BareNumberReading.LeadingASentence);
        Assert.NotNull(wide);
        Assert.Equal(expected, wide!.Value.Number);
        Assert.False(wide.Value.SpokenAlone);
    }

    /// <summary>
    /// Why the widest reading exists at all: Whisper's period after a heading number is not
    /// dependable, and specifically not across machines. The same probe window at 12:25:23 of
    /// "Corsa nello spazio" came back as the first of these on one GPU and the second on another
    /// (2026-08-05) - so a rule resting on that period finds different chapters on different
    /// hardware, and only the reading that ignores punctuation entirely sees both.
    /// </summary>
    [Fact]
    public void BareNumberAnnouncement_WidestReadingSurvivesAMissingPeriod()
    {
        const string withPeriod = "45. Zhang Mingoua lanciò un'occhiata alla lettura della data.";
        const string without = "45 Zangmingoa lanciò un'occhiata alla lettura della data.";
        var strict = NumberWordParser.BareNumberReading.SpokenAloneAtSegmentStart;
        var widest = NumberWordParser.BareNumberReading.LeadingASentence;

        Assert.Equal(45, NumberWordParser.FindBareNumberAnnouncement(withPeriod, "it", strict)!.Value.Number);
        Assert.Null(NumberWordParser.FindBareNumberAnnouncement(without, "it", strict));
        Assert.Equal(45, NumberWordParser.FindBareNumberAnnouncement(without, "it", widest)!.Value.Number);
    }

    /// <summary>
    /// The two readings differ on exactly one thing: a number Whisper buried behind the tail of the
    /// previous chapter. The narrow reading (Probe's forward scan) passes it over, the wide one
    /// takes it and flags that it did not open the segment - which is what makes
    /// <see cref="AnnouncementIsolation"/>'s verdict, rather than the segmentation, decisive.
    /// </summary>
    [Fact]
    public void BareNumberAnnouncement_WideReadingReachesPastTheFirstSentence()
    {
        const string text = "ha trovato un'astronave aliena. 3. Il presidente Amanda Santeros.";
        Assert.Null(NumberWordParser.FindBareNumberAnnouncement(
            text, "it", NumberWordParser.BareNumberReading.SpokenAloneAtSegmentStart));

        var wide = NumberWordParser.FindBareNumberAnnouncement(
            text, "it", NumberWordParser.BareNumberReading.SpokenAloneAnywhere);
        Assert.NotNull(wide);
        Assert.Equal(3, wide!.Value.Number);
        Assert.False(wide.Value.SpokenAlone);
    }

    /// <summary>
    /// The sentence split needs whitespace after the period, or a dotted number falls apart into a
    /// spurious announcement. "Epilogo. 2.179. Spazio profondo." is the real line behind this - the
    /// epilogue of "Corsa nello spazio" is followed by the year 2179, and splitting on the period
    /// alone would have offered chapter 2 at the very end of the book.
    /// </summary>
    [Fact]
    public void BareNumberAnnouncement_DoesNotSplitADottedNumber()
        => Assert.Null(NumberWordParser.FindBareNumberAnnouncement(
            "Epilogo. 2.179. Spazio profondo.", "it",
            NumberWordParser.BareNumberReading.LeadingASentence));
}
