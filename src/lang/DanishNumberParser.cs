// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

namespace ABChapterize.Lang;

/// <summary>
/// Parses Danish spoken numbers 0-999. Danish tens are vigesimal (base-20) for 50-90:
/// "halvtreds" (50) is short for "halvtredsindstyve", literally "half-third times twenty"
/// (2.5 x 20); "tres" (60), "halvfjerds" (70), "firs" (80) and "halvfems" (90) follow the
/// same pattern. Both the short spoken forms and their historical long "-(sinds)tyve"
/// forms are accepted. Units and tens fuse into one word with "og" ("enogtyve" = 21,
/// "trekvartfems"-style oddities aside), while the hundreds are a separate word
/// ("hundrede") optionally preceded by its multiplier and an "og" connector: "tre hundrede
/// og enogtyve" = 321. Ordinals are understood only for the common, non-compound forms
/// 1st-20th ("Første kapitel") - the ordinal tens 50th-90th use irregular long forms
/// ("halvtredsindstyvende") that are vanishingly rare in ordinal-first chapter
/// announcements, so - like the Spanish and Italian parsers - compound ordinals are out
/// of scope here.
/// </summary>
public sealed class DanishNumberParser : INumberWordParser
{
    /// <inheritdoc/>
    public string LanguageCode => "da";

    /// <summary>
    /// Danish digit ordinals are a bare number plus a trailing period ("21."), already
    /// handled by the generic digit/period fallback, so no suffix is needed here.
    /// </summary>
    /// <inheritdoc/>
    public string DigitOrdinalSuffixPattern => "";

    /// <summary>Units 0-9, including both "en" and "et" for one ("et hundrede", "et kapitel").</summary>
    private static readonly Dictionary<string, int> Units = new()
    {
        ["nul"] = 0, ["en"] = 1, ["et"] = 1, ["to"] = 2, ["tre"] = 3, ["fire"] = 4,
        ["fem"] = 5, ["seks"] = 6, ["syv"] = 7, ["otte"] = 8, ["ni"] = 9,
    };

    /// <summary>Teens 10-19.</summary>
    private static readonly Dictionary<string, int> Teens = new()
    {
        ["ti"] = 10, ["elleve"] = 11, ["tolv"] = 12, ["tretten"] = 13, ["fjorten"] = 14,
        ["femten"] = 15, ["seksten"] = 16, ["sytten"] = 17, ["atten"] = 18, ["nitten"] = 19,
    };

    /// <summary>Bare tens 20-90, short spoken form and long historical form alike.</summary>
    private static readonly Dictionary<string, int> Tens = new()
    {
        ["tyve"] = 20,
        ["tredive"] = 30,
        ["fyrre"] = 40, ["fyrretyve"] = 40,
        ["halvtreds"] = 50, ["halvtredsindstyve"] = 50,
        ["tres"] = 60, ["tresindstyve"] = 60,
        ["halvfjerds"] = 70, ["halvfjerdsindstyve"] = 70,
        ["firs"] = 80, ["firsindstyve"] = 80,
        ["halvfems"] = 90, ["halvfemsindstyve"] = 90,
    };

    /// <summary>
    /// The short tens forms only, used to recognize/build the fused "&lt;unit&gt;og&lt;tens&gt;"
    /// compound - Danish never fuses the long "-indstyve" forms into a compound.
    /// </summary>
    private static readonly (string Word, int Value)[] ShortTens =
    [
        ("tyve", 20), ("tredive", 30), ("fyrre", 40), ("halvtreds", 50),
        ("tres", 60), ("halvfjerds", 70), ("firs", 80), ("halvfems", 90),
    ];

    /// <summary>The fixed "en" prefix compounds always use for one, regardless of gender.</summary>
    private static readonly Dictionary<string, int> UnitsForCompound = new()
    {
        ["en"] = 1, ["et"] = 1, ["to"] = 2, ["tre"] = 3, ["fire"] = 4, ["fem"] = 5,
        ["seks"] = 6, ["syv"] = 7, ["otte"] = 8, ["ni"] = 9,
    };

    /// <summary>Ordinals 1st-20th; compound ordinals beyond that are out of scope (see class docs).</summary>
    private static readonly Dictionary<string, int> Ordinals = new()
    {
        ["forste"] = 1, ["anden"] = 2, ["andet"] = 2, ["tredje"] = 3, ["fjerde"] = 4,
        ["femte"] = 5, ["sjette"] = 6, ["syvende"] = 7, ["ottende"] = 8, ["niende"] = 9,
        ["tiende"] = 10, ["ellevte"] = 11, ["tolvte"] = 12, ["trettende"] = 13,
        ["fjortende"] = 14, ["femtende"] = 15, ["sekstende"] = 16, ["syttende"] = 17,
        ["attende"] = 18, ["nittende"] = 19, ["tyvende"] = 20,
    };

    /// <inheritdoc/>
    public bool TryParse(IReadOnlyList<string> tokens, out int number, out int consumed)
    {
        number = 0;
        consumed = 0;
        if (tokens.Count == 0)
            return false;

        var i = 0;
        var total = 0;
        var any = false;

        // Hundreds: an optional unit multiplier followed by "hundrede", or bare "hundrede" (= 1).
        if (i + 1 < tokens.Count && Units.TryGetValue(Normalize(tokens[i]), out var mult)
            && Normalize(tokens[i + 1]) == "hundrede")
        {
            total = mult * 100;
            any = true;
            i += 2;
            consumed = i;
        }
        else if (Normalize(tokens[i]) == "hundrede")
        {
            total = 100;
            any = true;
            i += 1;
            consumed = i;
        }

        if (any)
        {
            // An optional "og" connector, then the 1-99 remainder (cardinal or ordinal) as
            // a single word. Only consumed when a valid remainder actually follows, so a
            // bare "hundrede" (possibly followed by unrelated prose) still succeeds.
            var hasOg = i < tokens.Count && Normalize(tokens[i]) == "og";
            var tryIndex = hasOg ? i + 1 : i;
            if (tryIndex < tokens.Count && TryParseSub100(tokens[tryIndex], out var sub))
            {
                total += sub;
                consumed = tryIndex + 1;
            }
            number = total;
            return true;
        }

        // No hundreds: the whole number is a single sub-100 word (cardinal or ordinal).
        if (TryParseSub100(tokens[0], out var value))
        {
            number = value;
            consumed = 1;
            return true;
        }

        return false;
    }

    /// <summary>Parses a single word as a cardinal or ordinal value 0-99.</summary>
    private static bool TryParseSub100(string token, out int number)
    {
        var s = Normalize(token);

        if (Units.TryGetValue(s, out number) || Teens.TryGetValue(s, out number)
            || Tens.TryGetValue(s, out number) || Ordinals.TryGetValue(s, out number))
            return true;

        // Fused compound like "enogtyve": <unit>og<tens>, using the short tens form only.
        foreach (var (unitWord, unitValue) in UnitsForCompound)
        {
            if (!s.StartsWith(unitWord + "og", StringComparison.Ordinal))
                continue;
            var tensPart = s[(unitWord.Length + 2)..];
            foreach (var (tensWord, tensValue) in ShortTens)
            {
                if (tensPart == tensWord)
                {
                    number = unitValue + tensValue;
                    return true;
                }
            }
        }

        number = 0;
        return false;
    }

    /// <summary>Lowercases and strips the Danish diacritics (æ, ø, å) - only "første" needs it.</summary>
    private static string Normalize(string token) => token.ToLowerInvariant()
        .Replace('æ', 'a').Replace('ø', 'o').Replace('å', 'a');
}
