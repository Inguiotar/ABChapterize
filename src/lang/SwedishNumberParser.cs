// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

namespace ABChapterize.Lang;

/// <summary>
/// Parses Swedish spoken numbers 0-999, which are written as a single compound word with
/// no connector: "sju", "tjugoett", "hundratjugotre", "niohundra". Ordinals are understood
/// too ("Första kapitlet", "tjugoförsta"): the regular pattern appends "-de" or "-nde" to
/// the cardinal, with a handful of irregular stems (första, andra, tredje, fjärde, sjätte)
/// for the low numbers, and in a compound only the last part inflects ("hundratjugotredje"
/// = 123rd, "hundra" stays cardinal). Since Swedish chapters are usually announced with a
/// plain cardinal ("Kapitel tjugoett"), ordinal-first announcements are understood too.
/// </summary>
public sealed class SwedishNumberParser : INumberWordParser
{
    /// <inheritdoc/>
    public string LanguageCode => "sv";

    /// <summary>
    /// The colon Swedish puts before its ordinal suffix is mandatory (unlike Turkish's
    /// optional apostrophe), so it is baked into this pattern rather than assumed by
    /// <see cref="NumberWordParser"/>: "a" for 1st/2nd ("1:a", "2:a"), "e" for the rest
    /// ("3:e", "21:e").
    /// </summary>
    /// <inheritdoc/>
    public string DigitOrdinalSuffixPattern => ":(?:a|e)";

    /// <summary>Simple (non-compound) cardinal words, including "en"/"ett" for one.</summary>
    private static readonly Dictionary<string, int> Simple = new()
    {
        ["noll"] = 0, ["en"] = 1, ["ett"] = 1, ["tva"] = 2, ["tre"] = 3, ["fyra"] = 4,
        ["fem"] = 5, ["sex"] = 6, ["sju"] = 7, ["atta"] = 8, ["nio"] = 9, ["tio"] = 10,
        ["elva"] = 11, ["tolv"] = 12, ["tretton"] = 13, ["fjorton"] = 14, ["femton"] = 15,
        ["sexton"] = 16, ["sjutton"] = 17, ["arton"] = 18, ["nitton"] = 19, ["tjugo"] = 20,
        ["trettio"] = 30, ["fyrtio"] = 40, ["femtio"] = 50, ["sextio"] = 60,
        ["sjuttio"] = 70, ["attio"] = 80, ["nittio"] = 90, ["hundra"] = 100,
    };

    /// <summary>Tens words 20-90, for building/recognizing compounds (longest first).</summary>
    private static readonly (string Word, int Value)[] Tens =
    [
        ("tjugo", 20), ("trettio", 30), ("fyrtio", 40), ("femtio", 50),
        ("sextio", 60), ("sjuttio", 70), ("attio", 80), ("nittio", 90),
    ];

    /// <summary>
    /// Irregular/base ordinals, keyed in normalized form. Covers 1-19, the bare tens
    /// 20-90 and 100, which is everything a regular suffix rule cannot derive cleanly.
    /// </summary>
    private static readonly Dictionary<string, int> OrdinalBase = new()
    {
        ["forsta"] = 1, ["andra"] = 2, ["tredje"] = 3, ["fjarde"] = 4, ["femte"] = 5,
        ["sjatte"] = 6, ["sjunde"] = 7, ["attonde"] = 8, ["nionde"] = 9, ["tionde"] = 10,
        ["elfte"] = 11, ["tolfte"] = 12, ["trettonde"] = 13, ["fjortonde"] = 14,
        ["femtonde"] = 15, ["sextonde"] = 16, ["sjuttonde"] = 17, ["artonde"] = 18,
        ["nittonde"] = 19, ["tjugonde"] = 20, ["trettionde"] = 30, ["fyrtionde"] = 40,
        ["femtionde"] = 50, ["sextionde"] = 60, ["sjuttionde"] = 70, ["attionde"] = 80,
        ["nittionde"] = 90, ["hundrade"] = 100,
    };

