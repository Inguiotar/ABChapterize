namespace Chapterize.Lang;

/// <summary>
/// Parses English spoken numbers 0-999: "seven", "twenty-one", "one hundred and five",
/// "three hundred twenty-one". Hyphenated and space-separated tens work equally.
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

    /// <inheritdoc/>
    public bool TryParse(IReadOnlyList<string> tokens, out int number)
    {
        number = 0;
        var current = 0;
        var any = false;

        foreach (var token in tokens)
        {
            // "twenty-one" arrives as one token; process its parts in order.
            var parts = token.ToLowerInvariant().Split('-', StringSplitOptions.RemoveEmptyEntries);
            var consumed = false;
            foreach (var part in parts)
            {
                if (Units.TryGetValue(part, out var u))
                {
                    current += u;
                    consumed = any = true;
                }
                else if (Tens.TryGetValue(part, out var t))
                {
                    current += t;
                    consumed = any = true;
                }
                else if (part == "hundred")
                {
                    current = (current == 0 ? 1 : current) * 100;
                    consumed = any = true;
                }
                else if (part == "and" && any)
                {
                    // "one hundred and five" - only meaningful once a number has started.
                    consumed = true;
                }
                else
                {
                    consumed = false;
                    break;
                }
            }
            if (!consumed)
                break;
        }

        if (!any || current > 999)
            return false;
        number = current;
        return true;
    }
}
