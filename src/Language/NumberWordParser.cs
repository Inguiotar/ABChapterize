// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using System.Globalization;
using System.Text.RegularExpressions;
using ABChapterize.Language.Parsers;

namespace ABChapterize.Language;

/// <summary>
/// Extracts chapter numbers from transcribed text. Understands plain digits in any language,
/// digit ordinals ("2nd", "2e", "2ème"), Roman numerals (see <see cref="RomanNumerals"/>), and
/// spoken number words (0-999, cardinal and
/// ordinal) via the per-language parsers in <see cref="ABChapterize.Language.Parsers"/>; unknown language codes fall
/// back to the English parser. Numbers can be extracted after the chapter phrase
/// ("Chapter Seven") or before it ("Erstes Kapitel", "2. Kapitel", "Birinci Bölüm").
/// </summary>
public static class NumberWordParser
{
    /// <summary>
    /// One tokenized word, plus whether the transcript wrote a period directly after it - the
    /// single piece of punctuation this parser cannot afford to discard, because it is what
    /// separates a one-letter Roman numeral from an ordinary word (see
    /// <see cref="TryParseRoman"/>).
    /// </summary>
    /// <param name="Text">The word with its surrounding punctuation stripped.</param>
    /// <param name="TrailingPeriod">Whether a "." directly followed it before stripping.</param>
    private readonly record struct Token(string Text, bool TrailingPeriod);

    /// <summary>
    /// Matches a digit ordinal: 1-3 digits plus the ordinal suffix of any registered
    /// language's parser ("2nd", "1er", "2e", "2ème", "2de", "2ste", "5'inci", "3º",
    /// "21:a"). Assembled from each parser's own <see cref="INumberWordParser.DigitOrdinalSuffixPattern"/>
    /// rather than hardcoded here, so a language brings its own suffixes (and any
    /// separator it needs, like Turkish's optional apostrophe or Swedish's mandatory
    /// colon) simply by declaring them.
    /// </summary>
    private static readonly Regex DigitOrdinalRegex = BuildDigitOrdinalRegex();

