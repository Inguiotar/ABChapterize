// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

namespace ABChapterize.Language.Parsers;

/// <summary>
/// Parses Czech spoken numbers 0-999: "sedm", "dvacet jedna", "sto dvacet jedna", "devet set
/// devadesat devet". Hundreds, tens and units are separate words with no connector, exactly as in
/// Polish, and the hundreds above one are themselves two words ("dve ste" = 200, "tri sta" = 300,
/// "pet set" = 500) whose multiplier and counted word may also be written fused ("dveste").
/// <para>
/// Ordinals are understood too. "Kapitola" is feminine, so an announcement agrees with it in the
/// feminine ("Kapitola prvni", "Dvacata prvni kapitola"); the masculine and neuter endings are
/// accepted alongside, being one stem apart and unable to mean anything else. As in Polish, a
/// compound ordinal marks BOTH its tens and its units word ("dvacata prvni" = 21st), while a
/// hundreds word ahead of them stays cardinal unless it is itself the number's last component
/// ("dvousta" = 200th).
/// </para>
/// </summary>
/// <remarks>
/// Czech also fuses a units-first compound in everyday speech - "jedenadvacet" (21),
/// "petadvacaty" (25th), unit + "a" + tens - which is the same shape German and Danish use and is
/// accepted here for the same reason: it costs one branch, and a spelling that is not recognized
/// costs a chapter. The formal "dvacet jedna" remains what a written heading is read from.
/// </remarks>
public sealed class CzechNumberParser : INumberWordParser
{
    /// <inheritdoc/>
    public string LanguageCode => "cs";

    /// <summary>
    /// Czech digit ordinals are a bare number plus a trailing period ("21."), already handled by
    /// the generic digit/period fallback, so no suffix is needed here.
    /// </summary>
    /// <inheritdoc/>
    public string DigitOrdinalSuffixPattern => "";

    /// <summary>Cardinal units 0-9, keyed in normalized (accent-free) form. Two spellings of one
    /// ("jeden" masculine, "jedna" feminine, "jedno" neuter) and of two ("dva", "dve").</summary>
    private static readonly Dictionary<string, int> Units = new()
    {
        ["nula"] = 0, ["jeden"] = 1, ["jedna"] = 1, ["jedno"] = 1, ["dva"] = 2, ["dve"] = 2,
        ["tri"] = 3, ["ctyri"] = 4, ["pet"] = 5, ["sest"] = 6, ["sedm"] = 7, ["osm"] = 8,
        ["devet"] = 9,
    };

    /// <summary>Cardinal teens 10-19.</summary>
    private static readonly Dictionary<string, int> Teens = new()
    {
        ["deset"] = 10, ["jedenact"] = 11, ["dvanact"] = 12, ["trinact"] = 13,
        ["ctrnact"] = 14, ["patnact"] = 15, ["sestnact"] = 16, ["sedmnact"] = 17,
        ["osmnact"] = 18, ["devatenact"] = 19,
    };

    /// <summary>Cardinal tens 20-90.</summary>
    private static readonly Dictionary<string, int> Tens = new()
    {
        ["dvacet"] = 20, ["tricet"] = 30, ["ctyricet"] = 40, ["padesat"] = 50,
        ["sedesat"] = 60, ["sedmdesat"] = 70, ["osmdesat"] = 80, ["devadesat"] = 90,
    };

    /// <summary>
    /// The counted word of a hundreds pair, which changes with its multiplier: "sto" alone,
    /// "dve ste", "tri/ctyri sta", "pet" and up "set". Only ever read after a unit 2-9, which is
    /// what keeps "sta" and "ste" from being confused with the 100th ordinal - see
    /// <see cref="TryParse"/>.
    /// </summary>
    private static readonly string[] HundredWords = ["sto", "ste", "sta", "set"];

    /// <summary>The unit combining forms a fused hundred is built from ("dveste", "petset").</summary>
    private static readonly Dictionary<string, int> HundredMultipliers = new()
    {
        ["dve"] = 2, ["tri"] = 3, ["ctyri"] = 4, ["pet"] = 5, ["sest"] = 6, ["sedm"] = 7,
        ["osm"] = 8, ["devet"] = 9,
    };

    /// <summary>
    /// Ordinal stems 1st-19th. A hard ordinal inflects for gender in its final vowel, so the stem
    /// is stored once and <see cref="Genders"/> supplies the three endings; the two soft ordinals
    /// ("prvni", "treti") are the same in every gender and carry no ending of their own.
    /// </summary>
    private static readonly (string Stem, int Value)[] OrdinalStems =
    [
        ("prvn", 1), ("druh", 2), ("tret", 3), ("ctvrt", 4), ("pat", 5), ("sest", 6),
        ("sedm", 7), ("osm", 8), ("devat", 9), ("desat", 10), ("jedenact", 11),
        ("dvanact", 12), ("trinact", 13), ("ctrnact", 14), ("patnact", 15),
        ("sestnact", 16), ("sedmnact", 17), ("osmnact", 18), ("devatenact", 19),
    ];

