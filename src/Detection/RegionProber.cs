// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Audio;
using ABChapterize.Cli;
using ABChapterize.Language;
using ABChapterize.Transcription;
using ABChapterize.Ui;
using ABChapterize.Vad;
using static ABChapterize.Detection.DetectionFormatting;
using static ABChapterize.Detection.DetectionTuning;
using static ABChapterize.Detection.GapPlanning;
using static ABChapterize.Detection.JingleGeometry;
using static ABChapterize.Detection.PhraseMatching;
using static ABChapterize.Detection.TranscriptTime;

namespace ABChapterize.Detection;

/// <summary>
/// Everything a <see cref="RegionProber"/> borrows from the <see cref="ChapterDetector"/> that
/// created it: the tools it probes with, and the detector-owned operations that must stay the
/// detector's (recognition that tallies toward the file's Whisper statistics, the once-per-file
/// language resolution, the --verbose transcript log, the
/// <see cref="CliOptions.MaxChapterNumber"/>-capped phrase matcher and the shared mark placer).
/// Bundled so a prober's constructor is about the region it probes rather than about plumbing;
/// one instance serves every region of one file.
/// </summary>
/// <param name="Options">Validated command line options.</param>
/// <param name="Audio">Audio source the probe windows are decoded from.</param>
/// <param name="Vad">The voice-activity detector, or null when the VAD pre-pass did not run - which
/// switches probing between its VAD-aware and its silence-only geometry throughout.</param>
/// <param name="Log">Sink for --verbose log messages, or null when not verbose.</param>
/// <param name="Marks">The file's mark placer, shared with every other pass.</param>
/// <param name="TranscribeCounting">The detector's statistics-counting transcribe wrapper.</param>
/// <param name="LogTranscript">Logs a decoded window's transcript under --verbose.</param>
/// <param name="FindCappedPhraseMatches">The detector's --max-chapter-number-capped phrase matcher.</param>
/// <param name="SecondOpinion">Transcribes samples with the heavier <c>--pass3-model</c> in a given
/// language, for the two Pass 2 steps that are worth asking a better recognizer:
/// <see cref="SuspectNumberMender"/>'s re-read of an implausible chapter number, and
/// <see cref="RegionProber.RereadJingleSpeechAsync"/>'s second look at an announcement the first
/// decode's framing lost. Null when no upgrade model was chosen, and null for a pass 2.5 re-probe,
/// which already decodes every window through that model and so has no better opinion left to
/// ask.</param>
internal sealed record ProbeEnvironment(
    CliOptions Options,
    IAudioSource Audio,
    IVoiceActivityDetector? Vad,
    Action<string>? Log,
    MarkPlacer Marks,
    Func<float[], CancellationToken, ITranscriber?, Task<List<TranscriptSegment>>> TranscribeCounting,
    Action<string, List<TranscriptSegment>> LogTranscript,
    Func<List<TranscriptSegment>, LanguageProfile, int?, IEnumerable<PhraseMatch>> FindCappedPhraseMatches,
    Func<float[], string, CancellationToken, Task<List<TranscriptSegment>>>? SecondOpinion = null);

/// <summary>
/// Region-loop-invariant Pass 2 inputs, gathered here instead of threading each field through
/// <see cref="RegionProber"/>'s constructor on its own. One instance per file, shared by every
/// region of it.
/// </summary>
/// <param name="File">Path of the audio file.</param>
/// <param name="Info">The file's probed media info (duration, size, decoder).</param>
/// <param name="Work">Progress tracker for the phase/byte accounting.</param>
/// <param name="BytesPerSecond">The file's average bytes per second of play time, used to
/// convert probed play time into the byte-based progress the bar counts in.</param>
/// <param name="JingleCeilingSeconds">Probe window length ceiling: --max-jingle-length plus
/// <see cref="PhraseMarginSeconds"/>, never exceeded even while the window self-tightens.</param>
/// <param name="AllSilences">Every silence Pass 1 retained, down to
/// <see cref="MinStoredSilenceSeconds"/> - seam snapping and mark anchoring, not candidates.</param>
/// <param name="Silences">The silences that become probe candidates: the subset at or above
/// --min-silence-length, or - for one of Pass 2.5's sub-floor sweeps - a single band below it
/// (see <see cref="RegionProber"/>'s sweep remarks).</param>
/// <param name="NonSpeechRegions">The VAD pre-pass's non-speech regions, empty when it did not run.</param>
/// <param name="SpeechSegments">The VAD pre-pass's speech segments, empty when it did not run.</param>
/// <param name="EarlyAbortSeconds">Play time that may be probed without a single find before
/// --early-abort gives up, or +infinity when the check does not apply.</param>
/// <param name="ExpectedStartChapter">--expected-start-chapter's abort threshold, or null when
/// the check does not apply.</param>
/// <param name="Transcriber">The recognizer this region's probes decode with - the pass-2
/// transcriber for Pass 2 proper, the pass-3 one for a pass 2.5 re-probe (see
/// <see cref="ChapterDetector.RunPass25Async"/>). Only the probe transcriptions follow it; mark
/// placement keeps refining on the pass-2 model either way, exactly as Pass 3 already does.</param>
/// <param name="SecondGuessNumbers">Whether an implausible chapter number is re-read before being
/// acted on (<see cref="SuspectNumberMender"/>). False for a pass 2.5 re-probe: its windows already go
/// through the heavier model, and its whole purpose is to re-read the numbers a gap is missing, so
/// questioning its readings against the very sequence it is repairing would be circular. Pass 3 never
/// probes and so never asks. Pass 2's own sequence-gap re-probe <em>does</em> ask, and used to be
/// exempted alongside pass 2.5 on the reasoning that a wider window was already the remedy being
/// applied - which "Die Cyber-Brutzellen" (2026-08-01) refuted: the wider window is what produced
/// the mishearing, an announcement 27 s deep into a 44 s window coming back as chapter 40 instead of
/// 14. A re-probe is now the <em>best</em> place to question a number, since it alone knows both
/// ends of the hole it is filling (see <see cref="RegionProber.SequenceBounds"/>).</param>
internal readonly record struct Pass2Context(
    string File, MediaInfo Info, WorkTracker Work, double BytesPerSecond, double JingleCeilingSeconds,
    List<Silence> AllSilences, List<Silence> Silences, List<NonSpeechRegion> NonSpeechRegions,
    List<SpeechSegment> SpeechSegments, double EarlyAbortSeconds, int? ExpectedStartChapter,
    ITranscriber Transcriber, bool SecondGuessNumbers = true);

/// <summary>One position Pass 2 may probe: the region start, a silence's end, or the start of a
/// VAD jingle region. Exactly one of the two anchors is set, except for the region-start candidate,
/// which has neither.</summary>
/// <param name="Start">Absolute time the probe window starts at.</param>
/// <param name="Silence">The silence whose end this is, when a silence triggered the candidate.</param>
/// <param name="VadRegion">The VAD non-speech region this starts, when one triggered the candidate.</param>
internal readonly record struct ProbeCandidate(
    double Start, Silence? Silence, NonSpeechRegion? VadRegion);

/// <summary>
/// One probe window as planned, together with the candidate sequence it came from - enough for the
/// decode to work out how far it may read ahead (see <see cref="RegionProber.ExtendToPlannedSeam"/>)
/// without the intervening layers having to thread a candidate list through by hand.
/// </summary>
/// <param name="Candidates">The candidate sequence being walked - the region's own, or the skipped
/// subset a sequence-gap re-probe forms.</param>
/// <param name="Index">Index of this window's candidate within <paramref name="Candidates"/>.</param>
/// <param name="End">The window's planned end (see <see cref="RegionProber.WindowEndFor"/>).</param>
internal readonly record struct WindowPlan(
    IReadOnlyList<ProbeCandidate> Candidates, int Index, double End);

/// <summary>One chapter mark a probe window produced.</summary>
/// <param name="Number">The detected chapter number.</param>
/// <param name="MarkSilence">The silence the mark falls into, or null when it sits on a VAD region
/// (or on nothing at all) - the input to the --min-silence-length auto tightening.</param>
/// <param name="Confidence">Whisper's confidence for the segment the phrase was found in, which
/// decides whether this mark settles its whole overlapping window sequence.</param>
/// <param name="ReachSeconds">How far into its window the announcement <em>ended</em>, i.e. the
/// window width this mark actually required. Distinct from the jingle length
/// <see cref="RegionProber.ObserveJingleLength"/> measures: that one is anchored to the silence or
/// region the mark fell into and says how long this book's jingles run, while this is measured from
/// the candidate the window started at and says how far a window from that candidate has to reach
/// before the phrase is complete. The two diverge exactly when the mark's anchor is not the
/// candidate that triggered the probe.</param>
internal readonly record struct ProbeMark(
    int Number, Silence? MarkSilence, double Confidence, double ReachSeconds);

/// <summary>
/// Runs Pass 2 candidate probing for a single <see cref="DetectionRegion"/>, appending every
/// accepted chapter mark to the caller's accumulator in place.
/// <para>
/// Constructed per region, which is what makes the invariant hold that every piece of per-region
/// probe state - the probe window size and its adaptive resizing, the --min-silence-length auto
/// threshold, the transcript-reuse cache and the "last accepted number" - starts fresh: a region is
/// probed as if it were its own small file, not a continuation of whatever an earlier region
/// happened to learn (see <see cref="DetectionRegion"/>'s remarks for why carrying it over would be
/// wrong in both directions). The one thing that does carry across regions is the language
/// resolution, handed in and read back out as a <see cref="LanguageState"/>.
/// </para>
/// </summary>
internal sealed class RegionProber
{
    private readonly ProbeEnvironment _env;
    private readonly Pass2Context _ctx;
    private readonly DetectionRegion _region;

    /// <summary>Re-reads a chapter number from the audio when the one in hand cannot be used: a
    /// number the sequence cannot continue with (gated by <see cref="Pass2Context.SecondGuessNumbers"/>
    /// at the call site) or no readable number at all. Region-scoped like the prober itself, since the
    /// windows it re-frames are clipped to the region's bounds.</summary>
    private readonly SuspectNumberMender _mender;

    /// <summary>How many unreadable-number re-reads this region has spent, against
    /// <see cref="MaxUnnumberedMendsPerRegion"/>.</summary>
    private int _unnumberedMends;

    /// <summary>Accumulator of confirmed chapters across all regions of the file; mutated in place
    /// as marks are accepted, so the sequence Pass 3 later inspects is one seamless list regardless
    /// of which region contributed what.</summary>
    private readonly List<DetectedChapter> _found;

    /// <summary>Accumulator of the file's non-numbered marks, shared across regions exactly as
    /// <see cref="_found"/> is. Holds at most one mark per non-repeatable
    /// <see cref="NamedPhrase.Kind"/> (prologue, epilogue) and any number of repeatable ones
    /// (<c>--custom</c>) - see <see cref="AcceptNamedMatchAsync"/> for both rules.</summary>
    private readonly List<DetectedMark> _namedFound;

    /// <summary>
    /// Seconds added to a candidate's absolute position before it is reported as progress, i.e. the
    /// offset between this region's own time base and the one the enclosing phase counts in.
    /// <para>
    /// Zero for a phase whose total is the whole file (Pass 2 proper, and the gap-scoped Pass 2 a
    /// --verify recovery runs): there the absolute position <em>is</em> the progress. A phase whose
    /// total covers only its regions - pass 2.5, whose budget is the summed gap length, exactly like
    /// Pass 3's - passes the offset that maps this region onto that shorter timeline, so the bar
    /// advances monotonically from 0 to 100 % across the whole pass instead of reporting a
    /// whole-file position against a gap-sized total.
    /// </para>
    /// </summary>
    private readonly double _progressOffsetSeconds;

    /// <summary>Current probe window length. Starts at the ceiling with --max-jingle-length, at
    /// <see cref="ProbeSecondsPlain"/> without it, and follows <see cref="_adaptedWindowSeconds"/>
    /// from the first qualifying jingle observation on.</summary>
    private double _probeSeconds;

    /// <summary>With --max-jingle-length auto, the adapted probe window:
    /// <see cref="JingleObservationSafetyFactor"/> times the longest real inter-chapter jingle
    /// observed so far in this region, plus <see cref="PhraseMarginSeconds"/>, capped at the
    /// ceiling. Null until the first qualifying observation; monotonically increasing from then on
    /// (see <see cref="JingleObservationSafetyFactor"/>).</summary>
    private double? _adaptedWindowSeconds;

    /// <summary>True while the sequence-gap recovery re-probes skipped candidates at the full
    /// ceiling window: observations made during the re-probe still feed
    /// <see cref="_adaptedWindowSeconds"/>, but must not pull <see cref="_probeSeconds"/> back down
    /// mid-re-probe - the whole point of the reset is that every re-probe runs at the ceiling.</summary>
    private bool _reprobing;

    /// <summary>
    /// While the sequence-gap recovery re-probes, the chapter number that closes the gap - the mark
    /// that revealed it. Null at every other time.
    /// <para>
    /// It is the single most informative fact available anywhere in Pass 2 and it used to be
    /// discarded: a re-probe of the hole between chapters 13 and 15 is searching for chapter 14 and
    /// nothing else, yet an announcement read as chapter 40 was accepted there unquestioned on "Die
    /// Cyber-Brutzellen" (2026-08-01) while <see cref="ReprobeGapCandidatesAsync"/> held a
    /// <c>missing</c> set containing exactly one number. Feeding it into
    /// <see cref="SequenceBounds"/> lets both the mender and the refinement vote hold a re-read to
    /// the hole it is filling. Pass 3 has enforced the same rule on its own gap chunks all along.
    /// </para>
    /// </summary>
    private int? _gapAbove;

