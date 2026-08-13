// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Language.Phrases;

namespace ABChapterize.Language;

/// <summary>
/// Answers the one question the mark refinement asks of a probe transcript: "is the announcement
/// in this text?" - for a phrase-based book by matching the chapter phrase, and for a
/// <c>--chapter-phrase none</c> book by reading a number spoken as a sentence of its own.
/// <para>
/// It exists because the refinement used to take a compiled expression, and a chapter announced by
/// its number alone has none: the phrase was a deliberately never-matching expression there. Every
/// numbered chapter of such a book therefore failed refinement by construction -
/// twice, since a failure is retried through the <c>--pass3-model</c> recognizer - and the failure
/// was silent, indistinguishable in the log from a genuinely unconfirmable announcement. Measured
/// on "Corsa nello spazio" (build 244, 2026-08-05): 1001 of 1012 survival probes answered "no",
/// the eleven that did not belonging to the two epilogue marks that carry a real regex, and the
/// doomed searches took 27 minutes of a 2 h 40 m run. Nothing was refined, and
/// <see cref="ABChapterize.Detection.RefinedNumberVote"/> - which feeds on the transcripts a
/// successful probe produces - never fired either, leaving the strongest of the four
/// misheard-number defences dead in exactly the mode that leans hardest on chapter numbering.
/// </para>
/// <para>
/// The bare-number matcher accepts any number <em>the chapter sequence can hold at that point</em>
/// rather than only the one the detecting window read, and that is what keeps the number itself
/// refinable: Whisper's notation for one announcement fluctuates between probes - "45",
/// "quarantacinque" and "XLV" are all readings of the same audio, and the same probe series has
/// been seen writing a number as digits once and as words the next time - so a matcher pinned to
/// one reading would reject the announcement it is standing on. Leaving the range open altogether
/// is what the first version did, and it cost four marks on the same book by letting the onset
/// walk climb onto a year or a quantity in the chapter's own opening sentence; see
/// <see cref="NumberWordParser.FindBareNumberAnnouncement"/> for the four. Bounding it by the
/// sequence admits every notation of the right number and nothing belonging to the prose, and
/// leaves <see cref="ABChapterize.Detection.RefinedNumberVote"/> to settle which number was
/// actually spoken, exactly as it does for a phrase-based book.
/// </para>
/// </summary>
public sealed class AnnouncementMatcher
{
    private readonly Func<string, bool> _matches;

    /// <summary>Creates a matcher from the predicate that recognizes its announcement.</summary>
    /// <param name="matches">Whether one piece of transcribed text carries the announcement.</param>
    private AnnouncementMatcher(Func<string, bool> matches) => _matches = matches;

    /// <summary>Whether <paramref name="text"/> carries this announcement. Null and blank text
    /// answer false, so callers can hand a transcript segment straight over.</summary>
    /// <param name="text">One transcript segment's text, as the recognizer wrote it.</param>
    public bool Matches(string? text) => !string.IsNullOrWhiteSpace(text) && _matches(text);

    /// <summary>A matcher for a phrase whose wordings are all expressions - every phrase-based
    /// chapter phrase, and every prologue/epilogue/<c>--custom</c> one.</summary>
    /// <param name="pattern">The compiled phrase.</param>
    public static AnnouncementMatcher ForPattern(PhrasePattern pattern) => new(pattern.IsMatch);

    /// <summary>
    /// Widens a compiled phrase into a matcher, so the many call sites that have one to pass stay
    /// unchanged and only the ones that have to say something more - a number spoken alone, whose
    /// bounds the caller supplies - use the fuller overload. A phrase <em>is</em> an announcement
    /// recognizer here, with no information added or lost, which is the case an implicit conversion
    /// is for.
    /// </summary>
    /// <param name="pattern">The compiled phrase.</param>
    public static implicit operator AnnouncementMatcher(PhrasePattern pattern) => ForPattern(pattern);

    /// <summary>
    /// A matcher for a phrase one of whose wordings is a number spoken alone
    /// (<c>--chapter-phrase none</c>): that wording is looked for as a sentence of its own, anywhere
    /// in the probe's text rather than only at its start - a refinement probe decodes a few seconds
    /// with a lead-in, so the transcript routinely opens with a fragment of whatever preceded the
    /// announcement - while the phrase's other wordings are matched as usual.
    /// <para>
    /// <paramref name="reading"/> must be the one the match itself was found under, and the pairing
    /// matters more than it looks. The permissive reading is only safe where
    /// <see cref="ABChapterize.Detection.AnnouncementIsolation"/> vets the result, and a refinement
    /// is exactly where an over-eager matcher does its damage: in Italian "un", "una" and "uno" all
    /// parse as the number 1, so a probe window of ordinary prose can answer "found" and pull the
    /// onset - and with it the mark - onto a word. A Pass 2 mark has no isolation check behind it,
    /// so its refinement gets the strict reading; a gap hunt's does not, so its refinement may take
    /// the permissive one.
    /// </para>
    /// </summary>
    /// <param name="pattern">The compiled phrase, for the wordings that are expressions.</param>
    /// <param name="language">Two-letter language code steering number-word parsing.</param>
    /// <param name="reading">The reading this mark's own match was found under.</param>
    /// <param name="admits">Which numbers this stretch of the chapter sequence can hold - see
    /// <see cref="ABChapterize.Detection.NumberCheck"/>, which is where the rule lives. Required
    /// rather than optional because every caller has the bounds in hand and an unbounded matcher
    /// is the defect this parameter exists to prevent.</param>
    public static AnnouncementMatcher ForPattern(
        PhrasePattern pattern, string language,
        NumberWordParser.BareNumberReading reading, Func<int, bool> admits) =>
        new(text => pattern.IsMatch(text) || NumberWordParser.FindBareNumberAnnouncement(
            text,
            language,
            reading == NumberWordParser.BareNumberReading.LeadingASentence
                ? NumberWordParser.BareNumberReading.LeadingASentence
                : NumberWordParser.BareNumberReading.SpokenAloneAnywhere,
            admits) != null);
}
