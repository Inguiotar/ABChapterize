// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Language;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ABChapterize.Detection;

/// <summary>
/// Reads the chapter number out of a <em>pre-existing</em> chapter marking's title - what
/// <c>--verify</c> checks a marking against, what a resumed run reads its own committed state from,
/// and (as a negative test) what tells a chapter marking apart from a named one.
/// <para>
/// A title is not a transcript, and that distinction is the whole reason this class exists. The
/// transcript helpers read a number from text that <em>follows</em> a phrase already matched
/// elsewhere, so they inspect the first token and nothing else; a title is a label with the phrase
/// still inside it ("Capitolo uno", "Kapitel Fünf"), where the number is almost never token 0.
/// Handing a whole title to the transcript-tail helper - which is what happened until 2026-08-02 -
/// therefore failed on the ordinary form in every language, and the effect looked like "--verify
/// does nothing": a marking whose number will not parse is not counted as checked, and a file where
/// none of them parse is skipped with "none had a checkable number".
/// </para>
/// <para>
/// The fix is to anchor on the phrase rather than scan the string, which is what Pass 2 already does
/// with a transcript: read the number from the <em>beginning</em> of what follows the phrase, or the
/// end of what precedes it, and both word orders ("Capitolo uno", "Fünftes Kapitel") come out along
/// with digits, digit ordinals and Roman numerals - all of it through the same
/// <see cref="NumberWordParser"/> entry points, which are exhaustively tested per language.
/// Anchoring also settles the case a free scan cannot: "Capitolo uno - Il ritorno dei tre" stops at
/// "uno" and never reaches the "tre" in the heading.
/// </para>
/// <para>
/// The rules below are ordered so that the reliable evidence is consulted first and the guesswork
/// last. That ordering is not cosmetic: the loose digit scan at the bottom used to run <em>first</em>,
/// which made "Capitolo uno - Anno 1984" yield 1984 - a marking then verified against a chapter
/// 1984 that does not exist, booked as failed, and (before the wholesale-failure rule) enough to get
/// a whole file's markings discarded and redetected. A heading with a year in it was that expensive.
/// </para>
/// </summary>
internal static class MarkingTitleNumber
{
    /// <summary>
    /// Every registered language's own chapter wording, compiled once - the cross-language fallback
    /// below. Deliberately built from the language defaults and <em>not</em> from
    /// <see cref="ABChapterize.Cli.CliOptions.ResolveProfile"/>: a fallback language is only ever
    /// consulted when its own chapter word literally appears in the title, which is what keeps the
    /// net from catching prose. Without that, an explicit <c>--chapter-phrase</c> would hand the same
    /// anchor to all eleven number grammars at once, and an English "Chapter to the End" would come
    /// back as chapter 2 because Danish "to" means two.
    /// </summary>
    private static readonly LanguageProfile[] Registered =
    [
        .. LanguageRegistry.Languages.Select(l => new LanguageProfile(
            l.Code, l.ChapterPhrase, CompileAnchor(l.ChapterPhrase), PhraseHasNumberGroup: false,
            l.ChapterTitle, l.PartTitle, l.IntroTitle, []))
    ];

    /// <summary>
    /// A digit run that could be a chapter number, i.e. one to three digits with no further digit
    /// on either side. The width limit is the last line of defence against a title's own prose: every
    /// language's number grammar covers 0-999, so a four-digit run in a marking title is a year, not
    /// a chapter. It only applies here, at the bottom of the ladder - a number sitting next to the
    /// chapter word is parsed by the anchored rules above with no width limit at all.
    /// </summary>
    private static readonly Regex LooseDigits = new(@"(?<!\d)\d{1,3}(?!\d)", RegexOptions.CultureInvariant);

    /// <summary>
    /// Where a title stops being a chapter number and starts being a heading: the colon, the spaced
    /// hyphen and the dashes that every tagger writes "Chapter 1: The Beginning" with. Whitespace
    /// tokenization alone does not see them - punctuation is stripped off a word and the heading's
    /// first word ends up directly behind the number - and a language's grammar then reads the two as
    /// one: English turns "Chapter Two: Seven Days Later" into chapter <em>nine</em>, since "two
    /// seven" composes. So the heading is cut off before any number grammar is asked. The spaced
    /// hyphen is matched with its spaces so that "Chapter Thirty-Seven" keeps its own.
    /// </summary>
    private static readonly Regex HeadingSeparator =
        new(@"[:|–—·]|(?<=\s)-(?=\s)", RegexOptions.CultureInvariant);

