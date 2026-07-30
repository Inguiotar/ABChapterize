// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Language;
using ABChapterize.Transcription;
using static ABChapterize.Detection.DetectionFormatting;
using static ABChapterize.Detection.DetectionTuning;
using static ABChapterize.Detection.GapPlanning;
using static ABChapterize.Detection.PhraseMatching;

namespace ABChapterize.Detection;

/// <summary>
/// Second-guesses a chapter number the sequence cannot plausibly continue with, before
/// <see cref="RegionProber"/> acts on it - too far above the last accepted chapter (leaving more than
/// <see cref="SuspectGapMinMissing"/> missing) or at or below it. A misheard number is cheap to
/// correct here and ruinously expensive to live with: the numbers Whisper confuses are the ones that
/// sound alike rather than the ones that are close in value, so a single slip - "neunzehn" read as 90
/// on BARDIOC.m4b, 2026-07-30 - declares seventy chapters missing and commits Pass 2.5 and Pass 3 to
/// re-probing and then fully transcribing hours of audio that has nothing hidden in it, only to end up
/// with a mark that still carries the wrong number. The mirror slip costs a chapter outright: a number
/// misheard <em>downwards</em> reads as an in-text mention of a chapter already passed and the
/// announcement is discarded.
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
/// One rule decides both what is worth questioning (<see cref="WorthQuestioning"/>) and what may be
/// adopted: a number belongs to the sequence only if it continues it, i.e. sits above the last accepted
/// chapter and leaves at most <see cref="SuspectGapMinMissing"/> behind
/// (see <see cref="ContinuesSequence"/>). That is what makes
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
/// either way, so a correction never moves a mark.
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
    /// <param name="below">The chapter number the sequence stands at - see
    /// <see cref="RegionProber.SequenceFloor"/> for what stands in for it before a region's first
    /// mark.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number to use instead, or null to leave <paramref name="match"/> exactly as heard -
    /// which covers both "nothing to question" and "questioned, nothing better found".</returns>
    internal async Task<int?> MendAsync(
        PhraseMatch match, LanguageProfile profile, double start, double windowEnd, int below,
        CancellationToken ct)
    {
        if (!WorthQuestioning(match.Number, below))
            return null;

        var phraseAbs = start + match.PhraseStartSeconds;
        _env.Log?.Invoke(
            $"chapter {match.Number} at {FormatTimestamp(phraseAbs)} does not continue the sequence " +
            (match.Number > below
                ? $"after chapter {below} - it would leave {match.Number - below - 1} missing"
                : $"after chapter {below} - it is not above it") +
            ", re-reading its number");

        if (_env.SecondOpinion != null &&
            await ReadWithUpgradeAsync(profile, start, windowEnd, phraseAbs, below, ct) is { } upgraded &&
            Adopt(upgraded, below, match.Number, "the pass 3 model") is { } fromUpgrade)
            return fromUpgrade;

        foreach (var (lead, length) in SuspectGapReframes)
        {
            ct.ThrowIfCancellationRequested();
            if (await ReadReframedAsync(profile, phraseAbs, match, lead, length, below, ct) is { } reframed &&
                Adopt(reframed, below, match.Number, $"a {length:0.#} s window") is { } fromReframe)
                return fromReframe;
        }

        _env.Log?.Invoke(
            $"no number continuing the sequence could be read there - leaving it at {match.Number}");
        return null;
    }

    /// <summary>
    /// Whether <paramref name="number"/> is one the chapter sequence can continue with from
    /// <paramref name="below"/>: above it, and no further above than
    /// <see cref="SuspectGapMinMissing"/> unheard chapters would explain. This is what a re-read has to
    /// satisfy to be adopted.
    /// </summary>
    /// <param name="number">The chapter number in question.</param>
    /// <param name="below">The chapter number the sequence stands at.</param>
    internal static bool ContinuesSequence(int number, int below)
        => number > below && number - below - 1 <= SuspectGapMinMissing;

    /// <summary>
    /// Whether a heard number is worth spending transcriptions on. Everything
    /// <see cref="ContinuesSequence"/> rejects, <em>except</em> a number equal to
    /// <paramref name="below"/>: that is the signature of a later, overlapping probe window re-hearing
    /// the announcement already marked, not of a mishearing (all three occurrences on BARDIOC.m4b,
    /// 2026-07-30, were of exactly this kind, and none carried the "in-text mention?" note that a
    /// number genuinely below the sequence gets). Re-reading a duplicate has nothing to gain and one
    /// specific way to lose: the sequence prior, which everywhere else corroborates a re-read, here
    /// rewards inventing the very next number for an announcement that is not it.
    /// </summary>
    /// <param name="number">The chapter number in question.</param>
    /// <param name="below">The chapter number the sequence stands at.</param>
    internal static bool WorthQuestioning(int number, int below)
        => number < below || number - below - 1 > SuspectGapMinMissing;

    /// <summary>
    /// Accepts a re-read number only if it continues the sequence, logging either way - a re-read that
    /// changes nothing is worth a line, since it is the difference between "the number was checked and
    /// stands" and "the number was never questioned".
    /// </summary>
    /// <param name="reread">The number the re-read produced.</param>
    /// <param name="below">The chapter number the sequence stands at.</param>
    /// <param name="suspect">The number originally heard.</param>
    /// <param name="source">How the re-read was obtained, for the log line.</param>
    /// <returns><paramref name="reread"/> when it qualifies, else null.</returns>
    private int? Adopt(int reread, int below, int suspect, string source)
    {
        if (!ContinuesSequence(reread, below))
        {
            _env.Log?.Invoke($"{source} read it as {reread} - no improvement on {suspect}");
            return null;
        }
        _env.Log?.Invoke($"{source} read it as {reread} instead of {suspect} - " +
                         $"correcting the number, the mark stays where it is");
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
    /// <param name="below">The chapter number the sequence stands at.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<int?> ReadWithUpgradeAsync(
        LanguageProfile profile, double start, double windowEnd, double phraseAbs, int below,
        CancellationToken ct)
    {
        var samples = await _env.Audio.DecodePcmAsync(
            _ctx.File, start, windowEnd - start, _ctx.Info.InputDecoder, ct);
        var segments = await _env.SecondOpinion!(samples, profile.Language, ct);
        return NumberNear(segments, profile, start, phraseAbs, below);
    }

    /// <summary>
    /// Re-reads the announcement with the pass-2 model from a window of the given shape, clipped to
    /// the region. Skips a frame that the clipping has left shorter than the announcement itself
    /// needs, which would ask the recognizer to read a number out of audio that no longer contains
    /// it.
    /// </summary>
    /// <param name="profile">The resolved language profile.</param>
    /// <param name="phraseAbs">Absolute position of the announcement being re-read.</param>
    /// <param name="match">The original match, whose phrase end bounds the minimum useful frame.</param>
    /// <param name="lead">Seconds of the frame that precede the announcement.</param>
    /// <param name="length">Total frame length in seconds.</param>
    /// <param name="below">The chapter number the sequence stands at.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<int?> ReadReframedAsync(
        LanguageProfile profile, double phraseAbs, PhraseMatch match, double lead, double length,
        int below, CancellationToken ct)
    {
        var phraseLength = match.PhraseEndSeconds - match.PhraseStartSeconds;
        var from = Math.Max(_region.FromSeconds, phraseAbs - lead);
        var to = Math.Min(_region.ToSeconds, from + length);
        if (to - from < phraseAbs - from + phraseLength)
            return null;

        var samples = await _env.Audio.DecodePcmAsync(
            _ctx.File, from, to - from, _ctx.Info.InputDecoder, ct);
        var segments = await _env.TranscribeCounting(samples, ct, _ctx.Transcriber);
        _env.LogTranscript($"re-read {to - from:0.0}s@{FormatTimestamp(from)}", segments);
        return NumberNear(segments, profile, from, phraseAbs, below);
    }

    /// <summary>
    /// Picks the chapter number this re-read offers for the announcement at
    /// <paramref name="phraseAbs"/>: among the phrase matches within
    /// <see cref="PhraseMarginSeconds"/> of it, one that continues the sequence, nearest first. The
    /// proximity bound is what keeps a wide frame from answering with a different announcement's
    /// number.
    /// <para>
    /// Preferring a sequence-continuing reading over a merely nearer one is not looking for the
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
    /// <param name="below">The chapter number the sequence stands at.</param>
    /// <returns>A usable number read at that position, or null when the re-read offers none.</returns>
    private int? NumberNear(
        List<TranscriptSegment> segments, LanguageProfile profile, double frameStart, double phraseAbs,
        int below)
    {
        var candidates = _env.FindCappedPhraseMatches(segments, profile, null)
            .Select(m => (m.Number, Distance: Math.Abs(frameStart + m.PhraseStartSeconds - phraseAbs)))
            .Where(m => m.Distance <= PhraseMarginSeconds)
            .OrderByDescending(m => ContinuesSequence(m.Number, below))
            .ThenBy(m => m.Distance)
            .ToList();
        return candidates.Count > 0 ? candidates[0].Number : null;
    }
}
