// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Language.Parsers;

namespace ABChapterize.Language.Languages;

/// <summary>Czech.</summary>
public sealed class CzechLanguage : ILanguage
{
    /// <inheritdoc/>
    public string Code => "cs";

    /// <summary>
    /// The two number-adjacent alternatives take the nominative "kapitola", which is the form a
    /// heading is announced in; the bare third one stops at the stem, so every other case ending
    /// ("kapitole", "kapitoly", "kapitolu") is covered by the substring match and its number is
    /// read off what stands around it. That is the same division of labour Polish uses - its
    /// genitive test row matches the bare alternative rather than the number-adjacent one, which
    /// demands a space exactly where the ending sits.
    /// </summary>
    /// <inheritdoc/>
    public string ChapterPhrase => "/(?:^kapitola ()|^() kapitola|^kapitol)/";

    /// <inheritdoc/>
    public string ChapterTitle => "Kapitola";

    /// <inheritdoc/>
    public string PartTitle => "Část";

    /// <inheritdoc/>
    public string IntroTitle => "Úvod";

    /// <inheritdoc/>
    public string ProloguePhrase => "/prolog/";

    /// <inheritdoc/>
    public string PrologueTitle => "Prolog";

    /// <inheritdoc/>
    public string EpiloguePhrase => "/epilog/";

    /// <inheritdoc/>
    public string EpilogueTitle => "Epilog";

    /// <inheritdoc/>
    public INumberWordParser NumberParser { get; } = new CzechNumberParser();
}
