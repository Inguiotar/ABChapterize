// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

namespace ABChapterize.Language.Parsers;

/// <summary>
/// Parses Norwegian (Bokmal) spoken numbers 0-999. The shape is a hybrid of its two neighbours:
/// unit and tens fuse into a single word as in Swedish and Danish ("tjueen", "enogtyve"), while
/// the hundreds stay a separate word joined by "og" as in Danish ("to hundre og tjueen"). A fully
/// fused spelling ("tohundreogtjueen") is accepted as well, since both are written.
/// <para>
/// Norwegian counts two ways and an audiobook may use either. The 1951 reform put the tens first
/// ("tjueen" = 21, "tjueforste" = 21st); the older units-first system it replaced is the Danish one
/// ("enogtyve", "enogtyvende") and is still heard, particularly from older narrators. Both are
/// understood, as are the conservative spellings of the words the reform also touched: "syv" beside
/// "sju" (7), "tyve" beside "tjue" (20) and "tredve" beside "tretti" (30).
/// </para>
/// </summary>
/// <remarks>
/// Ordinals run the full 1-999 alongside the cardinals, being derived regularly from them, and only
/// the last part of a number is ever ordinal-marked ("hundre og tjueforste" = 121st, "hundre"
/// staying cardinal). The one exception is a round hundred, which is itself the last part:
/// "hundrede" = 100th, spelled apart from the cardinal "hundre".
/// </remarks>
public sealed class NorwegianNumberParser : INumberWordParser
{
    /// <inheritdoc/>
    public string LanguageCode => "no";

    /// <summary>
    /// Norwegian digit ordinals are a bare number plus a trailing period ("21."), already handled
    /// by the generic digit/period fallback, so no suffix is needed here.
    /// </summary>
    /// <inheritdoc/>
    public string DigitOrdinalSuffixPattern => "";

    /// <summary>
    /// Units 0-9. Both spellings of 1 ("en" common gender, "ett" neuter - "et kapittel" is neuter,
    /// so a narrator may say either) and of 7 ("sju" modern, "syv" conservative).
    /// </summary>
    private static readonly Dictionary<string, int> Units = new()
    {
        ["null"] = 0, ["en"] = 1, ["ett"] = 1, ["to"] = 2, ["tre"] = 3, ["fire"] = 4,
        ["fem"] = 5, ["seks"] = 6, ["sju"] = 7, ["syv"] = 7, ["åtte"] = 8, ["atte"] = 8,
        ["ni"] = 9,
    };

    /// <summary>Teens 10-19, none of which take part in a compound.</summary>
    private static readonly Dictionary<string, int> Teens = new()
    {
        ["ti"] = 10, ["elleve"] = 11, ["tolv"] = 12, ["tretten"] = 13, ["fjorten"] = 14,
        ["femten"] = 15, ["seksten"] = 16, ["sytten"] = 17, ["atten"] = 18, ["nitten"] = 19,
    };

    /// <summary>
    /// Bare tens 20-90, modern and conservative spellings alike. Ordered longest first so that a
    /// prefix search cannot stop at a shorter word that happens to begin another.
    /// </summary>
    private static readonly (string Word, int Value)[] Tens =
    [
        ("tjue", 20), ("tyve", 20), ("tretti", 30), ("tredve", 30), ("forti", 40),
        ("femti", 50), ("seksti", 60), ("sytti", 70), ("åtti", 80), ("atti", 80),
        ("nitti", 90),
    ];

    /// <summary>
    /// Ordinals 1st-19th. "andre" is the modern all-purpose form; "annen" (common gender) and
    /// "annet" (neuter) are the older gendered ones and both are still said.
    /// </summary>
    private static readonly Dictionary<string, int> Ordinals = new()
    {
        ["forste"] = 1, ["andre"] = 2, ["annen"] = 2, ["annet"] = 2,
        ["tredje"] = 3, ["fjerde"] = 4, ["femte"] = 5, ["sjette"] = 6, ["sjuende"] = 7,
        ["syvende"] = 7, ["åttende"] = 8, ["niende"] = 9, ["tiende"] = 10,
        ["ellevte"] = 11, ["tolvte"] = 12, ["trettende"] = 13, ["fjortende"] = 14,
        ["femtende"] = 15, ["sekstende"] = 16, ["syttende"] = 17, ["attende"] = 18,
        ["nittende"] = 19,
    };

