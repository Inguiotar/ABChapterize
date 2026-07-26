// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Audio;
using ABChapterize.Cli;
using ABChapterize.Language;
using ABChapterize.Transcription;
using ABChapterize.Vad;
using static ABChapterize.Detection.DetectionFormatting;
using static ABChapterize.Detection.DetectionTuning;

namespace ABChapterize.Detection;

/// <summary>
/// Outcome of <see cref="PreciseMarkRefiner.RefinePreciseMarkAsync"/>: the mark itself, plus
/// whether the chapter phrase was ever actually heard while producing it.
/// </summary>
/// <param name="Mark">The confirmed, corrected or (when nothing could be confirmed) unchanged
/// mark, quiet-snapped.</param>
/// <param name="PhraseHeard">True when the phrase was heard - either at the incoming mark itself
/// or at a candidate the search confirmed - so <paramref name="Mark"/> is known to sit
/// <see cref="DetectionTuning.DefaultMarkLeadSeconds"/> before a real announcement onset. False
/// only in the "could not confirm the phrase" case, where the mark is still whatever the default
/// heuristics produced and its distance from the announcement is unknown. Downstream steps that
/// reason about where the announcement is relative to the mark - <see
/// cref="PreciseMarkRefiner.VerifyMarkBeforeJingleAsync"/> above all - are only sound when this
/// holds.</param>
internal readonly record struct PreciseMarkResult(double Mark, bool PhraseHeard);

/// <summary>Implements the precise marking correction (<see cref="CliOptions.PreciseMark"/>):
/// verifies a default-mode mark by directly asking Whisper whether the chapter phrase starts
/// there, and if not, searches nearby VAD-candidate and fixed-step positions - see
/// <see cref="RefinePreciseMarkAsync"/>, the entry point <see cref="ChapterDetector"/> calls,
/// for the full algorithm.</summary>
internal sealed class PreciseMarkRefiner
{
    private readonly IAudioSource _audio;
    private readonly CliOptions _options;
    private readonly Action<string>? _log;
    private readonly Func<float[], CancellationToken, Task<List<TranscriptSegment>>> _transcribeCounting;

    /// <summary>Creates a refiner bound to the given tools and options.</summary>
    /// <param name="audio">Audio source used for PCM decoding.</param>
    /// <param name="options">Validated command line options.</param>
    /// <param name="log">Per-file --verbose log sink, or null when not verbose.</param>
    /// <param name="transcribeCounting">Delegate onto <see cref="ChapterDetector"/>'s own
    /// transcribe-with-stat-counting helper, so this class's transcriptions are tallied into the
    /// same per-file Whisper audio/time statistics as every other detection-path recognition,
    /// without duplicating that accumulation logic here.</param>
    internal PreciseMarkRefiner(
        IAudioSource audio, CliOptions options, Action<string>? log,
        Func<float[], CancellationToken, Task<List<TranscriptSegment>>> transcribeCounting)
    {
        _audio = audio;
        _options = options;
        _log = log;
        _transcribeCounting = transcribeCounting;
    }

