// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using System.Text;

namespace ABChapterize.Language;

/// <summary>
/// Parses Roman numerals written out as text ("XIII"). Language-independent, exactly like plain
/// digits: a narrator says "chapter thirteen" and Whisper decides for itself whether to write
/// that as "13", as "thirteen", or - in the all-caps heading style it likes for chapter
/// announcements - as "XIII". Which form comes out is not a property of the book or the language
/// but of how the audio happened to be framed, so every language needs this and none of them
/// needs its own version of it.
/// </summary>
/// <remarks>
/// Found the hard way on "I Shall Wear Midnight" (2026-07-30). Whisper transcribed the same
/// announcement as <c>"CHAPTER XIII. THE SHAKING OF THE SHEETS"</c> from a window starting at the
/// silence before it, and as <c>"Chapter 13 The Shaking of the Sheets"</c> from one starting 4.8 s
/// earlier - same audio, same model, same window length. Chapters 5, 6, 7, 10, 11 and 13 all came
/// out Roman in at least one pass; chapter 4 came out as the word "FOUR" and was found first try.
/// Chapter 13 was the one that drew the Roman form in every pass that saw it - pass 2's forward
/// sweep, the skipped-candidate re-probe and pass 3's full gap transcription alike - and was the
/// only chapter the run failed to mark.
/// </remarks>
public static class RomanNumerals
{
    /// <summary>
    /// Largest value accepted. Matches the 0-999 range the per-language spoken-number parsers
    /// cover, so no announcement form reaches further than another, and it usefully rejects the
    /// only ordinary English word that is also a canonical Roman numeral: "MIX" is 1009.
    /// </summary>
    private const int MaxValue = 999;

    /// <summary>Value/symbol table in descending order, subtractive pairs included - the standard
    /// greedy formatting table, which is what makes <see cref="Format"/> produce exactly one
    /// spelling per number and therefore what makes the canonical check in
    /// <see cref="TryParse(string,out int)"/> total.</summary>
    private static readonly (int Value, string Symbol)[] Table =
    [
        (900, "CM"), (500, "D"), (400, "CD"), (100, "C"),
        (90, "XC"), (50, "L"), (40, "XL"), (10, "X"),
        (9, "IX"), (5, "V"), (4, "IV"), (1, "I")
    ];

    /// <summary>
    /// Parses a token as a Roman numeral, accepting only the <em>canonical</em> spelling of a
    /// value in 1..<see cref="MaxValue"/>. "IIII", "XXXX" and "IC" are rejected although a lenient
    /// reader could assign them a value: this parser's input is arbitrary transcribed prose, so its
    /// job is less to be generous about numerals than to be certain a word is one at all. Requiring
    /// the exact output of <see cref="Format"/> reduces "is this a numeral?" to a string
    /// comparison, and takes most Latin-lettered words out of the running for free - "DIM", "LID",
    /// "CIVIC" and "MILD" all parse to nothing here.
    /// </summary>
    /// <param name="token">A single whitespace-delimited word, punctuation already stripped.</param>
    /// <param name="value">Receives the parsed value on success.</param>
    /// <returns>True when <paramref name="token"/> is a canonical Roman numeral in range.</returns>
    public static bool TryParse(string token, out int value)
    {
        value = 0;
        // MaxValue is three symbols of hundreds plus three of tens plus three of ones at worst
        // ("DCCCLXXXVIII" is 12); the bound only keeps a pathological token out of the loop below.
        if (token.Length is 0 or > 15)
            return false;

        var upper = token.ToUpperInvariant();
        var total = 0;
        var previous = 0;
        for (var i = upper.Length - 1; i >= 0; i--)
        {
            var digit = SymbolValue(upper[i]);
            if (digit == 0)
                return false;
            // Subtractive notation reads right to left as "smaller than the one to my right means
            // subtract", which is the whole of the arithmetic; the canonical check below is what
            // rejects the spellings this permissive rule would otherwise let through.
            total += digit < previous ? -digit : digit;
            previous = Math.Max(previous, digit);
        }

        if (total is < 1 or > MaxValue || Format(total) != upper)
            return false;
        value = total;
        return true;
    }

    /// <summary>Value of a single Roman symbol, or 0 when the character is not one.</summary>
    /// <param name="symbol">An upper-case character from the token being parsed.</param>
    private static int SymbolValue(char symbol) => symbol switch
    {
        'I' => 1, 'V' => 5, 'X' => 10, 'L' => 50, 'C' => 100, 'D' => 500, 'M' => 1000, _ => 0
    };

    /// <summary>Writes a value in canonical Roman notation - the reference spelling
    /// <see cref="TryParse(string,out int)"/> validates against. Internal rather than private so the
    /// tests can round-trip the whole accepted range against it.</summary>
    /// <param name="value">The value to format.</param>
    internal static string Format(int value)
    {
        var text = new StringBuilder();
        foreach (var (amount, symbol) in Table)
            while (value >= amount)
            {
                text.Append(symbol);
                value -= amount;
            }
        return text.ToString();
    }
}
