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
/// What this cannot do is tell an announcement from the sentence after it. A chapter's first
/// sentence is flanked by pauses too - that is what a sentence is - so a mark left one sentence
/// late passes here honestly, and on the build-249 re-run of the same book two of them did, at
/// -92 dBFS. That failure belongs to the refinement's own matcher and is fixed there; see
/// <see cref="NumberCheck.AdmitsAsAnnouncement"/>. Tightening a threshold below is the wrong
/// lever for it.
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
    /// The check a numbered chapter's mark is placed under: whatever the wording that matched asked
    /// for with its <c>^</c> and <c>$</c>, plus - for a number spoken alone found by a pass reading
    /// under the wider <see cref="NumberWordParser.BareNumberReading.LeadingASentence"/> - both
    /// flanks regardless, because that reading admits numbers Whisper did not set off by itself.
    /// <para>
    /// Every built-in chapter phrase carries a <c>^</c> on all three of its wordings, so an ordinary
    /// numbered chapter does ask for a leading pause. That is affordable only because
    /// <see cref="WithoutSatisfiedLeadIn"/> also takes the recognizer's word for it: measured over
    /// sixteen books (469 marks, 2026-08-13), a pause alone would have dropped exactly one of them -
    /// "I Shall Wear Midnight" chapter 9, whose announcement follows the previous chapter's last
    /// words after 0.64 s against a threshold of 0.85 s - and that chapter is transcribed as a
    /// segment of its own, so it passes on the second route. A phrase that writes no <c>^</c> still
    /// asks for nothing, its phrase being its own evidence.
    /// </para>
    /// </summary>
    /// <param name="match">The match being placed; its
    /// <see cref="PhraseMatching.PhraseMatch.SpokenAlone"/> decides whether there is anything to
    /// fall back on when the refinement confirms nothing.</param>
    /// <param name="phraseAbs">Absolute start of the segment the announcement was found in.</param>
    /// <param name="wideReading">Whether this pass read the transcript under
    /// <see cref="NumberWordParser.BareNumberReading.LeadingASentence"/> - the same flag that chose the
    /// reading, so the two can never drift apart.</param>
    internal static IsolationCheck ForChapter(
        PhraseMatching.PhraseMatch match, double phraseAbs, bool wideReading)
    {
        var rule = WithoutSatisfiedLeadIn(match.Guards, match.OpensSegment && !match.IsBareNumber);
        if (match.IsBareNumber && wideReading)
            rule |= IsolationRule.Both;
        return rule == IsolationCheck.None.Rule
            ? IsolationCheck.None
            : new IsolationCheck(rule, match.SpokenAlone ? phraseAbs : null);
    }

    /// <summary>
    /// Drops the lead-in requirement where the recognizer already answered it: a match that begins
    /// exactly where its transcript segment begins is one Whisper set off by itself. That is the
    /// second way a <c>^</c> can be satisfied, and it is why a heading whose pause is a shade too
    /// short for the threshold is not thrown away - "I Shall Wear Midnight" chapter 9, the one mark
    /// of the sixteen-book corpus with under 0.85 s in front of it (0.64 s), is transcribed as a
    /// segment of its own and passes on that.
    /// <para>
    /// Deliberately <em>not</em> extended to a number spoken alone, whose whole claim to being an
    /// announcement is the pause around it. That mode exists because Whisper's segmentation is not
    /// dependable - it glued ten of one book's announcements onto the following sentence
    /// (2026-08-05) - so re-admitting that signal there would undo the measurement the mode is
    /// built on. A phrase carries its own evidence and can afford the second reading; a bare number
    /// cannot. Note the segmentation's <em>unreliable</em> direction is the harmless one here: a
    /// real announcement Whisper failed to set off simply falls back on the pause test.
    /// </para>
    /// </summary>
    /// <param name="rule">What the wording asked for.</param>
    /// <param name="opensSegment">Whether the recognizer began a segment at the match.</param>
    private static IsolationRule WithoutSatisfiedLeadIn(IsolationRule rule, bool opensSegment)
        => opensSegment ? rule & ~IsolationRule.LeadIn : rule;

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

    /// <summary>
    /// How far <paramref name="at"/> sits past the start of the VAD speech segment containing it -
    /// i.e. how much continuous speech was already under way when the mark landed. Null when it
    /// falls in no segment at all, which is where a correct mark belongs: in a pause, or in jingle
    /// music, both of which read as non-speech.
    /// <para>
    /// Unlike <see cref="Measure"/> this asks about the finished mark rather than the announcement,
    /// and it deliberately does not look for a neighbouring segment: the question is only "is this
    /// position inside somebody's words", so an answer of "no" needs no further geometry.
    /// </para>
    /// </summary>
    /// <param name="at">The position to test, normally a finished mark.</param>
    /// <param name="speech">The VAD pre-pass's speech segments, chronological. Empty when the
    /// pre-pass did not run, which yields null - no measurement rather than a false verdict.</param>
    /// <returns>Seconds of speech already elapsed at <paramref name="at"/>, or null when it is not
    /// inside a speech segment.</returns>
    internal static double? DepthInsideSpeech(double at, IReadOnlyList<SpeechSegment> speech)
    {
        // Chronological and non-overlapping, so the last segment starting at or before `at` is the
        // only one that can contain it.
        var lo = 0;
        var hi = speech.Count - 1;
        var found = -1;
        while (lo <= hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (speech[mid].StartSeconds <= at)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return found >= 0 && at < speech[found].EndSeconds
            ? at - speech[found].StartSeconds
            : null;
    }

    /// <summary>Whether <paramref name="flanks"/> clear the thresholds <paramref name="rule"/>
    /// asks for.</summary>
    /// <param name="flanks">The measurement from <see cref="Measure"/>.</param>
    /// <param name="rule">Which flanks this kind of announcement must have; see
    /// <see cref="IsolationRule"/>.</param>
    internal static bool Satisfies(AnnouncementFlanks flanks, IsolationRule rule) =>
        (!rule.HasFlag(IsolationRule.LeadIn) || flanks.LeadInSeconds >= AnnouncementLeadInSeconds) &&
        (!rule.HasFlag(IsolationRule.LeadOut) || flanks.LeadOutSeconds >= AnnouncementLeadOutSeconds);

    /// <summary>The "0.70 s before, 3.23 s after; need 0.85/0.3" clause a rejection logs - the
    /// whole point being that the numbers behind a dropped mark are visible without a re-run. The
    /// thresholds print at two decimals rather than one because they are not round numbers, and a
    /// log line reading "need 0.9" against a measured 0.87 would look like a contradiction.</summary>
    /// <param name="flanks">The measurement that failed.</param>
    /// <param name="rule">The rule it was judged against.</param>
    internal static string Describe(AnnouncementFlanks flanks, IsolationRule rule)
    {
        var after = double.IsPositiveInfinity(flanks.LeadOutSeconds)
            ? "nothing after"
            : $"{flanks.LeadOutSeconds:0.00} s after";
        var need = rule switch
        {
            IsolationRule.LeadIn => $"need {AnnouncementLeadInSeconds:0.##} before",
            IsolationRule.LeadOut => $"need {AnnouncementLeadOutSeconds:0.##} after",
            _ => $"need {AnnouncementLeadInSeconds:0.##}/{AnnouncementLeadOutSeconds:0.##}",
        };
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
/// How much of a pause an announcement must sit in before its mark is kept. Flags rather than
/// levels, because the two sides are asked for separately - a phrase's <c>^</c> wants the leading
/// pause and its <c>$</c> the trailing one - and a mark's rule is the union of what the wording that
/// matched asked for and what the pass it was found in demands.
/// </summary>
[Flags]
internal enum IsolationRule
{
    /// <summary>No check. A chapter phrase that writes neither guard, whose phrase is then its own
    /// evidence (though every built-in one does write a <c>^</c>); an untagged <c>--custom</c>
    /// mapping, which names a recurring structural element whose place in the book is the user's
    /// business and not something this could second-guess; and Pass 2's first look at a window,
    /// which is deliberately left cheap - see <see cref="AnnouncementIsolation"/>.</summary>
    None = 0,

    /// <summary>A leading pause. What the prologue and epilogue get, and what a phrase's <c>^</c>
    /// asks for: a heading word sits at a section boundary and so always has a pause in front of it,
    /// but the narrator may run straight on into the text after it. Measured over the fourteen-book
    /// corpus (2026-08-05): the twelve genuine prologue/epilogue/<c>--custom</c> marks all have at
    /// least 1.56 s in front, while two of them - Gruelfin's "Zeittafel" at 0.16 s and "I Shall Wear
    /// Midnight"'s epilogue at 0.44 s - have almost nothing behind, so requiring a trailing pause
    /// would throw away real marks for nothing: no false positive on record survives the lead-in
    /// test.</summary>
    LeadIn = 1,

    /// <summary>A trailing pause, what a phrase's <c>$</c> asks for. Only ever sensible for an
    /// announcement that is spoken alone: over the sixteen-book corpus 17 of 469 marks have under
    /// 0.30 s behind them (2026-08-13), a narrator running from the heading straight into the
    /// text.</summary>
    LeadOut = 2,

    /// <summary>Both flanks. What a bare number gets, and what that wording's whole premise rests
    /// on: under <c>--chapter-phrase none</c> the number <em>is</em> the announcement, spoken alone
    /// with a pause on either side, and that shape is the only thing separating it from every year,
    /// price and house number in the prose.</summary>
    Both = LeadIn | LeadOut,
}
