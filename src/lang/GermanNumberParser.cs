// Chapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

namespace Chapterize.Lang;

/// <summary>
/// Parses German spoken numbers 0-999, which are written as a single compound word:
/// "sieben", "einundzwanzig", "hundertdreiundvierzig", "zweihundertfünf". Both umlaut
/// spellings and their ue/oe/ss transliterations are accepted, since Whisper may emit
/// either. Ordinals in all declensions are understood too ("Erstes Kapitel",
/// "dreiundzwanzigste"), since German chapters are often announced ordinal-first.
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
    public bool TryParse(IReadOnlyList<string> tokens, out int number, out int consumed)
    {
        // German numbers are one compound word; only the first token matters.
        number = 0;
        consumed = 0;
        if (tokens.Count == 0)
            return false;
        var s = tokens[0].ToLowerInvariant();

        if (TryParseCardinal(s, out number) || TryParseOrdinal(s, out number))
        {
            consumed = 1;
            return true;
        }
        return false;
    }

    /// <summary>Parses a single cardinal compound word ("dreiundvierzig", "zweihundertfünf").</summary>
    private static bool TryParseCardinal(string s, out int number)
    {
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

    /// <summary>
    /// Parses an ordinal in any declension ("erste", "erstes", "dreiundzwanzigster",
    /// "hundertdritte") by stripping the declension ending, undoing the irregular ordinal
    /// stems (erst-, dritt-, siebt-) and reducing the regular -t/-st suffix back to the
    /// cardinal, which is then parsed normally.
    /// </summary>
    private static bool TryParseOrdinal(string s, out int number)
    {
        number = 0;

        // Strip the declension: "erster/erstes/ersten/erstem" -> "erste" -> "erst".
        if (s.Length > 2 && s[^1] is 'r' or 's' or 'n' or 'm' && s[^2] == 'e')
            s = s[..^1];
        if (!s.EndsWith('e'))
            return false;
        var stem = s[..^1];

        // Irregular ordinal stems map onto their cardinal directly.
        if (stem.EndsWith("erst", StringComparison.Ordinal))
            return TryParseCardinal(stem[..^4] + "eins", out number);
        if (stem.EndsWith("dritt", StringComparison.Ordinal))
            return TryParseCardinal(stem[..^5] + "drei", out number);
        if (stem.EndsWith("siebent", StringComparison.Ordinal))
            return TryParseCardinal(stem[..^7] + "sieben", out number);
        if (stem.EndsWith("siebt", StringComparison.Ordinal))
            return TryParseCardinal(stem[..^5] + "sieben", out number);

        // Regular formation: "achte" -> "acht", "vierte" -> "viert" -> "vier",
        // "zwanzigste" -> "zwanzigst" -> "zwanzig".
        if (TryParseCardinal(stem, out number) && number != 1) // "eine" must not become 1st
            return true;
        if (stem.EndsWith('t') && TryParseCardinal(stem[..^1], out number))
            return true;
        if (stem.EndsWith("st", StringComparison.Ordinal) && TryParseCardinal(stem[..^2], out number))
            return true;

        number = 0;
        return false;
    }
}
