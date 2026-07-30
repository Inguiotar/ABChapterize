// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Audio;
using ABChapterize.Cli;
using ABChapterize.Transcription;
using ABChapterize.Vad;
using System.Text.RegularExpressions;
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
/// <see cref="CliOptions.MarkLeadSeconds"/> before a real announcement onset. False
/// only in the "could not confirm the phrase" case, where the mark is still whatever the default
/// heuristics produced and its distance from the announcement is unknown. Downstream steps that
/// reason about where the announcement is relative to the mark - <see
/// cref="PreciseMarkRefiner.VerifyMarkBeforeJingleAsync"/> above all - are only sound when this
/// holds.</param>
internal readonly record struct PreciseMarkResult(double Mark, bool PhraseHeard);

/// <summary>Implements the precise marking correction (<see cref="CliOptions.PreciseMark"/>):
/// verifies a default-mode mark by directly asking Whisper whether the chapter phrase starts
/// there, and if not, searches for where it really starts - see
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
    /// Checks whether <paramref name="phraseRegex"/>'s announcement is really the first thing heard
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
    /// <param name="phraseRegex">The announcement to look for: the chapter phrase for a numbered
    /// chapter, or the matching prologue/epilogue phrase for a named mark.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True when the first non-blank transcribed segment contains the chapter phrase.</returns>
    private async Task<bool> PreciseMarkPhraseFoundAsync(
        double start, string file, string? inputDecoder, Regex phraseRegex, CancellationToken ct)
    {
        // The bisection reaches a probe position by repeated halving and the sweep by repeated
        // addition, so an unrounded position accumulates binary-float dust (0.24999999999999997
        // for what is conceptually 0.25). Rounding here keeps every decode request, and every
        // timestamp derived from one, on clean values.
        start = Math.Round(start, 6);
        var decodeStart = Math.Round(Math.Max(0, start - PreciseMarkLeadInSeconds), 6);
        var length = Math.Round(PreciseMarkCheckWindowSeconds + (start - decodeStart), 6);
        var samples = await _audio.DecodePcmAsync(file, decodeStart, length, inputDecoder, ct);
        var transcript = await _transcribeCounting(samples, ct);
        // The lead-in can surface a trailing fragment of whatever preceded `start` as the first
        // segment (e.g. the jingle's own tail, or the previous chapter's last words) - the first
        // *non-blank* segment is what actually starts at or after the checked position.
        var first = transcript.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.Text));
        return first.Text != null && phraseRegex.IsMatch(first.Text);
    }

    /// <summary>
    /// Verifies (and if necessary, corrects) a default-mode mark by directly asking Whisper
    /// "does the chapter phrase start right here?" instead of trusting the VAD/duration
    /// heuristics <see cref="JingleGeometry.RefineDefaultMark"/> already applied - the precise marking option
    /// (<see cref="CliOptions.PreciseMark"/>). Those heuristics rest on a floor deliberately
    /// calibrated to err toward not skipping real speech (see
    /// <see cref="TransientSpeechFloorSeconds"/>'s remarks on cross-language uncertainty), which
    /// means a spurious VAD "speech" blip inside a jingle that happens to clear that floor - a
    /// vocal-like musical transient, or an occasional Whisper hallucination - can still fool them
    /// into stopping short of the true announcement; this asks the audio directly instead, at the
    /// cost of one or more extra transcriptions per chapter.
    /// <para>
    /// Every path here ends in <see cref="FindOnsetEdgeAsync"/>, which is what actually pins the
    /// announcement's onset; the searches before it only find <em>a</em> position where the phrase
    /// is audible, so that the edge walk has somewhere to start. That split is what keeps the
    /// accuracy guarantee uniform - the onset is located to within
    /// <see cref="PreciseMarkFixedStepSeconds"/> whichever route got there.
    /// </para>
    /// <para>
    /// <paramref name="mark"/> itself is deliberately <em>not</em> checked first as a shortcut, and
    /// hearing the phrase there would not license one: a jingle is exactly what Whisper does not
    /// transcribe, so a mark sitting seconds inside one answers "yes, the phrase starts here" just
    /// as readily as a mark sitting on the announcement, and accepting that answer left marks
    /// silently early by however long the jingle ran. The searches below have no such shortcut to
    /// offer - they hunt for a foothold and hand it to the edge walk, which is the only step that
    /// can tell the two apart.
    /// </para>
    /// <para>
    /// <see cref="LocatePhraseByShrinkingWindowAsync"/> does the whole job: it searches the stretch
    /// the phrase was matched in, which is what lets it recover a mark left far from its
    /// announcement, and it bisects a monotone predicate rather than sampling guessed positions.
    /// <see cref="JingleGeometry.ResolveDefaultPhraseOnset"/> is deliberately untouched by any of
    /// this: default-mode marking is all --quick-marks leaves in place, and its heuristic accuracy
    /// alone is what makes quick marks usable for jumping to a chapter.
    /// </para>
    /// <para>
    /// There used to be a cheap first round ahead of it, sampling
    /// <see cref="PreciseMarkPhraseFoundAsync"/> at VAD speech-segment starts within a
    /// jingle-plus-phrase span of the mark, from the days when the search below was a fixed-step
    /// sweep costing hundreds of transcriptions. Removed 2026-07-30 after the BARDIOC.m4b log
    /// settled it: over 26 refinements it confirmed 3 times, spent 606 checks and 8.1 minutes doing
    /// it, and the search below then closed all 23 of the remaining marks in 3.8 minutes. It could
    /// not be repaired either, for a structural reason worth recording. Its window was fixed-width,
    /// so shifting it moved both ends and "is the phrase heard here" was not a step function - it
    /// could only ever be sampled at guessed positions, never searched. The one guess that mattered,
    /// the announcement's own VAD speech start, lands <em>past</em> the onset by VAD's onset lag
    /// (0.60 s measured on Perry Rhodan "Die Dritte Macht" chapter 4, true onset 4142.572 s vs. a
    /// VAD start of 4143.168 s, 2026-07-30; ~0.4 s recorded earlier by <c>tools\vadprobe</c>'s
    /// <c>precise</c> prototype), i.e. on the far side of the plateau edge where confirmation
    /// depends on Whisper recovering a clipped first syllable. Nothing inside the plateau was ever a
    /// candidate, because only segment starts were collected and never the pauses between them.
    /// Widening its window would not have helped: the wider it got, the less "the phrase is the
    /// first thing heard" implied "the phrase starts near here". Anchoring the window's <em>end</em>
    /// instead, which is what makes the search below monotone and bisectable, is the only fix - and
    /// that is the search below.
    /// </para>
    /// <para>
    /// When nothing can be confirmed, <paramref name="mark"/> itself carries forward into the final
    /// step below rather than guessing - validated against a real audiobook's full set of known good
    /// and previously-broken marks (see <c>tools\vadprobe</c>'s <c>precise</c> prototype) before
    /// this was ported here. The default-mode heuristic it falls back to measured within 0.05-0.35 s
    /// of the true onset for 25 of BARDIOC.m4b's 26 marks, so the fallback is a fraction of a second
    /// of accuracy rather than a broken mark.
    /// </para>
    /// <para>
    /// Final cleanup step, applied to whichever mark the above produced (even one left exactly as
    /// given): <see cref="SnapToQuietestPointAsync"/> nudges it backward - never later, which could
    /// eat into the announcement - to a quieter point within
    /// <see cref="PreciseMarkQuietSnapRadiusSeconds"/> before it, provided one is at least
    /// <see cref="PreciseMarkQuietSnapMinImprovementDb"/> quieter, so a player seeking there starts
    /// in near-silence rather than with an audible "plop" mid-waveform.
    /// </para>
    /// </summary>
    /// <param name="mark">The mark <see cref="JingleGeometry.RefineDefaultMark"/> already computed.</param>
    /// <param name="file">Path of the audio file.</param>
    /// <param name="inputDecoder">Explicit input decoder to force, or null.</param>
    /// <param name="phraseRegex">The announcement to look for: the chapter phrase for a numbered
    /// chapter, or the matching prologue/epilogue phrase for a named mark.</param>
    /// <param name="phraseAbs">Absolute start of the transcript segment(s) the phrase was matched
    /// in - the search bracket, together with the two that follow.</param>
    /// <param name="phraseEndAbs">Absolute end of those segment(s).</param>
    /// <param name="transcriptEnd">Absolute end of the audio the phrase was detected in, capping
    /// that bracket - see <see cref="MarkContext.TranscriptEnd"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The confirmed/corrected mark (already correct, corrected in either direction, or
    /// left as given when nothing could be confirmed), quiet-snapped as a final step, paired
    /// with whether the phrase was ever actually heard - see <see cref="PreciseMarkResult"/>.</returns>
    internal async Task<PreciseMarkResult> RefinePreciseMarkAsync(
        double mark, string file, string? inputDecoder, Regex phraseRegex,
        double phraseAbs, double phraseEndAbs, double transcriptEnd, CancellationToken ct)
    {
        double result;
        var heard = true;
        var confirmed = await LocatePhraseByShrinkingWindowAsync(
            mark, phraseAbs, phraseEndAbs, transcriptEnd, file, inputDecoder, phraseRegex, ct);

        if (confirmed is { } hit)
        {
            var onset = await FindOnsetEdgeAsync(hit, file, inputDecoder, phraseRegex, ct);
            result = Math.Max(0, onset - _options.MarkLeadSeconds);
            _log?.Invoke(result == mark
                ? $"mark confirmed at {FormatTimestamp(mark)} - unchanged"
                : $"mark corrected from {FormatTimestamp(mark)} to {FormatTimestamp(result)} " +
                  $"(onset {FormatTimestamp(onset)})");
        }
        else
        {
            _log?.Invoke(
                $"could not confirm the phrase near {FormatTimestamp(mark)} - mark left unchanged");
            result = mark;
            heard = false;
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
    /// Deliberately <em>not</em> run when the phrase was heard, the overwhelmingly common case:
    /// <paramref name="originalMark"/> then sits <see cref="CliOptions.MarkLeadSeconds"/> before a real
    /// announcement onset and the walk only retreats from it, so the walked mark cannot physically
    /// sit on the announcement's audio - the very failure this check was written (2026-07-26) to
    /// catch. Against a confirmed mark it degenerates into an unreliable,
    /// one-transcription-per-chapter restatement of <c>originalMark - walked</c>, firing whenever
    /// the walk stopped within a probe window of the onset - which a genuinely short jingle and the
    /// deliberate "no jingle here, mark unchanged" outcome do as readily as a failed walk - and its
    /// backward search then drags a good mark into the previous chapter's narration. See the guard
    /// below for the same reasoning in its surviving case.
    /// </para>
    /// <para>
    /// <see cref="PreciseMarkPhraseFoundAsync"/> answers the question for <paramref name="walked"/>
    /// directly: if the phrase is <em>not</em> the first thing heard there, the walk reached clear
    /// of it and is trusted outright. If it <em>is</em>, the walk stopped too late, and
    /// <paramref name="walked"/> is corrected by searching backward from it - VAD speech-segment
    /// starts within a jingle-plus-margin span of it, falling back to a blind fixed-step scan
    /// of its own (<see cref="FixedStepCandidates"/>) - for the first (nearest) candidate where the
    /// phrase is no longer the first thing heard.
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
    /// <param name="phraseRegex">The announcement to look for: the chapter phrase for a numbered
    /// chapter, or the matching prologue/epilogue phrase for a named mark.</param>
    /// <param name="speechSegments">Raw VAD speech segments for the whole file, chronological;
    /// empty when the VAD pre-pass did not run, in which case there is nothing to search beyond
    /// <paramref name="walked"/> itself.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><paramref name="walked"/> itself once confirmed clear of the announcement (either
    /// immediately, or after a correction), or unchanged when the guard declined to check, or when
    /// no backward candidate ever cleared the check.</returns>
    internal async Task<double> VerifyMarkBeforeJingleAsync(
        double walked, double originalMark, string file, string? inputDecoder,
        Regex phraseRegex, List<SpeechSegment> speechSegments, CancellationToken ct)
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

        if (!await PreciseMarkPhraseFoundAsync(walked, file, inputDecoder, phraseRegex, ct))
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
            if (!await PreciseMarkPhraseFoundAsync(candidate, file, inputDecoder, phraseRegex, ct))
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
    /// Never moves the mark later: nudging earlier only trims a beat of trailing silence off the
    /// previous chapter, while nudging later risks eating into the next phrase's lead-in - no trade
    /// worth making for a marginally quieter spot. Decodes a small PCM window covering the lookback
    /// and slides a short (<see cref="PreciseMarkQuietWindowSeconds"/>) RMS window across it,
    /// tracking the quietest position by sum of squares alongside <paramref name="mark"/>'s own
    /// fixed baseline window. Ties are broken by proximity to <paramref name="mark"/>, so it never
    /// drifts further back than necessary for the same energy.
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
    /// Pins the announcement's true onset, given any position <paramref name="confirmed"/> the
    /// phrase was already heard at. This is what makes a confirmation worth anything: hearing the
    /// phrase at a position proves only that nothing transcribable precedes it within
    /// <see cref="PreciseMarkCheckWindowSeconds"/>, and a jingle is precisely the thing Whisper
    /// does not transcribe - so every position from somewhere inside the jingle right up to the
    /// onset itself answers "yes" identically. The answers form one plateau ending at the onset:
    /// step past it and the window opens mid-announcement, whose leading fragment no longer matches
    /// the phrase. The plateau's right edge <em>is</em> the onset, and finding it is the whole job.
    /// <para>
    /// Galloping (0.1 s, 0.2 s, 0.4 s, ...) to bracket that edge, then bisecting the bracket, holds
    /// the guaranteed accuracy at one <see cref="PreciseMarkFixedStepSeconds"/> - the returned
    /// position confirms and the position one step later does not, so the true onset lies within
    /// that step after it, never before - while costing a logarithmic number of transcriptions
    /// instead of one per step. A mark that was already right pays two extra checks; a mark 3.5 s
    /// into a jingle pays about twelve, where walking the whole plateau at a fixed 0.1 s cost
    /// thirty-five (measured on Stalker.m4b, 0:00:49.42 to 0:00:52.92, 2026-07-28, ~95 s for that
    /// single mark). Bisection is exact here rather than approximate, which it would not be for an
    /// arbitrary predicate: the plateau has exactly one edge, so no interval it discards could have
    /// held another.
    /// </para>
    /// <para>
    /// The gallop is capped at the same jingle-plus-phrase span the searches use, so a phrase that
    /// somehow keeps confirming outwards can never walk into the next chapter; that case returns
    /// the furthest position actually confirmed, the one place the step-accuracy guarantee does not
    /// hold. It has not been observed on real audio - it needs the announcement to stay the first
    /// audible thing across an entire jingle-length span.
    /// </para>
    /// </summary>
    /// <param name="confirmed">A position the phrase was already heard at; the search starts here
    /// and only ever moves later, since the plateau extends forward to the onset.</param>
    /// <param name="file">Path of the audio file.</param>
    /// <param name="inputDecoder">Explicit input decoder to force, or null.</param>
    /// <param name="phraseRegex">The announcement to look for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The announcement's onset: at or before the true one, and never more than
    /// <see cref="PreciseMarkFixedStepSeconds"/> before it.</returns>
    private async Task<double> FindOnsetEdgeAsync(
        double confirmed, string file, string? inputDecoder, Regex phraseRegex, CancellationToken ct)
    {
        var cap = _options.MaxJingleSeconds + PhraseMarginSeconds;
        var lo = confirmed;
        double? hi = null;

        for (var delta = PreciseMarkFixedStepSeconds; delta <= cap; delta *= 2)
        {
            ct.ThrowIfCancellationRequested();
            var probe = Math.Round(confirmed + delta, 6);
            if (!await PreciseMarkPhraseFoundAsync(probe, file, inputDecoder, phraseRegex, ct))
            {
                hi = probe;
                break;
            }
            lo = probe;
        }

        if (hi is not { } failed)
            return OnsetOf(lo);

        while (failed - lo > PreciseMarkFixedStepSeconds)
        {
            ct.ThrowIfCancellationRequested();
            var mid = Math.Round((lo + failed) / 2, 6);
            if (await PreciseMarkPhraseFoundAsync(mid, file, inputDecoder, phraseRegex, ct))
                lo = mid;
            else
                failed = mid;
        }
        return OnsetOf(lo);
    }

    /// <summary>
    /// Converts the last probe position that still heard the phrase into the announcement's onset.
    /// A probe decodes from <see cref="PreciseMarkLeadInSeconds"/> <em>before</em> the position it
    /// asks about, so it keeps answering yes until that lead-in has cleared the onset too - the
    /// plateau's right edge therefore sits one lead-in <em>past</em> the onset, not on it. Without
    /// this the reported onset lands consistently late by that much, which is the wrong side to err
    /// on: a mark derived from it would already have eaten into the announcement.
    /// </summary>
    /// <param name="lastConfirmed">The latest probe position at which the phrase was still the
    /// first thing heard.</param>
    private static double OnsetOf(double lastConfirmed)
        => Math.Round(Math.Max(0, lastConfirmed - PreciseMarkLeadInSeconds), 6);

    /// <summary>
    /// Precise marking's search (see <see cref="RefinePreciseMarkAsync"/>): finds a position where
    /// the announcement is the first thing heard, without any help from VAD, by asking a question
    /// that has a single answer rather than hunting for one that has many.
    /// <para>
    /// The obvious approach - stepping <see cref="PreciseMarkFixedStepSeconds"/> at a time across
    /// the span, checking each position with <see cref="PreciseMarkPhraseFoundAsync"/> - is what
    /// this replaces, and it could not be bisected: a fixed-width window slid across the audio
    /// answers "yes" only on the stretch that both reaches the announcement and has nothing else
    /// transcribable in front of it, an <em>island</em> somewhere in the span, and no comparison of
    /// two "no"s tells you which side of it you are on. Sweeping the island out one step at a time
    /// was therefore the only option, at up to 2 x span / step transcriptions - 726 of them, some
    /// twenty minutes, on the mark that prompted this (Stalker.m4b's "Zeittafel", 2026-07-29).
    /// </para>
    /// <para>
    /// Anchoring the window's <em>end</em> instead removes the island (the insight is the user's):
    /// ask whether the phrase appears anywhere between a moving start and a fixed end past the
    /// announcement, and the answer is yes for every start before the onset and no for every start
    /// after it. One step, one edge, bisectable - see
    /// <see cref="FindPhraseSurvivalEdgeAsync"/>. Verified before it was built, on the same mark:
    /// starts of 22.6, 30, 40, 46, 50, 51.5, 52 and 52.5 s all still found "Zeittafel" against an
    /// end of 54 s, and 53 s did not.
    /// </para>
    /// <para>
    /// That edge is not itself the answer the caller wants - it sits at or fractionally past the
    /// onset (see <see cref="PreciseMarkFootholdBackoffsSeconds"/>), whereas everything downstream
    /// is built on positions that err early. So the edge is only used to aim: a handful of
    /// backoffs behind it are offered to the ordinary check, and the first one it confirms is
    /// returned as the foothold for <see cref="FindOnsetEdgeAsync"/>. Returning null when none of
    /// them confirms is the honest outcome and not a
    /// fallback worth adding: the edge came from the phrase appearing <em>somewhere</em> in a long
    /// window, which an unrelated mention in the narration can also produce.
    /// </para>
    /// </summary>
    /// <param name="mark">The mark being refined; the gallop starts here, clamped into the
    /// bracket.</param>
    /// <param name="phraseAbs">Absolute start of the matched segment(s).</param>
    /// <param name="phraseEndAbs">Absolute end of the matched segment(s).</param>
    /// <param name="transcriptEnd">Absolute end of the audio the phrase was detected in.</param>
    /// <param name="file">Path of the audio file.</param>
    /// <param name="inputDecoder">Explicit input decoder to force, or null.</param>
    /// <param name="phraseRegex">The announcement to look for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A position at which the phrase is the first thing heard, or null when the search
    /// found no edge or no backoff behind it could be confirmed.</returns>
    private async Task<double?> LocatePhraseByShrinkingWindowAsync(
        double mark, double phraseAbs, double phraseEndAbs, double transcriptEnd, string file,
        string? inputDecoder, Regex phraseRegex, CancellationToken ct)
    {
        // The bracket the announcement has to lie in: the segment(s) it was matched in, a phrase
        // margin either side for a timestamp that reported them slightly narrow, and never past the
        // end of the audio it was heard in.
        var floor = Math.Max(0, phraseAbs - PhraseMarginSeconds);
        var ceiling = Math.Min(phraseEndAbs + PhraseMarginSeconds, transcriptEnd);
        if (ceiling - floor <= PreciseMarkFixedStepSeconds)
            return null;

        // Announced before it starts rather than after: this step can keep a single mark busy for a
        // while, and an unexplained silent wait is indistinguishable from a hang - which is exactly
        // how its predecessor was first reported, on Stalker.m4b's "Zeittafel", 2026-07-29.
        _log?.Invoke($"refining mark at {FormatTimestamp(mark)} - narrowing in on the phrase " +
                     $"between {FormatTimestamp(floor)} and {FormatTimestamp(ceiling)}");

        if (await FindPhraseSurvivalEdgeAsync(
                Math.Clamp(mark, floor, ceiling), floor, ceiling, file, inputDecoder, phraseRegex, ct)
            is not { } edge)
            return null;

        _log?.Invoke($"phrase survives up to {FormatTimestamp(edge)} - " +
                     $"confirming the announcement just before it");

        foreach (var backoff in PreciseMarkFootholdBackoffsSeconds)
        {
            var candidate = Math.Round(Math.Max(0, edge - backoff), 6);
            ct.ThrowIfCancellationRequested();
            if (await PreciseMarkPhraseFoundAsync(candidate, file, inputDecoder, phraseRegex, ct))
                return candidate;
            if (candidate == 0)
                break;
        }
        return null;
    }

    /// <summary>
    /// Finds the last position from which <paramref name="phraseRegex"/> still survives being cut
    /// off at the front - the step edge <see cref="LocatePhraseByShrinkingWindowAsync"/> aims by.
    /// Every probe transcribes from the position asked about to a <em>fixed</em> end past the
    /// announcement, so the predicate is monotone and a bracket can be halved; see that method for
    /// why a fixed-width window could not be.
    /// <para>
    /// Galloping outward from <paramref name="origin"/> before bisecting keeps the common case
    /// cheap - a mark already sitting near its announcement costs two or three probes - while still
    /// bracketing a mark tens of seconds away in about ten. Each stride doubles, so probes land 0.1,
    /// 0.3, 0.7, 1.5 s and onward from the start; doubling the stride rather than the offset means
    /// the failing probe leaves a bracket no larger than the ground already covered, which is what
    /// the bisection below then halves. The direction is decided by the answer at
    /// <paramref name="origin"/> itself: still surviving means the onset is later, not surviving
    /// means it is earlier.
    /// </para>
    /// <para>
    /// Why the caller's bracket is drawn round the matched <em>segment</em> rather than round the
    /// mark, and why that is not a detail: Whisper transcribes in 30 s chunks, so a window long
    /// enough to span more than one re-segments differently for a shift of a few hundred
    /// milliseconds, and the phrase can drop out of the transcript for reasons having nothing to do
    /// with where the window starts. The predicate is noise at that point, and bisecting noise
    /// converges on nonsense. Measured twice while this was built, both on Stalker.m4b's
    /// "Zeittafel" (true onset 52.7 s, 2026-07-29): anchored 55 s out, the phrase was reported
    /// surviving from 22.92 s but not from 23.32 s and the edge came back 29 s early; anchored at
    /// the detecting window's end but started from the mark 30 s before it, the very first stride
    /// straddled a chunk boundary and did it again. Bracketed to the matched segment - which
    /// contains the announcement however badly its start timestamp is smeared - no window exceeds
    /// that segment plus two phrase margins, and the same audio behaved perfectly monotonically.
    /// </para>
    /// <para>
    /// The anchor is additionally pulled in to just past every position that fails: a failure proves
    /// the phrase ends before that position plus a phrase's length. That matters for a Pass 3 gap
    /// chunk, whose matched segment can be far longer than Pass 2's whole window. It cannot
    /// invalidate an earlier answer - each new anchor still sits a full phrase margin past the
    /// onset, so anything that survived under the old one survives under the new one too.
    /// </para>
    /// </summary>
    /// <param name="origin">Position the gallop starts from; inside the bracket.</param>
    /// <param name="floor">Earliest position the onset can be at.</param>
    /// <param name="ceiling">Latest position it can be at, and the window's end anchor.</param>
    /// <param name="file">Path of the audio file.</param>
    /// <param name="inputDecoder">Explicit input decoder to force, or null.</param>
    /// <param name="phraseRegex">The announcement to look for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The last surviving position, accurate to <see cref="PreciseMarkFixedStepSeconds"/>;
    /// null when the phrase survives right across the range (so no edge exists to aim by) or is not
    /// found anywhere in it.</returns>
    private async Task<double?> FindPhraseSurvivalEdgeAsync(
        double origin, double floor, double ceiling, string file, string? inputDecoder,
        Regex phraseRegex, CancellationToken ct)
    {
        var end = ceiling;

        async Task<bool> SurvivesAsync(double from)
        {
            ct.ThrowIfCancellationRequested();
            var found = await PhraseSurvivesFromAsync(from, end, file, inputDecoder, phraseRegex, ct);
            if (!found)
                end = Math.Min(end, from + PhraseMarginSeconds);
            return found;
        }

        double lastTrue, firstFalse;
        var stride = PreciseMarkFixedStepSeconds;
        if (await SurvivesAsync(origin))
        {
            lastTrue = origin;
            while (true)
            {
                var probe = Math.Min(ceiling, Math.Round(lastTrue + stride, 6));
                if (!await SurvivesAsync(probe))
                {
                    firstFalse = probe;
                    break;
                }
                if (probe >= ceiling)
                    return null;
                lastTrue = probe;
                stride *= 2;
            }
        }
        else
        {
            firstFalse = origin;
            while (true)
            {
                var probe = Math.Max(floor, Math.Round(firstFalse - stride, 6));
                if (await SurvivesAsync(probe))
                {
                    lastTrue = probe;
                    break;
                }
                if (probe <= floor)
                    return null;
                firstFalse = probe;
                stride *= 2;
            }
        }

        while (firstFalse - lastTrue > PreciseMarkFixedStepSeconds)
        {
            var mid = Math.Round((lastTrue + firstFalse) / 2, 6);
            if (await SurvivesAsync(mid))
                lastTrue = mid;
            else
                firstFalse = mid;
        }
        return lastTrue;
    }

    /// <summary>
    /// Asks whether <paramref name="phraseRegex"/> is still found anywhere between
    /// <paramref name="from"/> and <paramref name="until"/> - the monotone question
    /// <see cref="FindPhraseSurvivalEdgeAsync"/> bisects on.
    /// <para>
    /// Two deliberate differences from <see cref="PreciseMarkPhraseFoundAsync"/>, which asks a
    /// different question of the same audio. There is no lead-in: cutting the audio exactly at
    /// <paramref name="from"/> is the entire experiment, and a lead-in would blunt the very edge
    /// being measured. And the match is against the whole transcript rather than its first non-blank
    /// segment: what is being tested is whether the phrase is still <em>there</em>, not whether it
    /// comes first, and a window this long will normally have plenty of narration before it.
    /// </para>
    /// </summary>
    /// <param name="from">Absolute position to start decoding at.</param>
    /// <param name="until">Absolute position to stop decoding at.</param>
    /// <param name="file">Path of the audio file.</param>
    /// <param name="inputDecoder">Explicit input decoder to force, or null.</param>
    /// <param name="phraseRegex">The announcement to look for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True when some transcribed segment of that stretch matches the phrase.</returns>
    private async Task<bool> PhraseSurvivesFromAsync(
        double from, double until, string file, string? inputDecoder, Regex phraseRegex,
        CancellationToken ct)
    {
        from = Math.Round(Math.Max(0, from), 6);
        var length = Math.Round(until - from, 6);
        if (length <= 0)
            return false;
        var samples = await _audio.DecodePcmAsync(file, from, length, inputDecoder, ct);
        var transcript = await _transcribeCounting(samples, ct);
        return transcript.Any(s => s.Text != null && phraseRegex.IsMatch(s.Text));
    }

    /// <summary>
    /// Generates the blind, fixed-step candidate positions
    /// <see cref="VerifyMarkBeforeJingleAsync"/> falls back on once its VAD candidates are
    /// exhausted: <paramref name="mark"/> plus/minus <see cref="PreciseMarkFixedStepSeconds"/>,
    /// 2x that, 3x that, and so on out to <paramref name="span"/>. Stepping is affordable there,
    /// unlike in <see cref="LocatePhraseByShrinkingWindowAsync"/>, because that search stops at the
    /// first candidate where the announcement is <em>no longer</em> audible - and a check window
    /// only reaches <see cref="PreciseMarkCheckWindowSeconds"/> forward, so retreating past that
    /// much silences it. Stops early, short of <paramref name="span"/>, if a backward step would
    /// otherwise go negative - there is nothing before the start of the file to check.
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

}
