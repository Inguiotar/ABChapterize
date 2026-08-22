// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

namespace ABChapterize.Language.Parsers;

/// <summary>
/// Parses spoken number words (as transcribed by Whisper) of one language into integers.
/// Implementations cover the range 0-999, which is plenty for chapter numbers, and should
/// be lenient about transcription quirks (accents, alternate spellings, hyphenation).
/// Both cardinals and the language's ordinals are understood, since chapters may be
/// announced either way ("Chapter Twelve", "Erstes Kapitel", "Birinci Bölüm").
/// </summary>
public interface INumberWordParser
{
    /// <summary>Code of the language this parser understands, as <c>--lang</c> and Whisper spell it.</summary>
    string LanguageCode { get; }

    /// <summary>
    /// Regex alternation fragment (no capturing groups) matching this language's digit
    /// ordinal suffix, e.g. "st|nd|rd|th" for English or "'?(?:inci|nci|uncu|ncu)" for
    /// Turkish's optional apostrophe. <see cref="NumberWordParser"/> combines every
    /// parser's fragment into one regex, so a language that needs a separator (Turkish's
    /// apostrophe, Swedish's colon) must bake it into its own fragment rather than assume
    /// one is shared. Empty for languages whose digit ordinals are a bare number plus a
    /// trailing period ("2.", "17."), which the generic digit/period fallback already
    /// handles without any suffix at all.
    /// </summary>
    string DigitOrdinalSuffixPattern { get; }

    /// <summary>
    /// Regex fragment (no capturing groups) matching one spoken number of this language, cardinal
    /// or ordinal, written out in words - what the <c>()</c> token of a phrase expands to, together
    /// with the digit and Roman notations every language shares. Matched case-insensitively, and
    /// against raw transcript text, so a spelling this parser only reaches through its own
    /// <c>Normalize</c> has to be admitted here explicitly (<see cref="NumberWordPatterns.Alt"/>'s
    /// diacritics map does that).
    /// <para>
    /// Deliberately a <em>superset</em> of what <see cref="TryParse"/> accepts: the pattern only
    /// says where the number ends, and the captured text is then read by that method, which is the
    /// authority on its value. Admitting one word too many costs nothing; missing a real spelling
    /// costs a chapter.
    /// </para>
    /// </summary>
    string NumberWordPattern { get; }

    /// <summary>
    /// Tries to parse a spoken number from the given word tokens. The tokens are
    /// lowercase-insensitive raw words with surrounding punctuation already stripped;
    /// the number is expected to start at the first token, and trailing non-number
    /// tokens must be ignored.
    /// </summary>
    /// <param name="tokens">Word tokens, the number starting at index 0.</param>
    /// <param name="number">Receives the parsed number (0-999) on success.</param>
    /// <param name="consumed">
    /// Receives the count of leading tokens that form the number. Callers matching a
    /// number that must end at a known position (e.g. directly before the chapter
    /// phrase) use this to reject parses that leave trailing tokens unconsumed.
    /// A token only <em>partly</em> numeric counts for nothing here even though it
    /// contributes to <paramref name="number"/>: a hyphenated "twenty-odd" arrives as one
    /// token, and its value is worth reading after a chapter phrase while it is emphatically
    /// not a number ending at one. So a parse may legitimately return true with a
    /// <paramref name="consumed"/> that does not reach the token it stopped in, and the
    /// callers that demand an exact end reject it on exactly that.
    /// </param>
    /// <returns>True when the leading tokens form a number.</returns>
    bool TryParse(IReadOnlyList<string> tokens, out int number, out int consumed);
}
