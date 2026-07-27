// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using System.Text.RegularExpressions;

namespace ABChapterize.Language;

/// <summary>
/// Where in a book a <see cref="NamedPhrase"/> may legitimately be announced. Both values are
/// expressed against the numbered chapter sequence rather than against absolute time, because
/// that is the only landmark detection has: a book's front matter can run for a minute or for
/// half an hour, and no fixed offset separates the two.
/// </summary>
public enum NamedPhraseScope
{
    /// <summary>Only before the first numbered chapter has been found - a prologue's own place,
    /// and the reason a later "the prologue explained..." cannot become a second mark.</summary>
    BeforeFirstChapter,

    /// <summary>Only once at least one numbered chapter has been found - an epilogue follows the
    /// book's chapters, so a mention before any of them is front matter, not the epilogue.</summary>
    AfterFirstChapter,
}

/// <summary>
/// A phrase that produces a mark with a fixed title instead of a numbered chapter: the prologue
/// and epilogue announcements. Unlike <see cref="LanguageProfile.PhraseRegex"/> no number is
/// parsed and none is expected, so such a mark takes no part in the chapter-number sequence -
/// it neither closes nor opens a gap, and it can never make a book look like it is missing a
/// chapter.
/// </summary>
/// <param name="Kind">Short identifier used in log lines and error messages ("prologue",
/// "epilogue"); never user-visible in a written chapter title.</param>
/// <param name="Regex">Compiled, case-insensitive expression that recognizes the announcement.</param>
/// <param name="Title">The chapter title written for a mark this phrase produced.</param>
/// <param name="Scope">Where in the book the phrase is accepted; see <see cref="NamedPhraseScope"/>.</param>
public sealed record NamedPhrase(string Kind, Regex Regex, string Title, NamedPhraseScope Scope);
