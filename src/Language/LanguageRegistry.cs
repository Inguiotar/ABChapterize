// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Language.Languages;

namespace ABChapterize.Language;

/// <summary>
/// The one list of every language ABChapterize knows. Adding a language means writing an
/// <see cref="ILanguage"/> implementation and adding it to <see cref="All"/> - nothing else in
/// the codebase enumerates languages, and nothing else needs editing. See
/// <c>doc\adding-a-language.md</c>.
/// </summary>
public static class LanguageRegistry
{
    /// <summary>
    /// Every supported language, in no particular order - <see cref="SupportedCodes"/> sorts for
    /// display. Kept as a plain array literal so the whole list is one obvious place to edit.
    /// </summary>
    private static readonly ILanguage[] All =
    [
        new EnglishLanguage(),
        new GermanLanguage(),
        new FrenchLanguage(),
        new SpanishLanguage(),
        new ItalianLanguage(),
        new DutchLanguage(),
        new TurkishLanguage(),
        new PortugueseLanguage(),
        new PolishLanguage(),
        new SwedishLanguage(),
        new DanishLanguage(),
        new NorwegianLanguage(),
        new CzechLanguage(),
    ];

    /// <summary>Lookup by ISO 639-1 code, case-insensitive because Whisper's detected code and a
    /// hand-typed <c>--lang</c> do not agree on casing.</summary>
    private static readonly Dictionary<string, ILanguage> ByCode =
        All.ToDictionary(l => l.Code, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// What an unsupported language code resolves to. English rather than "nothing" because a
    /// run must still produce marks for, say, a Czech audiobook: the spoken numbers will not
    /// parse, but digits in the transcript still will, and the file is processed rather than
    /// refused.
    /// </summary>
    public static ILanguage Fallback { get; } = ByCode["en"];

    /// <summary>Every registered language.</summary>
    public static IReadOnlyList<ILanguage> Languages => All;

    /// <summary>Registered language codes, sorted, for help and documentation output.</summary>
    public static IEnumerable<string> SupportedCodes => ByCode.Keys.Order();

    /// <summary>
    /// The language for a code, or <see cref="Fallback"/> when it is not registered.
    /// </summary>
    /// <param name="code">The code as <c>--lang</c> and Whisper spell it (never "auto" - resolve
    /// that first).</param>
    public static ILanguage For(string code) => ByCode.GetValueOrDefault(code, Fallback);
}