    /// <summary>
    /// The chapter number a marking's title announces, if any.
    /// </summary>
    /// <param name="title">The marking's title, as whatever tool wrote it left it.</param>
    /// <param name="profile">The file's resolved language profile - its phrase and title word are
    /// tried first, and its language steers the number grammar of the unanchored rules.</param>
    /// <param name="number">Receives the chapter number on success.</param>
    /// <returns>True when a number could be read.</returns>
    internal static bool TryParse(string title, LanguageProfile profile, out int number)
    {
        // 1. The file's own language, anchored on its chapter phrase or title word.
        if (TryAnchored(title, profile, out number))
            return true;

        // 2. Any other language whose own chapter word is in the title. A title is written text
        //    emitted by whatever tool tagged the file, and has no reason to match the narration's
        //    language: an English-tooled tagger writes "Chapter Five" onto a German audiobook, and
        //    an inconclusive auto-detection resolves to English and then meets "Kapitel Fünf".
        if (TryOtherLanguages(title, profile.Language, out number))
            return true;

        // 3. A number the title opens with, with no chapter word anywhere ("05 - Whatever", "Five",
        //    "XIII. The Shaking of the Sheets").
        if (NumberWordParser.TryExtractNumber(BeforeHeading(title), profile.Language, out number))
            return true;

        // 4. A Roman numeral elsewhere in the title, but only where nothing in it is a digit: a
        //    title carrying both takes its number from the digits, and requiring their absence is
        //    what keeps a stray "XL" or "CD" from being read as 40 or 400.
        if (!title.Any(char.IsAsciiDigit) && NumberWordParser.TryExtractRomanAnywhere(title, out number))
            return true;

        // 5. Any plausible digit run at all - a guess, and known to be one. See the class remarks.
        var digits = LooseDigits.Match(title);
        return digits.Success &&
               int.TryParse(digits.Value, NumberStyles.None, CultureInfo.InvariantCulture, out number);
    }

    /// <summary>
    /// The part a marking's title puts it in, 1-based, for a file whose chapter numbering restarts
    /// partway through (see <see cref="LanguageProfile.ChapterTitleFor"/>). Read back so a
    /// <c>--verify</c> or auto-resume run over a file this tool marked sees three parts rather than
    /// a numbering that jumps backwards twice - without it, <see cref="GapPlanning.Normalize"/>
    /// would keep the longest part and throw the others away.
    /// <para>
    /// Deliberately far stricter than <see cref="TryParse"/>: the prefix must open the title, and
    /// only the file's own language is consulted. Nothing but this tool ever writes a part prefix,
    /// so there is no foreign convention to be generous towards - while a free search would find
    /// Danish and Swedish "Del" inside ordinary words, and this is one of the two numbers a
    /// chapter's identity is made of.
    /// </para>
    /// </summary>
    /// <param name="title">The marking's title, as whatever tool wrote it left it.</param>
    /// <param name="profile">The file's resolved language profile, supplying the part word.</param>
    /// <param name="part">Receives the 1-based part number on success.</param>
    /// <returns>True when the title opens with a part prefix.</returns>
    internal static bool TryParsePart(string title, LanguageProfile profile, out int part)
    {
        part = 0;
        var trimmed = title.TrimStart();
        if (profile.PartTitle.Length == 0 ||
            !trimmed.StartsWith(profile.PartTitle, StringComparison.OrdinalIgnoreCase))
            return false;
        var after = trimmed[profile.PartTitle.Length..];
        // A letter directly behind it means the match is the head of a longer word - Danish "Delen",
        // Dutch "Deelname" - and not the prefix at all.
        if (after.Length == 0 || char.IsLetter(after[0]))
            return false;
        return NumberWordParser.TryExtractNumber(BeforeHeading(after), profile.Language, out part) &&
               part > 0;
    }

