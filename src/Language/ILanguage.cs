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
    /// this the same way: <c>/(?:^WORD ()|^() WORD|^WORD)/</c>, three wordings of one phrase. The
    /// first captures the number where it follows the word directly, which is the ordinary case; the
    /// second where it precedes it ("Erstes Kapitel", and in Turkish "Birinci Bölüm", that language's
    /// only order); the third is the bare word, and the number is then read off the words around it,
    /// which is what still finds an announcement the other two do not fit.
    /// <para>
    /// Every wording carries a <c>^</c>: an announcement is by definition set off from what precedes
    /// it, and saying so is what keeps a chapter word occurring in ordinary prose from becoming a
    /// mark. It was not always safe to say. Against a pause alone it would have cost a real
    /// chapter - "I Shall Wear Midnight" chapter 9, whose announcement follows the previous
    /// chapter's last words after 0.64 s against a threshold of 0.85 s, its mark otherwise perfect
    /// at -105.6 dBFS - and it is only affordable because a <c>^</c> is now satisfied by the
    /// recognizer setting the announcement off as a transcript segment of its own as well, which
    /// that chapter does (see <see cref="ABChapterize.Detection.AnnouncementIsolation.ForChapter"/>).
    /// It is the one mark of the sixteen-book corpus with under 0.85 s in front of it; the
    /// next-lowest has 0.99 s.
    /// </para>
    /// <para>
    /// The number-first wording is what gives an ordinal-first language that same rescue. Without it
    /// "Birinci Bölüm" is found by the bare word, whose match starts at "Bölüm" and so never opens
    /// its segment however the recognizer split it - leaving Turkish, alone among the eleven,
    /// dependent on the pause. It also earns the ordinal-first order a <c>${number}</c> a title can
    /// be built from, which reading the number off the surrounding words does not.
    /// </para>
    /// <para>
    /// It is also the one wording that can be claimed by words the narrator never said. Matches are
    /// taken leftmost-first, so on "Der erste Kapitel 5" it claims "erste Kapitel" before
    /// <c>kapitel ()</c> can claim "Kapitel 5", and the chapter would become 1. Three of the corpus's
    /// 12,916 probe transcripts read exactly like that (BARDIOC, Die Maahks and Gruelfin,
    /// 2026-08-14); the narrator says no such thing in any of them - "Der erste" is not even
    /// grammatical, and the neighbouring probes of the same announcement transcribe it plainly as
    /// "Kapitel 5." - so what it exposes is Whisper's habit of filling a short window's opening with
    /// plausible words. Which is why a superseded wording is kept rather than discarded: the sequence
    /// turns the 1 down and the reading behind it, "Kapitel 5", is taken instead (see
    /// <see cref="Phrases.PhrasePattern.MatchGroups"/> and Pass 2's accept loop).
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
