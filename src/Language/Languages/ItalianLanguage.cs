// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Language.Parsers;

namespace ABChapterize.Language.Languages;

/// <summary>Italian.</summary>
public sealed class ItalianLanguage : ILanguage
{
    /// <inheritdoc/>
    public string Code => "it";

    /// <inheritdoc/>
    public string ChapterPhrase => "/capitolo/";

    /// <inheritdoc/>
    public string ChapterTitle => "Capitolo";

    /// <inheritdoc/>
    public string IntroTitle => "Introduzione";

    /// <inheritdoc/>
    public string ProloguePhrase => "/prologo/";

    /// <inheritdoc/>
    public string PrologueTitle => "Prologo";

    /// <inheritdoc/>
    public string EpiloguePhrase => "/epilogo/";

    /// <inheritdoc/>
    public string EpilogueTitle => "Epilogo";

    /// <inheritdoc/>
    public INumberWordParser NumberParser { get; } = new ItalianNumberParser();
}
