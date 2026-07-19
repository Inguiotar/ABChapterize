// Chapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

namespace Chapterize.Lang;

/// <summary>
/// Parses Polish spoken numbers 0-999: "siedem", "dwadzieścia jeden", "sto dwadzieścia
/// jeden", "dziewięćset dziewięćdziesiąt dziewięć" - hundreds, tens and units are
/// separate space-separated words with no connector. Ordinals are understood too, in the
/// masculine singular nominative form that agrees with "rozdział" ("Rozdział pierwszy",
/// "Rozdział dwudziesty pierwszy"): unlike the cardinal, a compound ordinal marks BOTH its
/// tens and units word as ordinal ("dwudziesty pierwszy" = 21st, not "dwadzieścia
/// pierwszy"), while any hundreds word ahead of it stays cardinal unless it is itself the
/// number's last component ("dwusetny" = 200th). Both accented and ASCII-stripped
/// spellings are accepted, since Whisper may emit either.
/// </summary>
public sealed class PolishNumberParser : INumberWordParser
{
    /// <inheritdoc/>
    public string LanguageCode => "pl";

    /// <summary>
    /// Polish digit ordinals are a bare number plus a trailing period ("21."), already
    /// handled by the generic digit/period fallback, so no suffix is needed here.
    /// </summary>
    /// <inheritdoc/>
    public string DigitOrdinalSuffixPattern => "";

    /// <summary>Cardinal units 0-9, keyed in normalized (accent-free) form.</summary>
    private static readonly Dictionary<string, int> Units = new()
    {
        ["zero"] = 0, ["jeden"] = 1, ["jedna"] = 1, ["dwa"] = 2, ["dwie"] = 2,
        ["trzy"] = 3, ["cztery"] = 4, ["piec"] = 5, ["szesc"] = 6, ["siedem"] = 7,
        ["osiem"] = 8, ["dziewiec"] = 9,
    };

    /// <summary>Cardinal teens 10-19.</summary>
    private static readonly Dictionary<string, int> Teens = new()
    {
        ["dziesiec"] = 10, ["jedenascie"] = 11, ["dwanascie"] = 12, ["trzynascie"] = 13,
        ["czternascie"] = 14, ["pietnascie"] = 15, ["szesnascie"] = 16,
        ["siedemnascie"] = 17, ["osiemnascie"] = 18, ["dziewietnascie"] = 19,
    };

    /// <summary>Cardinal tens 20-90.</summary>
    private static readonly Dictionary<string, int> Tens = new()
    {
        ["dwadziescia"] = 20, ["trzydziesci"] = 30, ["czterdziesci"] = 40,
        ["piecdziesiat"] = 50, ["szescdziesiat"] = 60, ["siedemdziesiat"] = 70,
        ["osiemdziesiat"] = 80, ["dziewiecdziesiat"] = 90,
    };

    /// <summary>Cardinal hundreds 100-900.</summary>
    private static readonly Dictionary<string, int> HundredsCardinal = new()
    {
        ["sto"] = 100, ["dwiescie"] = 200, ["trzysta"] = 300, ["czterysta"] = 400,
        ["piecset"] = 500, ["szescset"] = 600, ["siedemset"] = 700, ["osiemset"] = 800,
        ["dziewiecset"] = 900,
    };

    /// <summary>Ordinal units 1st-9th, masculine nominative singular.</summary>
    private static readonly Dictionary<string, int> UnitsOrdinal = new()
    {
        ["pierwszy"] = 1, ["drugi"] = 2, ["trzeci"] = 3, ["czwarty"] = 4, ["piaty"] = 5,
        ["szosty"] = 6, ["siodmy"] = 7, ["osmy"] = 8, ["dziewiaty"] = 9,
    };

    /// <summary>Ordinal teens 10th-19th, masculine nominative singular.</summary>
    private static readonly Dictionary<string, int> TeensOrdinal = new()
    {
        ["dziesiaty"] = 10, ["jedenasty"] = 11, ["dwunasty"] = 12, ["trzynasty"] = 13,
        ["czternasty"] = 14, ["pietnasty"] = 15, ["szesnasty"] = 16, ["siedemnasty"] = 17,
        ["osiemnasty"] = 18, ["dziewietnasty"] = 19,
    };

