// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

namespace ABChapterize.Language.Parsers;

/// <summary>
/// Parses Spanish spoken numbers 0-999: "siete", "veintiuno", "treinta y uno",
/// "ciento uno", "novecientos noventa y nueve". Accented and unaccented spellings are
/// both accepted, and so are ordinals ("capítulo primero", "capítulo vigésimo primero"),
/// since Spanish chapters are customarily announced that way.
/// </summary>
/// <remarks>
/// Spanish ordinals are their own lexemes rather than a suffix on the cardinal, so unlike
/// Italian they cannot be derived - "vigésimo" has nothing in common with "veinte". They
/// are therefore listed, which bounds the range: the scale words stop at "centésimo", so
/// ordinals run 1-199 while cardinals run 0-999. The multiplied hundreds ordinals
/// ("ducentésimo" and up) are left out as unheard-of in a chapter announcement; a digit
/// ordinal ("200.º") still works at any value.
/// </remarks>
public sealed class SpanishNumberParser : INumberWordParser
{
    /// <inheritdoc/>
    public string LanguageCode => "es";

    /// <inheritdoc/>
    public string DigitOrdinalSuffixPattern => "º|ª";

    /// <summary>Number words 0-29 (fused forms included) plus the tens 30-90.</summary>
    private static readonly Dictionary<string, int> Small = new()
    {
        ["cero"] = 0, ["uno"] = 1, ["un"] = 1, ["una"] = 1, ["dos"] = 2, ["tres"] = 3,
        ["cuatro"] = 4, ["cinco"] = 5, ["seis"] = 6, ["siete"] = 7, ["ocho"] = 8,
        ["nueve"] = 9, ["diez"] = 10, ["once"] = 11, ["doce"] = 12, ["trece"] = 13,
        ["catorce"] = 14, ["quince"] = 15,
        ["dieciséis"] = 16, ["dieciseis"] = 16, ["diecisiete"] = 17,
        ["dieciocho"] = 18, ["diecinueve"] = 19,
        ["veinte"] = 20, ["veintiuno"] = 21, ["veintiún"] = 21, ["veintiun"] = 21,
        ["veintiuna"] = 21, ["veintidós"] = 22, ["veintidos"] = 22, ["veintitrés"] = 23,
        ["veintitres"] = 23, ["veinticuatro"] = 24, ["veinticinco"] = 25,
        ["veintiséis"] = 26, ["veintiseis"] = 26, ["veintisiete"] = 27,
        ["veintiocho"] = 28, ["veintinueve"] = 29,
        ["treinta"] = 30, ["cuarenta"] = 40, ["cincuenta"] = 50, ["sesenta"] = 60,
        ["setenta"] = 70, ["ochenta"] = 80, ["noventa"] = 90,
    };

    /// <summary>Hundreds words 100-900, masculine and feminine forms.</summary>
    private static readonly Dictionary<string, int> Hundreds = new()
    {
        ["cien"] = 100, ["ciento"] = 100,
        ["doscientos"] = 200, ["doscientas"] = 200,
        ["trescientos"] = 300, ["trescientas"] = 300,
        ["cuatrocientos"] = 400, ["cuatrocientas"] = 400,
        ["quinientos"] = 500, ["quinientas"] = 500,
        ["seiscientos"] = 600, ["seiscientas"] = 600,
        ["setecientos"] = 700, ["setecientas"] = 700,
        ["ochocientos"] = 800, ["ochocientas"] = 800,
        ["novecientos"] = 900, ["novecientas"] = 900,
    };

    /// <summary>
    /// Ordinal units 1st-9th, in every form a chapter announcement uses: masculine, feminine,
    /// and the apocopated "primer"/"tercer" that stand before a masculine noun ("primer
    /// capítulo"). Keys are accent-free; <see cref="Normalize"/> puts the input in that form.
    /// </summary>
    private static readonly Dictionary<string, int> OrdinalUnits = new()
    {
        ["primero"] = 1, ["primer"] = 1, ["primera"] = 1,
        ["segundo"] = 2, ["segunda"] = 2,
        ["tercero"] = 3, ["tercer"] = 3, ["tercera"] = 3,
        ["cuarto"] = 4, ["cuarta"] = 4, ["quinto"] = 5, ["quinta"] = 5,
        ["sexto"] = 6, ["sexta"] = 6, ["septimo"] = 7, ["septima"] = 7,
        ["octavo"] = 8, ["octava"] = 8,
        // "nono" is the older form that survives inside "decimonono" (19th).
        ["noveno"] = 9, ["novena"] = 9, ["nono"] = 9, ["nona"] = 9,
    };

