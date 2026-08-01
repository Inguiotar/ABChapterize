// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Cli;
using ABChapterize.Language;
using ABChapterize.Transcription;
using static ABChapterize.Detection.DetectionFormatting;
using static ABChapterize.Detection.DetectionTuning;
using static ABChapterize.Detection.GapPlanning;
using static ABChapterize.Detection.PhraseMatching;

namespace ABChapterize.Detection;

/// <summary>
/// The stretch of the chapter sequence a number has to fall in to be usable where it was heard -
/// the one rule <see cref="SuspectNumberMender"/> uses both to decide what is worth questioning and
/// to decide what a re-read may be believed about.
/// <para>
/// Open-ended above (<paramref name="Above"/> null) is the ordinary case: nothing is yet known to
/// follow, so the only bound is that the number continues the sequence without leaving an
/// implausible hole - more than <see cref="DetectionTuning.SuspectGapMinMissing"/> chapters
/// unaccounted for at one step. Bounded above is much stronger and arises wherever a stretch of
/// audio is being searched <em>between</em> two chapters already in hand: the Pass 2 sequence-gap
/// re-probe (see <see cref="RegionProber.ReprobeGapCandidatesAsync"/>), a --verify gap region, and
/// the post-hoc repair in <see cref="ChapterDetector.RepairSequenceOutliersAsync"/>. There the
/// admissible numbers are exactly the open interval, which for a one-chapter hole leaves a single
/// possibility and turns "re-read it and see" into "confirm the only answer there is".
/// </para>
/// </summary>
/// <param name="Below">The chapter number the sequence stands at - see
/// <see cref="RegionProber.SequenceBounds"/> for what stands in for it before a region's first
/// mark. An admissible number is strictly above it.</param>
/// <param name="Above">The chapter number already confirmed to follow, or null when nothing is. An
/// admissible number is strictly below it.</param>
internal readonly record struct NumberBounds(int Below, int? Above = null)
{
    /// <summary>Whether <paramref name="number"/> is one this stretch of sequence can hold.</summary>
    /// <param name="number">The chapter number in question.</param>
    internal bool Admits(int number)
        => number > Below &&
           (Above is { } above ? number < above : number - Below - 1 <= SuspectGapMinMissing);

    /// <summary>
    /// Whether a heard number is worth spending transcriptions on: one this stretch cannot hold,
    /// <em>except</em> a number equal to one of the bounds themselves. Those are the signature of an
    /// overlapping probe window re-hearing an announcement already marked, not of a mishearing (all
    /// three occurrences on BARDIOC.m4b, 2026-07-30, were of exactly this kind, and none carried the
    /// "in-text mention?" note that a number genuinely below the sequence gets). Re-reading a
    /// duplicate has nothing to gain and one specific way to lose: the sequence prior, which
    /// everywhere else corroborates a re-read, here rewards inventing a neighbouring number for an
    /// announcement that is not it.
    /// </summary>
    /// <param name="number">The chapter number in question.</param>
    internal bool WorthQuestioning(int number)
        => !Admits(number) && number != Below && number != Above;

    /// <summary>The bounds as a log line names them, so a reader can see what the re-read was held
    /// to without reconstructing it from the numbers.</summary>
    internal string Describe()
        => Above is { } above
            ? $"between chapters {Below} and {above}"
            : $"after chapter {Below}";

    /// <summary>The only number these bounds admit, or null when they admit none or several - the
    /// case that lets <see cref="ChapterDetector.RepairSequenceOutliersAsync"/> renumber a mark from the
    /// sequence alone, with no audio to ask.</summary>
    /// <param name="taken">Numbers already spoken for by other marks, which no longer count as
    /// possibilities however well they fit between the bounds.</param>
    internal int? SoleCandidate(IReadOnlySet<int> taken)
    {
        if (Above is not { } above)
            return null;
        int? only = null;
        for (var n = Below + 1; n < above; n++)
        {
            if (taken.Contains(n))
                continue;
            if (only != null)
                return null;
            only = n;
        }
        return only;
    }
}

