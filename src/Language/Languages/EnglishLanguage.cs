// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Language.Parsers;

namespace ABChapterize.Language.Languages;

/// <summary>
/// English, and the fallback every language without an entry in <see cref="LanguageRegistry"/>
/// resolves to - so these values are also what an audiobook in an unsupported language is
/// processed with.
/// </summary>
public sealed class EnglishLanguage : ILanguage
{
    /// <inheritdoc/>
    public string Code => "en";

    /// <inheritdoc/>
    public string ChapterPhrase => "/(?:^chapter ()|^() chapter|^chapter)/";

    /// <inheritdoc/>
    public string ChapterTitle => "Chapter";

    /// <inheritdoc/>
    public string PartTitle => "Part";

    /// <inheritdoc/>
    public string IntroTitle => "Intro";

    /// <summary>Also covers the American "prolog", being a substring of "prologue".</summary>
    /// <inheritdoc/>
    public string ProloguePhrase => "/prolog/";

    /// <inheritdoc/>
    public string PrologueTitle => "Prologue";

    /// <summary>Also covers the American "epilog" - see <see cref="ProloguePhrase"/>.</summary>
    /// <inheritdoc/>
    public string EpiloguePhrase => "/epilog/";

    /// <inheritdoc/>
    public string EpilogueTitle => "Epilogue";

    /// <inheritdoc/>
    public INumberWordParser NumberParser { get; } = new EnglishNumberParser();
}