    /// <summary>
    /// Ordinal tens 20th-90th. These serve twice - standing alone ("tjuende kapittel") and as the
    /// tail of a fused units-first compound ("enogtyvende") - which is why they are a list of their
    /// own rather than merged into <see cref="Ordinals"/>.
    /// </summary>
    private static readonly (string Word, int Value)[] OrdinalTens =
    [
        ("tjuende", 20), ("tyvende", 20), ("trettiende", 30), ("tredevte", 30),
        ("fortiende", 40), ("femtiende", 50), ("sekstiende", 60), ("syttiende", 70),
        ("åttiende", 80), ("attiende", 80), ("nittiende", 90),
    ];

    /// <summary>
    /// Ordinal units 1st-9th as they may stand at the tail of a tens-first compound. Kept apart
    /// from <see cref="Ordinals"/> for two reasons, both of which are about what a compound cannot
    /// be: a compound only ever ends in a unit, so admitting the teens here would read
    /// "tjueattende" as twenty plus eighteen, which is not a number Norwegian can express; and once
    /// the teens are out, the ASCII "attende" becomes unambiguous in this position and can be read
    /// as the 8th it must be. Standing alone that same word is 18th, which is why the entry lives
    /// here and not in the general table - see <see cref="Normalize"/> for why the two collide at
    /// all.
    /// </summary>
    private static readonly Dictionary<string, int> CompoundOrdinalUnits = BuildCompoundOrdinalUnits();

    /// <summary>Assembles <see cref="CompoundOrdinalUnits"/> from <see cref="Ordinals"/>.</summary>
    private static Dictionary<string, int> BuildCompoundOrdinalUnits()
    {
        var table = Ordinals.Where(o => o.Value is >= 1 and <= 9)
            .ToDictionary(o => o.Key, o => o.Value);
        table["attende"] = 8;
        return table;
    }

    /// <summary>
    /// The diacritics <see cref="Normalize"/> folds, mirroring it exactly so that the spelling a
    /// transcript carries reaches a table keyed the same way. Note the absence of a group for "a":
    /// unlike Danish and Swedish this parser leaves "å" standing, for the reason given on
    /// <see cref="Normalize"/>, and the ASCII spellings it still wants are listed in the tables
    /// themselves instead.
    /// </summary>
    private const string Accents = "oø;eé;";

    /// <summary>The word for a hundred, and the ordinal spelling that differs from it by one
    /// letter.</summary>
    private const string Hundred = "hundre";

    /// <summary>
    /// A run of words, one of which may be a fused compound: the units-first "enogtyve" of the old
    /// system, the tens-first "tjueen" of the new one, or a whole fused number carrying its own
    /// hundreds ("tohundreogtjueen"). The compounds are sub-patterns rather than word lists, ten
    /// units times a dozen tens spellings being two hundred alternatives that say one thing.
    /// </summary>
    /// <inheritdoc/>
    public string NumberWordPattern { get; } = BuildPattern();

