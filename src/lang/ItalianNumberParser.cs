// Chapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

namespace Chapterize.Lang;

/// <summary>
/// Parses Italian spoken numbers 0-999, which are written as a single compound word:
/// "sette", "ventuno", "trecentoventidue", "centottantotto". The mandatory vowel elision
/// of the tens ("ventuno", "ventotto") and the optional elision after the hundreds
/// ("centotto"/"centootto") are both handled, as is the accent on "ventitré". Ordinals
/// are understood too — the irregular 1st-10th ("capitolo primo") and every regular
/// "-esimo" form ("ventunesimo", "centesimo"), since Italian chapters are customarily
/// announced that way.
/// </summary>
public sealed class ItalianNumberParser : INumberWordParser
{
    /// <inheritdoc/>
    public string LanguageCode => "it";

    /// <summary>Simple number words 0-19, keyed in normalized (accent-free) form.</summary>
    private static readonly Dictionary<string, int> Simple = new()
    {
        ["zero"] = 0, ["uno"] = 1, ["un"] = 1, ["una"] = 1, ["due"] = 2, ["tre"] = 3,
        ["quattro"] = 4, ["cinque"] = 5, ["sei"] = 6, ["sette"] = 7, ["otto"] = 8,
        ["nove"] = 9, ["dieci"] = 10, ["undici"] = 11, ["dodici"] = 12, ["tredici"] = 13,
        ["quattordici"] = 14, ["quindici"] = 15, ["sedici"] = 16, ["diciassette"] = 17,
        ["diciotto"] = 18, ["diciannove"] = 19,
    };

    /// <summary>Tens words 20-90.</summary>
    private static readonly (string Word, int Value)[] Tens =
    [
        ("venti", 20), ("trenta", 30), ("quaranta", 40), ("cinquanta", 50),
        ("sessanta", 60), ("settanta", 70), ("ottanta", 80), ("novanta", 90),
    ];

    /// <summary>Unit words that attach to a tens word without elision (2-7, 9).</summary>
    private static readonly Dictionary<string, int> UnitsForCompound = new()
    {
        ["due"] = 2, ["tre"] = 3, ["quattro"] = 4, ["cinque"] = 5,
        ["sei"] = 6, ["sette"] = 7, ["nove"] = 9,
    };

    /// <summary>Hundreds words 100-900.</summary>
    private static readonly (string Word, int Value)[] Hundreds =
    [
        ("cento", 100), ("duecento", 200), ("trecento", 300), ("quattrocento", 400),
        ("cinquecento", 500), ("seicento", 600), ("settecento", 700),
        ("ottocento", 800), ("novecento", 900),
    ];

    /// <summary>Irregular ordinals 1st-10th, masculine and feminine ("capitolo primo").</summary>
    private static readonly Dictionary<string, int> Ordinals = new()
    {
        ["primo"] = 1, ["prima"] = 1, ["secondo"] = 2, ["seconda"] = 2,
        ["terzo"] = 3, ["terza"] = 3, ["quarto"] = 4, ["quarta"] = 4,
        ["quinto"] = 5, ["quinta"] = 5, ["sesto"] = 6, ["sesta"] = 6,
        ["settimo"] = 7, ["settima"] = 7, ["ottavo"] = 8, ["ottava"] = 8,
        ["nono"] = 9, ["nona"] = 9, ["decimo"] = 10, ["decima"] = 10,
    };

    /// <inheritdoc/>
    public bool TryParse(IReadOnlyList<string> tokens, out int number, out int consumed)
    {
        // Italian numbers are one compound word; only the first token matters.
        number = 0;
        consumed = 0;
        if (tokens.Count == 0)
            return false;
        var s = tokens[0].ToLowerInvariant().Replace('é', 'e').Replace('è', 'e');

        if (Ordinals.TryGetValue(s, out number)
            || TryParseCardinal(s, out number)
            || TryParseRegularOrdinal(s, out number))
        {
            consumed = 1;
            return true;
        }
        return false;
    }

    /// <summary>Parses a single cardinal compound word ("trecentoventidue", "centottanta").</summary>
    private static bool TryParseCardinal(string s, out int number)
    {
        if (TrySub100(s, out number))
            return true;

        foreach (var (word, value) in Hundreds)
        {
            if (s.StartsWith(word, StringComparison.Ordinal))
            {
                var rest = s[word.Length..];
                if (rest.Length == 0)
                {
                    number = value;
                    return true;
                }
                if (TrySub100(rest, out var r))
                {
                    number = value + r;
                    return true;
                }
            }
            // Optional elision of the final "o" before a vowel: "centotto" (108),
            // "duecentuno" (201), "centottanta" (180).
            var elided = word[..^1];
            if (s.StartsWith(elided, StringComparison.Ordinal))
            {
                var rest = s[elided.Length..];
                if ((rest.StartsWith('o') || rest.StartsWith('u')) && TrySub100(rest, out var r))
                {
                    number = value + r;
                    return true;
                }
            }
        }

        number = 0;
        return false;
    }

    /// <summary>
    /// Parses a regular "-esimo/-esima" ordinal ("undicesimo", "ventunesimo", "centesimo")
    /// by stripping the suffix and restoring the final vowel the formation elides, then
    /// parsing the resulting cardinal normally.
    /// </summary>
    private static bool TryParseRegularOrdinal(string s, out int number)
    {
        number = 0;
        if (s.Length <= 5 || !(s.EndsWith("esimo", StringComparison.Ordinal)
                               || s.EndsWith("esima", StringComparison.Ordinal)))
            return false;
        var stem = s[..^5];

        // "trentatreesimo"/"ventiseiesimo" keep the cardinal intact; otherwise the elided
        // final vowel must be restored ("ventun-" -> "ventuno", "ventes-" ... "vent-" -> "venti").
        foreach (var candidate in new[] { stem, stem + "o", stem + "i", stem + "e", stem + "a" })
        {
            if (TryParseCardinal(candidate, out number))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Parses the 0-99 part of an Italian number, including the elided compounds
    /// "ventuno"/"ventotto" and the plain ones like "ventidue".
    /// </summary>
    /// <param name="s">Normalized lowercase word (or word remainder).</param>
    /// <param name="number">Receives the parsed value on success.</param>
    private static bool TrySub100(string s, out int number)
    {
        if (Simple.TryGetValue(s, out number))
            return true;

        foreach (var (word, value) in Tens)
        {
            if (s == word)
            {
                number = value;
                return true;
            }
            if (s.StartsWith(word, StringComparison.Ordinal)
                && UnitsForCompound.TryGetValue(s[word.Length..], out var u))
            {
                number = value + u;
                return true;
            }
            // Mandatory elision before "uno" and "otto": "ventuno" (21), "ottantotto" (88).
            var elided = word[..^1];
            if (s == elided + "uno")
            {
                number = value + 1;
                return true;
            }
            if (s == elided + "otto")
            {
                number = value + 8;
                return true;
            }
        }

        number = 0;
        return false;
    }
}
