// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Language;
using ABChapterize.Vad;
using static ABChapterize.Detection.DetectionTuning;

namespace ABChapterize.Detection;

/// <summary>How much non-speech flanks an announcement: the pause (silence or jingle music - to a
/// speech detector both read the same) between it and the narration on either side.</summary>
/// <param name="LeadInSeconds">Non-speech directly before the announcement's own speech.</param>
/// <param name="LeadOutSeconds">Non-speech directly after it; +infinity when nothing follows in
/// the file at all.</param>
/// <param name="SpeechStartSeconds">Where VAD puts the start of the announcement's speech - the
/// segment the measurement was taken around, carried for the log line a rejection writes.</param>
internal readonly record struct AnnouncementFlanks(
    double LeadInSeconds, double LeadOutSeconds, double SpeechStartSeconds);

/// <summary>
/// Checks that a detected announcement really is set off from the narration by pauses, from the
/// VAD pre-pass's speech segments alone - no decoding, no recognition, just Pass 1 geometry.
/// <para>
/// This is the check that replaces trusting Whisper's own segmentation. The old
/// <c>--chapter-phrase none</c> rule ("the transcript segment is a number start to finish") was
/// really an isolation test wearing a recognizer's clothes: it asked Whisper where the speaking
/// stopped and read a pause out of the answer. Whisper is not dependable about that - see
/// <see cref="Language.NumberWordParser.FindBareNumberAnnouncement"/> for the ten chapters it cost
/// on one book - whereas the VAD pre-pass measures the pauses directly and at frame resolution.
/// </para>
/// <para>
/// Measured on "Corsa nello spazio" (build 244, 2026-08-05), replaying the guard over that run's
/// own Pass 1 speech segments at each announcement's true onset: every one of the 65 chapters is
/// flanked by about 3.0-3.9 s before and 1.0-2.2 s after, the number's own speech running
/// 0.3-1.3 s. The false epilogue the same run wrote - Whisper heard "riepilogo" (Italian for
/// "summary") mid-sentence and <c>/epilogo/</c> matched inside it - measures 0.64 s before and
/// 0.73 s after, on a 3.59 s stretch of continuous speech. So the thresholds below sit in a gap
/// with real room on both sides rather than being fitted to one number.
/// </para>
/// <para>
/// Deliberately measured at the <em>refined</em> onset. Five of that run's marks would have failed
/// the check at the position they were actually written to, every one of them a mark left seconds
/// late because refinement was broken (chapter 6 landed 2.2 s past its announcement, chapter 3
/// by 4.4 s); at their true onsets all five pass comfortably. The guard is therefore only as good
/// as the onset it is handed, which is why it runs after refinement and falls back to the phrase's
/// own segment start when nothing could be confirmed.
/// </para>
/// </summary>
internal static class AnnouncementIsolation
{
    /// <summary>
    /// The check a numbered chapter's mark is placed under. Nothing at all for a phrase-based book,
    /// whose phrase is its own evidence, and nothing for the pass that is deliberately left cheap:
    /// only where a bare number was read under the wider
    /// <see cref="NumberWordParser.BareNumberReading.LeadingASentence"/> reading does the position have
    /// to be earned, because that reading admits numbers Whisper did not set off by itself.
    /// </summary>
    /// <param name="profile">The file's language profile, for whether this is a bare-number book.</param>
    /// <param name="match">The match being placed; its
    /// <see cref="PhraseMatching.PhraseMatch.SpokenAlone"/> decides whether there is anything to
    /// fall back on when the refinement confirms nothing.</param>
    /// <param name="phraseAbs">Absolute start of the segment the number was found in.</param>
    /// <param name="wideReading">Whether this pass read the transcript under
    /// <see cref="NumberWordParser.BareNumberReading.LeadingASentence"/> - the same flag that chose the
    /// reading, so the two can never drift apart.</param>
    internal static IsolationCheck ForChapter(
        LanguageProfile profile, PhraseMatching.PhraseMatch match, double phraseAbs, bool wideReading)
        => profile.BareNumberAnnouncements && wideReading
            ? new IsolationCheck(IsolationRule.Both, match.SpokenAlone ? phraseAbs : null)
            : IsolationCheck.None;

    /// <summary>
    /// Measures the non-speech either side of the announcement at <paramref name="onset"/>: finds
    /// the VAD speech segment the onset falls in (or the next one to start, VAD's own onset lag
    /// putting a speech start a few tenths after the sound really resumes) and reports the gaps to
    /// its neighbours.
    /// </summary>
    /// <param name="onset">Absolute time the announcement starts at.</param>
    /// <param name="speech">The VAD pre-pass's speech segments, chronological. Empty when the
    /// pre-pass did not run, which is what makes the measurement unavailable rather than failing.</param>
    /// <returns>The flanking non-speech, or null when there is nothing to measure it from - no VAD
    /// speech segments at all, or an onset past the last of them.</returns>
    internal static AnnouncementFlanks? Measure(double onset, IReadOnlyList<SpeechSegment> speech)
    {
        if (speech.Count == 0)
            return null;

        // The announcement's own speech is the first segment that has not already finished by the
        // onset. The small tolerance absorbs the other direction: an onset anchored to the sound's
        // true resumption can land a frame or two inside the segment before it.
        var i = 0;
        while (i < speech.Count && speech[i].EndSeconds <= onset + OnsetSegmentToleranceSeconds)
            i++;
        if (i >= speech.Count)
            return null;

        var leadIn = i > 0 ? speech[i].StartSeconds - speech[i - 1].EndSeconds : speech[i].StartSeconds;
        var leadOut = i + 1 < speech.Count
            ? speech[i + 1].StartSeconds - speech[i].EndSeconds
            : double.PositiveInfinity;
        return new AnnouncementFlanks(leadIn, leadOut, speech[i].StartSeconds);
    }

