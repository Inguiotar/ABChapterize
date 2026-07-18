namespace Chapterize.Lang;

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
