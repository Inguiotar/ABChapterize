// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Language.Parsers;

namespace ABChapterize.Language.Languages;

/// <summary>Swedish.</summary>
public sealed class SwedishLanguage : ILanguage
{
    /// <inheritdoc/>
    public string Code => "sv";

    /// <summary>
    /// Covers both the indefinite "kapitel" ("Kapitel sju") and the definite "kapitlet", which
    /// is what an ordinal announcement uses ("Första kapitlet"). The definite form drops the
    /// stem's second e, so it is not reachable by a suffix and needs the alternation.
    /// </summary>
    /// <inheritdoc/>
    public string ChapterPhrase => "/(?:^kapit(?:el|let) ()|^() kapit(?:el|let)|^kapit(?:el|let))/";

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
    public INumberWordParser NumberParser { get; } = new SwedishNumberParser();
}
