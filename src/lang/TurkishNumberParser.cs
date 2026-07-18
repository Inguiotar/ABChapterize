namespace Chapterize.Lang;

/// <summary>
/// Parses Turkish spoken numbers 0-999, which are space-separated words in big-endian
/// order: "yedi", "yirmi bir", "iki yuz otuz dort", "dokuz yuz doksan dokuz". The Turkish
/// letters (dotless i, u/o with diaeresis, s/c with cedilla, soft g) are normalized to
/// plain ASCII, so both proper Turkish and ASCII-ified transcriptions parse - and the
/// notorious dotted/dotless-I casing rules cannot bite under invariant globalization.
/// Ordinals 1st-10th ("birinci") are understood too.
/// (This source file is deliberately ASCII-only; Turkish letters appear as \uXXXX escapes.)
/// </summary>
public sealed class TurkishNumberParser : INumberWordParser
{
    /// <inheritdoc/>
    public string LanguageCode => "tr";

    /// <summary>Directly addable words 0-10, keyed in normalized ASCII form.</summary>
    private static readonly Dictionary<string, int> Units = new()
    {
        ["sifir"] = 0, ["bir"] = 1, ["iki"] = 2, ["uc"] = 3, ["dort"] = 4,
        ["bes"] = 5, ["alti"] = 6, ["yedi"] = 7, ["sekiz"] = 8, ["dokuz"] = 9,
        ["on"] = 10,
    };

    /// <summary>Tens words 20-90, keyed in normalized ASCII form.</summary>
    private static readonly Dictionary<string, int> Tens = new()
    {
        ["yirmi"] = 20, ["otuz"] = 30, ["kirk"] = 40, ["elli"] = 50,
        ["altmis"] = 60, ["yetmis"] = 70, ["seksen"] = 80, ["doksan"] = 90,
    };

    /// <summary>Ordinals 1st-10th, keyed in normalized ASCII form ("birinci").</summary>
    private static readonly Dictionary<string, int> Ordinals = new()
    {
        ["birinci"] = 1, ["ikinci"] = 2, ["ucuncu"] = 3, ["dorduncu"] = 4,
        ["besinci"] = 5, ["altinci"] = 6, ["yedinci"] = 7, ["sekizinci"] = 8,
        ["dokuzuncu"] = 9, ["onuncu"] = 10,
    };

    /// <inheritdoc/>
    public bool TryParse(IReadOnlyList<string> tokens, out int number)
    {
        number = 0;
        var total = 0;
        var last = -1;    // value of the previous numeric word, for "yuz" multiplication
        var any = false;

        foreach (var token in tokens)
        {
            var part = Normalize(token);

            if (part == "yuz")
            {
                // "iki yuz" is 2 x 100; bare "yuz" is 100.
                if (last is >= 1 and <= 9)
                    total += last * 100 - last; // replace the unit by unit x 100
                else
                    total += 100;
                last = 100;
                any = true;
            }
            else if (Tens.TryGetValue(part, out var t))
            {
                total += t;
                last = t;
                any = true;
            }
            else if (Units.TryGetValue(part, out var u))
            {
                total += u;
                last = u;
                any = true;
            }
            else if (!any && Ordinals.TryGetValue(part, out var o))
            {
                total = o;
                any = true;
                break;
            }
            else
            {
                break; // first non-number word ends the number
            }
        }

        if (!any || total > 999)
            return false;
        number = total;
        return true;
    }

    /// <summary>
    /// Lowercases and maps the Turkish letters to plain ASCII: dotless i (U+0131) to i,
    /// u-diaeresis to u, o-diaeresis to o, s-cedilla to s, c-cedilla to c, soft g to g.
    /// A dotted capital I (U+0130) is mapped to plain i, whether lowercasing left it
    /// unchanged or turned it into "i" plus a combining dot above (U+0307).
    /// </summary>
    private static string Normalize(string token) => token.ToLowerInvariant()
        .Replace('İ', 'i')
        .Replace("̇", "")
        .Replace('ı', 'i').Replace('ü', 'u').Replace('ö', 'o')
        .Replace('ş', 's').Replace('ç', 'c').Replace('ğ', 'g');
}
