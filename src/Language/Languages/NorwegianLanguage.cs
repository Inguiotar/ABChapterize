// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Language.Parsers;

namespace ABChapterize.Language.Languages;

/// <summary>Norwegian, in the Bokmal written standard - Nynorsk is a separate Whisper code
/// (<c>nn</c>) and is not covered here.</summary>
public sealed class NorwegianLanguage : ILanguage
{
    /// <inheritdoc/>
    public string Code => "no";

    /// <summary>
    /// The definite form drops the second "t" and the "e" ("kapittel" -> "kapitlet"), so the stem
    /// alone cannot cover both and the two endings are spelled out, exactly as Swedish and Danish
    /// do. Deliberately does <em>not</em> also admit their "kapitel": the double "t" is the one
    /// thing that separates the three spellings, and widening it here would hand a Danish or
    /// Swedish book Norwegian's number grammar whenever language detection wavered.
    /// </summary>
    /// <inheritdoc/>
    public string ChapterPhrase => "/(?:^kapit(?:tel|let) ()|^() kapit(?:tel|let)|^kapit(?:tel|let))/";

    /// <inheritdoc/>
    public string ChapterTitle => "Kapittel";

    /// <inheritdoc/>
    public string PartTitle => "Del";

    /// <inheritdoc/>
    public string IntroTitle => "Introduksjon";

    /// <inheritdoc/>
    public string ProloguePhrase => "/prolog/";

    /// <inheritdoc/>
    public string PrologueTitle => "Prolog";

    /// <inheritdoc/>
    public string EpiloguePhrase => "/epilog/";

    /// <inheritdoc/>
    public string EpilogueTitle => "Epilog";

    /// <inheritdoc/>
    public INumberWordParser NumberParser { get; } = new NorwegianNumberParser();
}