/// <summary>
/// Asks the audio again for a chapter number <see cref="RegionProber"/> cannot use as heard. Two
/// shapes reach it, sharing every mechanism below and differing only in what triggers them:
/// a number the sequence cannot plausibly continue with (<see cref="MendAsync"/>) and an
/// announcement whose number could not be read at all (<see cref="ReadUnnumberedAsync"/>).
/// <para>
/// The first is a number the sequence cannot hold where it was heard (see
/// <see cref="NumberBounds"/>). A misheard number is cheap to
/// correct here and ruinously expensive to live with: the numbers Whisper confuses are the ones that
/// sound alike rather than the ones that are close in value, so a single slip - "neunzehn" read as 90
/// on BARDIOC.m4b, 2026-07-30 - declares seventy chapters missing and commits Pass 2.5 and Pass 3 to
/// re-probing and then fully transcribing hours of audio that has nothing hidden in it, only to end up
/// with a mark that still carries the wrong number. The mirror slip costs a chapter outright: a number
/// misheard <em>downwards</em> reads as an in-text mention of a chapter already passed and the
/// announcement is discarded.
/// </para>
/// <para>
/// Two ways to ask again, tried in that order and stopping at the first answer that helps:
/// the heavier <c>--pass3-model</c> where one was chosen (a better recognizer on the same audio and
/// the same framing, so the model is the only variable), and otherwise - or in addition, when the
/// heavier model reads it the same way - the pass-2 model on differently framed windows over the
/// same announcement (<see cref="SuspectGapReframes"/>). The second path matters because the
/// upgrade path is opt-in and the mishearing is not: a run without <c>--pass3-model</c> is exactly
/// the run that can least afford a needless Pass 3.
/// </para>
/// <para>
/// One rule decides both what is worth questioning (<see cref="NumberBounds.WorthQuestioning"/>)
/// and what may be adopted: a number belongs to the sequence only if the stretch it was heard in can
/// hold it (<see cref="NumberBounds.Admits"/>). That is what makes
/// second-guessing safe rather than a second chance to be wrong - the sequence itself is strong
/// evidence, chapter numbers ascending one at a time, so a re-read that lands where the sequence
/// expects one is corroborated by the sequence, while a re-read agreeing with the suspect reading, or
/// wandering somewhere else entirely, has nothing to recommend it over the reading already in hand.
/// The suspect number can never satisfy the rule that flagged it, so "the re-read agrees" and "nothing
/// better was found" are the same outcome, and the caller is left to do exactly what it would have
/// done unaided.
/// </para>
/// <para>
/// Nothing but the number changes: the mark's position comes from the original window's transcript
/// either way, so a correction never moves a mark - and an unnumbered announcement that a re-read
/// rescues is marked exactly where the original reading heard it, not where the re-framed window
/// happened to time it.
/// </para>
/// </summary>
internal sealed class SuspectNumberMender
{
    private readonly ProbeEnvironment _env;
    private readonly Pass2Context _ctx;
    private readonly DetectionRegion _region;

    /// <summary>Creates a mender for one region's probing.</summary>
    /// <param name="env">The file-wide probe environment (logging, transcription, phrase matching).</param>
    /// <param name="ctx">The file-wide Pass 2 context (file, decoder, probe transcriber).</param>
    /// <param name="region">The region being probed, whose bounds clip every re-framed window.</param>
    internal SuspectNumberMender(ProbeEnvironment env, Pass2Context ctx, DetectionRegion region)
    {
        _env = env;
        _ctx = ctx;
        _region = region;
    }

