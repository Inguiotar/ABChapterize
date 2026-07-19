// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using System.Text.RegularExpressions;

namespace ABChapterize;

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
public sealed record LanguageProfile(
    string Language, string ChapterPhrase, Regex PhraseRegex, bool PhraseHasNumberGroup,
    string Title, string IntroTitle);
