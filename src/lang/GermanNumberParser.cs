namespace Chapterize.Lang;

/// <summary>
/// Parses German spoken numbers 0-999, which are written as a single compound word:
/// "sieben", "einundzwanzig", "hundertdreiundvierzig", "zweihundertfünf". Both umlaut
/// spellings and their ue/oe/ss transliterations are accepted, since Whisper may emit either.
/// </summary>
public sealed class GermanNumberParser : INumberWordParser
{
    /// <inheritdoc/>
    public string LanguageCode => "de";

    /// <summary>Simple (non-compound) number words, including alternate spellings.</summary>
    private static readonly Dictionary<string, int> Simple = new()
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

    /// <summary>Unit words usable as the first part of a "&lt;unit&gt;und&lt;tens&gt;" compound.</summary>
    private static readonly (string Word, int Value)[] UnitsForCompound =
    [
        ("ein", 1), ("zwei", 2), ("drei", 3), ("vier", 4), ("fuenf", 5), ("fünf", 5),
        ("sechs", 6), ("sieben", 7), ("acht", 8), ("neun", 9),
    ];

    /// <inheritdoc/>
    public bool TryParse(IReadOnlyList<string> tokens, out int number)
    {
        // German numbers are one compound word; only the first token matters.
        number = 0;
        if (tokens.Count == 0)
            return false;
        var s = tokens[0].ToLowerInvariant();

        if (Simple.TryGetValue(s, out number))
            return true;

        var hundreds = 0;
        var idx = s.IndexOf("hundert", StringComparison.Ordinal);
        if (idx >= 0)
        {
            var prefix = s[..idx];
            if (prefix.Length == 0) hundreds = 1;
            else if (Simple.TryGetValue(prefix, out var h) && h is >= 1 and <= 9) hundreds = h;
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

        if (Simple.TryGetValue(s, out var rest) && rest < 100)
        {
            number = hundreds * 100 + rest;
            return true;
        }

        // Compound like "einundzwanzig": <unit>und<tens>.
        foreach (var (word, value) in UnitsForCompound)
        {
            if (s.StartsWith(word + "und", StringComparison.Ordinal))
            {
                var tensPart = s[(word.Length + 3)..];
                if (Simple.TryGetValue(tensPart, out var tens) && tens is >= 20 and <= 90)
                {
                    number = hundreds * 100 + value + tens;
                    return true;
                }
            }
        }

        number = 0;
        return false;
    }
}
