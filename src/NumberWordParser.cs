using System.Globalization;
using Chapterize.Lang;

namespace Chapterize;

/// <summary>
/// Extracts chapter numbers from transcribed text. Understands plain digits in any language
/// plus spoken number words (0-999) via the per-language parsers in <see cref="Lang"/>;
/// unknown language codes fall back to the English parser.
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
        }
        .ToDictionary(p => p.LanguageCode, StringComparer.OrdinalIgnoreCase);

    /// <summary>Fallback parser for language codes without a dedicated implementation.</summary>
    private static readonly INumberWordParser Fallback = Parsers["en"];

    /// <summary>Language codes with a dedicated number-word parser, for help/docs output.</summary>
    public static IEnumerable<string> SupportedLanguages => Parsers.Keys.Order();

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
        if (int.TryParse(tokens[0], NumberStyles.None, CultureInfo.InvariantCulture, out var n))
        {
            number = n;
            return true;
        }

        var parser = Parsers.GetValueOrDefault(language, Fallback);
        return parser.TryParse(tokens, out number);
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
            var t = raw.Trim('.', ',', ':', ';', '!', '?', '"', '\'', '(', ')', '…', '„', '“', '”');
            if (t.Length > 0)
                tokens.Add(t);
            if (tokens.Count >= 5)
                break;
        }
        return tokens;
    }
}
