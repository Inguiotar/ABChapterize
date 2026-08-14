// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using System.Collections.Concurrent;
using ABChapterize.Language.Parsers;

namespace ABChapterize.Language.Phrases;

/// <summary>
/// What the <c>()</c> token of a phrase expands to: every notation one chapter number can arrive
/// in. Three of them are the same in every language - plain digits, a digit with an ordinal suffix,
/// and a Roman numeral - and the fourth is the language's own spelled-out grammar, which each
/// <see cref="INumberWordParser"/> supplies as <see cref="INumberWordParser.NumberWordPattern"/>.
/// <para>
/// The pattern only delimits; it never decides. Whatever it captures is read back by
/// <see cref="NumberWordParser.TryExtractNumber"/>, which is the authority on the value, so the
/// pattern is written to admit a little too much rather than a little too little - a capture that
/// will not parse leaves an announcement whose number could not be read, which is a case this tool
/// already reports and re-reads, while a spelling the pattern misses is an announcement never seen
/// at all.
/// </para>
/// </summary>
internal static class NumberPattern
{
    /// <summary>
    /// The trailing period is part of the number on purpose. It is the whole of the one-letter
    /// Roman guard - "CHAPTER V. THE MOTHER OF TONGUES" is a numeral, "chapter I wrote" is not
    /// (see <see cref="NumberWordParser"/>) - and since the capture is what gets re-parsed, a
    /// period left outside it would take that evidence away. Everything else strips harmlessly.
    /// </summary>
    private const string OptionalPeriod = @"\.?";

    /// <summary>
    /// What must not follow the number: another letter or digit. Written as a lookahead rather than
    /// <c>\b</c> because the match may end on the optional period, where a word boundary is the
    /// wrong question. It also does the work of an ordering rule - a shorter alternative that would
    /// have swallowed only the head of a longer word ("drei" out of "dreizehn") fails here and the
    /// engine backtracks into the right one.
    /// </summary>
    private const string EndOfNumber = @"(?![\p{L}\p{N}])";

    /// <summary>
    /// The same at the front: a number may not start in the middle of a word. Needed because Roman
    /// numerals are ordinary letters, so without it the <em>tail</em> of a word reads as one -
    /// "Kaska<b>l.</b>" as 50, "A<b>D</b>" as 500, "Part<b>i</b>" as 1. Harmless where the phrase
    /// puts something in front of the group (<c>/chapter ()/</c> starts it after a space), and not
    /// harmless at all where the number opens the wording, which is exactly what a phrase written
    /// <c>/() chapter/</c> does. Found replaying the corpus transcripts, 2026-08-14.
    /// </summary>
    private const string StartOfNumber = @"(?<![\p{L}\p{N}])";

    /// <summary>Roman numerals, validated afterwards by <see cref="RomanNumerals"/> - the pattern
    /// only says "these letters and nothing else", which admits "MIX" and "DIM" as readily as
    /// "XIII". The length bound is the longest canonical numeral in range plus room to spare.</summary>
    private const string Roman = "[IVXLCDM]{1,15}";

    /// <summary>One to four digits: enough for any chapter number a book has ever had, and short
    /// enough that a year or a phone number in the prose cannot pose as one indefinitely. The
    /// notation is language-independent, and so is this bound.</summary>
    private const string Digits = @"\d{1,4}";

    /// <summary>Built per language and kept, since a run resolves one profile per file and the
    /// alternation of an entire number grammar is not worth rebuilding for each of them.</summary>
    private static readonly ConcurrentDictionary<string, string> Cache = new();

    /// <summary>
    /// The expansion of <c>()</c> for one language, as a bare fragment with no capturing group of
    /// its own - <see cref="PhraseCompiler"/> wraps it in the named group.
    /// </summary>
    /// <param name="language">Two-letter language code; an unregistered one falls back to English,
    /// exactly as the number parsing itself does.</param>
    internal static string For(string language)
        => Cache.GetOrAdd(LanguageRegistry.For(language).Code, Build);

    /// <summary>Assembles one language's expansion. The spoken words come first, deliberately: they
    /// and the Roman numerals collide - French "dix" is the word for ten and, read as Roman, the
    /// canonical spelling of 509 - and the spoken word is what the narrator actually said.</summary>
    /// <param name="code">The registered language's own code.</param>
    private static string Build(string code)
    {
        var words = LanguageRegistry.For(code).NumberParser.NumberWordPattern;
        return $@"{StartOfNumber}(?:{words}|{Digits}(?:{DigitOrdinalSuffixes()})?{OptionalPeriod}|" +
               $"{Roman}{OptionalPeriod}){EndOfNumber}";
    }

    /// <summary>
    /// Every registered language's digit-ordinal suffix, not just the one being resolved for.
    /// Whisper writes what it hears in whatever convention it likes, and
    /// <see cref="NumberWordParser"/> already reads them all - a pattern narrower than the parser
    /// behind it would reject text that parses perfectly well.
    /// </summary>
    private static string DigitOrdinalSuffixes()
        => string.Join('|', LanguageRegistry.Languages
            .Select(l => l.NumberParser.DigitOrdinalSuffixPattern)
            .Where(p => p.Length > 0)
            .Distinct());
}
