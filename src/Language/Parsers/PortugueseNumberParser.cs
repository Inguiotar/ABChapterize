// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

namespace ABChapterize.Language.Parsers;

/// <summary>
/// Parses Portuguese spoken numbers 0-999: "sete", "vinte e um", "cento e um",
/// "novecentos e noventa e nove". Unlike Spanish, the "e" connector links every
/// component, not just tens and units. Both European (dezasseis, dezanove) and
/// Brazilian (dezesseis, dezenove) spellings are accepted, and so are ordinals
/// ("capítulo primeiro", "capítulo vigésimo primeiro"), since Portuguese chapters are
/// customarily announced that way.
/// </summary>
/// <remarks>
/// Portuguese ordinals are their own lexemes rather than a suffix on the cardinal, so they
/// are listed, which bounds the range: the scale words stop at "centésimo", so ordinals run
/// 1-199 while cardinals run 0-999. The multiplied hundreds ordinals ("ducentésimo" and up)
/// are left out as unheard-of in a chapter announcement; a digit ordinal ("200.º") still
/// works at any value. Unlike Spanish, Portuguese does not fuse the tens and the unit into
/// one word - it is always "vigésimo primeiro", never "vigesimoprimeiro".
/// </remarks>
public sealed class PortugueseNumberParser : INumberWordParser
{
    /// <inheritdoc/>
    public string LanguageCode => "pt";

    /// <inheritdoc/>
    public string DigitOrdinalSuffixPattern => "º|ª";

    /// <summary>Number words 0-29 plus the tens 30-90.</summary>
    private static readonly Dictionary<string, int> Small = new()
    {
        ["zero"] = 0, ["um"] = 1, ["uma"] = 1, ["dois"] = 2, ["duas"] = 2, ["três"] = 3,
        ["tres"] = 3, ["quatro"] = 4, ["cinco"] = 5, ["seis"] = 6, ["sete"] = 7,
        ["oito"] = 8, ["nove"] = 9, ["dez"] = 10, ["onze"] = 11, ["doze"] = 12,
        ["treze"] = 13, ["catorze"] = 14, ["quatorze"] = 14, ["quinze"] = 15,
        ["dezasseis"] = 16, ["dezesseis"] = 16, ["dezassete"] = 17, ["dezessete"] = 17,
        ["dezoito"] = 18, ["dezanove"] = 19, ["dezenove"] = 19,
        ["vinte"] = 20, ["trinta"] = 30, ["quarenta"] = 40, ["cinquenta"] = 50,
        ["sessenta"] = 60, ["setenta"] = 70, ["oitenta"] = 80, ["noventa"] = 90,
    };

    /// <summary>Hundreds words 100-900, masculine and feminine forms.</summary>
    private static readonly Dictionary<string, int> Hundreds = new()
    {
        ["cem"] = 100, ["cento"] = 100,
        ["duzentos"] = 200, ["duzentas"] = 200,
        ["trezentos"] = 300, ["trezentas"] = 300,
        ["quatrocentos"] = 400, ["quatrocentas"] = 400,
        ["quinhentos"] = 500, ["quinhentas"] = 500,
        ["seiscentos"] = 600, ["seiscentas"] = 600,
        ["setecentos"] = 700, ["setecentas"] = 700,
        ["oitocentos"] = 800, ["oitocentas"] = 800,
        ["novecentos"] = 900, ["novecentas"] = 900,
    };

    /// <summary>Ordinal units 1st-9th, masculine and feminine. Keys are accent-free;
    /// <see cref="Normalize"/> puts the input in that form.</summary>
    private static readonly Dictionary<string, int> OrdinalUnits = new()
    {
        ["primeiro"] = 1, ["primeira"] = 1,
        ["segundo"] = 2, ["segunda"] = 2,
        ["terceiro"] = 3, ["terceira"] = 3,
        ["quarto"] = 4, ["quarta"] = 4, ["quinto"] = 5, ["quinta"] = 5,
        ["sexto"] = 6, ["sexta"] = 6, ["setimo"] = 7, ["setima"] = 7,
        ["oitavo"] = 8, ["oitava"] = 8, ["nono"] = 9, ["nona"] = 9,
    };

    /// <summary>
    /// Ordinal scale words 10th-100th, in descending order so that a run like
    /// "centésimo vigésimo primeiro" (121st) can require each rank to be smaller than the last.
    /// Several carry a European/Brazilian doublet, which is why one value has more than one key.
    /// </summary>
    private static readonly (string Word, int Value)[] OrdinalScales =
    [
        ("centesim", 100), ("nonagesim", 90),
        ("octogesim", 80), ("oitogesim", 80),
        ("septuagesim", 70), ("setuagesim", 70),
        ("sexagesim", 60),
        ("quinquagesim", 50), ("cinquagesim", 50),
        ("quadragesim", 40), ("trigesim", 30), ("vigesim", 20), ("decim", 10),
    ];

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

            if (part == "e" && any && stage < 3)
            {
                // "cento e vinte", "vinte e um" - the connector links every component;
                // it only counts as consumed when a real number word actually follows.
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
                stage = v is >= 20 and <= 90 ? 2 : 3; // only plain tens may take "e <unit>"
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
    /// ("centésimo vigésimo primeiro" = 121st). Requiring each rank to be smaller than the last
    /// is what keeps a nonsense sequence like "vigésimo trigésimo" from adding up.
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

        foreach (var token in tokens)
        {
            var s = Normalize(token);

            if (TryScale(s, ceiling, out var scale))
            {
                total += scale;
                ceiling = scale;
                consumed++;
                continue;
            }
            if (OrdinalUnits.TryGetValue(s, out var unit))
            {
                total += unit;
                consumed++;
            }
            break; // a unit ends the run, and so does anything unrecognized
        }

        if (total == 0)
            return false;
        number = total;
        return true;
    }

    /// <summary>Matches one scale word below <paramref name="ceiling"/>, in either gender.</summary>
    /// <param name="s">One normalized word.</param>
    /// <param name="ceiling">The previous rank's value; the match must come in below it.</param>
    /// <param name="scale">Receives the scale's value on success.</param>
    private static bool TryScale(string s, int ceiling, out int scale)
    {
        foreach (var (word, value) in OrdinalScales)
        {
            if (value < ceiling && (s == word + "o" || s == word + "a"))
            {
                scale = value;
                return true;
            }
        }
        scale = 0;
        return false;
    }

    /// <summary>Lowercases and strips the accents the ordinal tables are keyed without
    /// ("sétimo" and "setimo" are one entry).</summary>
    private static string Normalize(string token) => token.ToLowerInvariant()
        .Replace('á', 'a').Replace('â', 'a').Replace('ã', 'a').Replace('é', 'e')
        .Replace('ê', 'e').Replace('í', 'i').Replace('ó', 'o').Replace('ô', 'o')
        .Replace('õ', 'o').Replace('ú', 'u').Replace('ç', 'c');
}