    /// <summary>
    /// Checks whether <paramref name="profile"/>'s chapter phrase is really the first thing heard
    /// starting at <paramref name="start"/>, by transcribing a short, isolated window there
    /// directly - the precise marking correction's basic building block (see
    /// <see cref="RefinePreciseMarkAsync"/>), used both for the mark itself and for every
    /// candidate position considered while correcting it. Sidesteps Whisper's own segment
    /// timestamps entirely, which is the point: those are demonstrably unreliable close to a
    /// jingle (a single segment has been observed spanning almost an entire 18 s jingle) and
    /// therefore useless for pinpointing anything - this only ever asks a binary question of a
    /// small, self-contained decode instead of trusting where a large decode claims a phrase
    /// began.
    /// </summary>
    /// <param name="start">Absolute position to check.</param>
    /// <param name="file">Path of the audio file.</param>
    /// <param name="inputDecoder">Explicit input decoder to force, or null.</param>
    /// <param name="profile">Language profile supplying the phrase to look for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True when the first non-blank transcribed segment contains the chapter phrase.</returns>
    private async Task<bool> PreciseMarkPhraseFoundAsync(
        double start, string file, string? inputDecoder, LanguageProfile profile, CancellationToken ct)
    {
        var decodeStart = Math.Max(0, start - PreciseMarkLeadInSeconds);
        var samples = await _audio.DecodePcmAsync(
            file, decodeStart, PreciseMarkCheckWindowSeconds + (start - decodeStart), inputDecoder, ct);
        var transcript = await _transcribeCounting(samples, ct);
        // The lead-in can surface a trailing fragment of whatever preceded `start` as the first
        // segment (e.g. the jingle's own tail, or the previous chapter's last words) - the first
        // *non-blank* segment is what actually starts at or after the checked position.
        var first = transcript.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.Text));
        return first.Text != null && profile.PhraseRegex.IsMatch(first.Text);
    }

    /// <summary>
    /// Verifies (and if necessary, corrects) a default-mode mark by directly asking Whisper
    /// "does the chapter phrase start right here?" instead of trusting the VAD/duration
    /// heuristics <see cref="RefineDefaultMark"/> already applied - the precise marking option
    /// (<see cref="CliOptions.PreciseMark"/>). Those heuristics rest on a floor deliberately
    /// calibrated to err toward not skipping real speech (see
    /// <see cref="TransientSpeechFloorSeconds"/>'s remarks on cross-language uncertainty), which
    /// means a spurious VAD "speech" blip inside a jingle that happens to clear that floor - a
    /// vocal-like musical transient, or an occasional Whisper hallucination - can still fool them
    /// into stopping short of the true announcement; this asks the audio directly instead, at the
    /// cost of one or more extra transcriptions per chapter.
    /// <para>
    /// First checks <paramref name="mark"/> itself: if its own phrase is heard there, it is
    /// already correct and is returned unchanged - the common case, and the only cost paid for a
    /// chapter that needed no correction. Otherwise, round 1 searches VAD speech-segment starts
    /// within a plausible jingle-plus-phrase span (<see cref="CliOptions.MaxJingleSeconds"/> plus
    /// <see cref="PhraseMarginSeconds"/>) of <paramref name="mark"/> - the same swallowed-blip
    /// candidates <see cref="ResolveDefaultPhraseOnset"/> already reasons about, not a blind
    /// fixed-step scan - via <see cref="WalkPreciseMarkCandidatesInterleavedAsync"/>, which tries
    /// the forward (later-than-mark) and backward (earlier-than-mark) candidates one at a time in
    /// alternation - backward, then forward, then backward again - rather than exhausting one
    /// direction before ever trying the other.
    /// </para>
    /// <para>
    /// The two directions are trusted asymmetrically, for two distinct failure shapes. A forward
    /// success only locks in once the <em>next</em> forward candidate afterward fails - that
    /// success-then-fail pattern confirms the phrase truly begins at the earlier candidate, immune
    /// to a single stray false positive elsewhere in the jingle, since a real chapter
    /// announcement's own audio ends and narration (or some unrelated cue) resumes right after it.
    /// A backward success, by contrast, is accepted the moment it is heard, with no further
    /// checking: <see cref="ResolveDefaultPhraseOnset"/>'s swallowed-blip clustering can
    /// occasionally promote a later, unrelated blip inside an over-merged jingle region over the
    /// announcement's own earlier one, landing <paramref name="mark"/> generously past the true
    /// onset instead of short of it (confirmed live on chapters whose true onset sat mere seconds
    /// before what the heuristic reported - Perry Rhodan "Die Dritte Macht", chapters 8 and 20,
    /// 2026-07-24) - once the search has walked back past that later blip to the real
    /// announcement, there is nothing earlier still worth preferring over it. Because forward
    /// needs corroboration but backward does not, the instant any forward candidate succeeds,
    /// backward is abandoned for the rest of this search - only that forward success still needs
    /// confirming, by further forward candidates alone. Both spans are bounded to the same
    /// jingle-plus-phrase distance from <paramref name="mark"/>, so neither direction can wander
    /// into a neighbouring chapter's own territory. This deliberately does not touch <see
    /// cref="ResolveDefaultPhraseOnset"/> itself - default-mode marking, which is all --quick-marks
    /// leaves in place, must stay exactly as heuristically accurate as it already is, since it
    /// alone is what makes quick marks usable for jumping to a chapter.
    /// </para>
    /// <para>
    /// If round 1 never confirms anything in either direction - it relies entirely on
    /// VAD-reported speech-segment starts, so it can only find what VAD itself noticed - a second
    /// round repeats the same interleaved search over the same span, but stepping by a fixed <see
    /// cref="PreciseMarkFixedStepSeconds"/> instead of VAD candidates: a blind scan immune to VAD
    /// missing or mis-clustering the announcement's blip entirely (e.g. a jingle quiet enough, or
    /// short enough, that VAD reports no speech segment inside it at all). Costs more
    /// transcriptions than round 1 (a check roughly every <see
    /// cref="PreciseMarkFixedStepSeconds"/> across the whole span instead of only at actual VAD
    /// segment starts), so it only ever runs after round 1 has already come up empty.
    /// </para>
    /// <para>
    /// When neither round nor direction ever confirms a candidate, <paramref name="mark"/> itself
    /// carries forward into the final step below rather than guessing - validated against a real
    /// audiobook's full set of known good and previously-broken marks (see <c>tools\vadprobe</c>'s
    /// <c>precise</c> prototype) before this was ported here.
    /// </para>
    /// <para>
    /// Final cleanup step, applied regardless of which of the above produced the mark (even one
    /// left exactly as given): <see cref="SnapToQuietestPointAsync"/> nudges it backward - never
    /// later - to a quieter point within <see cref="PreciseMarkQuietSnapRadiusSeconds"/> before it,
    /// provided one is at least <see cref="PreciseMarkQuietSnapMinImprovementDb"/> quieter than the
    /// mark's own position, so a player seeking to it starts playback in close to true silence
    /// rather than risking an audible "plop" from starting abruptly mid-waveform - without ever
    /// risking eating into the announcement itself by moving later.
    /// </para>
    /// </summary>
    /// <param name="mark">The mark <see cref="RefineDefaultMark"/> already computed.</param>
    /// <param name="file">Path of the audio file.</param>
    /// <param name="inputDecoder">Explicit input decoder to force, or null.</param>
    /// <param name="profile">Language profile supplying the phrase to look for.</param>
    /// <param name="speechSegments">Raw VAD speech segments for the whole file, chronological;
    /// empty when the VAD pre-pass did not run, in which case there is nothing to check beyond
    /// <paramref name="mark"/> itself and it is returned unchanged whenever its own check fails.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The confirmed/corrected mark (already correct, corrected in either direction, or
    /// left as given when no candidate could be confirmed), quiet-snapped as a final step, paired
    /// with whether the phrase was ever actually heard - see <see cref="PreciseMarkResult"/>.</returns>
    internal async Task<PreciseMarkResult> RefinePreciseMarkAsync(
        double mark, string file, string? inputDecoder, LanguageProfile profile,
        List<SpeechSegment> speechSegments, CancellationToken ct)
    {
        double result;
        var heard = true;
        if (await PreciseMarkPhraseFoundAsync(mark, file, inputDecoder, profile, ct))
        {
            _log?.Invoke($"mark confirmed at {FormatTimestamp(mark)} - unchanged");
            result = mark;
        }
        else
        {
            var span = _options.MaxJingleSeconds + PhraseMarginSeconds;
            var forwardCandidates = speechSegments
                .Where(s => s.StartSeconds > mark && s.StartSeconds <= mark + span)
                .Select(s => s.StartSeconds)
                .OrderBy(s => s);
            var backwardCandidates = speechSegments
                .Where(s => s.StartSeconds < mark && s.StartSeconds >= mark - span)
                .Select(s => s.StartSeconds)
                .OrderByDescending(s => s);
            var confirmed = await WalkPreciseMarkCandidatesInterleavedAsync(
                forwardCandidates, backwardCandidates, file, inputDecoder, profile, ct);

            if (confirmed is null)
            {
                var forwardSteps = FixedStepCandidates(mark, span, forward: true);
                var backwardSteps = FixedStepCandidates(mark, span, forward: false);
                confirmed = await WalkPreciseMarkCandidatesInterleavedAsync(
                    forwardSteps, backwardSteps, file, inputDecoder, profile, ct);
            }

            if (confirmed is { } onset)
            {
                _log?.Invoke(
                    $"mark corrected from {FormatTimestamp(mark)} to {FormatTimestamp(onset)}");
                result = Math.Max(0, onset - DefaultMarkLeadSeconds);
            }
            else
            {
                _log?.Invoke(
                    $"could not confirm the phrase near {FormatTimestamp(mark)} - mark left unchanged");
                result = mark;
                heard = false;
            }
        }

        var quietest = await SnapToQuietestPointAsync(result, file, inputDecoder, ct);
        if (quietest != result)
            _log?.Invoke($"nudged {FormatTimestamp(result)} to quieter {FormatTimestamp(quietest)}");
        return new PreciseMarkResult(quietest, heard);
    }

    /// <summary>
    /// Last-resort verification/correction for --mark-before-jingle's own backward-walked mark
    /// (<see cref="JingleGeometry.ComputeMarkBeforeJingle"/>), for the one case where the walk
    /// started from a mark of unknown accuracy: precise marking ran but never actually heard the
    /// phrase (<see cref="PreciseMarkResult.PhraseHeard"/> false, the "could not confirm the
    /// phrase near ..." branch of <see cref="RefinePreciseMarkAsync"/>). Mirrors that method's
    /// "ask Whisper directly instead of trusting the heuristic" philosophy, applied to a different
    /// question: not "is the phrase heard right here" (the walked mark is deliberately placed
    /// <em>before</em> the announcement, so asking that would always fail) but "is the
    /// announcement <em>not</em> immediately audible any more" - the condition that confirms the
    /// walk reached the far side of the jingle rather than stopping short of it.
    /// <para>
    /// Deliberately <em>not</em> run when the phrase was heard, which is the overwhelmingly common
    /// case. There, <paramref name="originalMark"/> is known to sit <see
    /// cref="DefaultMarkLeadSeconds"/> before a real announcement onset, and the walk only ever
    /// retreats from it, so the walked mark cannot physically sit on the announcement's audio -
    /// the very failure this check was originally written (2026-07-26) to catch. Against a
    /// confirmed mark the check degenerates into an unreliable, one-transcription-per-chapter
    /// restatement of the arithmetic <c>originalMark - walked</c>: it fires whenever the walk
    /// stopped within a probe window of the onset, which covers a genuinely short jingle and the
    /// deliberate "no jingle here, mark unchanged" outcome just as readily as a walk that failed -
    /// and its backward search then drags a perfectly good mark into the previous chapter's
    /// narration. See the guard below for the same reasoning in its surviving case.
    /// </para>
    /// <para>
    /// <see cref="PreciseMarkPhraseFoundAsync"/> answers the question for <paramref name="walked"/>
    /// directly: if the phrase is <em>not</em> the first thing heard there, the walk reached clear
    /// of it and is trusted outright. If it <em>is</em>, the walk stopped too late, and
    /// <paramref name="walked"/> is corrected by searching backward from it - VAD speech-segment
    /// starts within the same jingle-plus-margin span precise marking's own round 1 already
    /// searches (see <see cref="RefinePreciseMarkAsync"/>), falling back to the same fixed-step
    /// blind scan as its round 2 - for the first (nearest) candidate where the phrase is no longer
    /// the first thing heard.
    /// </para>
    /// <para>
    /// Search is backward-only and never re-tries forward: the observed failure always left the
    /// walk too late, never too early, and --mark-before-jingle's whole purpose is landing before
    /// the jingle, not after it, so there is no symmetric "too early" case to correct for here.
    /// When no backward candidate ever clears the check - the announcement's own audio, or
    /// whatever the transcript keeps mistaking for it, extends across the entire searched span, an
    /// extreme case never observed on real audio - <paramref name="walked"/> is returned unchanged
    /// rather than guessing further back with nothing to support it.
    /// </para>
    /// </summary>
    /// <param name="walked">The mark <see cref="JingleGeometry.ComputeMarkBeforeJingle"/> already
    /// computed.</param>
    /// <param name="originalMark">The pre-walk mark the walk retreated from, for the
    /// <see cref="MarkBeforeJingleVerifyMinGapSeconds"/> guard.</param>
    /// <param name="file">Path of the audio file.</param>
    /// <param name="inputDecoder">Explicit input decoder to force, or null.</param>
    /// <param name="profile">Language profile supplying the phrase to look for.</param>
    /// <param name="speechSegments">Raw VAD speech segments for the whole file, chronological;
    /// empty when the VAD pre-pass did not run, in which case there is nothing to search beyond
    /// <paramref name="walked"/> itself.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><paramref name="walked"/> itself once confirmed clear of the announcement (either
    /// immediately, or after a correction), or unchanged when the guard declined to check, or when
    /// no backward candidate ever cleared the check.</returns>
    internal async Task<double> VerifyMarkBeforeJingleAsync(
        double walked, double originalMark, string file, string? inputDecoder,
        LanguageProfile profile, List<SpeechSegment> speechSegments, CancellationToken ct)
    {
        // The probe window at `walked` reaches forward far enough to hear the announcement the walk
        // retreated from, so a "still audible" reading there would be structurally guaranteed
        // rather than evidence of anything. Nothing to learn: trust the walk.
        if (originalMark - walked < MarkBeforeJingleVerifyMinGapSeconds)
        {
            _log?.Invoke(
                $"--mark-before-jingle: skipped verification at {FormatTimestamp(walked)} - " +
                $"only {originalMark - walked:0.00}s behind the mark, inside the probe window");
            return walked;
        }

        if (!await PreciseMarkPhraseFoundAsync(walked, file, inputDecoder, profile, ct))
            return walked;

        var span = _options.MaxJingleSeconds + PhraseMarginSeconds;
        var vadCandidates = speechSegments
            .Where(s => s.StartSeconds < walked && s.StartSeconds >= walked - span)
            .Select(s => s.StartSeconds)
            .OrderByDescending(s => s);
        var candidates = vadCandidates.Concat(FixedStepCandidates(walked, span, forward: false));

        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (!await PreciseMarkPhraseFoundAsync(candidate, file, inputDecoder, profile, ct))
            {
                _log?.Invoke(
                    $"--mark-before-jingle: verification moved {FormatTimestamp(walked)} " +
                    $"back to {FormatTimestamp(candidate)} (the announcement was still audible)");
                return candidate;
            }
        }

        _log?.Invoke(
            $"--mark-before-jingle: verification found the announcement audible " +
            $"all the way back to {FormatTimestamp(walked - span)} - mark left unchanged");
        return walked;
    }

    /// <summary>
    /// Final quiet-point cleanup step shared by precise marking's own placement (see
    /// <see cref="RefinePreciseMarkAsync"/>) and --mark-before-jingle's (<see
    /// cref="ChapterDetector"/>, after <see cref="JingleGeometry.ComputeMarkBeforeJingle"/>):
    /// nudges <paramref name="mark"/> backward to a quieter point within <see
    /// cref="PreciseMarkQuietSnapRadiusSeconds"/> before it, so a player seeking there starts
    /// playback as close to true silence as the audio actually offers nearby. Even a mark sitting
    /// exactly on the chapter phrase's own onset (or the previous chapter's last spoken sound, for
    /// --mark-before-jingle) can coincide with a comparatively loud sample, and abruptly starting
    /// playback there is audible as a "plop".
    /// <para>
    /// Never moves the mark later: only positions before <paramref name="mark"/> are ever
    /// considered, since nudging earlier only ever trims a beat of trailing silence from the
    /// previous chapter, while nudging later risks eating into the next phrase's own lead-in - not
    /// a trade worth making for a marginally quieter spot. Decodes a small window of raw PCM
    /// covering that lookback and slides a short (<see cref="PreciseMarkQuietWindowSeconds"/>) RMS
    /// window across it, tracking the quietest position found (by sum of squares) together with
    /// <paramref name="mark"/>'s own, fixed current-position window. Ties among backward candidates
    /// are broken by proximity to <paramref name="mark"/>, so drifting further back than necessary
    /// for the same energy never happens.
    /// </para>
    /// <para>
    /// The quietest backward candidate only ever wins if it is at least
    /// <see cref="PreciseMarkQuietSnapMinImprovementDb"/> quieter than <paramref name="mark"/>'s own
    /// position - a small, possibly noise-floor-only difference is not reason enough to move a mark
    /// that was already confirmed (or left as-is) by the search above. When nothing nearby clears
    /// that bar, <paramref name="mark"/> is returned unchanged.
    /// </para>
    /// </summary>
    /// <param name="mark">The mark to snap.</param>
    /// <param name="file">Path of the audio file.</param>
    /// <param name="inputDecoder">Explicit input decoder to force, or null.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A quieter position before <paramref name="mark"/>, or <paramref name="mark"/>
    /// unchanged when nothing nearby is quiet enough to be worth moving to, or when too little
    /// audio decoded to analyze.</returns>
    internal async Task<double> SnapToQuietestPointAsync(
        double mark, string file, string? inputDecoder, CancellationToken ct)
    {
        var decodeStart = Math.Max(0, mark - PreciseMarkQuietSnapRadiusSeconds);
        var samples = await _audio.DecodePcmAsync(
            file, decodeStart, mark - decodeStart + PreciseMarkQuietWindowSeconds, inputDecoder, ct);

        var windowSamples = (int)(PreciseMarkQuietWindowSeconds * FfmpegClient.SampleRate);
        if (windowSamples < 1 || samples.Length < windowSamples)
            return mark;

        var halfWindow = windowSamples / 2;
        var markSample = (int)Math.Round((mark - decodeStart) * FfmpegClient.SampleRate);

        double sumSquares = 0;
        for (var i = 0; i < windowSamples; i++)
            sumSquares += (double)samples[i] * samples[i];

        // The mark's own current-position window (centered on markSample) is the fixed baseline
        // every backward candidate is measured against, not another candidate to search over.
        var currentStart = Math.Clamp(markSample - halfWindow, 0, samples.Length - windowSamples);
        var currentSumSquares = double.NaN;

        int? bestStart = null;
        var bestSumSquares = double.PositiveInfinity;
        var bestDistance = int.MaxValue;

        void Consider(int start, double windowSumSquares)
        {
            if (start == currentStart)
                currentSumSquares = windowSumSquares;

            var center = start + halfWindow;
            // Only positions at or before the mark are ever candidates - see the "never moves the
            // mark later" remark above.
            if (center > markSample)
                return;
            var distance = markSample - center;
            if (windowSumSquares < bestSumSquares ||
                (windowSumSquares == bestSumSquares && distance < bestDistance))
            {
                bestSumSquares = windowSumSquares;
                bestStart = start;
                bestDistance = distance;
            }
        }

        Consider(0, sumSquares);
        for (var start = 1; start <= samples.Length - windowSamples; start++)
        {
            sumSquares += (double)samples[start + windowSamples - 1] * samples[start + windowSamples - 1]
                          - (double)samples[start - 1] * samples[start - 1];
            Consider(start, sumSquares);
        }

        if (bestStart is not { } winningStart || bestDistance == 0 ||
            !IsQuieterByAtLeast(bestSumSquares, currentSumSquares, PreciseMarkQuietSnapMinImprovementDb))
            return mark;

        // Rounded to microsecond precision - finer than one sample (62.5us at 16 kHz) already is,
        // so this only cleans up floating-point noise from the addition chain above rather than
        // losing anything the sample grid could actually distinguish.
        return Math.Round(
            Math.Max(0, decodeStart + (winningStart + halfWindow) / (double)FfmpegClient.SampleRate), 6);
    }

    /// <summary>
    /// Whether <paramref name="candidateSumSquares"/> is at least <paramref name="thresholdDb"/>
    /// quieter than <paramref name="currentSumSquares"/> on a power-ratio (dB) scale - the gate
    /// <see cref="SnapToQuietestPointAsync"/> applies before nudging to a backward candidate. A
    /// candidate at true digital silence (zero) always qualifies against any nonzero current level
    /// (an infinite improvement); the current position already being true silence never qualifies,
    /// since there is no quieter place left to go.
    /// </summary>
    private static bool IsQuieterByAtLeast(double candidateSumSquares, double currentSumSquares, double thresholdDb)
    {
        if (currentSumSquares <= 0)
            return false;
        if (candidateSumSquares <= 0)
            return true;

        var ratioDb = 10.0 * Math.Log10(currentSumSquares / candidateSumSquares);
        return ratioDb >= thresholdDb;
    }

    /// <summary>
    /// Generates precise marking round 2's blind, fixed-step candidate positions (see
    /// <see cref="RefinePreciseMarkAsync"/>): <paramref name="mark"/> plus/minus
    /// <see cref="PreciseMarkFixedStepSeconds"/>, 2x that, 3x that, and so on out to
    /// <paramref name="span"/>, unlike round 1's candidates, which come from actual VAD
    /// speech-segment starts. Stops early, short of <paramref name="span"/>, if a backward step
    /// would otherwise go negative - there is nothing before the start of the file to check.
    /// </summary>
    /// <param name="mark">Position the steps count out from; never itself included.</param>
    /// <param name="span">How far out from <paramref name="mark"/> to keep stepping.</param>
    /// <param name="forward">True to step later than <paramref name="mark"/>, false to step earlier.</param>
    private static IEnumerable<double> FixedStepCandidates(double mark, double span, bool forward)
    {
        var steps = (int)Math.Round(span / PreciseMarkFixedStepSeconds);
        for (var i = 1; i <= steps; i++)
        {
            var candidate = forward
                ? mark + i * PreciseMarkFixedStepSeconds
                : mark - i * PreciseMarkFixedStepSeconds;
            if (candidate < 0)
                yield break;
            yield return Math.Round(candidate, 6);
        }
    }

    /// <summary>
    /// Interleaves <paramref name="forwardCandidates"/> and <paramref name="backwardCandidates"/>
    /// one at a time - backward, then forward, then backward again, and so on - rather than
    /// exhausting either direction before ever trying the other, so whichever direction the true
    /// announcement actually lies in is typically found in about half the checks a fully
    /// sequential search would need. The two directions are accepted asymmetrically, as described
    /// on <see cref="RefinePreciseMarkAsync"/>:
    /// <list type="bullet">
    /// <item>A backward success is trusted immediately and returned on the spot, with no further
    /// checking.</item>
    /// <item>A forward success instead switches the search to a forward-only continuation -
    /// backward is abandoned outright from that point on - and forward candidates keep being
    /// checked, in order, until one fails (which locks in the <em>previous</em> one as the
    /// answer) or they simply run out (which accepts whichever was confirmed last; the caller's
    /// search span already bounds how far this can wander).</item>
    /// </list>
    /// A run of consecutive forward successes during that continuation is logged as ambiguous,
    /// each time moving the tentative answer to the most recently confirmed one.
    /// </summary>
    /// <param name="forwardCandidates">Positions later than the mark, in the order to try them.</param>
    /// <param name="backwardCandidates">Positions earlier than the mark, in the order to try them.</param>
    /// <param name="file">Path of the audio file.</param>
    /// <param name="inputDecoder">Explicit input decoder to force, or null.</param>
    /// <param name="profile">Language profile supplying the phrase to look for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The confirmed candidate position, or null if neither direction ever confirmed one.</returns>
    private async Task<double?> WalkPreciseMarkCandidatesInterleavedAsync(
        IEnumerable<double> forwardCandidates, IEnumerable<double> backwardCandidates,
        string file, string? inputDecoder, LanguageProfile profile, CancellationToken ct)
    {
        using var forwardEnumerator = forwardCandidates.GetEnumerator();
        using var backwardEnumerator = backwardCandidates.GetEnumerator();
        var forwardHasMore = forwardEnumerator.MoveNext();
        var backwardHasMore = backwardEnumerator.MoveNext();
        double? forwardConfirmed = null;

        while (forwardConfirmed is null && (backwardHasMore || forwardHasMore))
        {
            if (backwardHasMore)
            {
                var candidate = backwardEnumerator.Current;
                ct.ThrowIfCancellationRequested();
                if (await PreciseMarkPhraseFoundAsync(candidate, file, inputDecoder, profile, ct))
                    return candidate;
                backwardHasMore = backwardEnumerator.MoveNext();
            }

            if (forwardHasMore)
            {
                var candidate = forwardEnumerator.Current;
                ct.ThrowIfCancellationRequested();
                if (await PreciseMarkPhraseFoundAsync(candidate, file, inputDecoder, profile, ct))
                    forwardConfirmed = candidate;
                forwardHasMore = forwardEnumerator.MoveNext();
            }
        }

        if (forwardConfirmed is null)
            return null;

        while (forwardHasMore)
        {
            var candidate = forwardEnumerator.Current;
            ct.ThrowIfCancellationRequested();
            if (await PreciseMarkPhraseFoundAsync(candidate, file, inputDecoder, profile, ct))
            {
                _log?.Invoke(
                    $"consecutive candidates confirmed ({FormatTimestamp(forwardConfirmed.Value)} " +
                    $"then {FormatTimestamp(candidate)}) - ambiguous, keeping the latter");
                forwardConfirmed = candidate;
                forwardHasMore = forwardEnumerator.MoveNext();
                continue;
            }
            return forwardConfirmed;
        }
        return forwardConfirmed;
    }
}