    /// <summary>Ordinal tens stems 20th-90th, which behave exactly like the units above.</summary>
    private static readonly (string Stem, int Value)[] OrdinalTensStems =
    [
        ("dvacat", 20), ("tricat", 30), ("ctyricat", 40), ("padesat", 50), ("sedesat", 60),
        ("sedmdesat", 70), ("osmdesat", 80), ("devadesat", 90),
    ];

    /// <summary>
    /// Ordinal hundreds stems 100th-900th, used only where the hundred is the number's last part.
    /// From five up these build on the counting combining form rather than on the cardinal
    /// ("petist-" from "peti-", not from "pet"), which is why they are listed rather than derived.
    /// Four is written both ways and both are admitted.
    /// </summary>
    private static readonly (string Stem, int Value)[] OrdinalHundredsStems =
    [
        ("st", 100), ("dvoust", 200), ("trist", 300), ("ctyrst", 400), ("ctyrist", 400),
        ("petist", 500), ("sestist", 600), ("sedmist", 700), ("osmist", 800),
        ("devitist", 900),
    ];

    /// <summary>
    /// The endings a hard ordinal stem takes, in their normalized form: masculine "-y" for "-ý",
    /// feminine "-a" for "-á", neuter "-e" for "-é". The soft ordinals take "-i" for "-í" instead,
    /// which is listed here too so that one lookup covers both classes - a stem only ever forms
    /// real words with one of the two sets, and admitting the other spells nothing that means
    /// anything else.
    /// </summary>
    private static readonly string[] Genders = ["y", "a", "e", "i"];

    /// <summary>Every ordinal 1-99, expanded from the stems above.</summary>
    private static readonly Dictionary<string, int> Ordinals = Expand(OrdinalStems);

    /// <summary>Every ordinal ten 20-90, expanded from the stems above. Kept apart from
    /// <see cref="Ordinals"/> because a units ordinal may follow one to complete a compound
    /// ("dvacata prvni") where nothing may follow a units ordinal.</summary>
    private static readonly Dictionary<string, int> OrdinalTens = Expand(OrdinalTensStems);

    /// <summary>Every ordinal hundred 100-900, expanded from the stems above.</summary>
    private static readonly Dictionary<string, int> OrdinalHundreds = Expand(OrdinalHundredsStems);

    /// <summary>The diacritics <see cref="Normalize"/> strips, so the pattern admits both the
    /// accented spelling and the ASCII one the tables are keyed with.</summary>
    private const string Accents = "aá;cč;dď;eéě;ií;nň;oó;rř;sš;tť;uúů;yý;zž;";

    /// <summary>
    /// A run of separate words - the ordinary Czech shape - where one token may instead be a fused
    /// hundred ("dveste") or a fused units-first compound ("jedenadvacet").
    /// </summary>
    /// <inheritdoc/>
    public string NumberWordPattern { get; } = BuildPattern();

    /// <summary>Assembles <see cref="NumberWordPattern"/> from the tables above.</summary>
    private static string BuildPattern()
    {
        var units = NumberWordPatterns.Alt(Units.Keys, Accents);
        var tensLike = NumberWordPatterns.Alt(Tens.Keys.Concat(OrdinalTens.Keys), Accents);
        var words = NumberWordPatterns.Alt(
            Units.Keys.Concat(Teens.Keys).Concat(Tens.Keys).Concat(Ordinals.Keys)
                .Concat(OrdinalTens.Keys).Concat(OrdinalHundreds.Keys).Concat(HundredWords),
            Accents);
        var fusedHundred = NumberWordPatterns.Alt(HundredMultipliers.Keys, Accents)
            + NumberWordPatterns.Alt(HundredWords, Accents);
        var fusedTens = $"{units}a{tensLike}";
        return NumberWordPatterns.Run(NumberWordPatterns.AnyOf(fusedHundred, fusedTens, words));
    }