    /// <summary>The last chapter number accepted in this region, seeded from
    /// <see cref="DetectionRegion.LowerNumber"/> when a chapter is already confirmed to precede it
    /// and null for a from-file-start region. Holds the previous value (not yet the current
    /// window's) while a probe is in flight, which is exactly what a gap re-probe needs to accept
    /// the in-between numbers.</summary>
    private int? _lastNumber;

    /// <summary>The previous probe's decoded span, transcribed, in absolute file time; see
    /// <see cref="_cacheTo"/> for what it is for. Wider than that probe's own window whenever the
    /// decode read ahead (<see cref="ExtendToPlannedSeam"/>), which is the point of reading ahead:
    /// the surplus is what later windows are served from.</summary>
    private List<TranscriptSegment> _cacheSegmentsAbs = [];

    /// <summary>Start of the absolute span <see cref="_cacheSegmentsAbs"/> covers.</summary>
    private double _cacheFrom;

    /// <summary>
    /// End of that span - as far as the previous decode may be trusted, which is at or beyond the
    /// window that decode was run for but not necessarily as far as it read (see
    /// <see cref="CacheableEnd"/>). When the next candidate's window overlaps the span, the
    /// overlapping segments are reused verbatim instead of being re-run through Whisper: a window
    /// that fits inside it costs no Whisper call at all, and one that reaches past it decodes only
    /// the fresh tail beyond the planned seam. The span test (start inside
    /// [<see cref="_cacheFrom"/>, <see cref="_cacheTo"/>)) doubles as the seam-stitching check: it
    /// holds exactly when the previous decode really did run up to a seam this window's plan can
    /// pick up from, and when it does not (e.g. that window was skipped by the adaptive threshold)
    /// the probe falls back to decoding its full window from the candidate start - nothing is ever
    /// left covered by neither decode. Starts at negative infinity so the very first probe of a
    /// region never counts as an overlap and always does a full transcribe.
    /// </summary>
    private double _cacheTo = double.NegativeInfinity;

    /// <summary>
    /// The --min-silence-length auto threshold as adapted so far, or null while probing is still
    /// unthrottled. Probing proceeds unthrottled until the second mark is found (its anchor silence
    /// is the first real inter-chapter break - the silence before the first mark is typically the
    /// intro/title silence, often longer, so it must not be used to tighten). From there each
    /// mark's anchor silence proposes <see cref="AdaptiveTightenFactor"/> times its own length, and
    /// this is the running <em>minimum</em> of those proposals - the first one raises the effective
    /// threshold from the floor, every later one can only lower it (see
    /// <see cref="AdaptiveTightenFactor"/> for why a raise is never safe).
    /// </summary>
    private double? _adaptedThresholdSeconds;

    /// <summary>The silence length a candidate must reach to be probed at all; the
    /// --min-silence-length floor until <see cref="_adaptedThresholdSeconds"/> starts moving it.
    /// Without --min-silence-length auto every candidate is probed unconditionally and this never
    /// changes, exactly as before that feature existed.</summary>
    private double _threshold;

    /// <summary>
    /// Candidates passed over since the last accepted mark. A sequence gap re-probes all of them
    /// unconditionally (see <see cref="ReprobeGapCandidatesAsync"/>) and folds the recovered marks'
    /// own anchor silences into <see cref="_adaptedThresholdSeconds"/>, so gap-filling stays inside
    /// Pass 2 where possible and the threshold can never again sit above a silence that has proven
    /// to precede a chapter. Collects the windows the overlap-sequence skip passes over too - in
    /// every mode, not just auto - so the same re-probe covers the unlikely case of a skipped
    /// sequence window having hidden a second transition.
    /// </summary>
    private readonly List<ProbeCandidate> _skippedSinceLastMark = [];

    /// <summary>
    /// Candidates actually probed since the last accepted mark, each with the window end it was
    /// probed with. A sequence gap re-probes the subset whose window has since been narrowed by
    /// --max-jingle-length auto (see <see cref="WiderWindowWouldReach"/>), because a window sized
    /// off the jingles seen so far can end before an unusually late announcement and come back empty
    /// from audio that does hold the missing chapter - the same suspicion the ceiling reset already
    /// applied to <see cref="_skippedSinceLastMark"/>, which has no reason to stop at candidates
    /// that were never probed. Recording the end each window really got (rather than recomputing it)
    /// is what keeps the re-probe from re-running windows at a width they already had.
    /// </summary>
    private readonly List<(ProbeCandidate Candidate, double WindowEnd)> _probedSinceLastMark = [];

    /// <summary>The file's language resolution, settled before Pass 2 started and read-only from
    /// here - see <see cref="LanguageResolver"/>.</summary>
    private readonly LanguageState Language;

    /// <summary>Whether --early-abort fired in this region: enough play time probed without a
    /// single find that further probing is pointless.</summary>
    internal bool EarlyAborted { get; private set; }

    /// <summary>The first chapter number found, when it sat below --expected-start-chapter and
    /// detection was therefore abandoned for this file; null otherwise.</summary>
    internal int? BelowExpectedStartNumber { get; private set; }

    /// <summary>Whether <see cref="DetectionTuning.MaxCustomMarksPerFile"/> was reached in this
    /// region and further --custom matches were therefore dropped.</summary>
    internal bool CustomLimitHit { get; private set; }

    /// <summary>
    /// Whether this prober is one of Pass 2.5's sub-floor silence sweeps
    /// (<see cref="ChapterDetector.SweepSubFloorSilencesAsync"/>) rather than an ordinary region
    /// probe. A sweep's <see cref="Pass2Context.Silences"/> is a single band of silences that all
    /// sit <em>below</em> --min-silence-length, which changes three things and nothing else: every
    /// one of them is probed (the threshold that excluded them is exactly what the sweep is
    /// suspending, so consulting it again would skip the entire band), the adaptive threshold is
    /// left alone (it can never drop below the floor, so a sweep could only ever tighten itself out
    /// of its own band), and neither the region-start candidate nor the VAD jingle regions are
    /// probed again - the ordinary attempt on this gap already covered both, and a sweep re-running
    /// them would pay a full window decode per band for audio it has read once.
    /// </summary>
    private readonly bool _sweeping;

    /// <summary>Creates a prober for one region.</summary>
    /// <param name="env">The detector-owned tools and callbacks to probe with.</param>
    /// <param name="ctx">Region-loop-invariant Pass 2 inputs.</param>
    /// <param name="region">The region to probe.</param>
    /// <param name="found">Accumulator of confirmed chapters across all regions.</param>
    /// <param name="namedFound">Accumulator of the file's prologue/epilogue marks.</param>
    /// <param name="language">The file's settled language resolution.</param>
    /// <param name="progressOffsetSeconds">Offset onto the enclosing phase's time base; see
    /// <see cref="_progressOffsetSeconds"/>. Defaults to 0, i.e. report absolute file positions.</param>
    /// <param name="sweepingSubFloorSilences">Whether this is a Pass 2.5 sub-floor sweep; see
    /// <see cref="_sweeping"/>.</param>
    internal RegionProber(ProbeEnvironment env, Pass2Context ctx, DetectionRegion region,
        List<DetectedChapter> found, List<DetectedMark> namedFound, LanguageState language,
        double progressOffsetSeconds = 0, bool sweepingSubFloorSilences = false)
    {
        _env = env;
        _ctx = ctx;
        _region = region;
        _mender = new SuspectNumberMender(env, ctx, region);
        _found = found;
        _namedFound = namedFound;
        Language = language;
        _progressOffsetSeconds = progressOffsetSeconds;
        _sweeping = sweepingSubFloorSilences;
        _probeSeconds = env.Options.MaxJingleSeconds > 0 ? ctx.JingleCeilingSeconds : ProbeSecondsPlain;
        _lastNumber = region.LowerNumber > 0 ? region.LowerNumber : null;
        _cacheFrom = region.FromSeconds;
        _threshold = env.Options.MinSilenceSeconds;
    }

    /// <summary>
    /// Probes every candidate of the region in chronological order, stopping early on an
    /// --early-abort or --expected-start-chapter abort. Reports its outcome through
    /// <see cref="EarlyAborted"/> and <see cref="BelowExpectedStartNumber"/>; the marks themselves
    /// land in the accumulator this prober was constructed with.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    internal async Task RunAsync(CancellationToken ct)
    {
        var candidates = BuildCandidates();
        for (var ci = 0; ci < candidates.Count; ci++)
        {
            var candidate = candidates[ci];
            ReportProgress(candidate.Start);

            if (ShouldEarlyAbort(candidate))
                break;
            if (ShouldSkipCandidate(candidate))
                continue;

            var foundNoneYet = _found.Count == 0;
            var plan = new WindowPlan(candidates, ci, WindowEndFor(candidates, ci));
            var probeMarks = await ProbeAsync(candidate, plan, ct);

            if (foundNoneYet && IsBelowExpectedStart())
                break;

            // Recorded after the marks are applied, so it lands in the list that survives them: this
            // window is history for whatever gap a *later* mark reveals, never for its own.
            await ApplyProbeMarksAsync(probeMarks, ct);
            _probedSinceLastMark.Add((candidate, plan.End));
            ci = SkipSettledWindows(candidates, ci, plan.End, probeMarks);
        }
    }

    /// <summary>Reports how far probing has got as the byte-based progress the bar counts in,
    /// translated onto the enclosing phase's time base (see <see cref="_progressOffsetSeconds"/>).
    /// Probe costs vary wildly - full window decode vs. reused overlap vs. skipped candidate - so a
    /// fixed per-probe budget would drift far off; position is honest about <em>where</em> the pass
    /// is, at the price of nonlinear (and, during gap re-probes, briefly backwards) movement.</summary>
    /// <param name="positionSeconds">Absolute position in the file that probing has reached.</param>
    private void ReportProgress(double positionSeconds)
        => _ctx.Work.SetPhaseProgress(
            (long)((positionSeconds + _progressOffsetSeconds) * _ctx.BytesPerSecond));

    /// <summary>
    /// The probe candidates for this region: its own start (mirroring the whole-file case's
    /// start-of-file candidate), plus every silence and - when the VAD pre-pass ran - every VAD
    /// non-speech region whose own candidate position falls inside
    /// [<see cref="DetectionRegion.FromSeconds"/>, <see cref="DetectionRegion.ToSeconds"/>), in
    /// chronological order - bar the region's last second, which a silence candidate is held clear
    /// of: its window would be clamped to under a second of audio, too little to hold an
    /// announcement and enough to cost a Whisper pass finding that out. A window can never decode
    /// past the region end regardless (see
    /// <see cref="GapPlanning.PlanWindowEnd"/>'s duration clamp), so the region boundary alone is
    /// enough containment - no extra check is needed here for that. VAD regions only qualify when
    /// they start at their own jingle start (i.e. nothing else leads them) and are long enough to be
    /// worth observing yet short enough to still be this book's jingle.
    /// <para>
    /// A sub-floor sweep takes the silences and nothing else - see <see cref="_sweeping"/>.
    /// </para>
    /// </summary>
    private List<ProbeCandidate> BuildCandidates()
    {
        var candidates = _sweeping
            ? []
            : new List<ProbeCandidate> { new(_region.FromSeconds, null, null) };
        candidates.AddRange(_ctx.Silences
            .Where(s => s.EndSeconds >= _region.FromSeconds && s.EndSeconds < _region.ToSeconds - 1)
            .Select(s => new ProbeCandidate(s.EndSeconds, s, null)));
        if (_env.Vad == null || _sweeping)
            return candidates;

        foreach (var vadRegion in _ctx.NonSpeechRegions)
        {
            var jingleStart = JingleStart(vadRegion, _ctx.Silences, _ctx.SpeechSegments);
            if (jingleStart != vadRegion.StartSeconds)
                continue;
            if (jingleStart < _region.FromSeconds || jingleStart >= _region.ToSeconds)
                continue;
            var length = vadRegion.EndSeconds - jingleStart;
            if (length < MinJingleObservationSeconds || length > _ctx.JingleCeilingSeconds)
                continue;
            candidates.Add(new ProbeCandidate(jingleStart, null, vadRegion));
        }
        return candidates.OrderBy(c => c.Start).ToList();
    }

    /// <summary>
    /// Where the window of <paramref name="index"/> ends. Computed on the fly, right before that
    /// window's probe runs, rather than pre-planned in bulk: an overlapping neighbor gets the shared
    /// border snapped to a silence mid-point, which moves this window's decode end itself - possibly
    /// past its natural end - rather than merely choosing where to stop reusing cache after the
    /// fact. Deciding per window also keeps every end consistent with the
    /// <see cref="_probeSeconds"/> in effect at that moment, with no stale bulk plan to drift from
    /// what earlier probes actually decoded.
    /// </summary>
    /// <param name="list">The candidate sequence being walked - the region's own, or the skipped
    /// subset a sequence-gap re-probe forms.</param>
    /// <param name="index">Index within <paramref name="list"/>.</param>
    private double WindowEndFor(IReadOnlyList<ProbeCandidate> list, int index)
        => PlanWindowEnd(list[index].Start,
            index + 1 < list.Count ? list[index + 1].Start : null,
            _probeSeconds, _region.ToSeconds, _ctx.AllSilences, _ctx.NonSpeechRegions, _env.Vad != null);

