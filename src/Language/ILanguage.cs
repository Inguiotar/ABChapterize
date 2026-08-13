// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Language.Parsers;

namespace ABChapterize.Language;

/// <summary>
/// Everything ABChapterize knows about one language: the announcements it listens for, the words
/// it writes chapter titles from, and the parser that turns its spoken numbers into integers.
/// One implementation per language, all of them listed in <see cref="LanguageRegistry"/> - so
/// adding a language means writing one class here and adding one line there, with nothing else
/// in the codebase to touch. <c>doc\adding-a-language.md</c> walks through it.
/// <para>
/// The phrase properties hold what a user could equally have typed after
/// <c>--chapter-phrase</c>: a plain word, or a <c>/regexp/</c> when the language has spellings
/// worth covering at once. They are matched case-insensitively and as a substring, so an
/// inflected ending needs no pattern of its own ("rozdziału" is found by <c>rozdzia[łl]</c>) and
/// a shorter spelling covers the longer one ("prologue" is found by <c>prolog</c>). What does
/// need a pattern: dropped diacritics, which Whisper produces often enough to matter
/// (<c>cap[íi]tulo</c>), and a stem change an ending cannot express (Swedish and Danish
/// "kapitel"/"kapitlet").
/// </para>
/// <para>
/// The chapter phrase must not contain a capturing group: <see cref="LanguageProfile.PhraseHasNumberGroup"/>
/// reads one as "the user is capturing the chapter number here", which no built-in default does.
/// Write <c>(?:...)</c> for grouping.
/// </para>
/// </summary>
public interface ILanguage
{
    /// <summary>Two-letter ISO 639-1 code, matching what <c>--lang</c> and Whisper use.</summary>
    string Code { get; }

    /// <summary>The word or <c>/regexp/</c> a chapter announcement is recognized by.</summary>
    string ChapterPhrase { get; }

    /// <summary>The word chapter titles are built from: "Chapter 1", "Kapitel 1", ...</summary>
    string ChapterTitle { get; }

    /// <summary>
    /// The word part titles are built from, for a book whose chapter numbering restarts partway
    /// through: "Part 2 - Chapter 1", "Teil 2 - Kapitel 1". Only ever written when a file really
    /// holds more than one chapter sequence, so the ordinary book never sees it.
    /// <para>
    /// A structural division of the book, not a synonym for the chapter word - Turkish uses
    /// "Kısım" here and "Bölüm" for a chapter, and picking the same word for both would produce
    /// "Bölüm 2 - Bölüm 1".
    /// </para>
    /// </summary>
    string PartTitle { get; }

    /// <summary>Title of the synthetic mark covering whatever precedes the first chapter.</summary>
    string IntroTitle { get; }

    /// <summary>
    /// The word or <c>/regexp/</c> a prologue announcement is recognized by. Each language uses
    /// its Latin-derived form ("Prolog", "Prólogo", ...) rather than a native near-synonym such as
    /// German "Vorwort" or Turkish "Önsöz": those name a foreword, which is front matter about the
    /// book, while a prologue is part of the story and is what a narrator actually announces.
    /// </summary>
    string ProloguePhrase { get; }

    /// <summary>Title written for a detected prologue.</summary>
    string PrologueTitle { get; }

    /// <summary>The word or <c>/regexp/</c> an epilogue announcement is recognized by.</summary>
    string EpiloguePhrase { get; }

    /// <summary>Title written for a detected epilogue.</summary>
    string EpilogueTitle { get; }

    /// <summary>
    /// Turns this language's spoken number words into integers. A separate class rather than more
    /// members on this one: the phrase/title data above is a handful of strings, while a number
    /// grammar is a few hundred lines, and the two are edited for completely different reasons.
    /// </summary>
    INumberWordParser NumberParser { get; }
}
