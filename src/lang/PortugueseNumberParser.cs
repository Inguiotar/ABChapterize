// Chapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

namespace Chapterize.Lang;

/// <summary>
/// Parses Portuguese spoken numbers 0-999: "sete", "vinte e um", "cento e um",
/// "novecentos e noventa e nove". Unlike Spanish, the "e" connector links every
/// component, not just tens and units. Both European (dezasseis, dezanove) and
/// Brazilian (dezesseis, dezenove) spellings are accepted, and "capítulo primeiro"
/// style ordinals (1st-10th) are understood too, since Portuguese chapter one is
/// customarily announced that way.
/// </summary>
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

    /// <summary>Ordinals 1st-10th, with and without accents ("capítulo primeiro").</summary>
    private static readonly Dictionary<string, int> Ordinals = new()
    {
        ["primeiro"] = 1, ["primeira"] = 1,
        ["segundo"] = 2, ["segunda"] = 2,
        ["terceiro"] = 3, ["terceira"] = 3,
        ["quarto"] = 4, ["quarta"] = 4, ["quinto"] = 5, ["quinta"] = 5,
        ["sexto"] = 6, ["sexta"] = 6, ["sétimo"] = 7, ["setimo"] = 7, ["sétima"] = 7,
        ["setima"] = 7, ["oitavo"] = 8, ["oitava"] = 8, ["nono"] = 9, ["nona"] = 9,
        ["décimo"] = 10, ["decimo"] = 10, ["décima"] = 10, ["decima"] = 10,
    };

    /// <inheritdoc/>
    public bool TryParse(IReadOnlyList<string> tokens, out int number, out int consumed)
    {
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
            if (!any && Ordinals.TryGetValue(part, out var o))
            {
                total = o;
                any = true;
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
}
