// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Language.Parsers;

namespace ABChapterize.Language.Languages;

/// <summary>German. "Kapitel" is invariant in the nominative and accusative a narrator
/// announces it in ("Kapitel eins", "Erstes Kapitel"), so no spelling variants are needed.</summary>
public sealed class GermanLanguage : ILanguage
{
    /// <inheritdoc/>
    public string Code => "de";

    /// <inheritdoc/>
    public string ChapterPhrase => "/(?:^kapitel ()|^() kapitel|^kapitel)/";

    /// <inheritdoc/>
    public string ChapterTitle => "Kapitel";

    /// <inheritdoc/>
    public string PartTitle => "Teil";

    /// <inheritdoc/>
    public string IntroTitle => "Intro";

    /// <inheritdoc/>
    public string ProloguePhrase => "/prolog/";

    /// <inheritdoc/>
    public string PrologueTitle => "Prolog";

    /// <inheritdoc/>
    public string EpiloguePhrase => "/epilog/";

    /// <inheritdoc/>
    public string EpilogueTitle => "Epilog";

    /// <inheritdoc/>
    public INumberWordParser NumberParser { get; } = new GermanNumberParser();
}
