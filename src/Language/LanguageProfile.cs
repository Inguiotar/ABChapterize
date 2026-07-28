// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using System.Text.RegularExpressions;
using ABChapterize.Cli;

namespace ABChapterize.Language;

/// <summary>
/// Fully resolved language-dependent settings for one detection run: the language passed to
/// Whisper, the compiled chapter-phrase regex, and the localized title words. With an
/// explicit <c>--lang</c> this is resolved once for the whole run (<see cref="CliOptions.DefaultProfile"/>).
/// With auto-detection (the default) a fresh profile is resolved per file from its detected
/// (or English-fallback) language via <see cref="CliOptions.ResolveProfile"/>.
/// </summary>
/// <param name="Language">Two-letter language code actually used for transcription and number parsing.</param>
/// <param name="ChapterPhrase">The word/phrase or "/regexp/" identifying a chapter announcement.</param>
/// <param name="PhraseRegex">Compiled, case-insensitive regular expression built from <paramref name="ChapterPhrase"/>.</param>
/// <param name="PhraseHasNumberGroup">True when <paramref name="PhraseRegex"/> has an explicit capturing group for the chapter number.</param>
/// <param name="Title">Word used to build chapter titles ("Chapter 1", "Kapitel 1", ...).</param>
/// <param name="IntroTitle">Title of the synthetic intro chapter.</param>
/// <param name="NamedPhrases">The non-numbered announcements to look for alongside the chapter
/// phrase - the prologue, the epilogue and every <c>--custom</c> mapping - each with the title its
/// mark is written under. Empty when the prologue and epilogue were both switched off with an empty
/// <c>--prologue-phrase</c>/<c>--epilogue-phrase</c> and no <c>--custom</c> mapping was given.</param>
public sealed record LanguageProfile(
    string Language, string ChapterPhrase, Regex PhraseRegex, bool PhraseHasNumberGroup,
    string Title, string IntroTitle, IReadOnlyList<NamedPhrase> NamedPhrases)
{
    /// <summary>
    /// The chapter phrase dressed up as a <see cref="NamedPhrase"/>, for
    /// <c>--ignore-chapter-numbers</c>: with no sequence to place an announcement in, a chapter is
    /// exactly what a prologue already is - a title at a position - and reusing that path gives it
    /// the same placement, deduplication and threshold feedback for free. Repeatable and unscoped,
    /// since a book announces many chapters and nothing bounds where they may fall. The title here
    /// is the bare word only; the spoken number is appended per match by
    /// <see cref="ABChapterize.Detection.PhraseMatching.FindChapterAnnouncements"/>, which is where
    /// it is parsed.
    /// </summary>
    public NamedPhrase ChapterAnnouncement { get; } =
        new("chapter", PhraseRegex, Title, NamedPhraseScope.Anywhere, Repeatable: true);
}
