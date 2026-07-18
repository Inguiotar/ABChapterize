namespace Chapterize.Lang;

/// <summary>
/// Parses Spanish spoken numbers 0-999: "siete", "veintiuno", "treinta y uno",
/// "ciento uno", "novecientos noventa y nueve". Accented and unaccented spellings are
/// both accepted, and "capítulo primero" style ordinals (1st-10th) are understood too,
/// since Spanish chapter one is customarily announced that way.
/// </summary>
public sealed class SpanishNumberParser : INumberWordParser
{
    /// <inheritdoc/>
    public string LanguageCode => "es";

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

    /// <summary>Ordinals 1st-10th, with and without accents ("capítulo primero").</summary>
    private static readonly Dictionary<string, int> Ordinals = new()
    {
        ["primero"] = 1, ["primer"] = 1, ["primera"] = 1,
        ["segundo"] = 2, ["segunda"] = 2,
        ["tercero"] = 3, ["tercer"] = 3, ["tercera"] = 3,
        ["cuarto"] = 4, ["cuarta"] = 4, ["quinto"] = 5, ["quinta"] = 5,
        ["sexto"] = 6, ["sexta"] = 6, ["séptimo"] = 7, ["septimo"] = 7, ["séptima"] = 7,
        ["septima"] = 7, ["octavo"] = 8, ["octava"] = 8, ["noveno"] = 9, ["novena"] = 9,
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
