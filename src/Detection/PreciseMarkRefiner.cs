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
/// <param name="PhraseReadings">Every probe transcript in which the announcement was actually
/// found, in probe order. The search itself only wants a yes or no from each, but the transcripts
/// behind those answers are the closest, most narrowly framed look at the announcement anything in
/// the run gets - typically five of them, sometimes seventeen - and reading a chapter
/// <em>number</em> out of them costs nothing extra (<see cref="RefinedNumberVote"/>).</param>
internal readonly record struct PreciseMarkResult(
    double Mark, bool PhraseHeard, IReadOnlyList<List<TranscriptSegment>> PhraseReadings);

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
    private readonly Action<string>? _debug;
    private readonly Func<float[], CancellationToken, Task<List<TranscriptSegment>>> _transcribeCounting;

    /// <summary>The same through the heavier <c>--pass3-model</c>, or null when the run has no
    /// upgrade model to fall back on - see <see cref="RefinePreciseMarkAsync"/>'s retry.</summary>
    private readonly Func<float[], string, CancellationToken, Task<List<TranscriptSegment>>>? _transcribeUpgraded;

    /// <summary>
    /// The recognizer every probe below goes through: <see cref="_transcribeCounting"/> at all
    /// times except inside <see cref="RefinePreciseMarkAsync"/>'s upgrade-model retry, which swaps
    /// it for the duration of that second attempt. Scoped exactly like
    /// <see cref="_phraseReadings"/> and safe for the same reason - one file's placements run
    /// strictly one after another - and swapped in the same <c>try/finally</c>, so nothing outside
    /// that attempt (<see cref="VerifyMarkBeforeJingleAsync"/> above all) can ever see the heavier
    /// model. Threading it through the search instead would put a parameter on all seven methods of
    /// a call tree for a value that is constant across any one attempt.
    /// </summary>
    private Func<float[], CancellationToken, Task<List<TranscriptSegment>>> _transcribe;

    /// <summary>
    /// Collector for <see cref="PreciseMarkResult.PhraseReadings"/> while
    /// <see cref="RefinePreciseMarkAsync"/> is running, and null at every other time - which is what
    /// scopes the collection to one refinement without threading a parameter through the whole
    /// probe tree. Null is not merely the idle state: it also excludes
    /// <see cref="VerifyMarkBeforeJingleAsync"/>, whose probes ask about a position deliberately
    /// placed <em>before</em> the announcement and whose transcripts therefore say nothing about
    /// which chapter this is. One file's placements run strictly one after another, so a single
    /// field is enough.
    /// </summary>
    private List<List<TranscriptSegment>>? _phraseReadings;

    /// <summary>Creates a refiner bound to the given tools and options.</summary>
    /// <param name="audio">Audio source used for PCM decoding.</param>
    /// <param name="options">Validated command line options.</param>
    /// <param name="log">This file's log sinks; default when nothing is listening.</param>
    /// <param name="transcribeCounting">Delegate onto <see cref="ChapterDetector"/>'s own
    /// transcribe-with-stat-counting helper, so this class's transcriptions are tallied into the
    /// same per-file Whisper audio/time statistics as every other detection-path recognition,
    /// without duplicating that accumulation logic here.</param>
    /// <param name="transcribeUpgraded">The same through the <c>--pass3-model</c> recognizer, which
    /// takes the language as well since that recognizer is not the one the file's language was set
    /// on; null when no upgrade model was chosen, which is what switches the retry off.</param>
    internal PreciseMarkRefiner(
        IAudioSource audio, CliOptions options, DetectionLog log,
        Func<float[], CancellationToken, Task<List<TranscriptSegment>>> transcribeCounting,
        Func<float[], string, CancellationToken, Task<List<TranscriptSegment>>>? transcribeUpgraded = null)
    {
        _audio = audio;
        _options = options;
        _log = log.Fanout();
        _debug = log.Debug;
        _transcribeCounting = transcribeCounting;
        _transcribeUpgraded = transcribeUpgraded;
        _transcribe = transcribeCounting;
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
        var transcript = await _transcribe(samples, ct);
        // The lead-in can surface a trailing fragment of whatever preceded `start` as the first
        // segment (e.g. the jingle's own tail, or the previous chapter's last words) - the first
        // *non-blank* segment is what actually starts at or after the checked position.
        var first = transcript.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.Text));
        var found = first.Text != null && phraseRegex.IsMatch(first.Text);
        if (found)
            _phraseReadings?.Add(transcript);
        LogProbe($"onset probe {length:0.00}s@{FormatTimestamp(decodeStart)} " +
                 $"(checking {FormatTimestamp(start)}) -> {(found ? "phrase" : "no")}", transcript);
        return found;
    }

    /// <summary>
    /// Records one of this class's own probe decodes in the --debug file. These transcriptions are
    /// invisible everywhere else - the search only ever passes their yes/no answer upwards - and a
    /// misplaced mark is almost always a probe that answered wrong, so reconstructing one after the
    /// fact previously meant re-running the same decodes by hand in a throwaway harness.
    /// </summary>
    /// <param name="context">Description of the decode and what it concluded.</param>
    /// <param name="transcript">What Whisper returned, times relative to the decode.</param>
    private void LogProbe(string context, List<TranscriptSegment> transcript)
        => _debug?.Invoke(FormatTranscript(context, transcript));

    /// <summary>
    /// Verifies (and if necessary, corrects) a default-mode mark by directly asking Whisper
    /// "does the chapter phrase start right here?" instead of trusting the VAD/duration
    /// heuristics <see cref="JingleGeometry.RefineDefaultMark"/> already applied - the precise marking option
    /// (<see cref="CliOptions.PreciseMark"/>). Those heuristics rest on a floor deliberately
    /// calibrated to err toward not skipping real speech (see
    /// <see cref="TransientSpeechFloorSeconds"/>'s remarks on cross-language uncertainty), which
    /// means a spurious VAD "speech" blip inside a jingle that happens to clear that floor - a
    /// vocal-like musical transient, or an occasional Whisper hallucination - can still fool them
    /// into stopping short of the true announcement; this asks Whisper instead, at the cost of one
    /// or more extra transcriptions per chapter.
    /// <para>
    /// It is worth being clear about what that does and does not buy, because the name invites the
    /// opposite reading: this is not ground truth replacing a heuristic, it is one heuristic's
    /// assumptions swapped for another's. The searches below are exact, but every one of them rests
    /// on an empirical claim about how a neural net behaves - that the phrase stays audible right up
    /// to the onset and not past it. When that claim fails, an exact search over it fails
    /// confidently. Die Dritte Macht chapter 7 is the case on record (2026-07-31, see
    /// <see cref="PreciseMarkPlateauProbesSeconds"/>): the heuristic mark this method exists to
    /// correct was 0.04 s off, and the correction moved it 1.16 s early. So a disagreement between
    /// the two is not evidence that this one is right, and the fallback when nothing confirms -
    /// keeping the heuristic mark - is a sound outcome rather than a failure.
    /// </para>
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
    /// A search that confirms nothing is run a second time through the <c>--pass3-model</c>
    /// recognizer, where the run has one that outclasses the probing model - the whole procedure
    /// again, not a patch on the first attempt, since which position the search converges on depends
    /// on every answer along the way. The failure this addresses is the model, not the geometry: a
    /// quietly-spoken announcement inside a jingle drops out of a smaller model's reading of a long
    /// window as plain "[Musik]", which is indistinguishable from "the announcement is not here" and
    /// sends the survival edge (<see cref="FindPhraseSurvivalEdgeAsync"/>) back to well before the
    /// onset, where no foothold can then be confirmed. Measured on the two marks that prompted this
    /// (2026-08-02, chapters 6 and 14 of one German audiobook, <c>-m small -M turbo</c>): the single
    /// probe that broke each search read "* Musik *" on the small model and "Kapitel 6 Fernweh 3"
    /// respectively "Kapitel 14 Srimavo" on the upgrade one, and every foothold the corrected edge
    /// would then have offered confirms on it.
    /// </para>
    /// <para>
    /// Affordable because it is rare and small: over a ten-book run only five marks of 271 reached
    /// the failure branch at all, and a refinement is a handful of short decodes. The heavier model
    /// is loaded lazily, so a run that never needed it still never loads it - the same trade
    /// <see cref="ChapterDetector"/>'s suspect-number re-read already makes.
    /// </para>
    /// <para>
    /// When neither model can confirm, <paramref name="mark"/> itself carries forward into the final
    /// step below rather than guessing - validated against a real audiobook's full set of known good
    /// and previously-broken marks (see <c>tools\vadprobe</c>'s <c>precise</c> prototype) before
    /// this was ported here. The default-mode heuristic it falls back to measured within 0.05-0.35 s
    /// of the true onset for 25 of BARDIOC.m4b's 26 marks, so the fallback is a fraction of a second
    /// of accuracy rather than a broken mark.
    /// </para>
    /// <para>
    /// Two cleanup steps then run on whatever the above produced. First,
    /// <see cref="AnchorOnsetToSoundAsync"/> moves a confirmed onset back onto the point where
    /// sound resumes after the pause in front of it, which is what makes the mark carry its full
    /// <c>--mark-lead</c> instead of however much of it the plateau's late right edge left over.
    /// Then <see cref="SnapToQuietestPointAsync"/> nudges the resulting mark backward - never later,
    /// which could eat into the announcement - to a quieter point within
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
    /// <param name="language">The file's resolved language, for the upgrade-model retry.</param>
    /// <param name="phraseAbs">Absolute start of the transcript segment(s) the phrase was matched
    /// in - the search bracket, together with the two that follow.</param>
    /// <param name="phraseEndAbs">Absolute end of those segment(s).</param>
    /// <param name="transcriptEnd">Absolute end of the audio the phrase was detected in, tightening
    /// that bracket where it can - see <see cref="MarkContext.Transcript"/> and
    /// <see cref="LocatePhraseByShrinkingWindowAsync"/>'s ceiling for the case where it cannot.</param>
    /// <param name="silences">Every silence Pass 1 stored, chronological, for
    /// <see cref="AnchorOnsetToSoundAsync"/>. Empty is a valid input - a file whose audio offered no
    /// silence at all, or a caller that has none to hand - and simply skips that step.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The confirmed/corrected mark (already correct, corrected in either direction, or
    /// left as given when neither model could confirm it), quiet-snapped as a final step, paired
    /// with whether the phrase was ever actually heard - see <see cref="PreciseMarkResult"/>.</returns>
    internal async Task<PreciseMarkResult> RefinePreciseMarkAsync(
        double mark, string file, string? inputDecoder, Regex phraseRegex, string language,
        double phraseAbs, double phraseEndAbs, double transcriptEnd,
        IReadOnlyList<Silence> silences, CancellationToken ct)
    {
        var readings = new List<List<TranscriptSegment>>();
        _phraseReadings = readings;
        try
        {
            var onset = await LocateOnsetAsync(
                mark, phraseAbs, phraseEndAbs, transcriptEnd, file, inputDecoder, phraseRegex, ct);
            if (onset == null && _transcribeUpgraded is { } upgraded)
            {
                _log?.Invoke($"could not confirm the phrase near {FormatTimestamp(mark)} - " +
                             "trying again with the --pass3-model recognizer");
                _transcribe = (samples, innerCt) => upgraded(samples, language, innerCt);
                try
                {
                    onset = await LocateOnsetAsync(
                        mark, phraseAbs, phraseEndAbs, transcriptEnd, file, inputDecoder, phraseRegex, ct);
                }
                finally
                {
                    _transcribe = _transcribeCounting;
                }
            }

            double result;
            var heard = onset != null;
            if (onset is { } found)
            {
                var anchored = await AnchorOnsetToSoundAsync(
                    found, silences, file, inputDecoder, ct);
                if (anchored != found)
                    _log?.Invoke(
                        $"onset {FormatTimestamp(found)} anchored back to {FormatTimestamp(anchored)}, " +
                        "where sound resumes after the pause in front of the announcement");
                result = Math.Max(0, anchored - _options.MarkLeadSeconds);
                _log?.Invoke(result == mark
                    ? $"mark confirmed at {FormatTimestamp(mark)} - unchanged"
                    : $"mark corrected from {FormatTimestamp(mark)} to {FormatTimestamp(result)} " +
                      $"(onset {FormatTimestamp(anchored)})");
            }
            else
            {
                _log?.Invoke(
                    $"could not confirm the phrase near {FormatTimestamp(mark)} - mark left unchanged");
                result = mark;
            }

            var quietest = await SnapToQuietestPointAsync(result, file, inputDecoder, ct);
            if (quietest != result)
                _log?.Invoke($"nudged {FormatTimestamp(result)} to quieter {FormatTimestamp(quietest)}");
            return new PreciseMarkResult(quietest, heard, readings);
        }
        finally
        {
            _phraseReadings = null;
        }
    }

    /// <summary>
    /// Moves a located onset back onto the point where sound actually resumes after the pause in
    /// front of the announcement. Corrects the one error <see cref="FindOnsetEdgeAsync"/>
    /// structurally cannot: its plateau ends where Whisper stops recognizing a phrase cut off at
    /// the front, not where the phrase begins, and a clipped leading word is something Whisper
    /// reconstructs well enough that the two are up to half a second apart - which a 0.35 s
    /// <c>--mark-lead</c> cannot absorb, so the mark lands on the announcement's own first word.
    /// <para>
    /// Two steps, and the second is what makes the first honest. The silence Pass 1 recorded gives
    /// a floor: nothing audible precedes the end of a silence, so the announcement cannot begin
    /// before it. But that end is only a floor. silencedetect's verdict is a fixed
    /// <see cref="SilenceNoiseDb"/> threshold against individual samples, so a single click - a
    /// mouth noise, a page, a chair - closes the silence while the narrator is still drawing
    /// breath. Anchoring straight to it therefore lands early by however long that gap runs.
    /// Measured over the fourteen-book run of 2026-08-03 (114 marks with a silence in reach, each
    /// one's audio decoded and measured): the sound really does start within 0.1 s of the silence
    /// end 113 times, and once - Paula Monti chapter 19, a -47 dBFS click at 3:18:50.72 with the
    /// word "Première" not beginning until 3:18:51.13 - it starts 0.39 s later. So the scan below
    /// walks forward from the floor to the first stretch of genuine sound, and that is the onset.
    /// </para>
    /// <para>
    /// "Genuine" is <see cref="PreciseMarkOnsetSustainSeconds"/> of audio staying within
    /// <see cref="PreciseMarkOnsetFloorDb"/> of the loudest thing in the window, which is the
    /// announcement itself - a relative test, so a quietly mastered book and a loud one behave
    /// identically and no absolute level needs calibrating. Taking the <em>first</em> such stretch
    /// rather than the last is what keeps the pauses <em>inside</em> an announcement out of it:
    /// "Chapter" and "Five" are separated by about a tenth of a second of near-silence, and a scan
    /// working backward from the onset would stop there instead.
    /// </para>
    /// <para>
    /// Never moves the onset later, and never runs at all without a silence to start from - which
    /// is why the correction stays silent on the books that need it least, a jingle being music
    /// rather than silence and leaving no floor within
    /// <see cref="PreciseMarkSilenceAnchorSeconds"/>. An onset that already falls <em>inside</em> a
    /// silence is likewise left alone: the last silence to close before it is then a different,
    /// earlier one, far outside that tolerance.
    /// </para>
    /// </summary>
    /// <param name="onset">The onset <see cref="FindOnsetEdgeAsync"/> converged on; also the
    /// latest position the scan may return.</param>
    /// <param name="silences">Every silence Pass 1 stored, chronological.</param>
    /// <param name="file">Path of the audio file.</param>
    /// <param name="inputDecoder">Explicit input decoder to force, or null.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Where sound resumes after the pause in front of the announcement, or
    /// <paramref name="onset"/> unchanged when no silence closed within
    /// <see cref="PreciseMarkSilenceAnchorSeconds"/> before it, or when too little audio decoded
    /// to measure.</returns>
    private async Task<double> AnchorOnsetToSoundAsync(
        double onset, IReadOnlyList<Silence> silences, string file, string? inputDecoder,
        CancellationToken ct)
    {
        if (PrecedingSilenceEnd(onset, silences) is not { } floor)
            return onset;

        // Reaches past the onset so the loudest sample in the window is the announcement's own,
        // whatever the onset itself happened to land on - the level everything below is judged
        // against. The extra audio is never a candidate: the scan stops at the onset.
        var samples = await _audio.DecodePcmAsync(
            file, floor, onset - floor + PreciseMarkCheckWindowSeconds, inputDecoder, ct);
        var frame = (int)(PreciseMarkQuietWindowSeconds * FfmpegClient.SampleRate);
        var frames = frame > 0 ? samples.Length / frame : 0;
        if (frames < 1)
            return floor;

        var energy = new double[frames];
        for (var f = 0; f < frames; f++)
        {
            double sumSquares = 0;
            for (var i = f * frame; i < (f + 1) * frame; i++)
                sumSquares += (double)samples[i] * samples[i];
            energy[f] = sumSquares / frame;
        }

        var peak = energy.Max();
        if (peak <= 0)
            return floor;
        var threshold = peak / Math.Pow(10, PreciseMarkOnsetFloorDb / 10);
        var sustain = Math.Max(1, (int)Math.Round(PreciseMarkOnsetSustainSeconds / PreciseMarkQuietWindowSeconds));

        for (var f = 0; f + sustain <= frames; f++)
        {
            var sound = floor + f * PreciseMarkQuietWindowSeconds;
            if (sound >= onset)
                break;
            var loud = true;
            for (var k = 0; k < sustain && loud; k++)
                loud = energy[f + k] >= threshold;
            if (loud)
                return Math.Round(sound, 6);
        }
        return onset;
    }

    /// <summary>
    /// The end of the last silence to close within <see cref="PreciseMarkSilenceAnchorSeconds"/>
    /// before <paramref name="onset"/> - the floor <see cref="AnchorOnsetToSoundAsync"/> scans
    /// forward from - or null when none did, which is the ordinary case on a book that plays a
    /// jingle into its announcements.
    /// </summary>
    /// <param name="onset">The onset to look behind.</param>
    /// <param name="silences">Every silence Pass 1 stored, chronological.</param>
    private static double? PrecedingSilenceEnd(double onset, IReadOnlyList<Silence> silences)
    {
        double? floor = null;
        foreach (var silence in silences)
        {
            if (silence.StartSeconds >= onset)
                break;
            if (silence.EndSeconds <= onset && onset - silence.EndSeconds <= PreciseMarkSilenceAnchorSeconds)
                floor = silence.EndSeconds;
        }
        return floor;
    }

    /// <summary>
    /// One whole attempt at locating the announcement's onset: find a position the phrase is
    /// audible at (<see cref="LocatePhraseByShrinkingWindowAsync"/>), then walk that foothold
    /// forward to the onset itself (<see cref="FindOnsetEdgeAsync"/>). Split out of
    /// <see cref="RefinePreciseMarkAsync"/> so the upgrade-model retry re-runs the identical
    /// procedure rather than resuming the first attempt somewhere in the middle: every position
    /// either half converges on depends on the answers the other gave, so a second opinion is only
    /// worth anything if it is asked from the beginning. Reads the recognizer off
    /// <see cref="_transcribe"/>, which is what the caller swaps between the two attempts.
    /// </summary>
    /// <param name="mark">The mark being refined; the search's own starting point.</param>
    /// <param name="phraseAbs">Absolute start of the matched segment(s).</param>
    /// <param name="phraseEndAbs">Absolute end of those segment(s).</param>
    /// <param name="transcriptEnd">Absolute end of the audio the phrase was detected in.</param>
    /// <param name="file">Path of the audio file.</param>
    /// <param name="inputDecoder">Explicit input decoder to force, or null.</param>
    /// <param name="phraseRegex">The announcement to look for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The announcement's onset, or null when nothing could be confirmed.</returns>
    private async Task<double?> LocateOnsetAsync(
        double mark, double phraseAbs, double phraseEndAbs, double transcriptEnd, string file,
        string? inputDecoder, Regex phraseRegex, CancellationToken ct)
    {
        var confirmed = await LocatePhraseByShrinkingWindowAsync(
            mark, phraseAbs, phraseEndAbs, transcriptEnd, file, inputDecoder, phraseRegex, ct);
        return confirmed is { } hit
            ? await FindOnsetEdgeAsync(hit, file, inputDecoder, phraseRegex, ct)
            : null;
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
        var candidates = vadCandidates.Concat(FixedStepCandidates(walked, span));

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
    /// onset itself answers "yes" identically. Those answers form a plateau ending at the onset:
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
    /// single mark).
    /// </para>
    /// <para>
    /// The plateau is <em>not</em> guaranteed to be unbroken, which is why the walk above is only
    /// half the method. A smaller model can drop the phrase from a window that plainly contains it,
    /// punching a hole of several tenths of a second into the run of "yes" well before the onset -
    /// measured, with the grid, in <see cref="PreciseMarkPlateauProbesSeconds"/>. A bisection cannot
    /// survive that on its own: it discards the interval past the first "no" it meets, and if that
    /// "no" was a hole, the discarded interval held the real edge. So every converged edge is
    /// re-examined by <see cref="FindResumedPlateauAsync"/>, and the walk restarts wherever the
    /// plateau proves to continue. Within one uninterrupted plateau the bisection remains exact -
    /// it has exactly one edge, so no interval it discards could have held another - and that is
    /// precisely the property the re-examination restores in the presence of holes.
    /// </para>
    /// <para>
    /// Everything is capped at the same jingle-plus-phrase span the searches use, measured from the
    /// position the search set out from, so neither the gallop nor any number of resumes can walk
    /// into the next chapter; that case returns the furthest position actually confirmed, the one
    /// place the step-accuracy guarantee does not hold. It has not been observed on real audio - it
    /// needs the announcement to stay the first audible thing across an entire jingle-length span.
    /// </para>
    /// </summary>
    /// <param name="confirmed">A position the phrase was already heard at; the search starts here
    /// and only ever moves later, since the plateau extends forward to the onset.</param>
    /// <param name="file">Path of the audio file.</param>
    /// <param name="inputDecoder">Explicit input decoder to force, or null.</param>
    /// <param name="phraseRegex">The announcement to look for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The plateau's right edge, less one lead-in: located to within
    /// <see cref="PreciseMarkFixedStepSeconds"/>, and biased late against the announcement's true
    /// onset by however far Whisper kept recognizing a clipped leading word - which is what
    /// <see cref="AnchorOnsetToSoundAsync"/> then takes back off.</returns>
    private async Task<double> FindOnsetEdgeAsync(
        double confirmed, string file, string? inputDecoder, Regex phraseRegex, CancellationToken ct)
    {
        // Measured from the position the search set out from, not from wherever a resumed walk
        // restarts, so no number of resumes can add up to walking into the next chapter.
        var limit = confirmed + _options.MaxJingleSeconds + PhraseMarginSeconds;
        var edge = await WalkPlateauEdgeAsync(confirmed, limit, file, inputDecoder, phraseRegex, ct);

        for (var resumes = 0; resumes < PreciseMarkPlateauResumeLimit; resumes++)
        {
            if (await FindResumedPlateauAsync(edge, limit, file, inputDecoder, phraseRegex, ct)
                is not { } resumed)
                break;
            _log?.Invoke($"phrase heard again at {FormatTimestamp(resumed)}, past the edge at " +
                         $"{FormatTimestamp(edge)} - resuming the onset walk there");
            edge = await WalkPlateauEdgeAsync(resumed, limit, file, inputDecoder, phraseRegex, ct);
        }
        return OnsetOf(edge);
    }

    /// <summary>
    /// One gallop-and-bisect pass: the last position at or after <paramref name="from"/> where the
    /// phrase is still the first thing heard, accurate to <see cref="PreciseMarkFixedStepSeconds"/>.
    /// Split out of <see cref="FindOnsetEdgeAsync"/> so the same walk can be re-run from a later
    /// foothold when the plateau turns out to resume past the edge this found.
    /// </summary>
    /// <param name="from">Position the phrase is known to be heard at; the walk only moves later.</param>
    /// <param name="limit">Absolute position the walk may not probe beyond.</param>
    /// <param name="file">Path of the audio file.</param>
    /// <param name="inputDecoder">Explicit input decoder to force, or null.</param>
    /// <param name="phraseRegex">The announcement to look for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The last probe position that still heard the phrase.</returns>
    private async Task<double> WalkPlateauEdgeAsync(
        double from, double limit, string file, string? inputDecoder, Regex phraseRegex,
        CancellationToken ct)
    {
        var lo = from;
        double? hi = null;

        for (var delta = PreciseMarkFixedStepSeconds; from + delta <= limit; delta *= 2)
        {
            ct.ThrowIfCancellationRequested();
            var probe = Math.Round(from + delta, 6);
            if (!await PreciseMarkPhraseFoundAsync(probe, file, inputDecoder, phraseRegex, ct))
            {
                hi = probe;
                break;
            }
            lo = probe;
        }

        if (hi is not { } failed)
            return lo;

        while (failed - lo > PreciseMarkFixedStepSeconds)
        {
            ct.ThrowIfCancellationRequested();
            var mid = Math.Round((lo + failed) / 2, 6);
            if (await PreciseMarkPhraseFoundAsync(mid, file, inputDecoder, phraseRegex, ct))
                lo = mid;
            else
                failed = mid;
        }
        return lo;
    }

    /// <summary>
    /// Asks whether the plateau resumes past <paramref name="edge"/> - i.e. whether the "no" the
    /// walk stopped at was the announcement's onset or merely a hole in an otherwise continuous run
    /// of "yes". See <see cref="PreciseMarkPlateauProbesSeconds"/> for the measured failure that
    /// makes this necessary and for why the probes are spaced as coarsely as they are.
    /// <para>
    /// The <em>rightmost</em> answering probe is returned rather than the nearest, since every one
    /// of them is by definition a position where the phrase is still heard, and starting the next
    /// walk from the furthest one skips the most ground. A hole never invalidates what the walk
    /// already established: the plateau is a property of the audio, so a later confirmation only
    /// ever means the earlier edge was premature.
    /// </para>
    /// </summary>
    /// <param name="edge">Edge the walk converged on.</param>
    /// <param name="limit">Absolute position no probe may be placed beyond.</param>
    /// <param name="file">Path of the audio file.</param>
    /// <param name="inputDecoder">Explicit input decoder to force, or null.</param>
    /// <param name="phraseRegex">The announcement to look for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The furthest position past <paramref name="edge"/> at which the phrase is still the
    /// first thing heard, or null when none is - the ordinary case, in which
    /// <paramref name="edge"/> really was the onset.</returns>
    private async Task<double?> FindResumedPlateauAsync(
        double edge, double limit, string file, string? inputDecoder, Regex phraseRegex,
        CancellationToken ct)
    {
        double? resumed = null;
        foreach (var offset in PreciseMarkPlateauProbesSeconds)
        {
            var probe = Math.Round(edge + offset, 6);
            if (probe > limit)
                break;
            ct.ThrowIfCancellationRequested();
            if (await PreciseMarkPhraseFoundAsync(probe, file, inputDecoder, phraseRegex, ct))
                resumed = probe;
        }
        return resumed;
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
    /// <para>
    /// When the gallop instead runs out of range with the phrase <em>still</em> surviving from the
    /// ceiling, there is no edge inside the bracket to aim by - the announcement lies at or past
    /// the far end - and the ceiling itself becomes the aim point. That is the mirror of the defect
    /// <see cref="PreciseMarkMaxBracketSeconds"/> extends the floor for, a segment Whisper
    /// timestamped <em>earlier</em> than the words in it, and it is answered differently for a
    /// reason: extending the floor only lets the search reach further, while extending the ceiling
    /// would lengthen the gallop's own probes and push them past
    /// <see cref="WhisperChunkSeconds"/>, where a window re-segments and the predicate stops being
    /// the step function the bisection needs. Aiming from the ceiling costs nothing at all - the
    /// foothold hunt and <see cref="FindOnsetEdgeAsync"/>'s walk were going to run either way - and
    /// gives up no accuracy, since the walk probes fixed-width windows and converges on the same
    /// plateau edge from any foothold inside the plateau.
    /// </para>
    /// <para>
    /// The reach that buys is one <see cref="PreciseMarkCheckWindowSeconds"/>: the foothold probe at
    /// the ceiling confirms while the announcement starts inside its own window, and fails honestly
    /// past that. Measured against the ten-book run of 2026-08-02 (222 refinements that reported an
    /// onset), the onset landed past the search ceiling exactly twice, by 1.4 s and 2.2 s, with a
    /// median headroom of 6.9 s - so a check window's reach covers every case on record with room
    /// to spare, and no wider bracket has ever been called for. Raumschiff Erde chapter 1 is the
    /// case this was built for and the only refinement of 278 that reached this branch: its
    /// detecting decode was a 26.7 s overlap tail from 0:04:55.38 and Whisper timestamped
    /// "Kapitel 1." as its very first segment, 0.0-7.0, twelve seconds before the words were
    /// spoken, so the bracket [0:04:42.38, 0:05:07.38] ended right at the announcement rather than
    /// behind it. The words are in fact at 0:05:07.0-0:05:12.0 and the onset at 307.50 s, the
    /// plateau's last confirming probe being 307.60 and the first failing one 307.70
    /// (<c>tools\wprobe</c>, ggml-small, 2026-08-02); a foothold probe at the ceiling, 307.38,
    /// confirms cleanly and the walk from it lands on exactly that onset.
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
        // margin either side for a timestamp that reported them slightly narrow, and normally no
        // further than the end of the audio it was heard in.
        //
        // That last cap is a tightening one, and it is only sound while the matched segment really
        // fits inside the window it came from - which is not something a window can promise. A
        // window admits a segment by its start alone (correctly: that is what keeps a smeared
        // segment from being dropped) and Whisper routinely stretches a window's last segment far
        // past its own audio, all the more so since a probe decode may read tens of seconds beyond
        // the window it was planned for. Where that happens the window end is not an upper bound on
        // the announcement at all, it is a position in front of it, and every probe below then asks
        // about audio the announcement is not in - a total failure that looks exactly like the
        // honest "this was an in-text mention" outcome. Measured on Raumschiff Erde chapter 25
        // (2026-08-02): window end 10:51:16.16, matched segment 10:51:15.66-10:51:33.16, so the
        // bracket came out as [10:50:51.17, 10:51:16.17] and all eight probes read "[Musik]" or the
        // previous chapter's narration. Letting the cap pull the ceiling below the segment's own
        // end is therefore the one thing it may not do; everything else about it is unchanged, so a
        // segment that ends inside its window brackets exactly as it always did.
        //
        // Blast radius, measured over the ten-book run of 2026-08-02 (278 refinements, debug logs
        // replayed): 12 marks get a different ceiling, by 0.29 s to 17.0 s. Two of them are total
        // failures this recovers - Raumschiff Erde chapter 25 and Die Dritte Macht chapter 15, both
        // with the incoming mark sitting past the old bracket entirely, which is the signature to
        // grep a log for. The other ten were confirmed anyway and stay confirmed for a structural
        // reason worth keeping in mind before touching this line again: the ceiling decides only
        // which foothold LocatePhraseByShrinkingWindowAsync hands over, and FindOnsetEdgeAsync -
        // which is what actually fixes the mark - probes fixed-width windows and converges on the
        // same plateau edge from any foothold inside the plateau. Seven of the ten had every
        // survival probe pinned at PreciseMarkMinSurvivalSeconds anyway.
        var ceiling = Math.Max(Math.Min(phraseEndAbs + PhraseMarginSeconds, transcriptEnd), phraseEndAbs);
        var segmentFloor = Math.Max(0, phraseAbs - PhraseMarginSeconds);
        // How far the backward gallop may run below that, for the case the segment's own timestamps
        // do not describe: one that Whisper timed *later* than the words in it, leaving the whole
        // segment bracket past the announcement (see PreciseMarkMaxBracketSeconds). Only the search's
        // reach is extended, not where it starts - the gallop still sets out from the segment, whose
        // first probe is the one long window in the whole search and therefore the one that most
        // wants to stay short (Stalker.m4b, 2026-07-30: 15.65 s from the segment floor reads
        // "Zeittafel", 25 s from a stretched floor reads "[Musik]" and nothing else).
        var floor = Math.Max(0, Math.Min(segmentFloor, ceiling - PreciseMarkMaxBracketSeconds));
        if (ceiling - floor <= PreciseMarkFixedStepSeconds)
        {
            _log?.Invoke($"cannot refine the mark at {FormatTimestamp(mark)} - the phrase was " +
                         $"matched in too short a stretch to search ({FormatTimestamp(floor)} to " +
                         $"{FormatTimestamp(ceiling)})");
            return null;
        }

        // Announced before it starts rather than after: this step can keep a single mark busy for a
        // while, and an unexplained silent wait is indistinguishable from a hang - which is exactly
        // how its predecessor was first reported, on Stalker.m4b's "Zeittafel", 2026-07-29.
        _log?.Invoke($"refining mark at {FormatTimestamp(mark)} - narrowing in on the phrase " +
                     $"between {FormatTimestamp(floor)} and {FormatTimestamp(ceiling)}");

        var origin = Math.Clamp(mark, Math.Min(segmentFloor, ceiling), ceiling);
        if (await FindPhraseSurvivalEdgeAsync(
                origin, floor, ceiling, file, inputDecoder, phraseRegex, ct)
            is not { } survival)
        {
            _log?.Invoke($"the phrase was not heard anywhere from {FormatTimestamp(floor)} on - " +
                         "the search range may not reach the announcement at all");
            return null;
        }

        var (aim, isEdge) = survival;
        _log?.Invoke(isEdge
            ? $"phrase survives up to {FormatTimestamp(aim)} - " +
              "confirming the announcement just before it"
            : $"the phrase still survives from {FormatTimestamp(aim)}, the far end of the search " +
              "range - the announcement lies at or past it, so that is where confirmation starts");

        foreach (var backoff in PreciseMarkFootholdBackoffsSeconds)
        {
            var candidate = Math.Round(Math.Max(0, aim - backoff), 6);
            ct.ThrowIfCancellationRequested();
            if (await PreciseMarkPhraseFoundAsync(candidate, file, inputDecoder, phraseRegex, ct))
                return candidate;
            if (candidate == 0)
                break;
        }
        _log?.Invoke(isEdge
            ? $"nothing just before {FormatTimestamp(aim)} opens with the announcement - " +
              "the phrase survived that far for some other reason (an in-text mention?)"
            : $"nothing at or just before {FormatTimestamp(aim)} opens with the announcement - " +
              "it lies further past the search range than one check window reaches");
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
    /// onset, so anything that survived under the old one survives under the new one too. Near the
    /// failing position itself the anchor stops being what the decode obeys, since
    /// <see cref="PhraseSurvivesFromAsync"/> never transcribes less than
    /// <see cref="PreciseMarkMinSurvivalSeconds"/>; that is the point of the floor, and the anchor's
    /// real work is bounding the probes further back.
    /// </para>
    /// </summary>
    /// <param name="origin">Position the gallop starts from; inside the bracket.</param>
    /// <param name="floor">Earliest position the onset can be at.</param>
    /// <param name="ceiling">Latest position it can be at, and the window's end anchor.</param>
    /// <param name="file">Path of the audio file.</param>
    /// <param name="inputDecoder">Explicit input decoder to force, or null.</param>
    /// <param name="phraseRegex">The announcement to look for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The furthest position the phrase is known to survive from, and whether that is the
    /// edge itself. <c>IsEdge</c> holds in the ordinary case, where the next step failed and the
    /// position is therefore accurate to <see cref="PreciseMarkFixedStepSeconds"/>; it is false when
    /// the gallop ran out of <paramref name="ceiling"/> with the phrase still surviving, where the
    /// position is only a lower bound on an edge that lies somewhere past the searched range - see
    /// <see cref="LocatePhraseByShrinkingWindowAsync"/> for why that is still worth aiming from.
    /// Null when the phrase is not found anywhere in the range, the one outcome with nothing to aim
    /// at; the caller logs it as its own event, distinct from the honest "nothing here matched" its
    /// own failure reports.</returns>
    private async Task<(double Position, bool IsEdge)?> FindPhraseSurvivalEdgeAsync(
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
                // Tested before probing rather than after, so a mark already sitting at the ceiling
                // does not pay for asking the identical question twice on its way out.
                if (lastTrue >= ceiling)
                    return (lastTrue, false);
                var probe = Math.Min(ceiling, Math.Round(lastTrue + stride, 6));
                if (!await SurvivesAsync(probe))
                {
                    firstFalse = probe;
                    break;
                }
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
        return (lastTrue, true);
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
    /// <para>
    /// The decode is never shorter than <see cref="PreciseMarkMinSurvivalSeconds"/>, whatever
    /// <paramref name="until"/> says, because below that Whisper's answer stops being reliable enough
    /// to bisect on; <paramref name="until"/> still decides whether there is a question to ask at all,
    /// so a window the caller has narrowed to nothing is answered "no" rather than reopened.
    /// </para>
    /// </summary>
    /// <param name="from">Absolute position to start decoding at.</param>
    /// <param name="until">Absolute position the searched span ends at - a lower bound on how far
    /// the decode reaches, not a cap on it.</param>
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
        if (until - from <= 0)
            return false;
        var length = Math.Round(Math.Max(until - from, PreciseMarkMinSurvivalSeconds), 6);
        var samples = await _audio.DecodePcmAsync(file, from, length, inputDecoder, ct);
        var transcript = await _transcribe(samples, ct);
        var survives = transcript.Any(s => s.Text != null && phraseRegex.IsMatch(s.Text));
        if (survives)
            _phraseReadings?.Add(transcript);
        LogProbe($"survival probe {length:0.00}s@{FormatTimestamp(from)} " +
                 $"(until {FormatTimestamp(until)}) -> {(survives ? "phrase" : "no")}", transcript);
        return survives;
    }

    /// <summary>
    /// Generates the blind, fixed-step candidate positions
    /// <see cref="VerifyMarkBeforeJingleAsync"/> falls back on once its VAD candidates are
    /// exhausted: <paramref name="mark"/> minus <see cref="PreciseMarkFixedStepSeconds"/>, 2x that,
    /// 3x that, and so on out to <paramref name="span"/>. Stepping is affordable there,
    /// unlike in <see cref="LocatePhraseByShrinkingWindowAsync"/>, because that search stops at the
    /// first candidate where the announcement is <em>no longer</em> audible - and a check window
    /// only reaches <see cref="PreciseMarkCheckWindowSeconds"/> forward, so retreating past that
    /// much silences it. Stops early, short of <paramref name="span"/>, once a step would go
    /// negative - there is nothing before the start of the file to check.
    /// <para>
    /// Backward only, matching its one caller: the observed failure always left the walk too late,
    /// never too early, and --mark-before-jingle's whole purpose is landing before the jingle. A
    /// direction flag existed here and was never passed anything but "backward".
    /// </para>
    /// </summary>
    /// <param name="mark">Position the steps count back from; never itself included.</param>
    /// <param name="span">How far back from <paramref name="mark"/> to keep stepping.</param>
    private static IEnumerable<double> FixedStepCandidates(double mark, double span)
    {
        var steps = (int)Math.Round(span / PreciseMarkFixedStepSeconds);
        for (var i = 1; i <= steps; i++)
        {
            var candidate = mark - i * PreciseMarkFixedStepSeconds;
            if (candidate < 0)
                yield break;
            yield return Math.Round(candidate, 6);
        }
    }
}