    /// <summary>
    /// Re-reads <paramref name="match"/>'s chapter number when it does not continue the sequence, in
    /// either direction.
    /// </summary>
    /// <param name="match">The phrase match in question, in window-relative time.</param>
    /// <param name="profile">The resolved language profile, for re-matching the phrase.</param>
    /// <param name="start">Absolute start of the probe window the match came from.</param>
    /// <param name="windowEnd">Absolute end of that window; the upgrade model re-reads exactly it.</param>
    /// <param name="bounds">Where in the chapter sequence this announcement sits - see
    /// <see cref="RegionProber.SequenceBounds"/> for how a probe derives them.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number to use instead, or null to leave <paramref name="match"/> exactly as heard -
    /// which covers both "nothing to question" and "questioned, nothing better found".</returns>
    internal async Task<int?> MendAsync(
        PhraseMatch match, LanguageProfile profile, double start, double windowEnd, NumberBounds bounds,
        CancellationToken ct)
    {
        if (!bounds.WorthQuestioning(match.Number))
            return null;

        var phraseAbs = start + match.PhraseStartSeconds;
        _env.Log?.Invoke(
            $"chapter {match.Number} at {FormatTimestamp(phraseAbs)} does not fit the sequence " +
            $"{bounds.Describe()} - {WhyItDoesNotFit(match.Number, bounds)}, re-reading its number");

        var reread = await ReReadNumberAsync(
            profile, start, windowEnd, phraseAbs, match.PhraseEndSeconds - match.PhraseStartSeconds,
            bounds, match.Number, ct);
        if (reread == null)
            _env.Log?.Invoke(
                $"no number continuing the sequence could be read there - leaving it at {match.Number}");
        return reread;
    }

    /// <summary>
    /// Reads a number for an announcement whose phrase was heard but whose number the parser could
    /// not make out at all - the same machinery <see cref="MendAsync"/> uses, aimed at the shape
    /// where there is no reading to disagree with rather than a wrong one.
    /// <para>
    /// Worth the decodes for the same reason the Pass 3 counterpart
    /// (<see cref="ChapterDetector.ScanUnnumberedRetriesAsync"/>) is: the recognizer was right there
    /// and got the words, and only the notation defeated the parser - "CHAPTER XIII" instead of
    /// "Chapter 13", "chapitre ban 5" for a slurred "vingt-cinq". Which of those a given stretch of
    /// audio comes out as follows the window framing, so re-framing it is a genuinely different
    /// question and not a re-roll. Bringing it to Pass 2 closes the one hole the Pass 3 version
    /// cannot reach: an announcement past the last detected chapter is in no gap at all, so without
    /// <c>--trailing-scan</c> nothing ever transcribes it again. That is how the final chapter of
    /// "Paula Monti" was lost on 2026-07-31 - heard as "1ère partie, chapitre ban 5, douleur" at
    /// 4:23:03 and never looked at again. Re-running that file with this in place, the upgrade model
    /// read the same window as chapter 25 on the first attempt, before either re-framing was needed.
    /// </para>
    /// <para>
    /// Adoption is the same <see cref="NumberBounds.Admits"/> rule, which is what keeps this from
    /// planting marks on prose: an in-text "the next chapter was harder" reaches here too, and so
    /// does a window re-hearing an announcement already marked (observed as "CHAPTER X" over an
    /// already-found chapter 10 on "I Shall Wear Midnight", 2026-07-31). Neither can produce a
    /// number that continues the sequence, so neither can produce a mark.
    /// </para>
    /// </summary>
    /// <param name="heard">The unreadable announcement, in window-relative time.</param>
    /// <param name="profile">The resolved language profile, for re-matching the phrase.</param>
    /// <param name="start">Absolute start of the probe window it was heard in.</param>
    /// <param name="windowEnd">Absolute end of that window; the upgrade model re-reads exactly it.</param>
    /// <param name="bounds">Where in the chapter sequence this announcement sits.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The chapter number to mark this announcement with, or null when none could be read.</returns>
    internal async Task<int?> ReadUnnumberedAsync(
        UnnumberedAnnouncement heard, LanguageProfile profile, double start, double windowEnd,
        NumberBounds bounds, CancellationToken ct)
    {
        var phraseAbs = start + heard.PhraseStartSeconds;
        _env.Log?.Invoke($"re-reading the unnumbered announcement at {FormatTimestamp(phraseAbs)}");

        var reread = await ReReadNumberAsync(
            profile, start, windowEnd, phraseAbs, heard.PhraseEndSeconds - heard.PhraseStartSeconds,
            bounds, null, ct);
        if (reread == null)
            _env.Log?.Invoke("no number continuing the sequence could be read there either - " +
                             "leaving the announcement unmarked");
        return reread;
    }