    /// <summary>Whether <paramref name="flanks"/> clear the thresholds <paramref name="rule"/>
    /// asks for.</summary>
    /// <param name="flanks">The measurement from <see cref="Measure"/>.</param>
    /// <param name="rule">Which flanks this kind of announcement must have; see
    /// <see cref="IsolationRule"/>.</param>
    internal static bool Satisfies(AnnouncementFlanks flanks, IsolationRule rule) =>
        rule switch
        {
            IsolationRule.None => true,
            IsolationRule.LeadIn => flanks.LeadInSeconds >= AnnouncementLeadInSeconds,
            _ => flanks.LeadInSeconds >= AnnouncementLeadInSeconds
                 && flanks.LeadOutSeconds >= AnnouncementLeadOutSeconds,
        };

    /// <summary>The "0.70 s before, 3.23 s after (need 1.0/0.5)" clause a rejection logs - the
    /// whole point being that the numbers behind a dropped mark are visible without a re-run.</summary>
    /// <param name="flanks">The measurement that failed.</param>
    /// <param name="rule">The rule it was judged against.</param>
    internal static string Describe(AnnouncementFlanks flanks, IsolationRule rule)
    {
        var after = double.IsPositiveInfinity(flanks.LeadOutSeconds)
            ? "nothing after"
            : $"{flanks.LeadOutSeconds:0.00} s after";
        var need = rule == IsolationRule.LeadIn
            ? $"need {AnnouncementLeadInSeconds:0.0} before"
            : $"need {AnnouncementLeadInSeconds:0.0}/{AnnouncementLeadOutSeconds:0.0}";
        return $"{flanks.LeadInSeconds:0.00} s before, {after}; {need}";
    }
}

/// <summary>
/// What a mark placement must confirm about its announcement's surroundings before the mark is
/// kept, and what to fall back on when the refinement confirmed nothing.
/// </summary>
/// <param name="Rule">How much of a pause the announcement must sit in; see
/// <see cref="IsolationRule"/>. <see cref="IsolationRule.None"/> makes the whole check a no-op.</param>
/// <param name="FallbackPosition">Where to measure when the refinement produced no onset - because
/// it could not confirm the announcement, or because <c>--quick-marks</c> switched it off. The
/// phrase's own segment start, which is the announcement's position whenever the match opened its
/// segment. Null switches the fallback off, so an unconfirmed match is simply dropped - which is
/// the right answer for a number found in the middle of a segment, whose whole claim to being an
/// announcement is the pause around it.</param>
internal readonly record struct IsolationCheck(IsolationRule Rule, double? FallbackPosition = null)
{
    /// <summary>The no-op check: what a phrase-based chapter, a <c>--custom</c> mark and Pass 2's
    /// first look at a window all pass without measuring anything.</summary>
    internal static readonly IsolationCheck None = new(IsolationRule.None);
}

/// <summary>
/// How much of a pause an announcement must sit in before its mark is kept. Three levels rather
/// than one, because the announcements differ in shape and in what a false positive costs.
/// </summary>
internal enum IsolationRule
{
    /// <summary>No check. A phrase-based chapter announcement, whose phrase is its own evidence; a
    /// <c>--custom</c> mapping, which names a recurring structural element whose place in the book
    /// is the user's business and not something this could second-guess; and Pass 2's first look at
    /// a window, which is deliberately left cheap - see <see cref="AnnouncementIsolation"/>.</summary>
    None,

    /// <summary>A leading pause only. What the prologue and epilogue get: a heading word sits at a
    /// section boundary and so always has a pause in front of it, but the narrator may run straight
    /// on into the text after it. Measured over the fourteen-book corpus (2026-08-05): the twelve
    /// genuine prologue/epilogue/<c>--custom</c> marks all have at least 1.56 s in front, while two
    /// of them - Gruelfin's "Zeittafel" at 0.16 s and "I Shall Wear Midnight"'s epilogue at 0.44 s -
    /// have almost nothing behind, so requiring a trailing pause would have thrown away real
    /// marks.</summary>
    LeadIn,

    /// <summary>Both flanks. What a bare number gets, and what the mode's whole premise rests on:
    /// under <c>--chapter-phrase none</c> the number <em>is</em> the announcement, spoken alone
    /// with a pause on either side, and that shape is the only thing separating it from every year,
    /// price and house number in the prose.</summary>
    Both,
}
