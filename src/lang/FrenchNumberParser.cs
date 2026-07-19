// Chapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

namespace Chapterize.Lang;

/// <summary>
/// Parses French spoken numbers 0-999: "sept", "vingt et un", "soixante-dix",
/// "quatre-vingt-dix-neuf", "deux cent trente-quatre". Hyphenated and space-separated
/// transcriptions work equally. Ordinals are understood too — "chapitre premier" as well
/// as every regular "-ième" form ("vingt et unième", "quatre-vingtième", "centième"),
/// since French chapters may be announced either way.
/// </summary>
public sealed class FrenchNumberParser : INumberWordParser
{
    /// <inheritdoc/>
    public string LanguageCode => "fr";

    /// <inheritdoc/>
    public string DigitOrdinalSuffixPattern => "ième|ieme|ème|eme|er|re|e";

    /// <summary>Directly addable number words (units, teens, tens up to 60).</summary>
    private static readonly Dictionary<string, int> Values = new()
    {
        ["zéro"] = 0, ["zero"] = 0, ["un"] = 1, ["une"] = 1, ["deux"] = 2, ["trois"] = 3,
        ["quatre"] = 4, ["cinq"] = 5, ["six"] = 6, ["sept"] = 7, ["huit"] = 8,
        ["neuf"] = 9, ["dix"] = 10, ["onze"] = 11, ["douze"] = 12, ["treize"] = 13,
        ["quatorze"] = 14, ["quinze"] = 15, ["seize"] = 16, ["trente"] = 30,
        ["quarante"] = 40, ["cinquante"] = 50, ["soixante"] = 60,
    };

    /// <summary>Irregular ordinals ("chapitre premier"), with and without accents.</summary>
    private static readonly Dictionary<string, int> Ordinals = new()
    {
        ["premier"] = 1, ["première"] = 1, ["premiere"] = 1,
        ["second"] = 2, ["seconde"] = 2,
    };

    /// <inheritdoc/>
    public bool TryParse(IReadOnlyList<string> tokens, out int number, out int consumed)
    {
        number = 0;
        consumed = 0;
        var total = 0;
        var last = -1;    // value of the previous numeric word, for vingt/cent multiplication
        var any = false;
        var done = false; // an ordinal part ends the number

        for (var i = 0; i < tokens.Count && !done; i++)
        {
            // "quatre-vingt-dix-neuf" arrives as one token; process its parts in order.
            var parts = tokens[i].ToLowerInvariant().Split('-', StringSplitOptions.RemoveEmptyEntries);
            var numeric = false; // did this token contribute to the value?
            var ok = parts.Length > 0;
            foreach (var raw in parts)
            {
                var part = raw;

                // A regular "-ième" ordinal is its cardinal plus the suffix; reduce it and
                // let the normal cardinal logic below handle it, then end the number. This
                // covers "vingt et unième" (21st) and "quatre-vingtième" (80th) alike.
                if (TryReduceOrdinal(part, out var cardinalPart))
                {
                    part = cardinalPart;
                    done = true;
                }

                if (part is "et" && any && !done)
                {
                    // "vingt et un", "soixante et onze" - only valid inside a number.
                }
                else if (part is "vingt" or "vingts")
                {
                    // "quatre-vingt(s)" is 4 x 20 = 80; any other vingt just adds 20.
                    if (last == 4)
                        total += 76; // the 4 is already in the total: 4 + 76 = 80
                    else
                        total += 20;
                    last = 20;
                    numeric = any = true;
                }
                else if (part is "cent" or "cents")
                {
                    // "deux cents" multiplies the preceding unit; bare "cent" is 100.
                    if (last is >= 2 and <= 9)
                        total += last * 100 - last; // replace the unit by unit x 100
                    else
                        total += 100;
                    last = 100;
                    numeric = any = true;
                }
                else if (Values.TryGetValue(part, out var v))
                {
                    total += v;
                    last = v;
                    numeric = any = true;
                }
                else if (!any && Ordinals.TryGetValue(part, out var o))
                {
                    total = o;
                    numeric = any = done = true;
                }
                else
                {
                    ok = false;
                    break;
                }
                if (done)
                    break;
            }
            if (!ok)
                break; // first non-number word ends the number
            if (numeric)
                consumed = i + 1; // connector-only tokens don't extend the number by themselves
        }

        if (!any || total > 999)
            return false;
        number = total;
        return true;
    }

    /// <summary>
    /// Reduces a regular "-ième" ordinal part to its cardinal part: "unième" -> "un",
    /// "quatrième" -> "quatre", "cinquième" -> "cinq", "neuvième" -> "neuf",
    /// "vingtième" -> "vingt", "centième" -> "cent". Accented and unaccented suffix
    /// spellings both work.
    /// </summary>
    /// <param name="part">Lowercased word part, possibly an ordinal.</param>
    /// <param name="cardinal">Receives the cardinal part on success.</param>
    private static bool TryReduceOrdinal(string part, out string cardinal)
    {
        cardinal = "";
        var normalized = part.Replace('è', 'e').Replace('é', 'e');
        if (!normalized.EndsWith("ieme", StringComparison.Ordinal))
            return false;
        var stem = normalized[..^4];
        if (stem.Length == 0)
            return false;

        // The cardinal is the stem itself ("sept-"), the stem plus a dropped final "e"
        // ("quatr-" -> "quatre"), or one of the two irregular stems cinqu-/neuv-.
        foreach (var candidate in new[] { stem, stem + "e" })
        {
            if (IsCardinalPart(candidate))
            {
                cardinal = candidate;
                return true;
            }
        }
        if (stem == "cinqu") { cardinal = "cinq"; return true; }
        if (stem == "neuv") { cardinal = "neuf"; return true; }
        return false;
    }

    /// <summary>Tells whether a word part is a known cardinal building block.</summary>
    private static bool IsCardinalPart(string part) =>
        part is "vingt" or "cent" || Values.ContainsKey(part);
}