    /// <summary>
    /// Which of the three ways a number can miss its bounds this one missed, for the log line. Worth
    /// spelling out rather than leaving the reader to subtract: "it would leave 88 missing" is the
    /// figure that explains why a Pass 3 over most of the book was about to be scheduled, and the
    /// three cases have quite different causes behind them.
    /// </summary>
    /// <param name="number">The number that did not fit.</param>
    /// <param name="bounds">The bounds it was measured against.</param>
    private static string WhyItDoesNotFit(int number, NumberBounds bounds)
        => number <= bounds.Below ? "it is not above it"
            : bounds.Above is { } above && number >= above ? $"chapter {above} already follows it"
            : $"it would leave {number - bounds.Below - 1} missing";

    /// <summary>
    /// Re-reads the number of an announcement that is only known by the mark placed on it - the
    /// question <see cref="ChapterDetector.RepairSequenceOutliersAsync"/> asks about a mark the chapter
    /// sequence has since contradicted.
    /// <para>
    /// Different from the two entry points above in one respect only: there is no probe window left
    /// to hand the upgrade model, since the window that produced this mark belonged to a pass that
    /// has finished. Only the re-framings run, which is no great loss - they are the half that
    /// changes the question rather than the recognizer, and by this point the announcement has
    /// already been through the upgrade model wherever <c>--pass3-model</c> made one available.
    /// </para>
    /// <para>
    /// Where the announcement is, is derived rather than remembered: precise marking places a mark
    /// <see cref="CliOptions.MarkLeadSeconds"/> before the onset it confirmed, so adding that lead
    /// back lands on the announcement. A mark it could not confirm - or one placed by
    /// <c>--quick-marks</c> or walked back by <c>--mark-before-jingle</c> - sits further ahead of
    /// the phrase, which the re-framings absorb: their leads are seconds wide and
    /// <see cref="NumberNear"/> accepts a reading within <see cref="PhraseMarginSeconds"/> of the
    /// estimate.
    /// </para>
    /// </summary>
    /// <param name="markSeconds">The placed mark whose number is in doubt.</param>
    /// <param name="markLeadSeconds">The lead the mark was placed with, added back to estimate where
    /// the announcement itself is.</param>
    /// <param name="profile">The resolved language profile, for re-matching the phrase.</param>
    /// <param name="bounds">Where in the chapter sequence this mark now has to fit.</param>
    /// <param name="suspect">The number the mark currently carries.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number to renumber the mark to, or null when nothing usable could be read.</returns>
    internal async Task<int?> ReReadAtMarkAsync(
        double markSeconds, double markLeadSeconds, LanguageProfile profile, NumberBounds bounds,
        int suspect, CancellationToken ct)
    {
        var phraseAbs = markSeconds + markLeadSeconds;
        foreach (var (lead, length) in SuspectGapReframes)
        {
            ct.ThrowIfCancellationRequested();
            if (await ReadReframedAsync(profile, phraseAbs, 0, lead, length, bounds, ct) is { } reframed &&
                Adopt(reframed, bounds, suspect, $"a {length:0.#} s window") is { } adopted)
                return adopted;
        }
        return null;
    }

