// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Language.Parsers;

namespace ABChapterize.Language.Languages;

/// <summary>French.</summary>
public sealed class FrenchLanguage : ILanguage
{
    /// <inheritdoc/>
    public string Code => "fr";

    /// <inheritdoc/>
    public string ChapterPhrase => "/(?:chapitre ()|chapitre)/";

    /// <inheritdoc/>
    public string ChapterTitle => "Chapitre";

    /// <inheritdoc/>
    public string PartTitle => "Partie";

    /// <inheritdoc/>
    public string IntroTitle => "Introduction";

    /// <inheritdoc/>
    public string ProloguePhrase => "/prologue/";

    /// <inheritdoc/>
    public string PrologueTitle => "Prologue";

    /// <summary>Accepts the unaccented "epilogue" as well: Whisper drops the leading
    /// accent often enough that a run would otherwise miss the mark entirely.</summary>
    /// <inheritdoc/>
    public string EpiloguePhrase => "/[ée]pilogue/";

    /// <inheritdoc/>
    public string EpilogueTitle => "Épilogue";

    /// <inheritdoc/>
    public INumberWordParser NumberParser { get; } = new FrenchNumberParser();
}
