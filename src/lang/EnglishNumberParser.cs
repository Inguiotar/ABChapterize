// Chapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

namespace Chapterize.Lang;

/// <summary>
/// Parses English spoken numbers 0-999: "seven", "twenty-one", "one hundred and five",
/// "three hundred twenty-one". Hyphenated and space-separated tens work equally.
/// Ordinals are understood too ("first", "twenty-first", "one hundredth"), since a
/// chapter may be announced as "the first chapter" rather than "chapter one".
/// </summary>
public sealed class EnglishNumberParser : INumberWordParser
{
    /// <inheritdoc/>
    public string LanguageCode => "en";

    /// <summary>Simple number words 0-19.</summary>
    private static readonly Dictionary<string, int> Units = new()
    {
        ["zero"] = 0, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4,
        ["five"] = 5, ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9,
        ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13,
        ["fourteen"] = 14, ["fifteen"] = 15, ["sixteen"] = 16, ["seventeen"] = 17,
        ["eighteen"] = 18, ["nineteen"] = 19,
    };

    /// <summary>Tens words 20-90.</summary>
    private static readonly Dictionary<string, int> Tens = new()
    {
        ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40, ["fifty"] = 50,
        ["sixty"] = 60, ["seventy"] = 70, ["eighty"] = 80, ["ninety"] = 90,
    };

    /// <summary>
    /// Ordinal words that add their value and end the number ("first", "twentieth").
    /// "hundredth" is handled separately because it multiplies like "hundred".
    /// </summary>
    private static readonly Dictionary<string, int> Ordinals = new()
    {
        ["zeroth"] = 0, ["first"] = 1, ["second"] = 2, ["third"] = 3, ["fourth"] = 4,
        ["fifth"] = 5, ["sixth"] = 6, ["seventh"] = 7, ["eighth"] = 8, ["ninth"] = 9,
        ["tenth"] = 10, ["eleventh"] = 11, ["twelfth"] = 12, ["thirteenth"] = 13,
        ["fourteenth"] = 14, ["fifteenth"] = 15, ["sixteenth"] = 16,
        ["seventeenth"] = 17, ["eighteenth"] = 18, ["nineteenth"] = 19,
        ["twentieth"] = 20, ["thirtieth"] = 30, ["fortieth"] = 40, ["fiftieth"] = 50,
        ["sixtieth"] = 60, ["seventieth"] = 70, ["eightieth"] = 80, ["ninetieth"] = 90,
    };

    /// <inheritdoc/>
    public bool TryParse(IReadOnlyList<string> tokens, out int number, out int consumed)
    {
        number = 0;
        consumed = 0;
        var current = 0;
        var any = false;
        var done = false; // an ordinal word ends the number

        for (var i = 0; i < tokens.Count && !done; i++)
        {
            // "twenty-one" arrives as one token; process its parts in order.
            var parts = tokens[i].ToLowerInvariant().Split('-', StringSplitOptions.RemoveEmptyEntries);
            var numeric = false; // did this token contribute to the value?
            var ok = parts.Length > 0;
            foreach (var part in parts)
            {
                if (Units.TryGetValue(part, out var u))
                {
                    current += u;
                    numeric = any = true;
                }
                else if (Tens.TryGetValue(part, out var t))
                {
                    current += t;
                    numeric = any = true;
                }
                else if (part == "hundred")
                {
                    current = (current == 0 ? 1 : current) * 100;
                    numeric = any = true;
                }
                else if (Ordinals.TryGetValue(part, out var o))
                {
                    current += o;
                    numeric = any = done = true;
                    break;
                }
                else if (part == "hundredth")
                {
                    current = (current == 0 ? 1 : current) * 100;
                    numeric = any = done = true;
                    break;
                }
                else if (part == "and" && any)
                {
                    // "one hundred and five" - only meaningful once a number has started.
                }
                else
                {
                    ok = false;
                    break;
                }
            }
            if (!ok)
                break;
            if (numeric)
                consumed = i + 1; // connector-only tokens don't extend the number by themselves
        }

        if (!any || current > 999)
            return false;
        number = current;
        return true;
    }
}