    /// <inheritdoc/>
    public bool TryParse(IReadOnlyList<string> tokens, out int number, out int consumed)
    {
        number = 0;
        consumed = 0;
        var total = 0;
        var i = 0;

        // The hundreds, which come first when present. The two-token form is tried ahead of every
        // single-word reading on purpose: "sta" and "ste" are the tails of 300 and 200 after a
        // multiplier, but on their own they are the feminine and neuter 100th ordinal, and only
        // the word in front tells the two apart.
        if (i + 1 < tokens.Count && HundredMultipliers.TryGetValue(Normalize(tokens[i]), out var m)
            && HundredWords.Contains(Normalize(tokens[i + 1])))
        {
            total = m * 100;
            i += 2;
        }
        else if (i < tokens.Count && TryFusedHundred(Normalize(tokens[i]), out var fused))
        {
            total = fused;
            i += 1;
        }
        else if (i < tokens.Count && Normalize(tokens[i]) == "sto")
        {
            total = 100;
            i += 1;
        }
        else if (i < tokens.Count && OrdinalHundreds.TryGetValue(Normalize(tokens[i]), out var ho))
        {
            // A hundreds ordinal usually ends the number ("dvousta" = 200th), but Czech also
            // inflects every part of a compound, so a remainder is allowed to follow it.
            total = ho;
            i += 1;
        }

        consumed = i;
        var hadHundreds = i > 0;

        // The 1-99 remainder: a teen, a tens word optionally followed by a unit, a bare unit, or
        // any of their ordinal forms. A fused units-first compound stands for the whole of it.
        if (i < tokens.Count && TryParseSub100(tokens, ref i, out var sub))
        {
            total += sub;
            consumed = i;
        }
        else if (!hadHundreds)
        {
            return false;
        }

        if (total > 999)
            return false;
        number = total;
        return true;
    }

    /// <summary>
    /// Reads the 1-99 part, advancing <paramref name="index"/> over the tokens it used.
    /// </summary>
    /// <param name="tokens">The token list.</param>
    /// <param name="index">Where to start; left just past the number on success.</param>
    /// <param name="value">Receives the value on success.</param>
    private static bool TryParseSub100(IReadOnlyList<string> tokens, ref int index, out int value)
    {
        var s = Normalize(tokens[index]);

        if (Teens.TryGetValue(s, out value) || Ordinals.TryGetValue(s, out value)
            || TryFusedTens(s, out value))
        {
            index += 1;
            return true;
        }

        // A tens word, cardinal or ordinal, optionally completed by a unit. Czech marks both parts
        // of an ordinal compound, so an ordinal ten takes an ordinal unit and a cardinal ten a
        // cardinal one; mixing them is admitted rather than policed, a transcript being no
        // authority on agreement.
        if (Tens.TryGetValue(s, out value) || OrdinalTens.TryGetValue(s, out value))
        {
            index += 1;
            if (index < tokens.Count)
            {
                var next = Normalize(tokens[index]);
                if ((Units.TryGetValue(next, out var u) || Ordinals.TryGetValue(next, out u))
                    && u is >= 1 and <= 9)
                {
                    value += u;
                    index += 1;
                }
            }
            return true;
        }

        if (Units.TryGetValue(s, out value))
        {
            index += 1;
            return true;
        }

        value = 0;
        return false;
    }

    /// <summary>Reads a fused hundred, the multiplier and its counted word written as one word
    /// ("dveste", "petset").</summary>
    /// <param name="s">The normalized token.</param>
    /// <param name="value">Receives the value on success.</param>
    private static bool TryFusedHundred(string s, out int value)
    {
        foreach (var (word, multiplier) in HundredMultipliers)
        {
            if (!s.StartsWith(word, StringComparison.Ordinal))
                continue;
            if (HundredWords.Contains(s[word.Length..]))
            {
                value = multiplier * 100;
                return true;
            }
        }
        value = 0;
        return false;
    }

    /// <summary>Reads the colloquial units-first compound, unit + "a" + tens ("jedenadvacet",
    /// "petadvacaty").</summary>
    /// <param name="s">The normalized token.</param>
    /// <param name="value">Receives the value on success.</param>
    private static bool TryFusedTens(string s, out int value)
    {
        foreach (var (word, unit) in Units)
        {
            if (unit < 1 || !s.StartsWith(word + "a", StringComparison.Ordinal))
                continue;
            var tail = s[(word.Length + 1)..];
            if (Tens.TryGetValue(tail, out var tens) || OrdinalTens.TryGetValue(tail, out tens))
            {
                value = unit + tens;
                return true;
            }
        }
        value = 0;
        return false;
    }

    /// <summary>Expands ordinal stems into every gender ending they may carry.</summary>
    /// <param name="stems">The stem table.</param>
    private static Dictionary<string, int> Expand((string Stem, int Value)[] stems)
    {
        var table = new Dictionary<string, int>();
        foreach (var (stem, value) in stems)
            foreach (var ending in Genders)
                table[stem + ending] = value;
        return table;
    }

    /// <summary>Lowercases and strips the Czech diacritics, both the acutes and the carons.</summary>
    /// <param name="token">The raw token.</param>
    private static string Normalize(string token) => token.ToLowerInvariant()
        .Replace('á', 'a').Replace('č', 'c').Replace('ď', 'd').Replace('é', 'e')
        .Replace('ě', 'e').Replace('í', 'i').Replace('ň', 'n').Replace('ó', 'o')
        .Replace('ř', 'r').Replace('š', 's').Replace('ť', 't').Replace('ú', 'u')
        .Replace('ů', 'u').Replace('ý', 'y').Replace('ž', 'z');
}