    /// <summary>
    /// Ordinal scale words 10th-100th, as stems without their gender vowel, in descending order
    /// so that a run like "centésimo vigésimo primero" (121st) can require each rank to be
    /// smaller than the last. Accent-free, matching <see cref="Normalize"/>; the fused spellings
    /// drop the accent anyway ("vigésimo" but "vigesimoprimero").
    /// </summary>
    private static readonly (string Stem, int Value)[] OrdinalScales =
    [
        ("centesim", 100), ("nonagesim", 90), ("octogesim", 80), ("septuagesim", 70),
        ("sexagesim", 60), ("quincuagesim", 50), ("cuadragesim", 40), ("trigesim", 30),
        ("vigesim", 20), ("decim", 10),
    ];

    /// <summary>The two ordinals with no compositional spelling of their own; 11th and 12th are
    /// also sayable as "décimo primero"/"decimoprimero", which the scale rules already cover.</summary>
    private static readonly Dictionary<string, int> OrdinalIrregulars = new()
    {
        ["undecimo"] = 11, ["undecima"] = 11, ["duodecimo"] = 12, ["duodecima"] = 12,
    };

    /// <summary>The accents <see cref="Normalize"/> folds away, so the pattern admits the spelling a
    /// transcript actually carries as well as the accent-free one the ordinal tables are keyed
    /// with.</summary>
    private const string Accents = "aá;eé;ií;oó;uú;";

    /// <summary>
    /// Every token a Spanish number may consist of. The ordinal scales are a sub-pattern rather
    /// than a word list because they fuse with the unit ("vigesimoprimero", "decimoctavo"), which
    /// as literals would be one alternative per stem-and-unit pair.
    /// </summary>
    /// <inheritdoc/>
    public string NumberWordPattern { get; } = NumberWordPatterns.Run(NumberWordPatterns.AnyOf(
        NumberWordPatterns.Alt(OrdinalScales.Select(s => s.Stem), Accents) +
            NumberWordPatterns.AnyOf(
                "[oa]" + NumberWordPatterns.Alt(OrdinalUnits.Keys, Accents) + "?",
                NumberWordPatterns.Alt(OrdinalUnits.Keys, Accents)),
        NumberWordPatterns.Alt(
            Small.Keys.Concat(Hundreds.Keys).Concat(OrdinalUnits.Keys)
                .Concat(OrdinalIrregulars.Keys).Append("y"),
            Accents)));

    /// <inheritdoc/>
    public bool TryParse(IReadOnlyList<string> tokens, out int number, out int consumed)
    {
        // Ordinals first: no ordinal word is also a cardinal, so this cannot shadow anything.
        if (TryParseOrdinal(tokens, out number, out consumed))
            return true;

        number = 0;
        consumed = 0;
        var total = 0;
        var any = false;
        var stage = 0; // 0 = expect hundreds/anything, 1 = hundreds seen, 2 = tens seen, 3 = done

        for (var i = 0; i < tokens.Count; i++)
        {
            var part = tokens[i].ToLowerInvariant();

            if (part == "y" && stage == 2)
            {
                // "treinta y uno" - the connector between tens and unit; it only counts
                // as consumed when a unit actually follows.
                continue;
            }
            if (stage == 0 && Hundreds.TryGetValue(part, out var h))
            {
                total = h;
                any = true;
                stage = 1;
                consumed = i + 1;
                continue;
            }
            if (stage <= 1 && Small.TryGetValue(part, out var v))
            {
                total += v;
                any = true;
                stage = v is >= 30 and <= 90 ? 2 : 3; // only plain tens may take "y <unit>"
                consumed = i + 1;
                continue;
            }
            if (stage == 2 && Small.TryGetValue(part, out var u) && u is >= 1 and <= 9)
            {
                total += u;
                stage = 3;
                consumed = i + 1;
                continue;
            }
            break; // first non-fitting word ends the number
        }

        if (!any || total > 999)
            return false;
        number = total;
        return true;
    }

