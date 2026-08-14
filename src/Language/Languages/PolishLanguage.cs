// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Language.Parsers;

namespace ABChapterize.Language.Languages;

/// <summary>Polish.</summary>
public sealed class PolishLanguage : ILanguage
{
    /// <inheritdoc/>
    public string Code => "pl";

    /// <summary>
    /// Stops at the stem so every case ending is covered by the substring match: "rozdział",
    /// "rozdziale", "rozdziału". The bare "l" alternative catches a transcript that writes the
    /// barred ł as a plain l.
    /// </summary>
    /// <inheritdoc/>
    public string ChapterPhrase => "/(?:^rozdzia[łl] ()|^rozdzia[łl])/";

    /// <inheritdoc/>
    public string ChapterTitle => "Rozdział";

    /// <inheritdoc/>
    public string PartTitle => "Część";

    /// <inheritdoc/>
    public string IntroTitle => "Wstęp";

    /// <inheritdoc/>
    public string ProloguePhrase => "/prolog/";

    /// <inheritdoc/>
    public string PrologueTitle => "Prolog";

    /// <inheritdoc/>
    public string EpiloguePhrase => "/epilog/";

    /// <inheritdoc/>
    public string EpilogueTitle => "Epilog";

    /// <inheritdoc/>
    public INumberWordParser NumberParser { get; } = new PolishNumberParser();
}
