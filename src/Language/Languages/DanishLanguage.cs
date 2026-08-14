// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Language.Parsers;

namespace ABChapterize.Language.Languages;

/// <summary>Danish, which spells its chapter word and both story-part words like
/// <see cref="SwedishLanguage"/> does - including the definite "kapitlet".</summary>
public sealed class DanishLanguage : ILanguage
{
    /// <inheritdoc/>
    public string Code => "da";

    /// <inheritdoc/>
    public string ChapterPhrase => "/(?:^kapit(?:el|let) ()|^kapit(?:el|let))/";

    /// <inheritdoc/>
    public string ChapterTitle => "Kapitel";

    /// <inheritdoc/>
    public string PartTitle => "Del";

    /// <inheritdoc/>
    public string IntroTitle => "Introduktion";

    /// <inheritdoc/>
    public string ProloguePhrase => "/prolog/";

    /// <inheritdoc/>
    public string PrologueTitle => "Prolog";

    /// <inheritdoc/>
    public string EpiloguePhrase => "/epilog/";

    /// <inheritdoc/>
    public string EpilogueTitle => "Epilog";

    /// <inheritdoc/>
    public INumberWordParser NumberParser { get; } = new DanishNumberParser();
}
