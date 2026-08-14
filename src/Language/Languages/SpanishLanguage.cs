// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Language.Parsers;

namespace ABChapterize.Language.Languages;

/// <summary>
/// Spanish. All three phrases carry their accent optionally: a Whisper transcript that writes
/// "capitulo" instead of "capítulo" is common, and a missed accent must not cost a chapter mark.
/// </summary>
public sealed class SpanishLanguage : ILanguage
{
    /// <inheritdoc/>
    public string Code => "es";

    /// <inheritdoc/>
    public string ChapterPhrase => "/(?:^cap[íi]tulo ()|^cap[íi]tulo)/";

    /// <inheritdoc/>
    public string ChapterTitle => "Capítulo";

    /// <inheritdoc/>
    public string PartTitle => "Parte";

    /// <inheritdoc/>
    public string IntroTitle => "Introducción";

    /// <inheritdoc/>
    public string ProloguePhrase => "/pr[óo]logo/";

    /// <inheritdoc/>
    public string PrologueTitle => "Prólogo";

    /// <inheritdoc/>
    public string EpiloguePhrase => "/ep[íi]logo/";

    /// <inheritdoc/>
    public string EpilogueTitle => "Epílogo";

    /// <inheritdoc/>
    public INumberWordParser NumberParser { get; } = new SpanishNumberParser();
}
