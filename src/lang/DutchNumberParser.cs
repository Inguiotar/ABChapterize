// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

namespace ABChapterize.Lang;

/// <summary>
/// Parses Dutch spoken numbers 0-999, which are written as a single compound word like
/// German: "zeven", "eenentwintig", "tweeëntwintig", "honderddrieënveertig". The trema
/// forms (ë) and accented "één" are normalized away, since Whisper may emit either.
/// Ordinals are understood too ("eerste hoofdstuk", "drieëntwintigste"), since Dutch
/// chapters are often announced ordinal-first.
/// </summary>
public sealed class DutchNumberParser : INumberWordParser
{
    /// <inheritdoc/>
    public string LanguageCode => "nl";

    /// <inheritdoc/>
    public string DigitOrdinalSuffixPattern => "ste|de";

    /// <summary>Simple (non-compound) number words, keyed in normalized (accent-free) form.</summary>
    private static readonly Dictionary<string, int> Simple = new()
    {
        ["nul"] = 0, ["een"] = 1, ["twee"] = 2, ["drie"] = 3, ["vier"] = 4,
        ["vijf"] = 5, ["zes"] = 6, ["zeven"] = 7, ["acht"] = 8, ["negen"] = 9,
        ["tien"] = 10, ["elf"] = 11, ["twaalf"] = 12, ["dertien"] = 13,
        ["veertien"] = 14, ["vijftien"] = 15, ["zestien"] = 16, ["zeventien"] = 17,
        ["achttien"] = 18, ["negentien"] = 19, ["twintig"] = 20, ["dertig"] = 30,
        ["veertig"] = 40, ["vijftig"] = 50, ["zestig"] = 60, ["zeventig"] = 70,
        ["tachtig"] = 80, ["negentig"] = 90, ["honderd"] = 100,
    };

    /// <summary>Unit words usable as the first part of an "&lt;unit&gt;en&lt;tens&gt;" compound.</summary>
    private static readonly (string Word, int Value)[] UnitsForCompound =
    [
        ("een", 1), ("twee", 2), ("drie", 3), ("vier", 4), ("vijf", 5),
        ("zes", 6), ("zeven", 7), ("acht", 8), ("negen", 9),
    ];

    /// <inheritdoc/>
    public bool TryParse(IReadOnlyList<string> tokens, out int number, out int consumed)
    {
        // Dutch numbers are one compound word; only the first token matters.
        number = 0;
        consumed = 0;
        if (tokens.Count == 0)
            return false;
        var s = Normalize(tokens[0]);

        if (TryParseCardinal(s, out number) || TryParseOrdinal(s, out number))
        {
            consumed = 1;
            return true;
        }
        return false;
    }

    /// <summary>Parses a single cardinal compound word ("drieenveertig", "honderdvijf").</summary>
    private static bool TryParseCardinal(string s, out int number)
    {
        if (Simple.TryGetValue(s, out number))
            return true;

        var hundreds = 0;
        var idx = s.IndexOf("honderd", StringComparison.Ordinal);
        if (idx >= 0)
        {
            var prefix = s[..idx];
            if (prefix.Length == 0) hundreds = 1;
            else if (Simple.TryGetValue(prefix, out var h) && h is >= 1 and <= 9) hundreds = h;
            else return false;
            s = s[(idx + "honderd".Length)..];
            if (s.StartsWith("en", StringComparison.Ordinal) // "honderdeneen" / "honderd en een"
                && !Simple.ContainsKey(s) && !StartsWithCompoundUnit(s))
                s = s[2..];
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

        // Compound like "eenentwintig": <unit>en<tens> (normalized from "<unit>ën<tens>").
        foreach (var (word, value) in UnitsForCompound)
        {
            if (s.StartsWith(word + "en", StringComparison.Ordinal))
            {
                var tensPart = s[(word.Length + 2)..];
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
    /// Parses an ordinal ("eerste", "achtste", "drieentwintigste", "honderdderde") by
    /// undoing the irregular stems (eerste-, derde-) and stripping the regular -ste/-de
    /// suffix back to the cardinal, which is then parsed normally.
    /// </summary>
    private static bool TryParseOrdinal(string s, out int number)
    {
        number = 0;

        // Irregular ordinals map onto their cardinal directly ("honderdeerste" = 101st).
        if (s.EndsWith("eerste", StringComparison.Ordinal))
            return TryParseCardinal(s[..^6] + "een", out number);
        if (s.EndsWith("derde", StringComparison.Ordinal))
            return TryParseCardinal(s[..^5] + "drie", out number);

        // Regular formation: -ste after 8 and 20+ ("achtste", "twintigste"),
        // -de otherwise ("tweede", "zevende").
        if (s.EndsWith("ste", StringComparison.Ordinal) && TryParseCardinal(s[..^3], out number))
            return true;
        if (s.EndsWith("de", StringComparison.Ordinal) && TryParseCardinal(s[..^2], out number))
            return true;

        number = 0;
        return false;
    }

    /// <summary>Lowercases and strips the diacritics Dutch numbers can contain (ë, é).</summary>
    private static string Normalize(string token) =>
        token.ToLowerInvariant().Replace('ë', 'e').Replace('é', 'e');

    /// <summary>
    /// Tells whether the remainder after "honderd" already starts with a valid
    /// "&lt;unit&gt;en&lt;tens&gt;" compound, so its leading "een" must not be eaten
    /// as a connector (e.g. "honderdeenentwintig" = 121, not 100 + "entwintig").
    /// </summary>
    private static bool StartsWithCompoundUnit(string s)
    {
        foreach (var (word, _) in UnitsForCompound)
        {
            if (!s.StartsWith(word + "en", StringComparison.Ordinal))
                continue;
            var tensPart = s[(word.Length + 2)..];
            if (Simple.TryGetValue(tensPart, out var tens) && tens is >= 20 and <= 90)
                return true;
        }
        return false;
    }
}
