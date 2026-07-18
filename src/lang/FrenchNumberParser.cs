namespace Chapterize.Lang;

/// <summary>
/// Parses French spoken numbers 0-999: "sept", "vingt et un", "soixante-dix",
/// "quatre-vingt-dix-neuf", "deux cent trente-quatre". Hyphenated and space-separated
/// transcriptions work equally, and "chapitre premier" style ordinals (1st-10th) are
/// understood too, since French chapter one is customarily announced that way.
/// </summary>
public sealed class FrenchNumberParser : INumberWordParser
{
    /// <inheritdoc/>
    public string LanguageCode => "fr";

    /// <summary>Directly addable number words (units, teens, tens up to 60).</summary>
    private static readonly Dictionary<string, int> Values = new()
    {
        ["zéro"] = 0, ["zero"] = 0, ["un"] = 1, ["une"] = 1, ["deux"] = 2, ["trois"] = 3,
        ["quatre"] = 4, ["cinq"] = 5, ["six"] = 6, ["sept"] = 7, ["huit"] = 8,
        ["neuf"] = 9, ["dix"] = 10, ["onze"] = 11, ["douze"] = 12, ["treize"] = 13,
        ["quatorze"] = 14, ["quinze"] = 15, ["seize"] = 16, ["trente"] = 30,
        ["quarante"] = 40, ["cinquante"] = 50, ["soixante"] = 60,
    };

    /// <summary>Ordinals 1st-10th, with and without accents ("chapitre premier").</summary>
    private static readonly Dictionary<string, int> Ordinals = new()
    {
        ["premier"] = 1, ["première"] = 1, ["premiere"] = 1,
        ["deuxième"] = 2, ["deuxieme"] = 2, ["second"] = 2, ["seconde"] = 2,
        ["troisième"] = 3, ["troisieme"] = 3, ["quatrième"] = 4, ["quatrieme"] = 4,
        ["cinquième"] = 5, ["cinquieme"] = 5, ["sixième"] = 6, ["sixieme"] = 6,
        ["septième"] = 7, ["septieme"] = 7, ["huitième"] = 8, ["huitieme"] = 8,
        ["neuvième"] = 9, ["neuvieme"] = 9, ["dixième"] = 10, ["dixieme"] = 10,
    };

    /// <inheritdoc/>
    public bool TryParse(IReadOnlyList<string> tokens, out int number)
    {
        number = 0;
        var total = 0;
        var last = -1;    // value of the previous numeric word, for vingt/cent multiplication
        var any = false;

        foreach (var token in tokens)
        {
            // "quatre-vingt-dix-neuf" arrives as one token; process its parts in order.
            var parts = token.ToLowerInvariant().Split('-', StringSplitOptions.RemoveEmptyEntries);
            var consumed = false;
            foreach (var part in parts)
            {
                if (part is "et" && any)
                {
                    // "vingt et un", "soixante et onze" - only valid inside a number.
                    consumed = true;
                }
                else if (part is "vingt" or "vingts")
                {
                    // "quatre-vingt(s)" is 4 x 20 = 80; any other vingt just adds 20.
                    if (last == 4)
                        total += 76; // the 4 is already in the total: 4 + 76 = 80
                    else
                        total += 20;
                    last = 20;
                    consumed = any = true;
                }
                else if (part is "cent" or "cents")
                {
                    // "deux cents" multiplies the preceding unit; bare "cent" is 100.
                    if (last is >= 2 and <= 9)
                        total += last * 100 - last; // replace the unit by unit x 100
                    else
                        total += 100;
                    last = 100;
                    consumed = any = true;
                }
                else if (Values.TryGetValue(part, out var v))
                {
                    total += v;
                    last = v;
                    consumed = any = true;
                }
                else if (!any && Ordinals.TryGetValue(part, out var o))
                {
                    total = o;
                    consumed = any = true;
                }
                else
                {
                    consumed = false;
                    break;
                }
            }
            if (!consumed)
                break; // first non-number word ends the number
        }

        if (!any || total > 999)
            return false;
        number = total;
        return true;
    }
}
