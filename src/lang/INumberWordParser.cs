namespace Chapterize.Lang;

/// <summary>
/// Parses spoken number words (as transcribed by Whisper) of one language into integers.
/// Implementations cover the range 0-999, which is plenty for chapter numbers, and should
/// be lenient about transcription quirks (accents, alternate spellings, hyphenation).
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
    /// <returns>True when the leading tokens form a number.</returns>
    bool TryParse(IReadOnlyList<string> tokens, out int number);
}
