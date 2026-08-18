// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using System.Text.RegularExpressions;

namespace ABChapterize.Language.Phrases;

/// <summary>
/// One alternative of a phrase - one way an announcement may be worded, with the guards that
/// wording asks for.
/// <para>
/// A phrase is held as a list of these rather than as one alternation because <c>^</c> and <c>$</c>
/// are not anchors here: they ask for a pause in front of the announcement and behind it, and that
/// is a property of the alternative that matched, not of the phrase. <c>/(?:^a$|b$)/</c> wants both
/// pauses around "a" and only the trailing one around "b", which one compiled expression cannot
/// report.
/// </para>
/// </summary>
/// <param name="Index">Position in the phrase, 0-based. Decides which alternative wins when two
/// match at the same place, so that "first one wins" means what it does inside a single
/// alternation.</param>
/// <param name="Regex">The compiled expression, or null for a bare-number alternative - see
/// <see cref="IsBareNumber"/>.</param>
/// <param name="Source">The alternative as written, for error messages and the debug log.</param>
/// <param name="HasNumberGroup">Whether a <c>()</c> (or an unnamed capturing group, which means the
/// same thing) supplies the chapter number, rather than it being read from the words around the
/// match.</param>
/// <param name="RequiresLeadIn">What a leading <c>^</c> asked for: the announcement must be set off
/// in front - by real non-speech, or by the recognizer writing it as a transcript segment of its own
/// (see <see cref="ABChapterize.Detection.AnnouncementIsolation"/>). Writing the <c>^</c> is also the
/// only way a <c>--custom</c> mapping can ask for it, the tag keyword that used to say the same
/// thing having been removed - see <see cref="ABChapterize.Cli.SpecTag"/>.</param>
/// <param name="RequiresLeadOut">What a trailing <c>$</c> asked for: non-speech behind the
/// announcement as well. What a number spoken alone leans on entirely.</param>
public sealed record PhraseAlternative(
    int Index, Regex? Regex, string Source, bool HasNumberGroup,
    bool RequiresLeadIn, bool RequiresLeadOut)
{
    /// <summary>
    /// Whether this alternative is a number spoken alone (<c>none</c>, i.e. <c>/^()$/</c>) rather
    /// than something a regular expression can look for. Such an alternative is read out of a
    /// transcript <em>segment by segment and sentence by sentence</em>
    /// (<see cref="NumberWordParser.FindBareNumberAnnouncement"/>), because what makes a lone number
    /// an announcement is where the sentence boundaries fall - a question the flattened window text
    /// a regex runs over no longer answers.
    /// </summary>
    public bool IsBareNumber => Regex is null;

    /// <summary>The name the <c>()</c> token expands to, and the one a title references as
    /// <c>${number}</c>. Public knowledge rather than an implementation detail: it is part of the
    /// phrase syntax the manual documents.</summary>
    public const string NumberGroup = "number";
}

/// <summary>One alternative's match, carrying the alternative so its guards travel with it.</summary>
/// <param name="Match">The regex match.</param>
/// <param name="Alternative">The alternative that produced it.</param>
public readonly record struct PhraseHit(Match Match, PhraseAlternative Alternative);

/// <summary>
/// A compiled phrase: everything a run listens for under one option, as the list of alternatives
/// <see cref="PhraseCompiler"/> resolved it into.
/// </summary>
/// <param name="Alternatives">The alternatives, in the order they were written.</param>
/// <param name="Source">The phrase as the user wrote it (or as a language default spells it), for
/// the debug log and error messages.</param>
public sealed record PhrasePattern(IReadOnlyList<PhraseAlternative> Alternatives, string Source)
{
    /// <summary>A phrase that matches nothing at all - what an empty <c>--prologue-phrase</c>
    /// resolves to, and what a language profile carries where a phrase was switched off.</summary>
    public static readonly PhrasePattern Nothing = new([], "");