    /// <summary>Assembles <see cref="NumberWordPattern"/> from the tables above.</summary>
    private static string BuildPattern()
    {
        var units = NumberWordPatterns.Alt(
            Units.Where(u => u.Value >= 1).Select(u => u.Key), Accents);
        var tens = NumberWordPatterns.Alt(Tens.Select(t => t.Word), Accents);
        var ordinalTens = NumberWordPatterns.Alt(OrdinalTens.Select(t => t.Word), Accents);
        var ordinals = NumberWordPatterns.Alt(Ordinals.Keys, Accents);
        var words = NumberWordPatterns.Alt(
            Units.Keys.Concat(Teens.Keys).Concat(Tens.Select(t => t.Word))
                .Concat(Ordinals.Keys).Concat(OrdinalTens.Select(t => t.Word))
                .Concat([Hundred, Hundred + "de", "og"]),
            Accents);
        // Old system first: "enogtyve" starts with a unit, which the new system's tens-first
        // alternative would otherwise claim only to fail on the rest.
        var sub100 = NumberWordPatterns.AnyOf(
            $"{units}og{NumberWordPatterns.AnyOf(tens, ordinalTens)}",
            $"{NumberWordPatterns.AnyOf(tens, ordinalTens)}{NumberWordPatterns.AnyOf(units, ordinals)}",
            words);
        var hundreds = $"{units}?{Hundred}(?:de)?(?:og)?(?:{sub100})?";
        return NumberWordPatterns.Run(NumberWordPatterns.AnyOf(hundreds, sub100));
    }

    /// <inheritdoc/>
    public bool TryParse(IReadOnlyList<string> tokens, out int number, out int consumed)
    {
        number = 0;
        consumed = 0;
        if (tokens.Count == 0)
            return false;

        var i = 0;
        var total = 0;

        // Hundreds written as separate words: an optional unit multiplier before "hundre", or a
        // bare "hundre" standing for one of them.
        if (i + 1 < tokens.Count && Units.TryGetValue(Normalize(tokens[i]), out var mult)
            && mult >= 1 && IsHundred(Normalize(tokens[i + 1]), out var multOrdinal))
        {
            total = mult * 100;
            i += 2;
            consumed = i;
            if (multOrdinal)
            {
                number = total;
                return true;
            }
        }
        else if (IsHundred(Normalize(tokens[i]), out var bareOrdinal))
        {
            total = 100;
            i += 1;
            consumed = i;
            if (bareOrdinal)
            {
                number = total;
                return true;
            }
        }

        if (consumed > 0)
        {
            // An optional "og" connector, then the 1-99 remainder as a single word. Consumed only
            // when a valid remainder really follows, so a bare "hundre" trailed by ordinary prose
            // still succeeds at 100.
            var afterOg = i < tokens.Count && Normalize(tokens[i]) == "og" ? i + 1 : i;
            if (afterOg < tokens.Count && TryParseSub100(tokens[afterOg], out var sub))
            {
                total += sub;
                consumed = afterOg + 1;
            }
            number = total;
            return true;
        }

        // No separate hundreds word: the whole number is one token, which may still carry its own
        // fused hundreds ("tohundreogtjueen").
        if (TryParseWholeWord(tokens[0], out number))
        {
            consumed = 1;
            return true;
        }
        return false;
    }

    /// <summary>Reads one token as a complete number, fused hundreds included.</summary>
    /// <param name="token">The raw token.</param>
    /// <param name="number">Receives the value on success.</param>
    private static bool TryParseWholeWord(string token, out int number)
    {
        number = 0;
        var s = Normalize(token);

        var idx = s.IndexOf(Hundred, StringComparison.Ordinal);
        if (idx < 0)
            return TryParseSub100(token, out number);

        var prefix = s[..idx];
        var mult = prefix.Length == 0 ? 1
            : Units.TryGetValue(prefix, out var h) && h >= 1 ? h : -1;
        if (mult < 0)
            return false;

        var rest = s[(idx + Hundred.Length)..];
        // "hundrede" and "tohundrede" are the round hundred as an ordinal, and end there.
        if (rest is "de" or "")
        {
            number = mult * 100;
            return true;
        }
        if (rest.StartsWith("og", StringComparison.Ordinal))
            rest = rest[2..];
        if (rest.Length == 0 || !TryParseSub100(rest, out var sub))
            return false;
        number = mult * 100 + sub;
        return true;
    }