    /// <summary>
    /// How far a decode that must cover up to <c>plan.End</c> is allowed to read on past it: the
    /// furthest end an upcoming candidate's window is already planned to have that still fits inside
    /// the <see cref="WhisperChunkSeconds"/> passes this decode is paying for anyway. The surplus
    /// goes into the overlap cache, so those upcoming windows are served from it outright instead of
    /// each buying a Whisper pass of its own.
    /// <para>
    /// The whole saving rests on the encoder's fixed input: it runs on a 30 s mel whatever it is
    /// handed, so a 6 s tail decode costs exactly what a 30 s one costs. A run of overlapping
    /// candidates therefore pays a full pass per candidate for a stretch that a chunk-sized decode
    /// covers in one. Measured on BARDIOC.m4b's 15.6 h debug log (2026-08-01, 1659 probe decodes):
    /// 1814 encoder passes without reading ahead, 1570 with it, 244 windows served from cache that
    /// had cost a pass each - and not one decode made longer than the passes it already bought.
    /// </para>
    /// <para>
    /// Why the reach is a <em>planned window end</em> and not simply the chunk boundary, although
    /// stopping at the boundary would save a further 124 passes: every planned end is snapped to a
    /// silence or non-speech mid-point (see <see cref="GapPlanning.PlanWindowEnd"/>), and stopping
    /// anywhere else would leave the cache ending mid-speech. A later window reusing it wholesale
    /// never re-reads that audio, so an announcement straddling the cut would be lost on both sides
    /// of it - the one failure this must not buy speed with.
    /// </para>
    /// <para>
    /// Reading ahead is all this does. How much of the surplus is then <em>trusted</em> is
    /// <see cref="CacheableEnd"/>'s question, and the answer is not "all of it": a longer decode is
    /// a worse-framed one, and where the recognizer fell silent the audio has to be read again
    /// rather than reused.
    /// </para>
    /// <para>
    /// Suppressed while a sequence gap re-probes (<see cref="_reprobing"/>). A re-probe's value is a
    /// differently framed second look at audio that has already been read once and yielded nothing;
    /// filling the cache ahead of it would hand some of its candidates the first look's transcript
    /// instead, which is exactly the trade that had to be reverted when gap re-probes last reused a
    /// transcript rather than re-decoding.
    /// </para>
    /// </summary>
    /// <param name="plan">The window being decoded, and the candidates that follow it.</param>
    /// <param name="decodeStart">Absolute time the decode itself starts at - the window start for a
    /// full decode, the overlap split point for a tail decode.</param>
    /// <returns>The absolute time the decode is to run to, never earlier than <c>plan.End</c>.</returns>
    private double ExtendToPlannedSeam(WindowPlan plan, double decodeStart)
    {
        if (_reprobing)
            return plan.End;

        var chunks = Math.Max(1, (int)Math.Ceiling((plan.End - decodeStart) / WhisperChunkSeconds));
        var budget = decodeStart + chunks * WhisperChunkSeconds;
        var reach = plan.End;
        for (var i = plan.Index + 1;
             i < plan.Candidates.Count && plan.Candidates[i].Start < budget;
             i++)
        {
            var end = WindowEndFor(plan.Candidates, i);
            if (end <= budget && end > reach)
                reach = end;
        }
        return reach;
    }

    /// <summary>
    /// --early-abort: once Pass 2 has probed this far into the file's play time without a single
    /// chapter found, give up rather than transcribe the rest of a book that plainly will not yield
    /// any (wrong --chapter-phrase, wrong --lang, or one that announces chapters differently).
    /// </summary>
    /// <param name="candidate">The candidate about to be probed.</param>
    private bool ShouldEarlyAbort(ProbeCandidate candidate)
    {
        // "Nothing found" means no numbered chapter - a lone prologue is not enough to call the
        // file productive, and BuildDetectionResult would discard it anyway. With
        // --ignore-chapter-numbers the chapters themselves land in the named list, so that is what
        // counts instead; otherwise every such run would abort at the threshold regardless.
        var foundSomething = _found.Count > 0 ||
                             (_env.Options.IgnoreChapterNumbers && _namedFound.Count > 0);
        if (candidate.Start < _ctx.EarlyAbortSeconds || foundSomething)
            return false;
        EarlyAborted = true;
        _env.Log?.Invoke($"early-abort: no chapter found within the first " +
                         $"{_env.Options.EarlyAbortMinutes:0.#} minute(s) of play time " +
                         $"(stopped probing at {FormatTimestamp(candidate.Start)})");
        return true;
    }

    /// <summary>
    /// Whether this candidate is passed over without a probe: its silence falls below the
    /// --min-silence-length auto threshold, or its VAD region has since grown too long for the
    /// probe window. A VAD candidate qualified against the window at merge time, but that window
    /// can since have narrowed (--max-jingle-length auto) once a baseline is known - rechecking
    /// here keeps probing skipping regions too long to be this book's jingle, same as the
    /// merge-time filter intends after the baseline exists. Either way the candidate is remembered
    /// for a possible sequence-gap re-probe.
    /// <para>
    /// A sub-floor sweep skips nothing: its candidate list <em>is</em> the set it means to probe
    /// (see <see cref="_sweeping"/>).
    /// </para>
    /// </summary>
    /// <param name="candidate">The candidate to judge.</param>
    private bool ShouldSkipCandidate(ProbeCandidate candidate)
    {
        if (_sweeping)
            return false;
        var belowThreshold = _env.Options.AutoMinSilence && candidate.Silence is { } silence &&
                             silence.EndSeconds - silence.StartSeconds < _threshold;
        var vadTooLong = candidate.VadRegion is { } vadRegion &&
                         vadRegion.EndSeconds - candidate.Start > _probeSeconds;
        if (!belowThreshold && !vadTooLong)
            return false;
        _skippedSinceLastMark.Add(candidate);
        return true;
    }

    /// <summary>
    /// --expected-start-chapter's abort half, consulted right after the probe that found the very
    /// first chapter of a fresh run - whether it added one match or several, the one case the
    /// option cares about. A later, lower in-text mention never reaches here at all, already
    /// rejected inside the probe as not topping the last accepted number.
    /// </summary>
    /// <returns>True when detection is to be abandoned for this file, in which case the finds so
    /// far have been discarded.</returns>
    private bool IsBelowExpectedStart()
    {
        if (_ctx.ExpectedStartChapter is not { } expected || _found.Count == 0 || _found[0].Number >= expected)
            return false;
        BelowExpectedStartNumber = _found[0].Number;
        _env.Log?.Invoke($"expected-start-chapter: first chapter found is {_found[0].Number}, " +
                         $"below the expected start of {expected} - aborting detection for this file");
        _found.Clear();
        return true;
    }

    /// <summary>
    /// Probes a single window and appends every chapter mark found in it to the accumulator. Since
    /// segment timestamps plus the full stored silence list let every detection be pinpointed
    /// independently of the triggering candidate, one window can yield several marks (e.g. a wide
    /// jingle window covering two transitions) - there is no one-chapter-per-window early return.
    /// </summary>
    /// <param name="candidate">The candidate whose window to probe. Its start stays the semantic
    /// anchor for the phrase-timing rule and for progress, both of which are relative to the
    /// triggering silence rather than to whatever seam the window plan chose.</param>
    /// <param name="plan">The window to probe: its <em>planned</em> end (see
    /// <see cref="WindowEndFor"/>), possibly snapped away from the natural start plus
    /// <see cref="_probeSeconds"/>, and the candidates that follow it, which only the decode's
    /// read-ahead looks at (see <see cref="ExtendToPlannedSeam"/>). Everything below this scans the
    /// planned window and nothing beyond it, whatever the decode read.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The accepted marks in window order.</returns>
    private async Task<List<ProbeMark>> ProbeAsync(
        ProbeCandidate candidate, WindowPlan plan, CancellationToken ct)
    {
        var start = candidate.Start;
        var windowEnd = plan.End;
        ct.ThrowIfCancellationRequested();
        // Position-based Pass 2 progress (see DetectCoreAsync's BeginPhase); reported here rather
        // than only in the candidate loop so gap re-probes show their (backwards) position too.
        ReportProgress(start);

        var (windowSegmentsAbs, mergeBoundarySegIndex) =
            await AssembleWindowTranscriptAsync(start, plan, ct);

        // Correct segment starts Whisper timestamped from a leading silence/jingle before shifting
        // to window-relative time (the cache keeps the raw absolute timings its reuse math needs).
        // The absolute trimmed transcript stays around for ResolveJingleAnchor's narration-aware
        // jingle edge adjustment.
        var trimmedAbs = TrimLeadingNonSpeech(
            windowSegmentsAbs, _ctx.AllSilences, _ctx.NonSpeechRegions, _env.Vad != null);
        var segments = ShiftSegments(trimmedAbs, -start);

        var namedBefore = _namedFound.Count;
        var marks = await ScanWindowForMarksAsync(
            candidate, start, windowEnd, segments, trimmedAbs, mergeBoundarySegIndex, ct);

        // A window that yielded nothing at all - no chapter, no prologue, no --custom mark - is the
        // only one worth a second look; one that produced a mark has already told this candidate's
        // story.
        if (marks.Count == 0 && _namedFound.Count == namedBefore)
            marks = await RereadJingleSpeechAsync(candidate, start, windowEnd, trimmedAbs, ct);
        if (marks.Count == 0)
            marks = await RecoverUnnumberedAnnouncementsAsync(
                candidate, start, windowEnd, segments, trimmedAbs, ct);
        return marks;
    }

    /// <summary>
    /// Second, short look at a probe window that heard no announcement while VAD insists there was
    /// speech inside its jingle - the one shape in which "nothing here" is contradicted by evidence
    /// the tool already holds. By this codebase's own working assumption the only speech inside a
    /// jingle <em>is</em> the announcement (see
    /// <see cref="JingleGeometry.RefineDefaultMark"/>'s remarks), so a VAD speech blip there that no
    /// transcript segment has any words for means the recognizer lost it rather than that it is not
    /// there.
    /// <para>
    /// Losing it is a framing artifact before it is anything else: crossing
    /// <see cref="WhisperChunkSeconds"/> is what does it (see that constant for Gruelfin.m4b's
    /// prologue, the case on record), so the re-read asks for the same announcement inside a
    /// single-pass window. Confined to windows that actually crossed the boundary, since below it
    /// the second decode would be the same framing as the first and could only produce the same
    /// answer - and, where the run has a <c>--pass3-model</c> upgrade, put through that recognizer
    /// rather than the probing one, for the reason the decode itself documents.
    /// </para>
    /// <para>
    /// The re-read window ends a phrase margin past the blip - far enough for the number after
    /// "Kapitel" and no further, because everything beyond it is narration competing for the decode -
    /// and reaches back to the candidate, or one <see cref="JingleRereadWindowSeconds"/>, whichever
    /// is shorter. Its transcript then goes through the ordinary window scan, so every acceptance
    /// rule (anchoring, sequence, named-phrase scope, dedupe) applies to a re-read mark exactly as it
    /// would to a first-pass one.
    /// </para>
    /// </summary>
    /// <param name="candidate">The candidate whose window came back empty.</param>
    /// <param name="start">Absolute start of that window.</param>
    /// <param name="windowEnd">Absolute planned end of that window.</param>
    /// <param name="trimmedAbs">Its transcript in absolute file time, already corrected for leading
    /// non-speech - which is what makes "no words for this blip" answerable at all, since an
    /// untrimmed segment routinely claims to start back at the window's own beginning.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The marks the re-read produced, or an empty list when it did not run or found
    /// nothing.</returns>
    private async Task<List<ProbeMark>> RereadJingleSpeechAsync(
        ProbeCandidate candidate, double start, double windowEnd,
        List<TranscriptSegment> trimmedAbs, CancellationToken ct)
    {
        if (_env.Vad == null || windowEnd - start <= WhisperChunkSeconds)
            return [];
        if (FindUnheardJingleSpeech(start, windowEnd, trimmedAbs) is not { } blip)
            return [];

        var to = Math.Min(windowEnd, blip.EndSeconds + PhraseMarginSeconds);
        var from = Math.Max(start, to - JingleRereadWindowSeconds);
        if (to - from <= PhraseMarginSeconds)
            return [];

        // Both remedies at once where the run has an upgrade model: a re-framed window and a better
        // recognizer. They address different halves of the same failure - the framing lost the
        // announcement, but what makes an announcement droppable in the first place is that it is
        // one or two quiet words against a jingle, which is exactly where model size tells (see
        // PreciseMarkRefiner.RefinePreciseMarkAsync's own upgrade retry, where the probe that broke
        // each search read "* Musik *" on the small model and the announcement on the large one).
        // Costs nothing extra: this decode was going to happen either way, so the only difference is
        // which recognizer it goes through - unlike the mender's second opinion, which is a decode
        // of its own. A pass 2.5 re-probe reaches this with SecondOpinion null and _ctx.Transcriber
        // already the heavier model, so it re-reads through that one without needing a branch here.
        var upgradeLanguage = _env.SecondOpinion != null ? Language.Profile.Language : null;
        _env.Log?.Invoke(
            $"nothing heard in the window at {FormatTimestamp(start)}, but VAD hears speech at " +
            $"{FormatTimestamp(blip.StartSeconds)} inside its jingle - re-reading it in a shorter " +
            (upgradeLanguage == null ? "window" : "window with the --pass3-model recognizer"));

        var samples = await _env.Audio.DecodePcmAsync(
            _ctx.File, from, to - from, _ctx.Info.InputDecoder, ct);
        var fresh = upgradeLanguage is { } language
            ? await _env.SecondOpinion!(samples, language, ct)
            : await _env.TranscribeCounting(samples, ct, _ctx.Transcriber);
        _env.LogTranscript($"jingle re-read {to - from:0.0}s@{FormatTimestamp(from)}", fresh);

        var freshAbs = TrimLeadingNonSpeech(
            ShiftSegments(fresh, from), _ctx.AllSilences, _ctx.NonSpeechRegions, true);
        return await ScanWindowForMarksAsync(
            candidate, from, to, ShiftSegments(freshAbs, -from), freshAbs, null, ct);
    }

