// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Language.Parsers;

namespace ABChapterize.Language.Languages;

/// <summary>
/// Portuguese, both European and Brazilian - the three phrases are spelled identically in each.
/// Their accents are optional for the same reason as in <see cref="SpanishLanguage"/>.
/// </summary>
public sealed class PortugueseLanguage : ILanguage
{
    /// <inheritdoc/>
    public string Code => "pt";

    /// <inheritdoc/>
    public string ChapterPhrase => "/cap[íi]tulo/";

    /// <inheritdoc/>
    public string ChapterTitle => "Capítulo";

    /// <inheritdoc/>
    public string PartTitle => "Parte";

    /// <inheritdoc/>
    public string IntroTitle => "Introdução";

    /// <inheritdoc/>
    public string ProloguePhrase => "/pr[óo]logo/";

    /// <inheritdoc/>
    public string PrologueTitle => "Prólogo";

    /// <inheritdoc/>
    public string EpiloguePhrase => "/ep[íi]logo/";

    /// <inheritdoc/>
    public string EpilogueTitle => "Epílogo";

    /// <inheritdoc/>
    public INumberWordParser NumberParser { get; } = new PortugueseNumberParser();
}