    /// <summary>Builds <see cref="DigitOrdinalRegex"/> from every registered parser's suffix fragment.</summary>
    private static Regex BuildDigitOrdinalRegex()
    {
        var fragments = LanguageRegistry.Languages
            .Select(l => l.NumberParser.DigitOrdinalSuffixPattern)
            .Where(p => p.Length > 0)
            .Distinct();
        var pattern = $@"^(\d{{1,3}})(?:{string.Join('|', fragments)})$";
        return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// Tries to extract a number from the beginning of <paramref name="text"/>,
    /// which is the transcribed text immediately following the chapter phrase.
    /// </summary>
    /// <param name="text">Text following the matched chapter phrase.</param>
    /// <param name="language">Two-letter language code steering number-word parsing.</param>
    /// <param name="number">Receives the extracted number on success.</param>
    /// <returns>True when a number could be extracted.</returns>
    public static bool TryExtractNumber(string text, string language, out int number)
    {
        number = 0;
        var tokens = Tokenize(text);
        if (tokens.Count == 0)
            return false;

        // Digits always win, regardless of language ("Chapter 12.").
        if (TryParseDigits(tokens[0].Text, out number))
            return true;

        var parser = LanguageRegistry.For(language).NumberParser;
        if (parser.TryParse(Words(tokens), out number, out _))
            return true;

        // Roman numerals last, and only where the language's own words found nothing: the two
        // notations collide. French "dix" is the word for ten and, read as Roman, the canonical
        // spelling of 509 - the spoken word is what the narrator said, so it has to win.
        return TryParseRoman(tokens[0], out number);
    }

    /// <summary>
    /// Tries to extract a number from the end of <paramref name="text"/>, which is the
    /// transcribed text immediately preceding the chapter phrase — the "Erstes Kapitel" /
    /// "Birinci Bölüm" / "2. Kapitel" announcement order. The number must end exactly at
    /// the phrase, so a number that merely occurs earlier in the sentence does not count.
    /// </summary>
    /// <param name="text">Text preceding the matched chapter phrase.</param>
    /// <param name="language">Two-letter language code steering number-word parsing.</param>
    /// <param name="number">Receives the extracted number on success.</param>
    /// <returns>True when a number could be extracted.</returns>
    public static bool TryExtractNumberBefore(string text, string language, out int number)
    {
        number = 0;
        var tokens = TokenizeTail(text);
        if (tokens.Count == 0)
            return false;

        // Digits directly before the phrase: "2. Kapitel", "3rd chapter".
        if (TryParseDigits(tokens[^1].Text, out number))
            return true;

        // Try every suffix of the token window; accept only a parse that consumes
        // everything up to the phrase ("sagte drei. Kapitel" must not yield 3).
        var parser = LanguageRegistry.For(language).NumberParser;
        var words = Words(tokens);
        for (var start = 0; start < tokens.Count; start++)
        {
            var slice = words.GetRange(start, words.Count - start);
            if (parser.TryParse(slice, out number, out var consumed)
                && start + consumed == tokens.Count)
                return true;
        }

        // "XIII. Kapitel" - and last, for the same collision reason as in TryExtractNumber.
        if (TryParseRoman(tokens[^1], out number))
            return true;

        number = 0;
        return false;
    }

    /// <summary>
    /// Finds an unambiguous Roman numeral anywhere in <paramref name="text"/>, rather than only at
    /// the end nearest a chapter phrase. Written for a pre-existing marking's <em>title</em>
    /// (<see cref="ABChapterize.Detection.MarkingTitleNumber"/>), where the number can sit behind a
    /// heading the phrase-anchored rules do not reach - and only ever reached there once those have
    /// found nothing, since scanning a whole string for something that is also a word is a last
    /// resort by nature. The one-letter guard applies exactly as everywhere else, so a stray "I" or
    /// "C" in a title is not a chapter number unless it is written like a heading.
    /// </summary>
    /// <param name="text">The text to scan.</param>
    /// <param name="number">Receives the parsed number on success.</param>
    /// <returns>True when some token is an unambiguous Roman numeral.</returns>
    public static bool TryExtractRomanAnywhere(string text, out int number)
    {
        foreach (var token in Tokenize(text, int.MaxValue))
            if (TryParseRoman(token, out number))
                return true;
        number = 0;
        return false;
    }

    /// <summary>
    /// Parses a token as a Roman numeral ("XIII"), subject to the one-letter guard below. Like
    /// digits, and unlike spoken words, the notation is the same in every language - but unlike
    /// digits it overlaps with real words, so every caller tries it <em>after</em> the language's
    /// own number-word parser rather than before.
    /// </summary>
    /// <param name="token">The token to parse, with its trailing-period flag.</param>
    /// <param name="number">Receives the parsed number on success.</param>
    private static bool TryParseRoman(Token token, out int number)
    {
        number = 0;
        if (!RomanNumerals.TryParse(token.Text, out var parsed) || !RomanNumeralIsUnambiguous(token))
            return false;
        number = parsed;
        return true;
    }

    /// <summary>
    /// Whether a token that <em>parses</em> as a Roman numeral may be read as one here. Only
    /// one-letter numerals are in doubt, and only they are gated: I, V, X, L, C, D and M are all
    /// ordinary words or initials somewhere ("in that chapter I wrote…", Polish "rozdział i
    /// epilog", where "i" means "and"), and reading one as a number would plant a chapter mark in
    /// the middle of prose. A trailing period resolves it, because that is how a transcript writes
    /// a heading number and not how it writes a pronoun.
    /// <para>
    /// Two letters or more need no such test: <see cref="RomanNumerals.TryParse"/> accepts only
    /// canonical spellings, and almost nothing that is also a word survives that
    /// ("DIM", "LID", "CIVIC", "MILD" are all non-canonical; "MIX" is 1009 and out of range).
    /// </para>
    /// <para>
    /// The rule is what real transcripts do, not a guess: on "I Shall Wear Midnight" (2026-07-30)
    /// Whisper wrote the one-letter cases as "CHAPTER V. THE MOTHER OF TONGUES" and "CHAPTER X.
    /// THE MELTING GIRL" - both with the period - while the multi-letter "CHAPTER VII SONGS IN THE
    /// NIGHT" came without one. The cost of being wrong is asymmetric and points the same way: a
    /// missed one-letter numeral is one chapter pass 3 still gets a shot at, whereas a false
    /// chapter 1 read out of an English pronoun displaces a real mark.
    /// </para>
    /// </summary>
    /// <param name="token">The token that parsed as a Roman numeral.</param>
    private static bool RomanNumeralIsUnambiguous(Token token)
        => token.Text.Length > 1 || token.TrailingPeriod;

    /// <summary>Parses a token that is a plain digit number or a digit ordinal ("2nd", "2e").</summary>
    /// <param name="token">The token text, punctuation already stripped.</param>
    /// <param name="number">Receives the parsed number on success.</param>
    private static bool TryParseDigits(string token, out int number)
    {
        if (int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out number))
            return true;
        var m = DigitOrdinalRegex.Match(token);
        if (m.Success)
        {
            number = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            return true;
        }
        number = 0;
        return false;
    }