    /// <summary>
    /// The first VAD speech segment inside the window that sits within one of its jingle regions and
    /// that the transcript covers with no segment at all - <see cref="RereadJingleSpeechAsync"/>'s
    /// trigger. Blips below <see cref="TransientSpeechFloorSeconds"/> are passed over for the same
    /// reason <see cref="JingleGeometry.AdvancePastNonSpeech"/> passes over them: a jingle's musical
    /// transients cross VAD's threshold too, and too short to be a spoken word is the one thing that
    /// tells them apart.
    /// <para>
    /// Known limit of "no segment covers it", accepted rather than fixed: Whisper routinely stretches
    /// a window's last segment's end timestamp far past the words in it, and such a segment then
    /// covers a jingle it has no words for. Observed on a BARDIOC.m4b clip around chapter 21
    /// (2026-07-30), where a 56 s window lost "Kapitel 21" exactly as Gruelfin's did while a 25 s one
    /// read it cleanly, but a segment reading "können." claimed 41.08-54.76 and so vetoed the
    /// re-read. Loosening this to "no segment <em>starts</em> inside the region" would catch it, at
    /// the price of firing on most empty long windows in the file - the stretch is that common - so
    /// the cheap, strict test stays until something measures that trade honestly.
    /// </para>
    /// </summary>
    /// <param name="start">Absolute start of the probe window.</param>
    /// <param name="windowEnd">Absolute planned end of the probe window.</param>
    /// <param name="trimmedAbs">The window's transcript in absolute file time.</param>
    private SpeechSegment? FindUnheardJingleSpeech(
        double start, double windowEnd, List<TranscriptSegment> trimmedAbs)
        => _ctx.SpeechSegments
            .Where(b => b.StartSeconds >= start && b.EndSeconds <= windowEnd &&
                        b.EndSeconds - b.StartSeconds >= TransientSpeechFloorSeconds)
            .Where(b => _ctx.NonSpeechRegions.Any(
                r => r.StartSeconds < b.StartSeconds && r.EndSeconds > b.EndSeconds))
            .Where(b => !trimmedAbs.Any(
                s => s.StartSeconds < b.EndSeconds && s.EndSeconds > b.StartSeconds))
            .Cast<SpeechSegment?>()
            .FirstOrDefault();

    /// <summary>
    /// Produces the probe window's full transcript in absolute file time, assembled from the
    /// previous window's cache (overlap reuse), a fresh Whisper decode, or a mix. The whole window
    /// is always represented, so nothing a reuse-only "search just the new tail" scheme would
    /// silently drop - e.g. a phrase the previous probe rejected for want of a qualifying anchor
    /// that this window can anchor - is ever lost.
    /// <para>
    /// --verbose logging only ever shows what Whisper actually transcribed just now, at its own
    /// (0-based) timestamps - never the reused portion restated at window-relative time, which would
    /// make every probe look like a fresh full-window decode even when most of it was cache. What
    /// the phrase matching then sees is unaffected; only what gets logged changes.
    /// </para>
    /// </summary>
    /// <param name="start">Absolute start of the window.</param>
    /// <param name="plan">The window being probed, and the candidates that follow it - the latter
    /// only so a decode can read ahead into them (see <see cref="ExtendToPlannedSeam"/>).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The window transcript, plus - for a partial-overlap assembly - the index of its
    /// first fresh segment, so a detection drawing on text from both sides of the cache/fresh
    /// boundary can be flagged (see <see cref="PhraseMatch.SpansMerge"/>); null when the window is
    /// entirely one or the other.</returns>
    private async Task<(List<TranscriptSegment> Segments, int? MergeBoundarySegIndex)>
        AssembleWindowTranscriptAsync(double start, WindowPlan plan, CancellationToken ct)
    {
        // A window whose start falls outside the cached span has no usable overlap.
        if (start < _cacheFrom || start >= _cacheTo)
            return (await DecodeFullWindowAsync(start, plan, ct), null);

        if (plan.End <= _cacheTo)
            // Fully contained in the cached span: reuse its transcript wholesale, no Whisper at all.
            // The (larger) cache is deliberately left untouched so a later candidate starting within
            // it can keep reusing it too.
            return (WindowSlice(_cacheSegmentsAbs, start, plan.End), null);

        return await DecodeOverlapTailAsync(start, plan, ct);
    }

    /// <summary>
    /// The part of a decoded or cached span that belongs to one window: the segments starting inside
    /// it. Every decode may run past the window it was asked for (see
    /// <see cref="ExtendToPlannedSeam"/>), and the surplus belongs to the cache alone - letting it
    /// through to the scan would place marks from audio beyond the window the candidate was probed
    /// with, at a candidate's window width that nothing planned.
    /// </summary>
    /// <param name="segmentsAbs">A decoded or cached span, in absolute file time.</param>
    /// <param name="start">Absolute start of the window.</param>
    /// <param name="windowEnd">Absolute planned end of the window.</param>
    private static List<TranscriptSegment> WindowSlice(
        List<TranscriptSegment> segmentsAbs, double start, double windowEnd)
        => segmentsAbs.Where(s => s.StartSeconds >= start && s.StartSeconds < windowEnd).ToList();