    /// <summary>
    /// Parses an ordinal run: at most one word per rank, strictly descending
    /// ("centésimo vigésimo primero" = 121st), where the tens and the unit may also be fused
    /// into one word ("vigesimoprimero", "decimoséptimo"). Requiring each rank to be smaller
    /// than the last is what keeps a nonsense sequence like "vigésimo trigésimo" from adding up.
    /// </summary>
    /// <param name="tokens">Words at the position the caller is reading from.</param>
    /// <param name="number">Receives the ordinal's value on success.</param>
    /// <param name="consumed">Receives how many tokens the ordinal occupied.</param>
    private static bool TryParseOrdinal(IReadOnlyList<string> tokens, out int number, out int consumed)
    {
        number = 0;
        consumed = 0;
        var total = 0;
        var ceiling = int.MaxValue; // the rank of the previous word; the next must be below it

        for (var i = 0; i < tokens.Count; i++)
        {
            var s = Normalize(tokens[i]);

            if (ceiling == int.MaxValue && OrdinalIrregulars.TryGetValue(s, out var irregular))
            {
                number = irregular;
                consumed = i + 1;
                return true;
            }
            if (TryScale(s, ceiling, out var scale, out var fusedUnit))
            {
                total += scale + fusedUnit;
                consumed = i + 1;
                if (fusedUnit != 0)
                    break; // a fused word carries its own unit, so the run ends with it
                ceiling = scale;
                continue;
            }
            if (total != 0 && OrdinalUnits.TryGetValue(s, out var unit))
            {
                total += unit;
                consumed = i + 1;
            }
            else if (total == 0 && i == 0 && OrdinalUnits.TryGetValue(s, out var lone))
            {
                total = lone;
                consumed = 1;
            }
            break; // a unit ends the run, and so does anything unrecognized
        }

        if (total == 0)
            return false;
        number = total;
        return true;
    }

    /// <summary>
    /// Matches one scale word below <paramref name="ceiling"/>, either bare ("vigésimo") or with
    /// a unit fused onto it ("vigesimoprimero"). The fused form appends the gender vowel of the
    /// scale, which elides against a unit that starts with the same vowel - hence
    /// "decimoctavo" for "decimo" + "octavo".
    /// </summary>
    /// <param name="s">One normalized word.</param>
    /// <param name="ceiling">The previous rank's value; the match must come in below it.</param>
    /// <param name="scale">Receives the scale's value on success.</param>
    /// <param name="fusedUnit">Receives the fused unit's value, or 0 when the word was bare.</param>
    private static bool TryScale(string s, int ceiling, out int scale, out int fusedUnit)
    {
        scale = 0;
        fusedUnit = 0;
        foreach (var (stem, value) in OrdinalScales)
        {
            if (value >= ceiling || !s.StartsWith(stem, StringComparison.Ordinal))
                continue;
            var rest = s[stem.Length..];
            if (rest is "o" or "a")
            {
                scale = value;
                return true;
            }
            // Either the vowel survives ("decimo|septimo") or it elided into the unit
            // ("decim|octavo"); both leave the unit as the whole remainder.
            var unitWord = rest.Length > 1 && (rest[0] == 'o' || rest[0] == 'a') ? rest[1..] : rest;
            if (OrdinalUnits.TryGetValue(unitWord, out var unit)
                || OrdinalUnits.TryGetValue(rest, out unit))
            {
                scale = value;
                fusedUnit = unit;
                return true;
            }
        }
        return false;
    }

    /// <summary>Lowercases and strips the acute accents the ordinal tables are keyed without,
    /// so that "vigésimo" and the accentless "vigesimo" of the fused forms share one entry.</summary>
    /// <param name="token">The word as Whisper transcribed it.</param>
    private static string Normalize(string token) => token.ToLowerInvariant()
        .Replace('á', 'a').Replace('é', 'e').Replace('í', 'i').Replace('ó', 'o').Replace('ú', 'u');
}