    /// <summary>Ordinal tens 20th-90th, masculine nominative singular.</summary>
    private static readonly Dictionary<string, int> TensOrdinal = new()
    {
        ["dwudziesty"] = 20, ["trzydziesty"] = 30, ["czterdziesty"] = 40,
        ["piecdziesiaty"] = 50, ["szescdziesiaty"] = 60, ["siedemdziesiaty"] = 70,
        ["osiemdziesiaty"] = 80, ["dziewiecdziesiaty"] = 90,
    };

    /// <summary>Ordinal hundreds 100th-900th, used only when the hundred is the last part.</summary>
    private static readonly Dictionary<string, int> HundredsOrdinal = new()
    {
        ["setny"] = 100, ["dwusetny"] = 200, ["trzysetny"] = 300, ["czterysetny"] = 400,
        ["piecsetny"] = 500, ["szescsetny"] = 600, ["siedemsetny"] = 700,
        ["osiemsetny"] = 800, ["dziewiecsetny"] = 900,
    };

    /// <inheritdoc/>
    public bool TryParse(IReadOnlyList<string> tokens, out int number, out int consumed)
    {
        number = 0;
        consumed = 0;
        var total = 0;
        var any = false;
        // 0 = expect hundreds/tens/units, 1 = hundreds seen, 2 = tens seen (cardinal), done after.
        var stage = 0;

        for (var i = 0; i < tokens.Count; i++)
        {
            var part = Normalize(tokens[i]);

            if (stage == 0 && HundredsOrdinal.TryGetValue(part, out var ho))
            {
                // A bare hundred ordinal is the whole number ("dwusetny" = 200th).
                total = ho;
                consumed = i + 1;
                return Finish(total, out number);
            }
            if (stage == 0 && HundredsCardinal.TryGetValue(part, out var hc))
            {
                total = hc;
                any = true;
                stage = 1;
                consumed = i + 1;
                continue;
            }
            if (stage <= 1 && TeensOrdinal.TryGetValue(part, out var teo))
            {
                total += teo;
                consumed = i + 1;
                return Finish(total, out number);
            }
            if (stage <= 1 && Teens.TryGetValue(part, out var te))
            {
                total += te;
                any = true;
                consumed = i + 1;
                return Finish(total, out number); // teens never combine further
            }
            if (stage <= 1 && TensOrdinal.TryGetValue(part, out var to))
            {
                total += to;
                any = true;
                consumed = i + 1;
                // A units ordinal may follow to complete a compound ("dwudziesty pierwszy").
                if (i + 1 < tokens.Count && UnitsOrdinal.TryGetValue(Normalize(tokens[i + 1]), out var uo))
                {
                    total += uo;
                    consumed = i + 2;
                }
                return Finish(total, out number);
            }
            if (stage <= 1 && Tens.TryGetValue(part, out var tc))
            {
                total += tc;
                any = true;
                stage = 2;
                consumed = i + 1;
                continue;
            }
            if (stage <= 1 && UnitsOrdinal.TryGetValue(part, out var uo2))
            {
                total += uo2;
                consumed = i + 1;
                return Finish(total, out number);
            }
            if (stage <= 2 && Units.TryGetValue(part, out var uc) && (stage != 2 || uc is >= 1 and <= 9))
            {
                total += uc;
                any = true;
                consumed = i + 1;
                return Finish(total, out number); // a bare unit always ends the number
            }
            break; // first non-fitting word ends the number
        }

        if (!any || total > 999)
            return false;
        number = total;
        return true;
    }

    /// <summary>Finalizes a successful parse, rejecting out-of-range totals.</summary>
    private static bool Finish(int total, out int number)
    {
        number = total;
        return total is >= 0 and <= 999;
    }

    /// <summary>Lowercases and strips the Polish diacritics (ą, ć, ę, ł, ń, ó, ś, ź, ż).</summary>
    private static string Normalize(string token) => token.ToLowerInvariant()
        .Replace('ą', 'a').Replace('ć', 'c').Replace('ę', 'e').Replace('ł', 'l')
        .Replace('ń', 'n').Replace('ó', 'o').Replace('ś', 's').Replace('ź', 'z').Replace('ż', 'z');
}
