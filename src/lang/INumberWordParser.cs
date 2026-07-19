// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

namespace ABChapterize.Lang;

/// <summary>
/// Parses spoken number words (as transcribed by Whisper) of one language into integers.
/// Implementations cover the range 0-999, which is plenty for chapter numbers, and should
/// be lenient about transcription quirks (accents, alternate spellings, hyphenation).
/// Both cardinals and the language's ordinals are understood, since chapters may be
/// announced either way ("Chapter Twelve", "Erstes Kapitel", "Birinci Bölüm").
/// </summary>
public interface INumberWordParser
{
    /// <summary>Two-letter ISO 639-1 code of the language this parser understands.</summary>
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
    /// </param>
    /// <returns>True when the leading tokens form a number.</returns>
    bool TryParse(IReadOnlyList<string> tokens, out int number, out int consumed);
}
