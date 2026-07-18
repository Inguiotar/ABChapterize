namespace Chapterize.Lang;

/// <summary>
/// Parses Turkish spoken numbers 0-999, which are space-separated words in big-endian
/// order: "yedi", "yirmi bir", "iki yuz otuz dort", "dokuz yuz doksan dokuz". The Turkish
/// letters (dotless i, u/o with diaeresis, s/c with cedilla, soft g) are normalized to
/// plain ASCII, so both proper Turkish and ASCII-ified transcriptions parse - and the
/// notorious dotted/dotless-I casing rules cannot bite under invariant globalization.
/// Ordinals are understood too - "Birinci Bolum" as well as every regular -inci/-uncu
/// form ("yirmi birinci", "yuzuncu") - since Turkish chapters are announced ordinal-first.
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

    /// <summary>
    /// The vowel-harmony variants of the ordinal suffix, in normalized ASCII form and
    /// longest-first, so "birinci" strips "inci" before "nci" could leave "biri".
    /// </summary>
    private static readonly string[] OrdinalSuffixes = ["inci", "uncu", "nci", "ncu"];

    /// <inheritdoc/>
    public bool TryParse(IReadOnlyList<string> tokens, out int number, out int consumed)
    {
        number = 0;
        consumed = 0;
        var total = 0;
        var last = -1;    // value of the previous numeric word, for "yuz" multiplication
        var any = false;

        for (var i = 0; i < tokens.Count; i++)
        {
            var part = Normalize(tokens[i]);

            // An ordinal is its cardinal plus a suffix; reduce it and let the normal
            // logic below handle it, then end the number. This covers "birinci" (1st)
            // as well as "yirmi birinci" (21st) and "iki yuzuncu" (200th).
            var ordinal = TryReduceOrdinal(part, out var cardinalPart);
            if (ordinal)
                part = cardinalPart;

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
            else
            {
                break; // first non-number word ends the number
            }

            consumed = i + 1;
            if (ordinal)
                break; // the ordinal suffix marks the end of the number
        }

        if (!any || total > 999)
            return false;
        number = total;
        return true;
    }

    /// <summary>
    /// Reduces a regular ordinal to its cardinal word: "birinci" -> "bir", "ikinci" ->
    /// "iki", "dorduncu" -> "dort", "yuzuncu" -> "yuz". All four vowel-harmony suffix
    /// variants are tried, and the consonant softening d -> t is undone.
    /// </summary>
    /// <param name="part">Normalized ASCII word, possibly an ordinal.</param>
    /// <param name="cardinal">Receives the cardinal word on success.</param>
    private static bool TryReduceOrdinal(string part, out string cardinal)
    {
        foreach (var suffix in OrdinalSuffixes)
        {
            if (part.Length <= suffix.Length || !part.EndsWith(suffix, StringComparison.Ordinal))
                continue;
            var stem = part[..^suffix.Length];
            if (IsCardinalWord(stem))
            {
                cardinal = stem;
                return true;
            }
            // "dorduncu": the final t of "dort" softens to d before the vowel suffix.
            if (stem.EndsWith('d') && IsCardinalWord(stem[..^1] + "t"))
            {
                cardinal = stem[..^1] + "t";
                return true;
            }
        }
        cardinal = "";
        return false;
    }

    /// <summary>Tells whether a normalized word is a known cardinal building block.</summary>
    private static bool IsCardinalWord(string word) =>
        word == "yuz" || Units.ContainsKey(word) || Tens.ContainsKey(word);

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
