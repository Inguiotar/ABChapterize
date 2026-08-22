// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Cli;
using ABChapterize.Language.Phrases;

namespace ABChapterize.Language;

/// <summary>
/// Fully resolved language-dependent settings for one detection run: the language passed to
/// Whisper, the compiled chapter phrase, and the localized title words. With an
/// explicit <c>--lang</c> this is resolved once for the whole run (<see cref="CliOptions.DefaultProfile"/>).
/// With auto-detection (the default) a fresh profile is resolved per file from its detected
/// (or English-fallback) language via <see cref="CliOptions.ResolveProfile"/>.
/// </summary>
/// <param name="Language">Language code actually used for transcription and number parsing.</param>
/// <param name="ChapterPhrase">The chapter phrase as written - one or more alternatives, each a
/// word, a "/regexp/" or "none". Kept alongside the compiled form for the debug log and the run
/// fingerprint.</param>
/// <param name="ChapterPattern">The compiled chapter phrase: every wording a chapter announcement
/// may take, with the guards each of them asks for. See <see cref="PhrasePattern"/>.</param>
/// <param name="Title">Word used to build chapter titles ("Chapter 1", "Kapitel 1", ...).</param>
/// <param name="PartTitle">Word used to build the part prefix of a book whose chapter numbering
/// restarts partway through ("Part 2 - Chapter 1"); see <see cref="ILanguage.PartTitle"/>. Never
/// written for a file holding a single chapter sequence, which is every ordinary book.</param>
/// <param name="IntroTitle">Title of the synthetic intro chapter.</param>
/// <param name="NamedPhrases">The non-numbered announcements to look for alongside the chapter
/// phrase - the prologue, the epilogue and every <c>--custom</c> mapping - each with the title its
/// mark is written under. Empty when the prologue and epilogue were both switched off with an empty
/// <c>--prologue-phrase</c>/<c>--epilogue-phrase</c> and no <c>--custom</c> mapping was given.</param>
public sealed record LanguageProfile(
    string Language, string ChapterPhrase, PhrasePattern ChapterPattern,
    string Title, string PartTitle, string IntroTitle, IReadOnlyList<NamedPhrase> NamedPhrases)
{
    /// <summary>
    /// True when a chapter may be announced by speaking its number and nothing else - the
    /// <c>--chapter-phrase none</c> wording, which is one alternative among possibly several rather
    /// than a mode the whole run is in. There is no phrase to match then, so
    /// <see cref="ABChapterize.Detection.PhraseMatching.FindPhraseMatches"/> reads such an
    /// announcement out of the transcript's sentence structure instead. Per language rather than
    /// per run, because a mixed-language batch may well hold one series that announces "Kapitel 17"
    /// and another that just says "Seventeen".
    /// </summary>
    public bool BareNumberAnnouncements => ChapterPattern.HasBareNumberAlternative;

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
    public NamedPhrase ChapterAnnouncement { get; } = new(
        "chapter", ChapterPattern, new TitleTemplate(Title, "chapter title"),
        NamedPhraseScope.Anywhere, Repeatable: true);

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
    /// What the mark refinement looks for at a numbered chapter's mark: the one wording that found
    /// the announcement, and nothing else. See <see cref="AnnouncementMatcher.ForWording"/> for what
    /// letting the whole phrase answer costs, and <see cref="AnnouncementMatcher"/> for why the
    /// refinement cannot simply take an expression.
    /// <para>
    /// A mark with no wording is one that was synthesized rather than found - a number a re-read
    /// supplied where the phrase itself was heard but unnumbered - and there is nothing better to
    /// hold it to than the whole phrase.
    /// </para>
    /// </summary>
    /// <param name="wording">The alternative this mark's match was found by, or null for a
    /// synthesized match.</param>
    /// <param name="reading">The reading this mark's own match was found under.</param>
    /// <param name="admits">Which numbers may be taken for this announcement; consulted only by a
    /// bare-number wording, an expression already saying what it is looking for.</param>
    public AnnouncementMatcher AnnouncementFor(
        Phrases.PhraseAlternative? wording,
        NumberWordParser.BareNumberReading reading, Func<int, bool> admits)
        => wording is { } one
            ? AnnouncementMatcher.ForWording(one, Language, reading, admits)
            : BareNumberAnnouncements
                ? AnnouncementMatcher.ForPattern(ChapterPattern, Language, reading, admits)
                : _phrase;

    /// <summary>The phrase matcher, built once rather than per mark - it is the same object for
    /// every mark of a book whose phrase holds no bare-number wording.</summary>
    private readonly AnnouncementMatcher _phrase = AnnouncementMatcher.ForPattern(ChapterPattern);
}