    /// <summary>
    /// The two ways of asking the audio again, tried in order and stopping at the first answer that
    /// <see cref="Adopt"/> accepts. Shared by both entry points because the question is identical
    /// once it is asked - only what prompted it differs.
    /// </summary>
    /// <param name="profile">The resolved language profile.</param>
    /// <param name="start">Absolute start of the probe window the announcement came from.</param>
    /// <param name="windowEnd">Absolute end of that window.</param>
    /// <param name="phraseAbs">Absolute position of the announcement.</param>
    /// <param name="phraseLengthSeconds">How long the announcement's own segment ran, which bounds
    /// how short a re-framed window may usefully be.</param>
    /// <param name="bounds">Where in the chapter sequence this announcement sits.</param>
    /// <param name="suspect">The number originally heard, or null when none could be read.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<int?> ReReadNumberAsync(
        LanguageProfile profile, double start, double windowEnd, double phraseAbs,
        double phraseLengthSeconds, NumberBounds bounds, int? suspect, CancellationToken ct)
    {
        if (_env.SecondOpinion != null &&
            await ReadWithUpgradeAsync(profile, start, windowEnd, phraseAbs, bounds, ct) is { } upgraded &&
            Adopt(upgraded, bounds, suspect, "the pass 3 model") is { } fromUpgrade)
            return fromUpgrade;

        foreach (var (lead, length) in SuspectGapReframes)
        {
            ct.ThrowIfCancellationRequested();
            if (await ReadReframedAsync(
                    profile, phraseAbs, phraseLengthSeconds, lead, length, bounds, ct) is { } reframed &&
                Adopt(reframed, bounds, suspect, $"a {length:0.#} s window") is { } fromReframe)
                return fromReframe;
        }
        return null;
    }

    /// <summary>
    /// Accepts a re-read number only if the sequence can hold it, logging either way - a re-read that
    /// changes nothing is worth a line, since it is the difference between "the number was checked and
    /// stands" and "the number was never questioned".
    /// </summary>
    /// <param name="reread">The number the re-read produced.</param>
    /// <param name="bounds">Where in the chapter sequence this announcement sits.</param>
    /// <param name="suspect">The number originally heard, or null when none could be read - the
    /// only difference the two entry points make once an answer is in hand, and one the log lines
    /// have to carry because "corrected 90 to 19" and "read a number where there was none" are
    /// different events to whoever is chasing a mark.</param>
    /// <param name="source">How the re-read was obtained, for the log line.</param>
    /// <returns><paramref name="reread"/> when it qualifies, else null.</returns>
    private int? Adopt(int reread, NumberBounds bounds, int? suspect, string source)
    {
        if (!bounds.Admits(reread))
        {
            _env.Log?.Invoke($"{source} read it as {reread} - " + (suspect is { } no
                ? $"no improvement on {no}"
                : $"which does not fit the sequence {bounds.Describe()}"));
            return null;
        }
        _env.Log?.Invoke(suspect is { } previous
            ? $"{source} read it as {reread} instead of {previous} - " +
              "correcting the number, the mark stays where it is"
            : $"{source} read it as chapter {reread} - marking the announcement after all");
        return reread;
    }

    /// <summary>
    /// Re-reads the announcement from the very window the suspect number came from, using the
    /// heavier <c>--pass3-model</c>. Deliberately the same audio and the same framing: with the
    /// recognizer as the only difference, a disagreement means the better model heard it better,
    /// which is the whole premise of choosing an upgrade for pass 3 in the first place.
    /// </summary>
    /// <param name="profile">The resolved language profile.</param>
    /// <param name="start">Absolute start of the probe window.</param>
    /// <param name="windowEnd">Absolute end of the probe window.</param>
    /// <param name="phraseAbs">Absolute position of the announcement being re-read.</param>
    /// <param name="bounds">Where in the chapter sequence this announcement sits.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<int?> ReadWithUpgradeAsync(
        LanguageProfile profile, double start, double windowEnd, double phraseAbs, NumberBounds bounds,
        CancellationToken ct)
    {
        var samples = await _env.Audio.DecodePcmAsync(
            _ctx.File, start, windowEnd - start, _ctx.Info.InputDecoder, ct);
        var segments = await _env.SecondOpinion!(samples, profile.Language, ct);
        return NumberNear(segments, profile, start, phraseAbs, bounds);
    }

    /// <summary>
    /// Re-reads the announcement with the pass-2 model from a window of the given shape, clipped to
    /// the region. Skips a frame that the clipping has left shorter than the announcement itself
    /// needs, which would ask the recognizer to read a number out of audio that no longer contains
    /// it.
    /// </summary>
    /// <param name="profile">The resolved language profile.</param>
    /// <param name="phraseAbs">Absolute position of the announcement being re-read.</param>
    /// <param name="phraseLengthSeconds">How long the announcement's own segment ran, which bounds
    /// the minimum useful frame.</param>
    /// <param name="lead">Seconds of the frame that precede the announcement.</param>
    /// <param name="length">Total frame length in seconds.</param>
    /// <param name="bounds">Where in the chapter sequence this announcement sits.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<int?> ReadReframedAsync(
        LanguageProfile profile, double phraseAbs, double phraseLengthSeconds, double lead,
        double length, NumberBounds bounds, CancellationToken ct)
    {
        var from = Math.Max(_region.FromSeconds, phraseAbs - lead);
        var to = Math.Min(_region.ToSeconds, from + length);
        if (to - from < phraseAbs - from + phraseLengthSeconds)
            return null;

        var samples = await _env.Audio.DecodePcmAsync(
            _ctx.File, from, to - from, _ctx.Info.InputDecoder, ct);
        var segments = await _env.TranscribeCounting(samples, ct, _ctx.Transcriber);
        _env.LogTranscript($"re-read {to - from:0.0}s@{FormatTimestamp(from)}", segments);
        return NumberNear(segments, profile, from, phraseAbs, bounds);
    }

    /// <summary>
    /// Picks the chapter number this re-read offers for the announcement at
    /// <paramref name="phraseAbs"/>: among the phrase matches within
    /// <see cref="PhraseMarginSeconds"/> of it, one the sequence can hold, nearest first. The
    /// proximity bound is what keeps a wide frame from answering with a different announcement's
    /// number.
    /// <para>
    /// Preferring a sequence-fitting reading over a merely nearer one is not looking for the
    /// desired answer: only such a reading can be adopted at all (see <see cref="Adopt"/>), so
    /// ranking by distance alone would discard usable readings without ever making a wrong one
    /// acceptable. What it costs is precision in one narrow case - a chapter announcement with an
    /// in-text mention of the very next expected chapter within five seconds of it - where the number
    /// adopted is the one the sequence expected anyway, and the mark's position is unaffected either
    /// way.
    /// </para>
    /// </summary>
    /// <param name="segments">The re-read transcript, relative to <paramref name="frameStart"/>.</param>
    /// <param name="profile">The resolved language profile.</param>
    /// <param name="frameStart">Absolute start of the window the transcript came from.</param>
    /// <param name="phraseAbs">Absolute position of the announcement being re-read.</param>
    /// <param name="bounds">Where in the chapter sequence this announcement sits.</param>
    /// <returns>A usable number read at that position, or null when the re-read offers none.</returns>
    private int? NumberNear(
        List<TranscriptSegment> segments, LanguageProfile profile, double frameStart, double phraseAbs,
        NumberBounds bounds)
    {
        var candidates = _env.FindCappedPhraseMatches(segments, profile, null)
            .Select(m => (m.Number, Distance: Math.Abs(frameStart + m.PhraseStartSeconds - phraseAbs)))
            .Where(m => m.Distance <= PhraseMarginSeconds)
            .OrderByDescending(m => bounds.Admits(m.Number))
            .ThenBy(m => m.Distance)
            .ToList();
        return candidates.Count > 0 ? candidates[0].Number : null;
    }
}
