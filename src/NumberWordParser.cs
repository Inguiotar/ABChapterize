// Chapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using System.Globalization;
using System.Text.RegularExpressions;
using Chapterize.Lang;

namespace Chapterize;

/// <summary>
/// Extracts chapter numbers from transcribed text. Understands plain digits in any language,
/// digit ordinals ("2nd", "2e", "2ème"), and spoken number words (0-999, cardinal and
/// ordinal) via the per-language parsers in <see cref="Lang"/>; unknown language codes fall
/// back to the English parser. Numbers can be extracted after the chapter phrase
/// ("Chapter Seven") or before it ("Erstes Kapitel", "2. Kapitel", "Birinci Bölüm").
/// </summary>
public static class NumberWordParser
{
    /// <summary>All available language parsers, keyed by their ISO 639-1 code.</summary>
    private static readonly Dictionary<string, INumberWordParser> Parsers =
        new INumberWordParser[]
        {
            new EnglishNumberParser(),
            new GermanNumberParser(),
            new FrenchNumberParser(),
            new SpanishNumberParser(),
            new DutchNumberParser(),
            new ItalianNumberParser(),
            new TurkishNumberParser(),
            new PortugueseNumberParser(),
            new PolishNumberParser(),
            new SwedishNumberParser(),
            new DanishNumberParser(),
        }
        .ToDictionary(p => p.LanguageCode, StringComparer.OrdinalIgnoreCase);

    /// <summary>Fallback parser for language codes without a dedicated implementation.</summary>
    private static readonly INumberWordParser Fallback = Parsers["en"];

    /// <summary>Language codes with a dedicated number-word parser, for help/docs output.</summary>
    public static IEnumerable<string> SupportedLanguages => Parsers.Keys.Order();

    /// <summary>
    /// Matches a digit ordinal: 1-3 digits plus the ordinal suffix of any registered
    /// language's parser ("2nd", "1er", "2e", "2ème", "2de", "2ste", "5'inci", "3º",
    /// "21:a"). Assembled from each parser's own <see cref="INumberWordParser.DigitOrdinalSuffixPattern"/>
    /// rather than hardcoded here, so a language brings its own suffixes (and any
    /// separator it needs, like Turkish's optional apostrophe or Swedish's mandatory
    /// colon) simply by declaring them.
    /// </summary>
    private static readonly Regex DigitOrdinalRegex = BuildDigitOrdinalRegex();

    /// <summary>Builds <see cref="DigitOrdinalRegex"/> from every registered parser's suffix fragment.</summary>
    private static Regex BuildDigitOrdinalRegex()
    {
        var fragments = Parsers.Values
            .Select(p => p.DigitOrdinalSuffixPattern)
            .Where(p => p.Length > 0)
            .Distinct();
        var pattern = $@"^(\d{{1,3}})(?:{string.Join('|', fragments)})$";
        return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// Tries to extract a number from the beginning of <paramref name="text"/>,
    /// which is the transcribed text immediately following the chapter phrase.
    /// </summary>
    /// <param name="text">Text following the matched chapter phrase.</param>
    /// <param name="language">Two-letter language code steering number-word parsing.</param>
    /// <param name="number">Receives the extracted number on success.</param>
    /// <returns>True when a number could be extracted.</returns>
    public static bool TryExtractNumber(string text, string language, out int number)
    {
        number = 0;
        var tokens = Tokenize(text);
        if (tokens.Count == 0)
            return false;

        // Digits always win, regardless of language ("Chapter 12.").
        if (TryParseDigits(tokens[0], out number))
            return true;

        var parser = Parsers.GetValueOrDefault(language, Fallback);
        return parser.TryParse(tokens, out number, out _);
    }

    /// <summary>
    /// Tries to extract a number from the end of <paramref name="text"/>, which is the
    /// transcribed text immediately preceding the chapter phrase — the "Erstes Kapitel" /
    /// "Birinci Bölüm" / "2. Kapitel" announcement order. The number must end exactly at
    /// the phrase, so a number that merely occurs earlier in the sentence does not count.
    /// </summary>
    /// <param name="text">Text preceding the matched chapter phrase.</param>
    /// <param name="language">Two-letter language code steering number-word parsing.</param>
    /// <param name="number">Receives the extracted number on success.</param>
    /// <returns>True when a number could be extracted.</returns>
    public static bool TryExtractNumberBefore(string text, string language, out int number)
    {
        number = 0;
        var tokens = TokenizeTail(text);
        if (tokens.Count == 0)
            return false;

        // Digits directly before the phrase: "2. Kapitel", "3rd chapter".
        if (TryParseDigits(tokens[^1], out number))
            return true;

        // Try every suffix of the token window; accept only a parse that consumes
        // everything up to the phrase ("sagte drei. Kapitel" must not yield 3).
        var parser = Parsers.GetValueOrDefault(language, Fallback);
        for (var start = 0; start < tokens.Count; start++)
        {
            var slice = tokens.GetRange(start, tokens.Count - start);
            if (parser.TryParse(slice, out number, out var consumed)
                && start + consumed == tokens.Count)
                return true;
        }

        number = 0;
        return false;
    }

    /// <summary>Parses a token that is a plain digit number or a digit ordinal ("2nd", "2e").</summary>
    private static bool TryParseDigits(string token, out int number)
    {
        if (int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out number))
            return true;
        var m = DigitOrdinalRegex.Match(token);
        if (m.Success)
        {
            number = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            return true;
        }
        number = 0;
        return false;
    }

    /// <summary>
    /// Splits text into words, stripping surrounding punctuation. Only the first few tokens
    /// are relevant, so tokenization stops after five words.
    /// </summary>
    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        foreach (var raw in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var t = TrimPunctuation(raw);
            if (t.Length > 0)
                tokens.Add(t);
            if (tokens.Count >= 5)
                break;
        }
        return tokens;
    }

    /// <summary>Splits text into words like <see cref="Tokenize"/>, keeping the LAST five.</summary>
    private static List<string> TokenizeTail(string text)
    {
        var tokens = new List<string>();
        foreach (var raw in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var t = TrimPunctuation(raw);
            if (t.Length > 0)
                tokens.Add(t);
            if (tokens.Count > 5)
                tokens.RemoveAt(0);
        }
        return tokens;
    }

    /// <summary>Strips the punctuation Whisper attaches to words.</summary>
    private static string TrimPunctuation(string raw) =>
        raw.Trim('.', ',', ':', ';', '!', '?', '"', '\'', '(', ')', '…', '„', '“', '”');
}