    /// <summary>
    /// Reads a number sitting next to one language's chapter wording. Both the phrase and the title
    /// word are tried, because they need not be the same string: <c>--chapter-title</c> changes what this
    /// tool writes into a file without changing what it listens for, so a file this tool marked as
    /// "Section 5" is unreadable by a phrase of "chapter" alone.
    /// </summary>
    /// <param name="title">The marking's title.</param>
    /// <param name="profile">The language profile supplying the anchors and the number grammar.</param>
    /// <param name="number">Receives the chapter number on success.</param>
    private static bool TryAnchored(string title, LanguageProfile profile, out int number)
    {
        foreach (Match anchor in profile.PhraseRegex.Matches(title))
            if (TryAtAnchor(title, anchor, profile, out number))
                return true;
        foreach (Match anchor in Regex.Matches(
                     title, Regex.Escape(profile.Title), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            if (TryAtAnchor(title, anchor, profile, out number))
                return true;
        number = 0;
        return false;
    }

    /// <summary>
    /// Reads the number belonging to one anchor occurrence: the phrase's own capturing group where
    /// the user wrote one, else the words behind the anchor, else the words in front of it. The same
    /// question <see cref="PhraseMatching"/> asks of a transcript, and answered through the same two
    /// <see cref="NumberWordParser"/> entry points - but a title is not a transcript, and the
    /// difference is <see cref="HeadingSeparator"/>: a transcript has no headings to cut off, while a
    /// title is mostly heading.
    /// </summary>
    /// <param name="title">The marking's title.</param>
    /// <param name="anchor">Where the chapter word sits in it.</param>
    /// <param name="profile">The language profile supplying the number grammar.</param>
    /// <param name="number">Receives the chapter number on success.</param>
    private static bool TryAtAnchor(string title, Match anchor, LanguageProfile profile, out int number)
    {
        if (profile.PhraseHasNumberGroup && anchor.Groups.Count > 1 && anchor.Groups[1].Success)
            return int.TryParse(
                anchor.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out number);

        var behind = BeforeHeading(title[(anchor.Index + anchor.Length)..]);
        if (NumberWordParser.TryExtractNumber(behind, profile.Language, out number))
            return true;
        return NumberWordParser.TryExtractNumberBefore(
            AfterHeading(title[..anchor.Index]), profile.Language, out number);
    }

    /// <summary>Whatever precedes the first heading separator - the stretch a number may be read
    /// forward from.</summary>
    /// <param name="text">The text behind an anchor, or a whole title.</param>
    private static string BeforeHeading(string text)
    {
        var separator = HeadingSeparator.Match(text);
        return separator.Success ? text[..separator.Index] : text;
    }

    /// <summary>Whatever follows the last heading separator - the stretch a number may be read
    /// backward from, so that "1984 - Kapitel" is not read as chapter 1984.</summary>
    /// <param name="text">The text in front of an anchor.</param>
    private static string AfterHeading(string text)
    {
        var separators = HeadingSeparator.Matches(text);
        return separators.Count == 0 ? text : text[(separators[^1].Index + separators[^1].Length)..];
    }

    /// <summary>
    /// The cross-language fallback: every registered language other than the file's own, accepted
    /// only when the ones that answer at all agree on a single number. Ambiguity is rejected rather
    /// than resolved by precedence - the languages that share a chapter word ("kapitel" across
    /// German, Danish and Swedish; "capítulo" across Spanish and Portuguese) also have distinct
    /// number words, so genuine disagreement means the title is not what it looks like.
    /// </summary>
    /// <param name="title">The marking's title.</param>
    /// <param name="own">The file's own language code, which rule 1 has already tried.</param>
    /// <param name="number">Receives the agreed chapter number on success.</param>
    private static bool TryOtherLanguages(string title, string own, out int number)
    {
        number = 0;
        var agreed = 0;
        var count = 0;
        foreach (var other in Registered)
        {
            if (string.Equals(other.Language, own, StringComparison.OrdinalIgnoreCase) ||
                !TryAnchored(title, other, out var read))
                continue;
            if (count == 0)
                agreed = read;
            else if (read != agreed)
                return false;
            count++;
        }
        if (count == 0)
            return false;
        number = agreed;
        return true;
    }

    /// <summary>
    /// Compiles one language's built-in chapter phrase into its anchor expression. The built-ins are
    /// all written in the same <c>/regexp/</c> spelling a user may type after
    /// <c>--chapter-phrase</c>, and none of them carries a capturing group
    /// (<c>EveryRegisteredLanguage_HasUsableDefaultPhrases</c> guards that) - so unlike
    /// <see cref="ABChapterize.Cli.CliOptions"/>'s own compiler this one has no error path to report
    /// and no capture convention to detect.
    /// </summary>
    /// <param name="phrase">The language's <see cref="ILanguage.ChapterPhrase"/>.</param>
    private static Regex CompileAnchor(string phrase)
    {
        var pattern = phrase.Length > 2 && phrase.StartsWith('/') && phrase.EndsWith('/')
            ? phrase[1..^1]
            : Regex.Escape(phrase);
        return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