    /// <summary>The bare words of a token list, which is all an <see cref="INumberWordParser"/>
    /// ever needs - the trailing-period flag exists solely for <see cref="TryParseRoman"/>.</summary>
    /// <param name="tokens">The tokens to strip down.</param>
    private static List<string> Words(List<Token> tokens)
        => tokens.ConvertAll(t => t.Text);

    /// <summary>
    /// Splits text into words, stripping surrounding punctuation. Only the first few tokens are
    /// relevant to a number following a phrase, so tokenization stops after five words by default.
    /// </summary>
    /// <param name="text">The text to split.</param>
    /// <param name="limit">How many tokens to keep; <see cref="TryExtractRomanAnywhere"/> lifts it,
    /// having a whole title to scan rather than the words right after a phrase.</param>
    private static List<Token> Tokenize(string text, int limit = 5)
    {
        var tokens = new List<Token>();
        foreach (var raw in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (MakeToken(raw) is { } token)
                tokens.Add(token);
            if (tokens.Count >= limit)
                break;
        }
        return tokens;
    }

    /// <summary>Splits text into words like <see cref="Tokenize"/>, keeping the LAST five.</summary>
    /// <param name="text">The text preceding the chapter phrase.</param>
    private static List<Token> TokenizeTail(string text)
    {
        var tokens = new List<Token>();
        foreach (var raw in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (MakeToken(raw) is { } token)
                tokens.Add(token);
            if (tokens.Count > 5)
                tokens.RemoveAt(0);
        }
        return tokens;
    }

    /// <summary>Turns one raw whitespace-delimited word into a <see cref="Token"/>, or null when
    /// nothing but punctuation is left of it.</summary>
    /// <param name="raw">The word as the transcript wrote it, punctuation included.</param>
    private static Token? MakeToken(string raw)
    {
        var text = TrimPunctuation(raw);
        if (text.Length == 0)
            return null;
        // Read off the raw word, and tolerant of what closes around it: a heading number lands as
        // "XIII." mid-line but as "V.\"" or "X.)" inside a quote or bracket, and all of those are
        // the same period as far as the numeral is concerned.
        return new Token(text, raw.TrimEnd(ClosingPunctuation).EndsWith('.'));
    }

    /// <summary>Strips the punctuation Whisper attaches to words.</summary>
    /// <param name="raw">The word as the transcript wrote it.</param>
    private static string TrimPunctuation(string raw) =>
        raw.Trim('.', ',', ':', ';', '!', '?', '"', '\'', '(', ')', '…', '„', '“', '”');

    /// <summary>The punctuation that may close around a word <em>after</em> its own period, and so
    /// has to come off before <see cref="MakeToken"/> can see that period. Deliberately excludes
    /// "." itself, which is the thing being looked for.</summary>
    private static readonly char[] ClosingPunctuation =
        [',', ':', ';', '!', '?', '"', '\'', ')', '…', '“', '”'];
}