    /// <inheritdoc/>
    public bool TryParse(IReadOnlyList<string> tokens, out int number, out int consumed)
    {
        // Swedish numbers are one compound word; only the first token matters.
        number = 0;
        consumed = 0;
        if (tokens.Count == 0)
            return false;
        var s = Normalize(tokens[0]);

        if (TryParseCardinal(s, out number) || TryParseOrdinal(s, out number))
        {
            consumed = 1;
            return true;
        }
        return false;
    }

    /// <summary>Parses a single cardinal compound word ("tjugoett", "hundratjugotre").</summary>
    private static bool TryParseCardinal(string s, out int number)
    {
        if (Simple.TryGetValue(s, out number))
            return true;

        var hundreds = 0;
        var idx = s.IndexOf("hundra", StringComparison.Ordinal);
        if (idx >= 0)
        {
            var prefix = s[..idx];
            if (prefix.Length == 0) hundreds = 1;
            else if (Simple.TryGetValue(prefix, out var h) && h is >= 1 and <= 9) hundreds = h;
            else return false;
            s = s[(idx + "hundra".Length)..];
            if (s.Length == 0)
            {
                number = hundreds * 100;
                return true;
            }
        }

        if (Simple.TryGetValue(s, out var rest) && rest < 100)
        {
            number = hundreds * 100 + rest;
            return true;
        }

        // Compound like "tjugoett": <tens><unit>, straight concatenation, no connector.
        foreach (var (word, value) in Tens)
        {
            if (s.StartsWith(word, StringComparison.Ordinal))
            {
                var unitPart = s[word.Length..];
                if (Simple.TryGetValue(unitPart, out var u) && u is >= 1 and <= 9)
                {
                    number = hundreds * 100 + value + u;
                    return true;
                }
            }
        }

        number = 0;
        return false;
    }

    /// <summary>
    /// Parses an ordinal ("tredje", "tjugoforsta", "hundratjugotredje") by checking the
    /// closed set of irregular/base forms first, then peeling off a cardinal "hundra"
    /// prefix or tens prefix and parsing the remainder as an ordinal, since only the last
    /// part of a compound number is ever ordinal-marked.
    /// </summary>
    private static bool TryParseOrdinal(string s, out int number)
    {
        number = 0;
        if (OrdinalBase.TryGetValue(s, out number))
            return true;

        // Ordinalized round hundred, the last part of the number: "hundrade" (100th),
        // "tvahundrade" (200th). Must be checked before the generic "hundra" search below,
        // since it ends in "hundra" + "de", not "hundra" + a further ordinal remainder.
        if (s.EndsWith("hundrade", StringComparison.Ordinal))
        {
            var prefix = s[..^"hundrade".Length];
            var mult = prefix.Length == 0 ? 1
                : Simple.TryGetValue(prefix, out var h0) && h0 is >= 1 and <= 9 ? h0 : -1;
            if (mult <= 0)
                return false;
            number = mult * 100;
            return true;
        }

        // Cardinal "hundra" followed by an ordinal remainder: "hundratjugotredje" = 123rd.
        var idx = s.IndexOf("hundra", StringComparison.Ordinal);
        if (idx >= 0)
        {
            var prefix = s[..idx];
            var mult = prefix.Length == 0 ? 1
                : Simple.TryGetValue(prefix, out var h1) && h1 is >= 1 and <= 9 ? h1 : -1;
            var rest = s[(idx + "hundra".Length)..];
            if (mult > 0 && rest.Length > 0 && TryParseOrdinal(rest, out var subOrdinal))
            {
                number = mult * 100 + subOrdinal;
                return true;
            }
            return false;
        }

        // "tjugotredje": cardinal tens prefix + ordinal unit ("tredje").
        foreach (var (word, value) in Tens)
        {
            if (s.StartsWith(word, StringComparison.Ordinal)
                && OrdinalBase.TryGetValue(s[word.Length..], out var u) && u is >= 1 and <= 9)
            {
                number = value + u;
                return true;
            }
        }

        return false;
    }

    /// <summary>Lowercases and strips the Swedish diacritics (å, ä, ö).</summary>
    private static string Normalize(string token) => token.ToLowerInvariant()
        .Replace('å', 'a').Replace('ä', 'a').Replace('ö', 'o');
}
