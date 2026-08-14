// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Language.Parsers;

namespace ABChapterize.Language.Languages;

/// <summary>Dutch.</summary>
public sealed class DutchLanguage : ILanguage
{
    /// <inheritdoc/>
    public string Code => "nl";

    /// <inheritdoc/>
    public string ChapterPhrase => "/(?:^hoofdstuk ()|^hoofdstuk)/";

    /// <inheritdoc/>
    public string ChapterTitle => "Hoofdstuk";

    /// <inheritdoc/>
    public string PartTitle => "Deel";

    /// <inheritdoc/>
    public string IntroTitle => "Intro";

    /// <inheritdoc/>
    public string ProloguePhrase => "/proloog/";

    /// <inheritdoc/>
    public string PrologueTitle => "Proloog";

    /// <inheritdoc/>
    public string EpiloguePhrase => "/epiloog/";

    /// <inheritdoc/>
    public string EpilogueTitle => "Epiloog";

    /// <inheritdoc/>
    public INumberWordParser NumberParser { get; } = new DutchNumberParser();
}
