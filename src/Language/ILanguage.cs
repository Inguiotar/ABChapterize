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
/// An unnamed capturing group in a phrase is read as "the chapter number is here" - <c>()</c> taking
/// the language's own number notation as its body - so a group written only to hold an alternation
/// must be non-capturing, <c>(?:...)</c>. See <c>doc\manual.md</c> for the whole phrase syntax,
/// including the <c>^</c> and <c>$</c> guards and the <c>${number}</c> a title may write.
/// </para>
/// </summary>
public interface ILanguage
{
    /// <summary>Two-letter ISO 639-1 code, matching what <c>--lang</c> and Whisper use.</summary>
    string Code { get; }

    /// <summary>
    /// The word or <c>/regexp/</c> a chapter announcement is recognized by. Every language spells
    /// this the same way: <c>/(?:WORD ()|WORD)/</c>, two wordings of one phrase. The first captures
    /// the number where it follows the word directly, which is the ordinary case and the only one a
    /// title's <c>${number}</c> can be built from; the second is the bare word, and the number is
    /// then read off the words around it - which is what covers the ordinal-first announcement order
    /// ("Erstes Kapitel", and in Turkish "Birinci Bölüm", that language's only order).
    /// <para>
    /// Deliberately without a <c>^</c> guard, although one would read naturally here. Requiring a
    /// pause in front of every chapter announcement was replayed over sixteen books (469 marks,
    /// 2026-08-13) and would have dropped exactly one of them: "I Shall Wear Midnight" chapter 9,
    /// where the previous chapter's last words end 0.64 s before the announcement against a
    /// threshold of 0.85 s, and whose mark is otherwise perfect at -105.6 dBFS.
    /// </para>
    /// <para>
    /// And deliberately without a <c>() WORD</c> wording for the number-first order, which the two
    /// wordings above cover between them. Adding one is not merely redundant, it is wrong: matches
    /// are taken leftmost-first, so on "Der erste Kapitel 5" the number-first wording claims "erste
    /// Kapitel" before <c>kapitel ()</c> can claim "Kapitel 5", and the chapter becomes 1. Three of
    /// the corpus's 12,916 probe transcripts read exactly like that (BARDIOC, Die Maahks and
    /// Gruelfin, 2026-08-14), each of them costing its chapter's real number. The narrator says no
    /// such thing in any of the three - "Der erste" is not even grammatical, and the neighbouring
    /// probes of the same announcement transcribe it plainly as "Kapitel 5." - so what the wording
    /// exposes is not an announcement style but Whisper's habit of filling a short window's opening
    /// with plausible words. Only a phrase that can take a number from in front of the word is
    /// vulnerable to that.
    /// </para>
    /// </summary>
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
