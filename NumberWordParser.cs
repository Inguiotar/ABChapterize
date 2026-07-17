using System.Globalization;

namespace Chapterize;

/// <summary>
/// Extracts chapter numbers from transcribed text. Understands plain digits in any language
/// plus spoken number words in English ("twenty-one") and German ("einundzwanzig"),
/// covering the range 0-999.
/// </summary>
public static class NumberWordParser
{
    private static readonly Dictionary<string, int> EnUnits = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zero"] = 0, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4,
        ["five"] = 5, ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9,
        ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13,
        ["fourteen"] = 14, ["fifteen"] = 15, ["sixteen"] = 16, ["seventeen"] = 17,
        ["eighteen"] = 18, ["nineteen"] = 19,
    };

    private static readonly Dictionary<string, int> EnTens = new(StringComparer.OrdinalIgnoreCase)
    {
        ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40, ["fifty"] = 50,
        ["sixty"] = 60, ["seventy"] = 70, ["eighty"] = 80, ["ninety"] = 90,
    };

    private static readonly Dictionary<string, int> DeSimple = new(StringComparer.OrdinalIgnoreCase)
    {
        ["null"] = 0, ["eins"] = 1, ["ein"] = 1, ["eine"] = 1, ["zwei"] = 2, ["drei"] = 3,
        ["vier"] = 4, ["fuenf"] = 5, ["fünf"] = 5, ["sechs"] = 6, ["sieben"] = 7,
        ["acht"] = 8, ["neun"] = 9, ["zehn"] = 10, ["elf"] = 11, ["zwoelf"] = 12,
        ["zwölf"] = 12, ["dreizehn"] = 13, ["vierzehn"] = 14, ["fuenfzehn"] = 15,
        ["fünfzehn"] = 15, ["sechzehn"] = 16, ["siebzehn"] = 17, ["achtzehn"] = 18,
        ["neunzehn"] = 19, ["zwanzig"] = 20, ["dreissig"] = 30, ["dreißig"] = 30,
        ["vierzig"] = 40, ["fuenfzig"] = 50, ["fünfzig"] = 50, ["sechzig"] = 60,
        ["siebzig"] = 70, ["achtzig"] = 80, ["neunzig"] = 90, ["hundert"] = 100,
        ["einhundert"] = 100,
    };

    private static readonly (string Word, int Value)[] DeUnitsForCompound =
    [
        ("ein", 1), ("zwei", 2), ("drei", 3), ("vier", 4), ("fuenf", 5), ("fünf", 5),
        ("sechs", 6), ("sieben", 7), ("acht", 8), ("neun", 9),
    ];

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

        return language switch
        {
            "de" => TryParseGerman(tokens[0], out number),
            _ => TryParseEnglish(tokens, out number),
        };
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

    /// <summary>
    /// Parses an English spoken number from the token list, e.g.
    /// "twelve", "twenty-one", "one hundred and five". Consumes tokens greedily.
    /// </summary>
    private static bool TryParseEnglish(List<string> tokens, out int number)
    {
        number = 0;
        var current = 0;
        var any = false;

        foreach (var token in tokens)
        {
            var consumedToken = false;
            // "twenty-one" and "twenty one" are both possible transcriptions.
            foreach (var part in token.Split('-', StringSplitOptions.RemoveEmptyEntries))
            {
                if (EnUnits.TryGetValue(part, out var u)) { current += u; any = consumedToken = true; }
                else if (EnTens.TryGetValue(part, out var t)) { current += t; any = consumedToken = true; }
                else if (part.Equals("hundred", StringComparison.OrdinalIgnoreCase))
                {
                    current = (current == 0 ? 1 : current) * 100;
                    any = consumedToken = true;
                }
                else if (part.Equals("and", StringComparison.OrdinalIgnoreCase) && any)
                {
                    consumedToken = true;
                }
                else
                {
                    consumedToken = false;
                    break;
                }
            }
            if (!consumedToken)
                break; // first non-number word ends the number
        }

        number = current;
        return any;
    }

    /// <summary>
    /// Parses a German spoken number, which is written as a single compound word,
    /// e.g. "sieben", "einundzwanzig", "hundertdreiundvierzig".
    /// </summary>
    private static bool TryParseGerman(string token, out int number)
    {
        number = 0;
        var s = token.ToLowerInvariant();

        if (DeSimple.TryGetValue(s, out number))
            return true;

        var hundreds = 0;
        var idx = s.IndexOf("hundert", StringComparison.Ordinal);
        if (idx >= 0)
        {
            var prefix = s[..idx];
            if (prefix.Length == 0) hundreds = 1;
            else if (DeSimple.TryGetValue(prefix, out var h) && h is >= 1 and <= 9) hundreds = h;
            else return false;
            s = s[(idx + "hundert".Length)..];
            if (s.StartsWith("und", StringComparison.Ordinal)) // "hundertundeins"
                s = s[3..];
            if (s.Length == 0)
            {
                number = hundreds * 100;
                return true;
            }
        }

        if (DeSimple.TryGetValue(s, out var rest))
        {
            number = hundreds * 100 + rest;
            return true;
        }

        // Compound like "einundzwanzig": <unit>und<tens>.
        foreach (var (word, value) in DeUnitsForCompound)
        {
            if (s.StartsWith(word + "und", StringComparison.Ordinal))
            {
                var tensPart = s[(word.Length + 3)..];
                if (DeSimple.TryGetValue(tensPart, out var tens) && tens is >= 20 and <= 90)
                {
                    number = hundreds * 100 + value + tens;
                    return true;
                }
            }
        }

        return hundreds > 0 && s.Length == 0;
    }
}
