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
/// <param name="PartTitle">Word used to build the part prefix of a book whose chapter numbering
/// restarts partway through ("Part 2 - Chapter 1"); see <see cref="ILanguage.PartTitle"/>. Never
/// written for a file holding a single chapter sequence, which is every ordinary book.</param>
/// <param name="IntroTitle">Title of the synthetic intro chapter.</param>
/// <param name="NamedPhrases">The non-numbered announcements to look for alongside the chapter
/// phrase - the prologue, the epilogue and every <c>--custom</c> mapping - each with the title its
/// mark is written under. Empty when the prologue and epilogue were both switched off with an empty
/// <c>--prologue-phrase</c>/<c>--epilogue-phrase</c> and no <c>--custom</c> mapping was given.</param>
/// <param name="BareNumberAnnouncements">True for <c>--chapter-phrase none</c>: this language's
/// books announce a chapter by speaking its number and nothing else, so there is no phrase to match
/// and <paramref name="PhraseRegex"/> is a regex that never matches anything. See
/// <see cref="ABChapterize.Detection.PhraseMatching.FindPhraseMatches"/> for what is looked for
/// instead. Per language rather than per run, because a mixed-language batch may well hold one
/// series that announces "Kapitel 17" and another that just says "Seventeen".</param>
public sealed record LanguageProfile(
    string Language, string ChapterPhrase, Regex PhraseRegex, bool PhraseHasNumberGroup,
    string Title, string PartTitle, string IntroTitle, IReadOnlyList<NamedPhrase> NamedPhrases,
    bool BareNumberAnnouncements = false)
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

    /// <summary>
    /// The title one numbered chapter is written under. The only place the part prefix is spelled
    /// out, because it has to be readable again: <see cref="ABChapterize.Detection.ExistingMarkTitle"/>
    /// takes both numbers back out of this string when a resumed or <c>--verify</c>ed run meets a
    /// file this tool marked, and a second spelling anywhere would break that round trip silently.
    /// </summary>
    /// <param name="number">The chapter number as announced, which restarts with every part.</param>
    /// <param name="part">The 1-based part number, or null for a file holding a single chapter
    /// sequence - every ordinary book, which is written exactly as it was before parts existed.</param>
    public string ChapterTitleFor(int number, int? part)
        => part is { } p ? $"{PartTitle} {p} - {Title} {number}" : $"{Title} {number}";

    /// <summary>
    /// What the mark refinement looks for at a numbered chapter's mark: the chapter phrase, or -
    /// under <c>--chapter-phrase none</c>, where <see cref="PhraseRegex"/> never matches anything -
    /// a number spoken as a sentence of its own. See <see cref="AnnouncementMatcher"/> for why the
    /// refinement cannot simply take the regex, and why the reading the match was found under has
    /// to travel with it.
    /// </summary>
    /// <param name="reading">The reading this mark's own match was found under.</param>
    /// <param name="admits">Which numbers may be taken for this announcement; ignored by a
    /// phrase-based book, whose phrase already says what it is looking for.</param>
    public AnnouncementMatcher AnnouncementFor(
        NumberWordParser.BareNumberReading reading, Func<int, bool> admits)
        => BareNumberAnnouncements
            ? AnnouncementMatcher.ForBareNumbers(Language, reading, admits)
            : _phrase;

    /// <summary>The phrase matcher, built once rather than per mark - it is the same object for
    /// every mark of a phrase-based book.</summary>
    private readonly AnnouncementMatcher _phrase = AnnouncementMatcher.ForPhrase(PhraseRegex);
}