    /// <summary>Whether any alternative is a number spoken alone; see
    /// <see cref="PhraseAlternative.IsBareNumber"/>. What used to be the run-wide
    /// <c>--chapter-phrase none</c> flag, now a property of one alternative among possibly several.</summary>
    public bool HasBareNumberAlternative => Alternatives.Any(a => a.IsBareNumber);

    /// <summary>Whether any alternative can match by pattern at all - false for a phrase that is
    /// nothing but bare numbers, and for one that was switched off.</summary>
    public bool HasRegexAlternative => Alternatives.Any(a => !a.IsBareNumber);

    /// <summary>
    /// Every place this phrase matches in one flattened window text, leftmost first and never
    /// overlapping - which is what a single alternation would have produced, with the alternative's
    /// own index breaking a tie at the same position.
    /// <para>
    /// Bare-number alternatives are not searched here and have to be read segment by segment by the
    /// caller (<see cref="ABChapterize.Detection.PhraseMatching"/>); this text has had its sentence
    /// structure flattened out of it, which is the only thing that makes a lone number an
    /// announcement.
    /// </para>
    /// </summary>
    /// <param name="text">The window's transcript, flattened and whitespace-normalized.</param>
    public IEnumerable<PhraseHit> Matches(string text)
        => MatchGroups(text).Select(g => g[0]);

    /// <summary>
    /// The same, but keeping the hits each winner displaced: one group per position, the wording
    /// that claimed it first and then the ones it superseded, in the order they were written.
    /// <para>
    /// A superseded wording is not noise. Two wordings of one phrase can read the same words
    /// differently - "Der erste Kapitel 5" is "erste Kapitel" to one and "Kapitel 5" to another -
    /// and which of them is right is a question about the audio, not about character positions. So
    /// detection keeps the losers within reach and lets the sequence, and then the announcement's
    /// own re-transcription, settle it (see
    /// <see cref="ABChapterize.Detection.RegionProber"/>'s accept loop). Every other caller takes
    /// <see cref="Matches"/> and sees only the winners, which is what a single alternation would
    /// have given them.
    /// </para>
    /// </summary>
    /// <param name="text">The window's transcript, flattened and whitespace-normalized.</param>
    public IEnumerable<IReadOnlyList<PhraseHit>> MatchGroups(string text)
    {
        var hits = new List<PhraseHit>();
        foreach (var alternative in Alternatives)
            if (alternative.Regex is { } regex)
                foreach (Match match in regex.Matches(text))
                    if (match.Length > 0)
                        hits.Add(new PhraseHit(match, alternative));
        hits.Sort((a, b) => a.Match.Index != b.Match.Index
            ? a.Match.Index.CompareTo(b.Match.Index)
            : a.Alternative.Index.CompareTo(b.Alternative.Index));

        var group = new List<PhraseHit>();
        var consumedTo = 0;
        foreach (var hit in hits)
        {
            if (hit.Match.Index < consumedTo)
            {
                // Overlaps the winner, so it is one of its rivals rather than an announcement of
                // its own - unless nothing has been claimed yet, which cannot happen here.
                group.Add(hit);
                continue;
            }
            if (group.Count > 0)
                yield return group.ToList();
            group.Clear();
            group.Add(hit);
            consumedTo = hit.Match.Index + hit.Match.Length;
        }
        if (group.Count > 0)
            yield return group;
    }

    /// <summary>Whether any alternative matches - the cheap question the mark refinement asks of a
    /// probe transcript, where <em>which</em> alternative found the announcement does not matter.</summary>
    /// <param name="text">The text to test.</param>
    public bool IsMatch(string text)
        => Alternatives.Any(a => a.Regex?.IsMatch(text) == true);

    /// <summary>Every capturing group a title may reference, across all alternatives. A group
    /// present in only one of them is still referenceable - it simply expands to nothing on a match
    /// from another - because the alternatives are one phrase written several ways.</summary>
    public IEnumerable<string> GroupNames => Alternatives
        .Where(a => a.Regex != null)
        .SelectMany(a => a.Regex!.GetGroupNames())
        .Where(name => !int.TryParse(name, out _))
        .Distinct();
}