    /// <summary>Parses a single word as a cardinal or ordinal value 0-99.</summary>
    /// <param name="token">The token, raw or already normalized - normalizing twice is harmless.</param>
    /// <param name="number">Receives the value on success.</param>
    private static bool TryParseSub100(string token, out int number)
    {
        var s = Normalize(token);

        if (Units.TryGetValue(s, out number) || Teens.TryGetValue(s, out number)
            || Ordinals.TryGetValue(s, out number)
            || TryTens(Tens, s, out number) || TryTens(OrdinalTens, s, out number))
            return true;

        // Old system, units first: "enogtyve", "enogtyvende". Tried before the new system's shape
        // because a unit word also opens it, and a wrong guess there would cost the whole parse.
        foreach (var (unitWord, unitValue) in Units)
        {
            if (unitValue < 1 || !s.StartsWith(unitWord + "og", StringComparison.Ordinal))
                continue;
            var tail = s[(unitWord.Length + 2)..];
            if (TryTens(Tens, tail, out var tensValue) || TryTens(OrdinalTens, tail, out tensValue))
            {
                number = unitValue + tensValue;
                return true;
            }
        }

        // New system, tens first: "tjueen", "tjueforste". The tens word stays cardinal even when
        // the number is an ordinal, only the unit inflecting.
        foreach (var (tensWord, tensValue) in Tens)
        {
            if (!s.StartsWith(tensWord, StringComparison.Ordinal))
                continue;
            var tail = s[tensWord.Length..];
            int u;
            if ((Units.TryGetValue(tail, out u) && u is >= 1 and <= 9)
                || CompoundOrdinalUnits.TryGetValue(tail, out u))
            {
                number = tensValue + u;
                return true;
            }
        }

        number = 0;
        return false;
    }

    /// <summary>Recognizes "hundre" and its ordinal "hundrede".</summary>
    /// <param name="word">The normalized word.</param>
    /// <param name="ordinal">True when the ordinal spelling was given, which ends the number.</param>
    private static bool IsHundred(string word, out bool ordinal)
    {
        ordinal = word == Hundred + "de";
        return ordinal || word == Hundred;
    }

    /// <summary>Looks one word up in a tens table.</summary>
    /// <param name="table">Either the cardinal <see cref="Tens"/> or <see cref="OrdinalTens"/>.</param>
    /// <param name="word">The normalized word, or the tail of a fused compound.</param>
    /// <param name="value">Receives the tens value on success.</param>
    private static bool TryTens((string Word, int Value)[] table, string word, out int value)
    {
        foreach (var (candidate, v) in table)
        {
            if (word == candidate)
            {
                value = v;
                return true;
            }
        }
        value = 0;
        return false;
    }

    /// <summary>
    /// Lowercases, folds "ø" and the acute of "en", and leaves "æ" alone, no number word carrying
    /// one.
    /// <para>
    /// Deliberately does <em>not</em> fold "a" the way the Danish and Swedish parsers do, because
    /// in Norwegian that conflates two different numbers: "attende" (8th) would become "attende",
    /// which is already 18th. Danish escapes this because its 8 is "otte", Swedish because its 18th
    /// is "artonde"; Norwegian has no such luck. The ASCII spellings still worth accepting are
    /// listed in the tables one by one instead - "atte" for 8, "atti" for 80, "attiende" for 80th -
    /// and 8th is left without one, that being the single entry where an ASCII reading would be
    /// wrong rather than merely ugly.
    /// </para>
    /// <para>
    /// What that leaves is small and irreducible. An ASCII "attende" is read as 18th wherever a
    /// teen could really stand - alone, and after a hundreds word, so 8th, 108th, 208th and so on
    /// are misread if a transcript drops the ring. At the tail of a tens-first compound it is read
    /// as the 8th it must be, no compound in the language ending in a teen; see
    /// <see cref="CompoundOrdinalUnits"/>. The cardinals are unaffected throughout, "atte" being
    /// listed outright.
    /// </para>
    /// </summary>
    /// <param name="token">The raw token.</param>
    private static string Normalize(string token) => token.ToLowerInvariant()
        .Replace('ø', 'o').Replace('é', 'e');
}
