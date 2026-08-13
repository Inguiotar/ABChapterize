// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Language.Parsers;

namespace ABChapterize.Language.Languages;

/// <summary>Turkish.</summary>
public sealed class TurkishLanguage : ILanguage
{
    /// <inheritdoc/>
    public string Code => "tr";

    /// <summary>
    /// Both dotted vowels are optional, covering a transcript that writes "bolum" for "bölüm".
    /// Turkish suffixes ("Bölüm'ün", "Bölümü") need no pattern of their own: the phrase is
    /// matched as a substring, so anything appended to the stem is found anyway.
    /// </summary>
    /// <inheritdoc/>
    public string ChapterPhrase => "/(?:b[öo]l[üu]m ()|b[öo]l[üu]m)/";

    /// <inheritdoc/>
    public string ChapterTitle => "Bölüm";

    /// <summary>"Kısım", not the chapter word - Turkish "Bölüm" serves as both "chapter" and
    /// "section", and reusing it would write "Bölüm 2 - Bölüm 1".</summary>
    /// <inheritdoc/>
    public string PartTitle => "Kısım";

    /// <inheritdoc/>
    public string IntroTitle => "Giriş";

    /// <inheritdoc/>
    public string ProloguePhrase => "/prolog/";

    /// <inheritdoc/>
    public string PrologueTitle => "Prolog";

    /// <inheritdoc/>
    public string EpiloguePhrase => "/epilog/";

    /// <inheritdoc/>
    public string EpilogueTitle => "Epilog";

    /// <inheritdoc/>
    public INumberWordParser NumberParser { get; } = new TurkishNumberParser();
}