    /// <summary>
    /// Transcribes a whole window from scratch and makes it the new cache. For a fresh run this is
    /// also where --lang auto resolves the language, once, from the very first probe's full
    /// samples; a gap-scoped run arrives with the profile already set, so this never re-resolves it.
    /// <para>
    /// Read-ahead reaches the language resolution too, harmlessly: Whisper detects from the first
    /// mel frame either way, so a window that already spanned a whole
    /// <see cref="WhisperChunkSeconds"/> sees exactly what it saw before, and a shorter one now has
    /// real audio where it had zero padding.
    /// </para>
    /// </summary>
    /// <param name="start">Absolute start of the window.</param>
    /// <param name="plan">The window being probed; see <see cref="AssembleWindowTranscriptAsync"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<List<TranscriptSegment>> DecodeFullWindowAsync(
        double start, WindowPlan plan, CancellationToken ct)
    {
        var decodeEnd = ExtendToPlannedSeam(plan, start);
        var samples = await _env.Audio.DecodePcmAsync(
            _ctx.File, start, decodeEnd - start, _ctx.Info.InputDecoder, ct);
        var fresh = await _env.TranscribeCounting(samples, ct, _ctx.Transcriber);
        var freshAbs = ShiftSegments(fresh, start);
        var cacheEnd = CacheableEnd(freshAbs, plan.End, decodeEnd);
        _env.LogTranscript(
            $"probe {decodeEnd - start:0.0}s@{FormatTimestamp(start)}" +
            ProbeNote(false, decodeEnd, plan.End, cacheEnd), fresh);
        return WindowSlice(CacheWindow(freshAbs, start, cacheEnd), start, plan.End);
    }

    /// <summary>
    /// Partial overlap: cuts between the reused cache and a fresh tail decode. The previous decode
    /// stopped at a planned window end, i.e. a seam snapped to a silence mid-point (see
    /// <see cref="GapPlanning.PlanWindowEnd"/>) - its own window's, or a later candidate's when it
    /// read ahead - so the cache normally ends exactly at a seam and the split search re-finds it at
    /// distance zero: the fresh decode starts right where the previous one stopped, stitching the
    /// transcripts together word-safely with nothing re-decoded and nothing dropped. It genuinely
    /// decides only for overlaps that plan did not anticipate (a probe-window resize in between),
    /// snapping to the best seam still covered by the cache; the border fallback means no seam
    /// exists, and hence no chapter transition in the overlap.
    /// </summary>
    /// <param name="start">Absolute start of the window.</param>
    /// <param name="plan">The window being probed; see <see cref="AssembleWindowTranscriptAsync"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<(List<TranscriptSegment> Segments, int? MergeBoundarySegIndex)>
        DecodeOverlapTailAsync(double start, WindowPlan plan, CancellationToken ct)
    {
        var splitPoint = FindOverlapSplitPoint(
            start, _cacheTo, plan.End, _ctx.AllSilences, _ctx.NonSpeechRegions, _env.Vad != null,
            allowBeyondBorder: false);
        var decodeEnd = ExtendToPlannedSeam(plan, splitPoint);
        var samples = await _env.Audio.DecodePcmAsync(
            _ctx.File, splitPoint, decodeEnd - splitPoint, _ctx.Info.InputDecoder, ct);
        var fresh = await _env.TranscribeCounting(samples, ct, _ctx.Transcriber);
        var reused = _cacheSegmentsAbs
            .Where(s => s.StartSeconds >= start && s.StartSeconds < splitPoint).ToList();
        List<TranscriptSegment> assembledAbs = [.. reused, .. ShiftSegments(fresh, splitPoint)];
        var cacheEnd = CacheableEnd(assembledAbs, plan.End, decodeEnd);
        _env.LogTranscript(
            $"probe {decodeEnd - splitPoint:0.0}s@{FormatTimestamp(splitPoint)}" +
            ProbeNote(true, decodeEnd, plan.End, cacheEnd), fresh);
        var assembled = CacheWindow(assembledAbs, start, cacheEnd);
        return (WindowSlice(assembled, start, plan.End), reused.Count);
    }

    /// <summary>The parenthetical a probe's --verbose line carries, so a log reader can tell what
    /// the decode was: an overlap tail rather than a whole window, how much of it ran past the
    /// window the candidate was actually probed with (that surplus is cache for the windows to
    /// come, never something this candidate's scan saw), and how much of <em>that</em> the
    /// recognizer left untranscribed and so will be read again rather than reused.</summary>
    /// <param name="tail">Whether this decode was an overlap tail.</param>
    /// <param name="decodeEnd">Absolute end of what was decoded.</param>
    /// <param name="windowEnd">Absolute planned end of the window it was decoded for.</param>
    /// <param name="cacheEnd">Absolute end of what the decode may be trusted for; see
    /// <see cref="CacheableEnd"/>.</param>
    private static string ProbeNote(bool tail, double decodeEnd, double windowEnd, double cacheEnd)
    {
        // Below a tenth of a second the read-ahead is a rounding artifact of the seam search, not
        // audio anyone gained; saying "+0.0s ahead" would only make every line noisier.
        var ahead = decodeEnd - windowEnd >= 0.05 ? $"+{decodeEnd - windowEnd:0.0}s ahead" : null;
        // Worth naming, because a log reader cannot see it otherwise: the transcript simply stops,
        // and only arithmetic against the decode length shows that it did. This is the shape
        // CacheableEnd exists for, so a run in which it never appears is a run in which the rule
        // never bit.
        var uncached = decodeEnd - cacheEnd >= 0.05 ? $"{decodeEnd - cacheEnd:0.0}s uncached" : null;
        var notes = new[] { tail ? "tail" : null, ahead, uncached }.Where(n => n != null);
        return string.Join(", ", notes) is { Length: > 0 } joined ? $" ({joined})" : "";
    }

    /// <summary>
    /// How far a decode may be trusted, i.e. how much of it becomes overlap cache: as far as the
    /// recognizer actually got, and never past that into audio it read but said nothing about.
    /// <para>
    /// A transcript that stops short of the decode end has not established silence there, it has
    /// failed to read it - and the cache cannot tell those apart, so a later window served from that
    /// stretch inherits the failure instead of decoding the audio itself with its own, better
    /// framing. "BARDIOC.m4b" (2026-08-02) is the case on record: the "Zeittafel" announcement at
    /// 0:00:51 was lost for a whole run. The probe at 0:00:00 planned a window to 0:00:40.73 and read
    /// ahead to 0:00:54.19; handed 54 s beginning with ~20 s of jingle music, Whisper stopped
    /// emitting at 0:00:37. The unread 17 s went into the cache as if empty, the next two candidates
    /// (a VAD jingle region at 0:00:19.48, a silence end at 0:00:43.33) were both served from it, and
    /// the next fresh decode began at 0:00:54.19 - past the phrase. Replayed through the real decoder
    /// and recognizer, the tail decode this rule restores (0:00:40.73-0:01:10.69) reads "Zeittafel
    /// 1971 bis 1984..." at p=0.72, and so do windows framed from 0:00:43.33 and 0:00:46. Nothing
    /// else could have caught it: Silero hears no speech at all between 0:00:23.3 and 0:00:54.56 at
    /// any threshold from 0.50 to 0.70, so the announcement is not a blip
    /// <see cref="RereadJingleSpeechAsync"/> could have found either.
    /// </para>
    /// <para>
    /// Floored at the window's own planned end, never below: the scan has already covered that far,
    /// and a planned end is a snapped seam (see <see cref="GapPlanning.PlanWindowEnd"/>), which is
    /// what the next overlap's split search expects to find. So this can only ever give back the
    /// read-ahead surplus - the worst case for a window is the behaviour it had before reading ahead
    /// existed, and the saving survives untouched wherever the recognizer did fill the audio it was
    /// handed.
    /// </para>
    /// <para>
    /// The floor leaves one residual, deliberately: a window whose own decode came back empty still
    /// caches its planned end as read. That is not the read-ahead's doing but the overlap cache's
    /// original bargain, unchanged since before it - re-deciding it would re-decode every quiet
    /// stretch in a book, which wants a measurement this fix does not have.
    /// </para>
    /// </summary>
    /// <param name="segmentsAbs">The decode's transcript, in absolute file time.</param>
    /// <param name="windowEnd">Absolute planned end of the window it was decoded for.</param>
    /// <param name="decodeEnd">Absolute end of what was decoded.</param>
    private static double CacheableEnd(
        List<TranscriptSegment> segmentsAbs, double windowEnd, double decodeEnd)
    {
        // Max rather than the last segment's end: Whisper's segment ends are not strictly ordered
        // once a window re-segments, and an end that overshoots the audio it was given is common
        // enough that the decode end has to cap it.
        var transcribedTo = segmentsAbs.Count == 0 ? windowEnd : segmentsAbs.Max(s => s.EndSeconds);
        return Math.Min(decodeEnd, Math.Max(windowEnd, transcribedTo));
    }

    /// <summary>Makes a freshly assembled transcript the overlap cache, and returns it unchanged so
    /// the callers can assemble and cache in one expression. The segments kept are all of them, the
    /// span claimed only as far as <see cref="CacheableEnd"/> allows - which reaches past the window
    /// whenever the decode read ahead and was transcribed that far. Slicing back down to the window
    /// is <see cref="WindowSlice"/>'s job, at the caller.</summary>
    /// <param name="segments">The transcript, in absolute file time.</param>
    /// <param name="start">Absolute start of the span it covers.</param>
    /// <param name="spanEnd">Absolute end of that span, from <see cref="CacheableEnd"/>.</param>
    private List<TranscriptSegment> CacheWindow(List<TranscriptSegment> segments, double start, double spanEnd)
    {
        _cacheSegmentsAbs = segments;
        _cacheFrom = start;
        _cacheTo = spanEnd;
        return segments;
    }

    /// <summary>Finds every chapter announcement in one decoded window and turns the acceptable
    /// ones into marks.</summary>
    /// <param name="candidate">The candidate whose window this is.</param>
    /// <param name="start">Absolute start of the window.</param>
    /// <param name="windowEnd">Absolute planned end of the window - what precise marking
    /// anchors its search against (see <see cref="MarkContext.Transcript"/>).</param>
    /// <param name="segments">The window transcript in window-relative time, for phrase matching.</param>
    /// <param name="trimmedAbs">The same transcript in absolute file time, for the jingle edge
    /// adjustment inside <see cref="JingleGeometry.ResolveJingleAnchor"/>.</param>
    /// <param name="mergeBoundarySegIndex">The cache/fresh boundary, if any; see
    /// <see cref="AssembleWindowTranscriptAsync"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<List<ProbeMark>> ScanWindowForMarksAsync(
        ProbeCandidate candidate, double start, double windowEnd, List<TranscriptSegment> segments,
        List<TranscriptSegment> trimmedAbs, int? mergeBoundarySegIndex, CancellationToken ct)
    {
        var marks = new List<ProbeMark>();
        // Window-local continuation of _lastNumber: several accepted marks within one window must
        // each top the previous one, exactly as consecutive windows' marks do.
        var windowLast = _lastNumber ?? 0;

        // The prologue's own scope closes the moment the first numbered chapter is accepted, so the
        // named scan runs first: a window holding both the prologue announcement and chapter 1
        // (a short front matter, or a wide jingle window) must still yield the prologue.
        await ScanWindowForNamedMarksAsync(candidate, start, windowEnd, segments, trimmedAbs, ct);

        // With --ignore-chapter-numbers a chapter is just another titled position, so it goes down
        // the same path the prologue does and nothing below this point applies to it.
        if (_env.Options.IgnoreChapterNumbers)
            return marks;

        foreach (var heard in _env.FindCappedPhraseMatches(segments, Language.Profile, mergeBoundarySegIndex))
        {
            var match = heard;
            var phraseAbs = start + match.PhraseStartSeconds;
            // Ahead of the sequence check rather than after it, because a number that fails that check
            // is exactly one of the two shapes worth questioning: a mishearing downwards is
            // indistinguishable from an in-text mention until the audio is asked again. A mend that
            // finds nothing leaves the reading untouched, so the check below then does what it always
            // did - including rejecting it.
            if (_ctx.SecondGuessNumbers &&
                await _mender.MendAsync(
                    match, Language.Profile, start, windowEnd, SequenceBounds(windowLast), ct) is { } mended)
                match = match with { Number = mended };
            if (IsOutOfSequence(match, phraseAbs, windowLast))
                continue;
            if (await AcceptMatchAsync(
                    match, candidate, start, windowEnd, phraseAbs, trimmedAbs, windowLast, ct)
                is not { } mark)
                continue;
            marks.Add(mark);
            windowLast = mark.Number;
        }

        return marks;
    }

    /// <summary>
    /// The stretch of the chapter sequence a fresh announcement is judged against
    /// (<see cref="SuspectNumberMender"/>, <see cref="RefinedNumberVote"/>).
    /// <para>
    /// The lower bound is normally the last number accepted; before this region has one, the sequence
    /// is expected to begin at --expected-start-chapter (or chapter 1), so the number below that
    /// expectation plays the same role. Without that, the one mishearing that costs the most - the
    /// file's <em>first</em> chapter read as some large number, which declares everything before it
    /// missing and sends Pass 3 across the whole book - would be the one case never questioned.
    /// </para>
    /// <para>
    /// The upper bound exists only while something is known to follow: a --verify gap region's
    /// <see cref="DetectionRegion.UpperNumber"/>, or - during a sequence-gap re-probe - the chapter
    /// that revealed the gap (<see cref="_gapAbove"/>). Both turn the question from "could the
    /// sequence continue like this?" into "can this hole hold that number?", which for a
    /// one-chapter hole has exactly one answer.
    /// </para>
    /// </summary>
    /// <param name="windowLast">The highest number accepted so far in this window's sequence, or 0
    /// when there is none.</param>
    internal NumberBounds SequenceBounds(int windowLast)
        => new(windowLast > 0 ? windowLast : (_env.Options.ExpectedStartChapter ?? 1) - 1,
               _gapAbove ?? _region.UpperNumber);

    /// <summary>
    /// Reports the announcements this window heard but could not number, queues the window for the
    /// sequence-gap re-probe, and asks <see cref="SuspectNumberMender.ReadUnnumberedAsync"/> to read
    /// the number out of differently framed audio - turning the announcement into an ordinary mark
    /// when it succeeds. Only ever called for a window that produced no mark of its own - counting
    /// <see cref="RereadJingleSpeechAsync"/>'s second look as this window's own, since a window it
    /// rescued was never a window that heard nothing. With a mark, a further bare "chapter" in the
    /// same transcript is prose, not a missed announcement.
    /// <para>
    /// Does nothing under --ignore-chapter-numbers, where an announcement without a number is the
    /// normal case and has already been marked as a named one.
    /// </para>
    /// <para>
    /// Every unreadable announcement is logged whether or not its re-read succeeds, and the window
    /// is queued regardless: the queue costs nothing until a gap actually appears, and the cases the
    /// re-read cannot fix - a word ordinal past a language's parser, a number above 999 - are
    /// exactly the ones where knowing the phrase was heard and discarded saves the next
    /// investigation. The re-probe that queue feeds re-decodes at the full ceiling window (see
    /// <see cref="ReprobeGapCandidatesAsync"/>), which is a different framing again, so the two
    /// recoveries overlap without duplicating: chapter 13 of "I Shall Wear Midnight" was read as
    /// "CHAPTER XIII" from the 16.1 s window it was probed with and as "Chapter 13" from a 48.8 s one
    /// over the same announcement (2026-07-30), and either route now reaches that.
    /// </para>
    /// <para>
    /// A mark the re-read produces goes through <see cref="AcceptMatchAsync"/> like any other, at the
    /// position and confidence of the reading that first heard it - the re-read contributes the
    /// number and nothing else. Bounded by <see cref="MaxUnnumberedMendsPerRegion"/>; the logging and
    /// queuing continue past that cap, only the decodes stop.
    /// </para>
    /// </summary>
    /// <param name="candidate">The candidate whose window this is.</param>
    /// <param name="start">Absolute start of the window.</param>
    /// <param name="windowEnd">Absolute planned end of the window - what the upgrade-model re-read
    /// re-decodes, and what precise marking anchors its search against.</param>
    /// <param name="segments">The window transcript, in window-relative time.</param>
    /// <param name="trimmedAbs">The same transcript in absolute file time, for the jingle anchor.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The marks the re-reads produced, empty when none did.</returns>
    private async Task<List<ProbeMark>> RecoverUnnumberedAnnouncementsAsync(
        ProbeCandidate candidate, double start, double windowEnd, List<TranscriptSegment> segments,
        List<TranscriptSegment> trimmedAbs, CancellationToken ct)
    {
        var marks = new List<ProbeMark>();
        if (_env.Options.IgnoreChapterNumbers)
            return marks;

        var queued = false;
        var windowLast = _lastNumber ?? 0;
        foreach (var heard in FindUnnumberedAnnouncements(segments, Language.Profile))
        {
            var phraseAbs = start + heard.PhraseStartSeconds;
            _env.Log?.Invoke(
                $"heard the chapter phrase at {FormatTimestamp(phraseAbs)} " +
                $"but could not read a number from it: \"{heard.Text}\"");
            if (!queued)
            {
                _skippedSinceLastMark.Add(candidate);
                queued = true;
            }

            if (_unnumberedMends >= MaxUnnumberedMendsPerRegion)
                continue;
            _unnumberedMends++;
            if (await _mender.ReadUnnumberedAsync(
                    heard, Language.Profile, start, windowEnd, SequenceBounds(windowLast), ct)
                is not { } number)
                continue;

            var match = new PhraseMatch(
                number, heard.PhraseStartSeconds, heard.PhraseEndSeconds, heard.Confidence);
            if (IsOutOfSequence(match, phraseAbs, windowLast))
                continue;
            if (await AcceptMatchAsync(
                    match, candidate, start, windowEnd, phraseAbs, trimmedAbs, windowLast, ct)
                is not { } mark)
                continue;
            marks.Add(mark);
            windowLast = mark.Number;
        }
        return marks;
    }

    /// <summary>
    /// Finds the prologue/epilogue announcements in one decoded window - plus the chapter
    /// announcements themselves under <c>--ignore-chapter-numbers</c> - and turns the in-scope ones
    /// into named marks. Kept apart from the numbered scan because nothing these produce takes part
    /// in the chapter sequence: no jingle length is observed and no window sequence is settled - a
    /// named mark is a title at a position and nothing more, so it must not steer machinery that
    /// reasons about consecutive chapters. It does feed the adaptive silence threshold, which is not
    /// sequence reasoning but a statement about how this book separates its sections, and which
    /// starves into probing every candidate in the file if only numbered chapters may feed it.
    /// </summary>
    /// <param name="candidate">The candidate whose window this is.</param>
    /// <param name="start">Absolute start of the window.</param>
    /// <param name="windowEnd">Absolute planned end of the window - what precise marking
    /// anchors its search against (see <see cref="MarkContext.Transcript"/>).</param>
    /// <param name="segments">The window transcript in window-relative time, for phrase matching.</param>
    /// <param name="trimmedAbs">The same transcript in absolute file time, for the jingle anchor.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task ScanWindowForNamedMarksAsync(
        ProbeCandidate candidate, double start, double windowEnd, List<TranscriptSegment> segments,
        List<TranscriptSegment> trimmedAbs, CancellationToken ct)
    {
        foreach (var match in FindNamedMatches(segments, Language.Profile))
        {
            if (!IsInScope(match.Phrase))
                continue;
            await AcceptNamedMatchAsync(match, candidate, start, windowEnd, trimmedAbs, ct);
        }

        if (!_env.Options.IgnoreChapterNumbers)
            return;

        // After the prologue/epilogue pass, so that a window holding both a scoped announcement and
        // a chapter still resolves the scoped one against the chapter count it had on arrival.
        foreach (var match in FindChapterAnnouncements(segments, Language.Profile))
            await AcceptNamedMatchAsync(match, candidate, start, windowEnd, trimmedAbs, ct);
    }

    /// <summary>
    /// Whether a named phrase may become a mark at this point of the file, judged purely by how
    /// many chapters are known so far - see <see cref="NamedPhraseScope"/> for why that is the only
    /// usable landmark. Rejections are silent: unlike a numbered match, which was plainly heard and
    /// whose disappearance is worth explaining, "epilogue" turning up in the middle of a book is an
    /// ordinary word in ordinary prose and logging every occurrence would drown the log.
    /// </summary>
    /// <param name="phrase">The phrase that matched.</param>
    private bool IsInScope(NamedPhrase phrase) => phrase.Scope switch
    {
        NamedPhraseScope.Anywhere => true,
        NamedPhraseScope.BeforeFirstChapter => ChaptersSoFar == 0,
        _ => ChaptersSoFar > 0,
    };

    /// <summary>How many chapter announcements this region has accepted so far - the landmark both
    /// positional <see cref="NamedPhraseScope"/>s are measured against. Under
    /// <c>--ignore-chapter-numbers</c> chapters live in the named list rather than in the numbered
    /// one, and counting only the latter would leave the epilogue's scope shut for the whole
    /// file.</summary>
    private int ChaptersSoFar => _env.Options.IgnoreChapterNumbers
        ? _namedFound.Count(m => m.Kind == ChapterKind)
        : _found.Count;

    /// <summary>Seconds every default-mode mark is placed ahead of the announcement onset
    /// (<c>--mark-lead</c>), named once here because all four placement paths below must agree on
    /// it - <see cref="JingleGeometry.RefineDefaultMark"/>'s no-op case depends on the value that
    /// produced its input.</summary>
    private double MarkLead => _env.Options.MarkLeadSeconds;

    /// <summary><see cref="NamedPhrase.Kind"/> of the synthetic chapter phrase, the one named kind
    /// that is exempt from the <c>--custom</c> mark cap.</summary>
    private string ChapterKind => Language.Profile.ChapterAnnouncement.Kind;

    /// <summary>
    /// Places, logs and records one in-scope named match - unless <see cref="ShouldDropNamedMatch"/>
    /// says this one adds nothing, which is checked first so a dropped match costs no mark placement
    /// at all (that is where the refinement transcriptions are spent).
    /// <para>
    /// A non-repeatable phrase replaces any earlier mark of its own kind, so the last match within
    /// the scope wins rather than the first: front matter routinely mentions what is coming
    /// ("...gelesen von...; Prolog") before the narrator actually announces it, and the real
    /// announcement is by construction the later of the two - whereas nothing follows the genuine
    /// one inside its own scope, which the prologue's closes at chapter 1 and the epilogue's at the
    /// end of the file. The replaced mark's own placement work is simply discarded; at one prologue
    /// and one epilogue per book that costs at most a couple of extra refinement transcriptions.
    /// </para>
    /// </summary>
    /// <param name="match">The named match, in window-relative time.</param>
    /// <param name="candidate">The candidate whose window this probe decoded.</param>
    /// <param name="start">Absolute start of that window.</param>
    /// <param name="windowEnd">Absolute planned end of the window - what precise marking
    /// anchors its search against (see <see cref="MarkContext.Transcript"/>).</param>
    /// <param name="trimmedAbs">The window's transcript in absolute file time.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task AcceptNamedMatchAsync(
        NamedMatch match, ProbeCandidate candidate, double start, double windowEnd,
        List<TranscriptSegment> trimmedAbs, CancellationToken ct)
    {
        var phraseAbs = start + match.PhraseStartSeconds;
        if (ShouldDropNamedMatch(match.Phrase, phraseAbs))
            return;

        // Same reasoning as TightenThreshold's _lastNumber guard: the silence before the region's
        // very first mark is front matter's, routinely far longer than any break between sections,
        // and adopting it alone would raise the threshold past every real candidate that follows.
        var teachesThreshold = _found.Count > 0 || _namedFound.Count > 0;
        if (ResolveNamedMark(match, candidate, start, trimmedAbs) is not { } placement)
            return;
        var (time, markSilence, markRegion) = placement;
        var markCtx = new MarkContext(_ctx.File, _ctx.Info.InputDecoder, match.Phrase.Regex,
            _ctx.AllSilences, _ctx.SpeechSegments, new TranscriptWindow(trimmedAbs, start, windowEnd),
            Language.Profile.Language);
        time = (await _env.Marks.PlaceAsync(
            null, time, phraseAbs, start + match.PhraseEndSeconds, markSilence, markRegion, markCtx, ct))
            .TimeSeconds;

        // Second dedupe pass, now against the placed time. The pre-placement one compares phrase
        // times, which two probes of the same announcement can easily disagree about by more than
        // the dedupe window - overlapping windows are re-segmented by Whisper from scratch, so the
        // same words can land in a segment starting seconds apart. Once both have been walked back
        // to their anchor they coincide exactly, and that is the only reliable moment to notice.
        // Confirmed on "Die Dritte Macht.m4b" 2026-07-28, where it produced four duplicate pairs
        // (among them "Kapitel 6" and "Kapitel 7", the same announcement heard two ways, both at
        // 2:46:06.53). Costs the placement work of the loser, which only a re-heard mark pays.
        if (_namedFound.Any(m => m.Kind == match.Phrase.Kind &&
                                 Math.Abs(m.TimeSeconds - time) < NamedMarkDedupeSeconds))
            return;

        if (teachesThreshold)
        {
            ProposeThreshold(markSilence);
            AdoptProposedThreshold($"\"{match.Title}\"");
        }
        if (!match.Phrase.Repeatable)
            _namedFound.RemoveAll(m => m.Kind == match.Phrase.Kind);
        _namedFound.Add(new DetectedMark(
            match.Phrase.Kind, match.Title, time, match.Confidence, phraseAbs, match.Phrase.Repeatable));
        _ctx.Work.NamedMarks = _namedFound.Count;
        _ctx.Work.ExtraMarks = _namedFound.Count(m => m.Kind != ChapterKind);
        _env.Log?.Invoke($"{match.Phrase.Kind} detected (\"{match.Title}\"), mark placed at " +
                         $"{FormatTimestamp(time)} (confidence {match.Confidence:0.00}" +
                         await _env.Marks.LoudnessNoteAsync(time, markCtx, ct) +
                         $"){LowConfidenceNote(match.Confidence)}");
    }

    /// <summary>
    /// Whether an in-scope named match is to be passed over without becoming a mark. Two reasons,
    /// both of them specific to a phrase that takes no part in the chapter sequence and so has
    /// nothing to be judged against:
    /// <list type="bullet">
    /// <item><description>the same announcement was already marked - overlapping probe windows
    /// re-decode the same audio routinely, and without this every such overlap would yield a
    /// duplicate mark a second or two from the first (see
    /// <see cref="DetectionTuning.NamedMarkDedupeSeconds"/>);</description></item>
    /// <item><description>the file has reached its --custom mark cap (see
    /// <see cref="DetectionTuning.MaxCustomMarksPerFile"/>), which is reported all the way out to
    /// the file's summary line rather than only logged. Chapter announcements are exempt: under
    /// --ignore-chapter-numbers they arrive through this same path, and a cap sized for structural
    /// interludes would cut an omnibus off partway through.</description></item>
    /// </list>
    /// </summary>
    /// <param name="phrase">The phrase that matched.</param>
    /// <param name="phraseAbs">Absolute time the announcement was heard at.</param>
    private bool ShouldDropNamedMatch(NamedPhrase phrase, double phraseAbs)
    {
        if (_namedFound.Any(m => m.Kind == phrase.Kind &&
                                 Math.Abs(m.PhraseTimeSeconds - phraseAbs) < NamedMarkDedupeSeconds))
            return true;

        if (!phrase.Repeatable || phrase.Kind == ChapterKind)
            return false;

        if (_namedFound.Count(m => m.Repeatable && m.Kind != ChapterKind) < MaxCustomMarksPerFile)
            return false;
        if (!CustomLimitHit)
            _env.Log?.Invoke($"custom mark limit of {MaxCustomMarksPerFile} reached at " +
                             $"{FormatTimestamp(phraseAbs)} - further --custom matches are ignored " +
                             "for this file (a mapping matching ordinary prose?)");
        CustomLimitHit = true;
        return true;
    }

    /// <summary>
    /// The default-mode mark for a named match - the same <see cref="ResolveAnnouncementMark"/> a
    /// numbered one goes through, rejection rules included, since a prologue, an epilogue and a
    /// <c>--custom</c> phrase are announcements in exactly the sense a chapter phrase is.
    /// </summary>
    /// <param name="match">The named match, in window-relative time.</param>
    /// <param name="candidate">The candidate whose window this probe decoded.</param>
    /// <param name="start">Absolute start of that window.</param>
    /// <param name="trimmedAbs">The window's transcript in absolute file time.</param>
    private (double Time, Silence? MarkSilence, NonSpeechRegion? MarkRegion)? ResolveNamedMark(
        NamedMatch match, ProbeCandidate candidate, double start, List<TranscriptSegment> trimmedAbs)
        => ResolveAnnouncementMark(
            match.PhraseStartSeconds, match.PhraseEndSeconds, candidate, start, trimmedAbs,
            $"{match.Phrase.Kind} \"{match.Title}\"");

    /// <summary>
    /// Whether a phrase match is rejected on its number alone, before any mark placement is
    /// attempted. Either failure is logged rather than swallowed: the number was plainly heard, and
    /// without a line saying so a --verbose run gives no hint why it did not become a mark, which is
    /// indistinguishable from the phrase matcher having missed it. Neither ends the window - a real
    /// announcement later in the same window is still found.
    /// </summary>
    /// <param name="match">The phrase match to judge.</param>
    /// <param name="phraseAbs">Its absolute phrase start time, for the log line.</param>
    /// <param name="windowLast">The highest number accepted so far, in this window or before it.</param>
    private bool IsOutOfSequence(PhraseMatch match, double phraseAbs, int windowLast)
    {
        // A duplicate or regression: an in-text mention like "as seen in chapter three", or a
        // re-detection of an already-marked chapter.
        if (match.Number <= windowLast)
        {
            _env.Log?.Invoke($"skipped chapter {match.Number} at {FormatTimestamp(phraseAbs)} - " +
                             $"not above the last accepted chapter {windowLast}" +
                             (match.Number < windowLast ? " (in-text mention?)" : ""));
            return true;
        }
        // A snapped window can, near a gap's own upper boundary, reach right up against the next
        // already-confirmed chapter's own announcement - reject a match at or above it outright so
        // gap recovery can never displace a chapter that is already in hand. Two kinds of gap set
        // this: a --verify region (never for a fresh run's whole-file region, whose UpperNumber is
        // always null) and a sequence-gap re-probe (see _gapAbove).
        if ((_gapAbove ?? _region.UpperNumber) is { } upperBound && match.Number >= upperBound)
        {
            _env.Log?.Invoke($"skipped chapter {match.Number} at {FormatTimestamp(phraseAbs)} - " +
                             $"at or above chapter {upperBound}, which bounds this gap");
            return true;
        }
        return false;
    }

    /// <summary>
    /// Turns one in-sequence phrase match into a placed, logged and recorded chapter mark, or
    /// rejects it for want of a qualifying anchor (see <see cref="ResolveProbeMark"/>, which logs
    /// why). An accepted mark is appended to the accumulator here, not by the caller.
    /// </summary>
    /// <param name="match">The phrase match, in window-relative time.</param>
    /// <param name="candidate">The candidate whose window this probe decoded.</param>
    /// <param name="start">Absolute start of that window.</param>
    /// <param name="windowEnd">Absolute planned end of the window - what precise marking
    /// anchors its search against (see <see cref="MarkContext.Transcript"/>).</param>
    /// <param name="phraseAbs">Absolute phrase start time.</param>
    /// <param name="trimmedAbs">The window's transcript in absolute file time.</param>
    /// <param name="windowLast">The highest number accepted so far, in this window or before it -
    /// the sequence position the refinement's own re-read of the number is held to
    /// (<see cref="SequenceBounds"/>).</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<ProbeMark?> AcceptMatchAsync(
        PhraseMatch match, ProbeCandidate candidate, double start, double windowEnd, double phraseAbs,
        List<TranscriptSegment> trimmedAbs, int windowLast, CancellationToken ct)
    {
        if (ResolveProbeMark(match, candidate, start, trimmedAbs) is not { } placement)
            return null;
        var (time, markSilence, markRegion) = placement;

        var markCtx = new MarkContext(_ctx.File, _ctx.Info.InputDecoder, Language.Profile.PhraseRegex,
            _ctx.AllSilences, _ctx.SpeechSegments, new TranscriptWindow(trimmedAbs, start, windowEnd),
            Language.Profile.Language);
        var placed = await _env.Marks.PlaceAsync(
            new NumberCheck(match.Number, Language.Profile, SequenceBounds(windowLast)),
            time, phraseAbs, start + match.PhraseEndSeconds, markSilence, markRegion, markCtx, ct);
        // Placement may have re-read the number out of the refinement's own probes, so everything
        // below reports and records what those settled on rather than what the window heard.
        time = placed.TimeSeconds;
        var number = placed.Number!.Value;

        if (match.SpansMerge)
            _env.Log?.Invoke($"chapter {number} detection spans the reused/fresh transcript " +
                             "merge from Pass 2's overlap reuse - worth a spot check");

        _found.Add(new DetectedChapter(number, time, match.Confidence));
        var (highest, missingNumbers) = ChapterProgress(_found, _env.Options.ExpectedStartChapter);
        _ctx.Work.HighestChapter = highest;
        _ctx.Work.MissingChapters = missingNumbers.Count;
        _env.Log?.Invoke($"chapter {number} detected, mark placed at {FormatTimestamp(time)} " +
                         $"(confidence {match.Confidence:0.00}" +
                         await _env.Marks.LoudnessNoteAsync(time, markCtx, ct) +
                         $"){LowConfidenceNote(match.Confidence)}" +
                         MissingNote(missingNumbers));

        ObserveJingleLength(phraseAbs, start, markSilence, markRegion);
        // start is the candidate's own position (see ProbeAsync), so the window-relative phrase end
        // is already the reach measured from the candidate.
        return new ProbeMark(
            number, markSilence, match.Confidence, match.PhraseEndSeconds);
    }

    /// <summary>
    /// Feeds the jingle length this mark just revealed into the --max-jingle-length auto window
    /// sizing. Only from the second mark found overall (including any seeded, already-confirmed
    /// ones) on, so the anchor is a real inter-chapter jingle - not the intro-to-chapter-1 gap,
    /// which can easily run longer (or shorter) than a book's regular jingles and would otherwise
    /// size the window off a one-off observation before any real jingle has even been seen. Same
    /// reasoning as the --min-silence-length auto tightening in <see cref="TightenThreshold"/>.
    /// <para>
    /// The length is measured from the silence or region the mark actually falls into: the raw
    /// offset from this probe's window start would inflate the observation whenever a false, earlier
    /// in-text pause triggered the probe. With a VAD region as anchor (no leading silence) it runs
    /// from the region start to the phrase, clipped at the region end - the announcement is often
    /// spoken inside the jingle, and the region end can itself be inflated when
    /// <see cref="JingleGeometry.ComputeNonSpeechRegions"/>'s short-speech-gap merge swallowed it;
    /// either way the phrase bounds the jingle.
    /// </para>
    /// </summary>
    /// <param name="phraseAbs">Absolute phrase start time.</param>
    /// <param name="start">Absolute start of the probe window, the last-resort anchor.</param>
    /// <param name="markSilence">The silence the mark fell into, if any.</param>
    /// <param name="markRegion">The VAD jingle region the mark fell into, if any.</param>
    private void ObserveJingleLength(
        double phraseAbs, double start, Silence? markSilence, NonSpeechRegion? markRegion)
    {
        if (_env.Vad == null || !_env.Options.AutoMaxJingle || _found.Count <= 1)
            return;

        var observedLength = markSilence is { } silence
            ? phraseAbs - silence.EndSeconds
            : markRegion is { } region
                ? Math.Min(region.EndSeconds, phraseAbs) - region.StartSeconds
                : phraseAbs - start;
        if (observedLength < MinJingleObservationSeconds)
            return;

        // The window this observation asks for; the adapted window is the running maximum of these
        // (monotonically increasing - see JingleObservationSafetyFactor), capped at the ceiling so
        // an outlier can never widen the window past what --max-jingle-length allows. During a gap
        // re-probe only the maximum moves; _probeSeconds stays at the ceiling until it is done.
        var proposed = Math.Min(_ctx.JingleCeilingSeconds,
            JingleObservationSafetyFactor * observedLength + PhraseMarginSeconds);
        _adaptedWindowSeconds = Math.Max(_adaptedWindowSeconds ?? proposed, proposed);
        if (!_reprobing && _adaptedWindowSeconds.Value != _probeSeconds)
        {
            _probeSeconds = _adaptedWindowSeconds.Value;
            _env.Log?.Invoke($"jingle probe window resized to {_probeSeconds:0.#} s");
        }
    }

    /// <summary>
    /// Resolves where one phrase match found in a probe window puts its default-mode mark, and
    /// which silence/jingle region that mark anchors to. The anchors are reported for the auto
    /// mechanisms and statistics regardless of --mark-before-jingle - only what
    /// <see cref="MarkPlacer"/> subsequently does with the mark depends on that option.
    /// <para>
    /// With the VAD pre-pass, the anchor is the VAD jingle region ending at the phrase, not
    /// whichever silence triggered this probe: a false in-text pause earlier in the previous chapter
    /// does not lead that region, so it must not become the anchor (which would mark at the pause
    /// and feed the auto mechanisms a bogus jingle length) - see
    /// <see cref="JingleGeometry.ResolveJingleAnchor"/>. The candidate's own VAD region is used
    /// directly only when this phrase is plausibly attached to it; a second announcement further
    /// along the window belongs to a different transition and must re-derive its own anchor. When
    /// neither a region nor a closer silence is found, this probe's own triggering silence is the
    /// fallback.
    /// </para>
    /// <para>
    /// Without it, the mark always goes <see cref="MarkLead"/> before the phrase
    /// itself, regardless of what precedes it. A phrase directly following the triggering silence
    /// (the classic shape) anchors to that silence. One deeper in the window than the timing rule
    /// allows can still be accepted right away, without waiting for a later candidate's window, but
    /// only when a candidate-grade silence directly precedes it: within the same
    /// <see cref="PhraseLatestStart"/> seconds the classic rule grants, and at least
    /// --min-silence-length long, so a breath pause before an in-text mention ("Chapter eight had
    /// been hard.") cannot qualify as an anchor.
    /// </para>
    /// </summary>
    /// <param name="match">The phrase match, in window-relative time.</param>
    /// <param name="candidate">The candidate whose window this probe decoded.</param>
    /// <param name="start">Absolute start of that window.</param>
    /// <param name="trimmedAbs">The window's transcript in absolute file time, for the VAD edge
    /// adjustment inside <see cref="JingleGeometry.ResolveJingleAnchor"/>.</param>
    /// <returns>The default-mode mark and its anchors, or null when the match has no qualifying
    /// anchor at all and must be rejected - see <see cref="RejectProbeMark"/>, which logs why.</returns>
    private (double Time, Silence? MarkSilence, NonSpeechRegion? MarkRegion)? ResolveProbeMark(
        PhraseMatch match, ProbeCandidate candidate, double start, List<TranscriptSegment> trimmedAbs)
        => ResolveAnnouncementMark(
            match.PhraseStartSeconds, match.PhraseEndSeconds, candidate, start, trimmedAbs,
            $"chapter {match.Number}");

    /// <summary>
    /// Places a mark for any announcement, numbered or named, and applies the rejection rules that
    /// separate a real announcement from an in-text mention of the same words.
    /// </summary>
    /// <remarks>
    /// Named phrases (prologue, epilogue, <c>--custom</c>) used to skip the rejection rules, on the
    /// grounds that they have no chapter-number sequence for a spurious mark to corrupt. That
    /// reasoning covered the wrong risk: the rules exist to decide whether the words were
    /// <em>announced</em> at all, which matters just as much for a mark nothing else depends on -
    /// a book whose narration happens to mention "Zeittafel" mid-sentence should no more get a mark
    /// there than one mentioning "chapter eight" should. Unified 2026-07-29 at the user's request:
    /// a named phrase is an announcement or it is nothing.
    /// <para>
    /// Note what this does <em>not</em> reach: with a VAD pre-pass - the default - the rules below
    /// never run for either kind, because the VAD path returns first and places every match it is
    /// given. Unifying the two therefore changes behaviour only under <c>--max-jingle-length 0</c>.
    /// </para>
    /// </remarks>
    /// <param name="phraseStartSeconds">Phrase start, relative to the window start.</param>
    /// <param name="phraseEndSeconds">End of the segment the phrase was found in, same time base.</param>
    /// <param name="candidate">The candidate whose window this probe decoded.</param>
    /// <param name="start">Absolute start of that window.</param>
    /// <param name="trimmedAbs">The window's transcript in absolute file time, for the VAD edge
    /// adjustment inside <see cref="JingleGeometry.ResolveJingleAnchor"/>.</param>
    /// <param name="what">How to name this announcement in a rejection log line, e.g.
    /// <c>chapter 8</c> or <c>custom mark "Zeittafel"</c>.</param>
    private (double Time, Silence? MarkSilence, NonSpeechRegion? MarkRegion)? ResolveAnnouncementMark(
        double phraseStartSeconds, double phraseEndSeconds, ProbeCandidate candidate, double start,
        List<TranscriptSegment> trimmedAbs, string what)
    {
        var phraseAbs = start + phraseStartSeconds;
        if (_env.Vad != null)
        {
            var candidateRegion = candidate.VadRegion is { } cvr &&
                phraseAbs >= cvr.StartSeconds - JinglePhraseMatchToleranceSeconds &&
                phraseAbs <= cvr.EndSeconds + JinglePhraseMatchToleranceSeconds
                ? candidate.VadRegion : null;
            var (leadingSilence, markRegion) = ResolveJingleAnchor(
                phraseAbs, start + phraseEndSeconds, start, _ctx.AllSilences,
                _ctx.NonSpeechRegions, candidateRegion, _ctx.SpeechSegments, trimmedAbs);
            var time = RefineDefaultMark(
                Math.Max(0, ResolveDefaultPhraseOnset(phraseAbs, markRegion, leadingSilence, _ctx.SpeechSegments)
                            - MarkLead),
                _ctx.SpeechSegments, MarkLead);
            // The candidate's own silence stands in for the statistics where neither a jingle nor a
            // silence leading one was found. It is deliberately not offered to the onset resolution
            // above, which asks where *this region's* music begins and would be misled by a silence
            // belonging to no region at all.
            var markSilence = leadingSilence ?? (markRegion == null ? candidate.Silence : null);
            return (time, markSilence, markRegion);
        }

        if (phraseStartSeconds <= PhraseLatestStart)
            return (Math.Max(0, phraseAbs - MarkLead), candidate.Silence, null);

        // Each of the three ways this can fail is named separately rather than folded into one
        // rejection: two of them point straight at a --min-silence-length that is too strict for
        // this book, which is exactly what someone chasing a missing chapter needs to be told.
        if (FindRealAnchorSilence(start, phraseAbs, _ctx.AllSilences) is not { } anchor)
            return RejectProbeMark(what, phraseAbs, "no silence precedes it inside the probe window");
        if (phraseAbs - anchor.EndSeconds > PhraseLatestStart)
            return RejectProbeMark(what, phraseAbs,
                $"the nearest silence ends {phraseAbs - anchor.EndSeconds:0.0} s before it, " +
                $"more than the {PhraseLatestStart:0.#} s allowed");
        if (anchor.EndSeconds - anchor.StartSeconds < _env.Options.MinSilenceSeconds)
            return RejectProbeMark(what, phraseAbs,
                $"the silence before it is only {anchor.EndSeconds - anchor.StartSeconds:0.00} s long, " +
                $"below --min-silence-length {_env.Options.MinSilenceSeconds:0.##} s");
        return (Math.Max(0, phraseAbs - MarkLead), anchor, null);
    }

    /// <summary>Logs why <see cref="ResolveAnnouncementMark"/> is dropping an announcement the
    /// recognizer did hear, and returns the null that stands for "no mark". A missing mark is far
    /// easier to chase when the log distinguishes "never heard" from "heard, but unanchorable,
    /// because &lt;reason&gt;".</summary>
    /// <param name="what">The rejected announcement, as named for the log line.</param>
    /// <param name="phraseAbs">Absolute phrase start time, for the log line's timestamp.</param>
    /// <param name="reason">Why no mark could be placed, phrased to follow "skipped X at T - ".</param>
    private (double Time, Silence? MarkSilence, NonSpeechRegion? MarkRegion)? RejectProbeMark(
        string what, double phraseAbs, string reason)
    {
        _env.Log?.Invoke($"skipped {what} at {FormatTimestamp(phraseAbs)} - {reason}");
        return null;
    }

    /// <summary>
    /// Applies everything one probe's marks change about the region's running state: a sequence gap
    /// triggers the re-probe of everything skipped since the last mark, each mark's anchor silence
    /// may tighten the --min-silence-length auto threshold, and the last accepted number advances.
    /// <para>
    /// The order matters and is the order Pass 2 resumes on: a gap re-probe runs first, so the
    /// threshold this mark then adopts and the jingle window the re-probe restores both already
    /// account for whatever the recovered chapters taught them. Where the candidate loop picks up
    /// afterwards needs no arranging - the re-probe walks its own copy of the sequence and leaves the
    /// loop index alone, so probing continues at the candidate after this mark's own window, past
    /// every position the re-probe just revisited.
    /// </para>
    /// </summary>
    /// <param name="probeMarks">The marks the probe produced, in window order.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task ApplyProbeMarksAsync(List<ProbeMark> probeMarks, CancellationToken ct)
    {
        foreach (var mark in probeMarks)
        {
            // The gap re-probe runs regardless of --min-silence-length mode: with the
            // overlap-sequence skip, candidates can be skipped even with an explicit threshold, and
            // a sequence gap is the signal that one of them hid a chapter.
            if (_lastNumber is { } previousNumber && mark.Number > previousNumber + 1)
                await HandleSequenceGapAsync(previousNumber, mark.Number, ct);

            if (_env.Options.AutoMinSilence && !_sweeping)
                TightenThreshold(mark);
            _skippedSinceLastMark.Clear();
            _probedSinceLastMark.Clear();
            _lastNumber = mark.Number;
        }
    }

    /// <summary>
    /// Reacts to the chapter numbers just found leaving a gap: everything Pass 2 has looked at since
    /// the last mark gets a second, unconditional chance before the region moves on. Nothing to
    /// re-probe is a routine outcome (all candidates were probed at the full window and simply held
    /// no readable announcement) and is logged as such rather than passed over in silence - the log
    /// then distinguishes "Pass 2 declined a candidate" from "Pass 2 never had one", which is the
    /// first thing worth knowing when a chapter goes missing.
    /// </summary>
    /// <param name="previousNumber">The chapter number below the gap.</param>
    /// <param name="number">The chapter number above it, i.e. the mark that revealed the gap.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task HandleSequenceGapAsync(int previousNumber, int number, CancellationToken ct)
    {
        // A probed window that heard an unreadable announcement is already queued as skipped by
        // RecoverUnnumberedAnnouncementsAsync, so it sits in both lists; taking it once keeps the re-probe
        // from transcribing the same audio twice and the count in the log honest.
        var widened = _probedSinceLastMark
            .Where(p => WiderWindowWouldReach(p.Candidate, p.WindowEnd) &&
                        !_skippedSinceLastMark.Contains(p.Candidate))
            .Select(p => p.Candidate)
            .ToList();
        var note = $"sequence gap between chapter {previousNumber} and {number}, ";
        if (_skippedSinceLastMark.Count == 0 && widened.Count == 0)
        {
            _env.Log?.Invoke(note + "nothing to re-probe since the last mark - deferred to the gap scan");
            return;
        }

        var candidates = _skippedSinceLastMark.Concat(widened).OrderBy(c => c.Start).ToList();
        _env.Log?.Invoke(
            note + $"re-probing {candidates.Count} candidate(s) unconditionally " +
            $"({_skippedSinceLastMark.Count} skipped, {widened.Count} at a wider window)");
        await ReprobeGapCandidatesAsync(candidates, previousNumber, number, ct);
    }

    /// <summary>
    /// Whether a probe window at the ceiling would reach past what this candidate's window actually
    /// covered, i.e. whether re-probing it can see audio its first probe could not. Compares natural
    /// spans rather than planned ends: <see cref="GapPlanning.PlanWindowEnd"/>'s seam snapping shifts
    /// an end by seconds in either direction depending on where the neighbors sit, and a candidate
    /// whose ceiling window is genuinely wider must not be excluded because its original end happened
    /// to be snapped forward. Only --max-jingle-length auto can narrow a window in the first place;
    /// in every other mode <see cref="_probeSeconds"/> is fixed for the region's whole life and this
    /// is always false, so the widened re-probe costs nothing where it cannot apply.
    /// </summary>
    /// <param name="candidate">The candidate that was probed.</param>
    /// <param name="windowEnd">The end its window was probed with.</param>
    private bool WiderWindowWouldReach(ProbeCandidate candidate, double windowEnd)
        => _env.Vad != null && _env.Options.AutoMaxJingle &&
           Math.Min(candidate.Start + _ctx.JingleCeilingSeconds, _region.ToSeconds) > windowEnd;

    /// <summary>
    /// Re-probes, unconditionally and at the full ceiling window, the candidates a sequence gap has
    /// put back in question. They form their own little window sequence, each end computed on the fly
    /// against its next neighbor in that sequence so adjacent re-probe windows get snapped shared
    /// borders too; the window width cannot change mid-re-probe (see <see cref="_reprobing"/>), so
    /// consecutive ends stay consistent for the whole sequence.
    /// <para>
    /// Stops the moment the gap is closed rather than walking the rest of the sequence: the
    /// candidates behind the recovered chapter cover the same audio and have nothing left to find,
    /// and each of them would pay for a full mark placement (the refinement alone costs tens of
    /// seconds) to arrive at a mark that is then dropped as a duplicate. Confirmed on BARDIOC.m4b
    /// 2026-07-30, where the chapter 10 that closed the gap was re-found and re-refined by the three
    /// following candidates - four identical marks at 5:14:48.15, about two minutes of pure waste.
    /// </para>
    /// <para>
    /// Every accepted gap mark advances <see cref="_lastNumber"/>, which is what makes that
    /// termination merely an optimisation rather than the fix: a later window overlapping the same
    /// announcement must no longer accept it, and it cannot once the number is no longer above the
    /// floor. The floor still starts at <paramref name="previousNumber"/> so the in-between numbers
    /// are acceptable at all, and raising it as they are found costs nothing - candidates run in
    /// chronological order and chapter numbers ascend with time, so no later window can hold a
    /// number the raised floor now rejects.
    /// </para>
    /// </summary>
    /// <param name="candidates">The candidates to re-probe, in chronological order.</param>
    /// <param name="previousNumber">The chapter number below the gap.</param>
    /// <param name="number">The chapter number above it, i.e. the mark that revealed the gap.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task ReprobeGapCandidatesAsync(
        List<ProbeCandidate> candidates, int previousNumber, int number, CancellationToken ct)
    {
        if (_env.Vad != null && _env.Options.AutoMaxJingle && _probeSeconds != _ctx.JingleCeilingSeconds)
        {
            _probeSeconds = _ctx.JingleCeilingSeconds;
            _env.Log?.Invoke($"jingle probe window reset to {_probeSeconds:0.#} s for the re-probe");
        }
        _reprobing = true;
        _gapAbove = number;
        var missing = Enumerable.Range(previousNumber + 1, number - previousNumber - 1).ToHashSet();
        for (var si = 0; si < candidates.Count; si++)
        {
            var gapMarks = await ProbeAsync(
                candidates[si], new WindowPlan(candidates, si, WindowEndFor(candidates, si)), ct);
            foreach (var gapMark in gapMarks)
            {
                _lastNumber = Math.Max(_lastNumber ?? 0, gapMark.Number);
                // A gap mark recovered from a *skipped* candidate has, by definition, an anchor
                // silence short enough to have been skipped - fold it into the running minimum so
                // the threshold can never again sit above a silence proven to precede a chapter. One
                // recovered from a widened window instead cleared the threshold already, so its
                // proposal cannot lower the running minimum and this is a no-op for it; both go
                // through the same guard rather than the caller having to remember which list a
                // candidate came from. Only genuine gap-fillers count either way; a re-detection of
                // a chapter outside this gap must not lower anything.
                if (!missing.Remove(gapMark.Number))
                    continue;
                if (_env.Options.AutoMinSilence)
                    ProposeThreshold(gapMark.MarkSilence);
                ProposeJingleWindow(gapMark.Number, gapMark.ReachSeconds);
            }
            if (missing.Count > 0)
                continue;
            if (si + 1 < candidates.Count)
                _env.Log?.Invoke($"gap before chapter {number} closed - re-probing stopped after " +
                                 $"{si + 1} of {candidates.Count} candidate(s)");
            break;
        }
        _reprobing = false;
        _gapAbove = null;
        // Re-probing done: bring the jingle window back down from the ceiling to the adapted value,
        // including anything the re-probed marks just taught us.
        if (_env.Vad != null && _env.Options.AutoMaxJingle &&
            _adaptedWindowSeconds is { } restoredWindow && _probeSeconds != restoredWindow)
        {
            _probeSeconds = restoredWindow;
            _env.Log?.Invoke($"jingle probe window restored to {_probeSeconds:0.#} s");
        }
    }

    /// <summary>
    /// Folds one accepted mark's anchor silence into the --min-silence-length auto threshold and
    /// announces an actual change. <see cref="_lastNumber"/> having a value means this is at least
    /// the second mark found, so that silence is a real inter-chapter break - not the
    /// intro-to-chapter-1 silence, which is routinely longer than that and would otherwise
    /// over-tighten the threshold from the very first mark.
    /// </summary>
    /// <param name="mark">The mark whose anchor silence to fold in.</param>
    private void TightenThreshold(ProbeMark mark)
    {
        if (_lastNumber.HasValue)
            ProposeThreshold(mark.MarkSilence);
        AdoptProposedThreshold($"chapter {mark.Number}");
    }

    /// <summary>Makes whatever <see cref="ProposeThreshold"/> has accumulated the threshold actually
    /// used from here on, announcing a real change.</summary>
    /// <param name="after">What was just marked, for the log line.</param>
    private void AdoptProposedThreshold(string after)
    {
        // The first set is a raise from the floor ("tightened"), everything after can only ever be
        // a lowering.
        var newThreshold = _adaptedThresholdSeconds ?? _env.Options.MinSilenceSeconds;
        if (newThreshold != _threshold)
            _env.Log?.Invoke($"threshold {(newThreshold > _threshold ? "tightened" : "lowered")} " +
                             $"to {newThreshold:0.##} s after {after}");
        _threshold = newThreshold;
    }

    /// <summary>
    /// Folds one anchor silence's proposal into <see cref="_adaptedThresholdSeconds"/>, keeping the
    /// running minimum. Never below the --min-silence-length floor: Pass 1 never reports candidates
    /// shorter than the floor in the first place, so a threshold below it would skip nothing at all.
    /// Does nothing when the mark had no anchor silence (it sat on a VAD region instead).
    /// </summary>
    /// <param name="markSilence">The silence the mark actually fell into, or null.</param>
    private void ProposeThreshold(Silence? markSilence)
    {
        if (markSilence is not { } silence)
            return;
        var proposed = Math.Max(_env.Options.MinSilenceSeconds,
            AdaptiveTightenFactor * (silence.EndSeconds - silence.StartSeconds));
        _adaptedThresholdSeconds = Math.Min(_adaptedThresholdSeconds ?? proposed, proposed);
    }

    /// <summary>
    /// Widens the --max-jingle-length auto window to whatever a gap-recovered chapter actually needed
    /// (see <see cref="ProbeMark.ReachSeconds"/>), plus the usual phrase margin. Only gap-recovered
    /// marks get this: the adapted window is otherwise sized from observed jingle *lengths*, anchored
    /// to the silence or region each mark fell into rather than to the candidate that triggered the
    /// probe, and the anti-inflation reason for that is sound - a window sized off every mark's raw
    /// offset would ratchet up on any book where probes routinely trigger on in-text pauses well
    /// before the announcement. A gap-recovered mark is the one case where the offset is trustworthy:
    /// the candidate is corroborated by a chapter that nothing else in the run found, so the reach is
    /// a measured fact about what this book's windows must span, not a guess off a false trigger.
    /// Trustworthy is not the same as affordable, though, so a single recovery may lift the window by
    /// at most <see cref="GapReachGrowthFactor"/>; an outlier reach is honoured over several
    /// recoveries rather than in one jump that would pin the window near the ceiling for the rest of
    /// the book.
    /// <para>
    /// The gap this closes was real, not theoretical. Measured on BARDIOC.m4b (2026-07-30, 15 h 39
    /// min, German): chapters 9, 12 and 10 were each missed with their announcement's *onset* already
    /// inside the window and only ~1.5 s of slack behind it - enough for "Kapitel" and not for the
    /// number, so the phrase was truncated and no number could be read. The marks anchored to short
    /// silences immediately before each announcement, so
    /// <see cref="ObserveJingleLength"/> measured ~1 s, below its 2 s floor, and discarded the
    /// observation: the window stayed at 23.1 s across both recoveries. The --min-silence-length half
    /// of the recovery could not compensate either, having already been pinned at its 1.5 s floor
    /// since chapter 8, which also means the near-anchor silences could never become candidates of
    /// their own. So neither adaptive mechanism learned anything from recovering chapters 9 and 10,
    /// and chapter 12 was then lost the same way and cost a second full re-probe. Chapter 9's reach
    /// would have set the window to ~28 s, which covers chapter 12's ~28.5 s requirement.
    /// </para>
    /// </summary>
    /// <param name="number">The recovered chapter, for the log line.</param>
    /// <param name="reachSeconds">How far into its window that chapter's announcement ended.</param>
    private void ProposeJingleWindow(int number, double reachSeconds)
    {
        if (_env.Vad == null || !_env.Options.AutoMaxJingle)
            return;
        // No safety factor on top: unlike a jingle length, which stands in for jingles not yet seen
        // and may legitimately be exceeded, this is the exact width one real announcement needed. The
        // phrase margin is what carries the headroom, and the ceiling caps it either way.
        var wanted = Math.Min(_ctx.JingleCeilingSeconds, reachSeconds + PhraseMarginSeconds);
        // A null adapted window means nothing has ever narrowed the ceiling, so the window in effect
        // *is* the ceiling and any proposal below it would narrow rather than widen - the restore at
        // the end of the re-probe would then hand the main loop a window smaller than the one it had.
        var current = _adaptedWindowSeconds ?? _ctx.JingleCeilingSeconds;
        if (wanted <= current)
            return;
        var proposed = Math.Min(wanted, current * GapReachGrowthFactor);
        _adaptedWindowSeconds = proposed;
        var capped = proposed < wanted ? $" (capped from {wanted:0.#} s)" : "";
        _env.Log?.Invoke($"chapter {number} needed {reachSeconds:0.#} s of probe window - " +
                         $"jingle probe window widened to {proposed:0.#} s{capped}");
    }

    /// <summary>
    /// A confident mark settles its whole overlapping window sequence (consecutive candidates whose
    /// windows each overlap the next): the remaining windows of the sequence cover the same
    /// continuous stretch of audio around the found transition, and a single sequence spanning two
    /// chapter transitions is highly unlikely - so they are skipped outright instead of probed. They
    /// still go into <see cref="_skippedSinceLastMark"/>, so the gap re-probe recovers the unlikely
    /// case after all (and Pass 3 remains the final net). A low-confidence mark settles nothing: the
    /// remaining windows keep their chance to re-detect the transition it may have gotten wrong.
    /// </summary>
    /// <param name="candidates">The region's candidate sequence.</param>
    /// <param name="ci">Index of the candidate just probed.</param>
    /// <param name="windowEnd">That window's <em>actual</em> probed end - a mid-probe resize
    /// (--max-jingle-length auto) must not retroactively pretend the window was narrower than what
    /// was really decoded - while the links beyond it use ends computed at the current width, the
    /// same ends those windows would be probed with.</param>
    /// <param name="probeMarks">The marks that window produced.</param>
    /// <returns>The index the candidate loop is to continue from.</returns>
    private int SkipSettledWindows(
        List<ProbeCandidate> candidates, int ci, double windowEnd, List<ProbeMark> probeMarks)
    {
        if (probeMarks.Count == 0 || probeMarks[^1].Confidence < LowConfidenceThreshold)
            return ci;

        var skipTo = ci;
        var reach = windowEnd;
        while (skipTo + 1 < candidates.Count && reach > candidates[skipTo + 1].Start)
        {
            skipTo++;
            reach = WindowEndFor(candidates, skipTo);
        }
        if (skipTo == ci)
            return ci;

        _env.Log?.Invoke($"{skipTo - ci} overlapping window(s) skipped");
        for (var si = ci + 1; si <= skipTo; si++)
            _skippedSinceLastMark.Add(candidates[si]);
        return skipTo;
    }
}
