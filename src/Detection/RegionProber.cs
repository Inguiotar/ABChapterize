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
/// <param name="ResolveLanguage">Resolves --lang auto from a probe window's samples, once per file.</param>
/// <param name="ChangeLanguage">Applies a resolved language to the file's pass-2 transcriber. Always
/// that one, even while a pass 2.5 re-probe decodes through a different model: the language belongs
/// to the file, not to whichever recognizer a given pass borrowed.</param>
/// <param name="LogTranscript">Logs a decoded window's transcript under --verbose.</param>
/// <param name="FindCappedPhraseMatches">The detector's --max-chapter-number-capped phrase matcher.</param>
internal sealed record ProbeEnvironment(
    CliOptions Options,
    IAudioSource Audio,
    IVoiceActivityDetector? Vad,
    Action<string>? Log,
    MarkPlacer Marks,
    Func<float[], CancellationToken, ITranscriber?, Task<List<TranscriptSegment>>> TranscribeCounting,
    Func<float[], CancellationToken, Task<(LanguageProfile Profile, string? DetectedLanguage, double DetectedProbability)>> ResolveLanguage,
    Action<string> ChangeLanguage,
    Action<string, List<TranscriptSegment>> LogTranscript,
    Func<List<TranscriptSegment>, LanguageProfile, int?, IEnumerable<PhraseMatch>> FindCappedPhraseMatches);

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
/// <param name="Silences">The subset at or above --min-silence-length: the probe candidates.</param>
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
internal readonly record struct Pass2Context(
    string File, MediaInfo Info, WorkTracker Work, double BytesPerSecond, double JingleCeilingSeconds,
    List<Silence> AllSilences, List<Silence> Silences, List<NonSpeechRegion> NonSpeechRegions,
    List<SpeechSegment> SpeechSegments, double EarlyAbortSeconds, int? ExpectedStartChapter,
    ITranscriber Transcriber);

/// <summary>The file's language resolution as it stands. A null <paramref name="Profile"/> means
/// --lang auto has not resolved the language yet, which the next full-window decode does; an
/// explicit --lang, and a gap-scoped run that inherits --verify's own resolution, both arrive with
/// it already set.</summary>
/// <param name="Profile">The resolved language profile, or null while still unresolved.</param>
/// <param name="DetectedLanguage">What Whisper's language detector reported, if it ran.</param>
/// <param name="DetectedProbability">Its confidence in <paramref name="DetectedLanguage"/>.</param>
internal readonly record struct LanguageState(
    LanguageProfile? Profile, string? DetectedLanguage, double DetectedProbability);

/// <summary>One position Pass 2 may probe: the region start, a silence's end, or the start of a
/// VAD jingle region. Exactly one of the two anchors is set, except for the region-start candidate,
/// which has neither.</summary>
/// <param name="Start">Absolute time the probe window starts at.</param>
/// <param name="Silence">The silence whose end this is, when a silence triggered the candidate.</param>
/// <param name="VadRegion">The VAD non-speech region this starts, when one triggered the candidate.</param>
internal readonly record struct ProbeCandidate(
    double Start, Silence? Silence, NonSpeechRegion? VadRegion);

/// <summary>One chapter mark a probe window produced.</summary>
/// <param name="Number">The detected chapter number.</param>
/// <param name="MarkSilence">The silence the mark falls into, or null when it sits on a VAD region
/// (or on nothing at all) - the input to the --min-silence-length auto tightening.</param>
/// <param name="Confidence">Whisper's confidence for the segment the phrase was found in, which
/// decides whether this mark settles its whole overlapping window sequence.</param>
internal readonly record struct ProbeMark(int Number, Silence? MarkSilence, double Confidence);

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

    /// <summary>Accumulator of confirmed chapters across all regions of the file; mutated in place
    /// as marks are accepted, so the sequence Pass 3 later inspects is one seamless list regardless
    /// of which region contributed what.</summary>
    private readonly List<DetectedChapter> _found;

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

    /// <summary>The last chapter number accepted in this region, seeded from
    /// <see cref="DetectionRegion.LowerNumber"/> when a chapter is already confirmed to precede it
    /// and null for a from-file-start region. Holds the previous value (not yet the current
    /// window's) while a probe is in flight, which is exactly what a gap re-probe needs to accept
    /// the in-between numbers.</summary>
    private int? _lastNumber;

    /// <summary>The previous probe's full window transcript in absolute file time; see
    /// <see cref="_cacheTo"/> for what it is for.</summary>
    private List<TranscriptSegment> _cacheSegmentsAbs = [];

    /// <summary>Start of the absolute span <see cref="_cacheSegmentsAbs"/> covers.</summary>
    private double _cacheFrom;

    /// <summary>
    /// End of that span. When the next candidate's window overlaps it, the overlapping segments are
    /// reused verbatim instead of being re-run through Whisper - only the fresh tail beyond the
    /// planned seam is decoded. The span test (start inside [<see cref="_cacheFrom"/>,
    /// <see cref="_cacheTo"/>)) doubles as the seam-stitching check: it holds exactly when the
    /// previous window really was decoded up to the seam this window's plan relies on, and when it
    /// does not (e.g. that window was skipped by the adaptive threshold) the probe falls back to
    /// decoding its full window from the candidate start - nothing is ever left covered by neither
    /// decode. Starts at negative infinity so the very first probe of a region never counts as an
    /// overlap and always does a full transcribe.
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
    /// unconditionally and folds the recovered marks' own anchor silences into
    /// <see cref="_adaptedThresholdSeconds"/>, so gap-filling stays inside Pass 2 where possible
    /// and the threshold can never again sit above a silence that has proven to precede a chapter.
    /// Collects the windows the overlap-sequence skip passes over too - in every mode, not just
    /// auto - so the same re-probe covers the unlikely case of a skipped sequence window having
    /// hidden a second transition.
    /// </summary>
    private readonly List<ProbeCandidate> _skippedSinceLastMark = [];

    /// <summary>The file's language resolution, as it stood on entry and as this region may have
    /// advanced it (a fresh run's very first full-window decode resolves --lang auto).</summary>
    internal LanguageState Language { get; private set; }

    /// <summary>Whether --early-abort fired in this region: enough play time probed without a
    /// single find that further probing is pointless.</summary>
    internal bool EarlyAborted { get; private set; }

    /// <summary>The first chapter number found, when it sat below --expected-start-chapter and
    /// detection was therefore abandoned for this file; null otherwise.</summary>
    internal int? BelowExpectedStartNumber { get; private set; }

    /// <summary>Creates a prober for one region.</summary>
    /// <param name="env">The detector-owned tools and callbacks to probe with.</param>
    /// <param name="ctx">Region-loop-invariant Pass 2 inputs.</param>
    /// <param name="region">The region to probe.</param>
    /// <param name="found">Accumulator of confirmed chapters across all regions.</param>
    /// <param name="language">The language resolution so far.</param>
    /// <param name="progressOffsetSeconds">Offset onto the enclosing phase's time base; see
    /// <see cref="_progressOffsetSeconds"/>. Defaults to 0, i.e. report absolute file positions.</param>
    internal RegionProber(ProbeEnvironment env, Pass2Context ctx, DetectionRegion region,
        List<DetectedChapter> found, LanguageState language, double progressOffsetSeconds = 0)
    {
        _env = env;
        _ctx = ctx;
        _region = region;
        _found = found;
        Language = language;
        _progressOffsetSeconds = progressOffsetSeconds;
        _probeSeconds = env.Options.MaxJingleSeconds > 0 ? ctx.JingleCeilingSeconds : ProbeSecondsPlain;
        _lastNumber = region.LowerNumber > 0 ? region.LowerNumber : null;
        _cacheFrom = region.FromSeconds;
        _threshold = env.Options.MinSilenceSeconds;
    }

    /// <summary>
    /// Probes every candidate of the region in chronological order, stopping early on an
    /// --early-abort or --expected-start-chapter abort. Reports its outcome through
    /// <see cref="Language"/>, <see cref="EarlyAborted"/> and
    /// <see cref="BelowExpectedStartNumber"/>; the marks themselves land in the accumulator this
    /// prober was constructed with.
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
            var windowEnd = WindowEndFor(candidates, ci);
            var probeMarks = await ProbeAsync(candidate, windowEnd, ct);

            if (foundNoneYet && IsBelowExpectedStart())
                break;

            await ApplyProbeMarksAsync(probeMarks, ct);
            ci = SkipSettledWindows(candidates, ci, windowEnd, probeMarks);
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
    /// chronological order. A window can never decode past the region end regardless (see
    /// <see cref="GapPlanning.PlanWindowEnd"/>'s duration clamp), so the region boundary alone is
    /// enough containment - no extra check is needed here for that. VAD regions only qualify when
    /// they start at their own jingle start (i.e. nothing else leads them) and are long enough to be
    /// worth observing yet short enough to still be this book's jingle.
    /// </summary>
    private List<ProbeCandidate> BuildCandidates()
    {
        var candidates = new List<ProbeCandidate> { new(_region.FromSeconds, null, null) };
        candidates.AddRange(_ctx.Silences
            .Where(s => s.EndSeconds >= _region.FromSeconds && s.EndSeconds < _region.ToSeconds - 1)
            .Select(s => new ProbeCandidate(s.EndSeconds, s, null)));
        if (_env.Vad == null)
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
    /// --early-abort: once Pass 2 has probed this far into the file's play time without a single
    /// chapter found, give up rather than transcribe the rest of a book that plainly will not yield
    /// any (wrong --chapter-phrase, wrong --lang, or one that announces chapters differently).
    /// </summary>
    /// <param name="candidate">The candidate about to be probed.</param>
    private bool ShouldEarlyAbort(ProbeCandidate candidate)
    {
        if (candidate.Start < _ctx.EarlyAbortSeconds || _found.Count > 0)
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
    /// </summary>
    /// <param name="candidate">The candidate to judge.</param>
    private bool ShouldSkipCandidate(ProbeCandidate candidate)
    {
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
    /// <param name="windowEnd">The window's <em>planned</em> end (see <see cref="WindowEndFor"/>),
    /// possibly snapped away from the natural start plus <see cref="_probeSeconds"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The accepted marks in window order.</returns>
    private async Task<List<ProbeMark>> ProbeAsync(
        ProbeCandidate candidate, double windowEnd, CancellationToken ct)
    {
        var start = candidate.Start;
        ct.ThrowIfCancellationRequested();
        // Position-based Pass 2 progress (see DetectCoreAsync's BeginPhase); reported here rather
        // than only in the candidate loop so gap re-probes show their (backwards) position too.
        ReportProgress(start);

        var (windowSegmentsAbs, mergeBoundarySegIndex) =
            await AssembleWindowTranscriptAsync(start, windowEnd, ct);

        // Correct segment starts Whisper timestamped from a leading silence/jingle before shifting
        // to window-relative time (the cache keeps the raw absolute timings its reuse math needs).
        // The absolute trimmed transcript stays around for ResolveJingleAnchor's narration-aware
        // jingle edge adjustment.
        var trimmedAbs = TrimLeadingNonSpeech(
            windowSegmentsAbs, _ctx.AllSilences, _ctx.NonSpeechRegions, _env.Vad != null);
        var segments = ShiftSegments(trimmedAbs, -start);

        return await ScanWindowForMarksAsync(candidate, start, segments, trimmedAbs, mergeBoundarySegIndex, ct);
    }

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
    /// <param name="windowEnd">Absolute planned end of the window.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The window transcript, plus - for a partial-overlap assembly - the index of its
    /// first fresh segment, so a detection drawing on text from both sides of the cache/fresh
    /// boundary can be flagged (see <see cref="PhraseMatch.SpansMerge"/>); null when the window is
    /// entirely one or the other.</returns>
    private async Task<(List<TranscriptSegment> Segments, int? MergeBoundarySegIndex)>
        AssembleWindowTranscriptAsync(double start, double windowEnd, CancellationToken ct)
    {
        // A window whose start falls outside the cached span has no usable overlap.
        if (start < _cacheFrom || start >= _cacheTo)
            return (await DecodeFullWindowAsync(start, windowEnd, ct), null);

        if (windowEnd <= _cacheTo)
            // Fully contained in the previous window: reuse its transcript wholesale, no Whisper at
            // all. The (larger) cache is deliberately left untouched so a later candidate starting
            // within it can keep reusing it too.
            return (_cacheSegmentsAbs
                .Where(s => s.StartSeconds >= start && s.StartSeconds < windowEnd).ToList(), null);

        return await DecodeOverlapTailAsync(start, windowEnd, ct);
    }

    /// <summary>
    /// Transcribes a whole window from scratch and makes it the new cache. For a fresh run this is
    /// also where --lang auto resolves the language, once, from the very first probe's full
    /// samples; a gap-scoped run arrives with the profile already set, so this never re-resolves it.
    /// </summary>
    /// <param name="start">Absolute start of the window.</param>
    /// <param name="windowEnd">Absolute planned end of the window.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<List<TranscriptSegment>> DecodeFullWindowAsync(
        double start, double windowEnd, CancellationToken ct)
    {
        var samples = await _env.Audio.DecodePcmAsync(
            _ctx.File, start, windowEnd - start, _ctx.Info.InputDecoder, ct);

        if (Language.Profile == null)
        {
            var (profile, detected, probability) = await _env.ResolveLanguage(samples, ct);
            Language = new LanguageState(profile, detected, probability);
            _env.ChangeLanguage(profile.Language);
        }

        var fresh = await _env.TranscribeCounting(samples, ct, _ctx.Transcriber);
        _env.LogTranscript($"probe {windowEnd - start:0.0}s@{FormatTimestamp(start)}", fresh);
        return CacheWindow(ShiftSegments(fresh, start), start, windowEnd);
    }

    /// <summary>
    /// Partial overlap: cuts between the reused cache and a fresh tail decode. The previous window's
    /// end was planned as a seam snapped to a silence mid-point inside this window (see
    /// <see cref="GapPlanning.PlanWindowEnd"/>), so the cache normally ends exactly at that seam and
    /// the split search re-finds it at distance zero - the fresh decode starts right where the
    /// previous one stopped, stitching the transcripts together word-safely with nothing re-decoded
    /// and nothing dropped. It genuinely decides only for overlaps that plan did not anticipate (a
    /// probe-window resize in between), snapping to the best seam still covered by the cache; the
    /// border fallback means no seam exists, and hence no chapter transition in the overlap.
    /// </summary>
    /// <param name="start">Absolute start of the window.</param>
    /// <param name="windowEnd">Absolute planned end of the window.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<(List<TranscriptSegment> Segments, int? MergeBoundarySegIndex)>
        DecodeOverlapTailAsync(double start, double windowEnd, CancellationToken ct)
    {
        var splitPoint = FindOverlapSplitPoint(
            start, _cacheTo, windowEnd, _ctx.AllSilences, _ctx.NonSpeechRegions, _env.Vad != null,
            allowBeyondBorder: false);
        var samples = await _env.Audio.DecodePcmAsync(
            _ctx.File, splitPoint, windowEnd - splitPoint, _ctx.Info.InputDecoder, ct);
        var fresh = await _env.TranscribeCounting(samples, ct, _ctx.Transcriber);
        var reused = _cacheSegmentsAbs
            .Where(s => s.StartSeconds >= start && s.StartSeconds < splitPoint).ToList();
        _env.LogTranscript($"probe {windowEnd - splitPoint:0.0}s@{FormatTimestamp(splitPoint)} (tail)", fresh);
        var assembled = CacheWindow(
            [.. reused, .. ShiftSegments(fresh, splitPoint)], start, windowEnd);
        return (assembled, reused.Count);
    }

    /// <summary>Makes a freshly assembled window transcript the overlap cache, and returns it
    /// unchanged so the callers can assemble and cache in one expression.</summary>
    /// <param name="segments">The window transcript, in absolute file time.</param>
    /// <param name="start">Absolute start of the span it covers.</param>
    /// <param name="windowEnd">Absolute end of that span.</param>
    private List<TranscriptSegment> CacheWindow(List<TranscriptSegment> segments, double start, double windowEnd)
    {
        _cacheSegmentsAbs = segments;
        _cacheFrom = start;
        _cacheTo = windowEnd;
        return segments;
    }

    /// <summary>Finds every chapter announcement in one decoded window and turns the acceptable
    /// ones into marks.</summary>
    /// <param name="candidate">The candidate whose window this is.</param>
    /// <param name="start">Absolute start of the window.</param>
    /// <param name="segments">The window transcript in window-relative time, for phrase matching.</param>
    /// <param name="trimmedAbs">The same transcript in absolute file time, for the jingle edge
    /// adjustment inside <see cref="JingleGeometry.ResolveJingleAnchor"/>.</param>
    /// <param name="mergeBoundarySegIndex">The cache/fresh boundary, if any; see
    /// <see cref="AssembleWindowTranscriptAsync"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<List<ProbeMark>> ScanWindowForMarksAsync(
        ProbeCandidate candidate, double start, List<TranscriptSegment> segments,
        List<TranscriptSegment> trimmedAbs, int? mergeBoundarySegIndex, CancellationToken ct)
    {
        var marks = new List<ProbeMark>();
        // Window-local continuation of _lastNumber: several accepted marks within one window must
        // each top the previous one, exactly as consecutive windows' marks do.
        var windowLast = _lastNumber ?? 0;

        // Language.Profile is resolved on the first probe, which is always a full decode (the cache
        // is empty then), so it is non-null by the time any transcript-reuse branch can run.
        foreach (var match in _env.FindCappedPhraseMatches(segments, Language.Profile!, mergeBoundarySegIndex))
        {
            var phraseAbs = start + match.PhraseStartSeconds;
            if (IsOutOfSequence(match, phraseAbs, windowLast))
                continue;
            if (await AcceptMatchAsync(match, candidate, start, phraseAbs, trimmedAbs, ct) is not { } mark)
                continue;
            marks.Add(mark);
            windowLast = mark.Number;
        }
        return marks;
    }

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
        // A snapped window can, near a gap region's own upper boundary, reach right up against the
        // next already-confirmed chapter's own announcement - reject a match at or above it outright
        // so gap recovery can never displace a chapter --verify already trusts. Never set for the
        // whole-file region (its UpperNumber is always null), so this never fires for a fresh run.
        if (_region.UpperNumber is { } upperBound && match.Number >= upperBound)
        {
            _env.Log?.Invoke($"skipped chapter {match.Number} at {FormatTimestamp(phraseAbs)} - " +
                             $"at or above chapter {upperBound}, which bounds this gap region");
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
    /// <param name="phraseAbs">Absolute phrase start time.</param>
    /// <param name="trimmedAbs">The window's transcript in absolute file time.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<ProbeMark?> AcceptMatchAsync(
        PhraseMatch match, ProbeCandidate candidate, double start, double phraseAbs,
        List<TranscriptSegment> trimmedAbs, CancellationToken ct)
    {
        if (ResolveProbeMark(match, candidate, start, trimmedAbs) is not { } placement)
            return null;
        var (time, markSilence, markRegion) = placement;

        var markCtx = new MarkContext(_ctx.File, _ctx.Info.InputDecoder, Language.Profile!,
            _ctx.AllSilences, _ctx.SpeechSegments, trimmedAbs);
        time = await _env.Marks.PlaceAsync(
            match.Number, time, phraseAbs, markSilence, markRegion, markCtx, ct);

        if (match.SpansMerge)
            _env.Log?.Invoke($"chapter {match.Number} detection spans the reused/fresh transcript " +
                             "merge from Pass 2's overlap reuse - worth a spot check");

        _found.Add(new DetectedChapter(match.Number, time, match.Confidence));
        var (highest, missingNumbers) = ChapterProgress(_found, _env.Options.ExpectedStartChapter);
        _ctx.Work.HighestChapter = highest;
        _ctx.Work.MissingChapters = missingNumbers.Count;
        _env.Log?.Invoke($"chapter {match.Number} detected, mark placed at {FormatTimestamp(time)} " +
                         $"(confidence {match.Confidence:0.00}" +
                         await _env.Marks.LoudnessNoteAsync(time, markCtx, ct) +
                         $"){LowConfidenceNote(match.Confidence)}" +
                         MissingNote(missingNumbers));

        ObserveJingleLength(phraseAbs, start, markSilence, markRegion);
        return new ProbeMark(match.Number, markSilence, match.Confidence);
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
    /// Without it, the mark always goes <see cref="DefaultMarkLeadSeconds"/> before the phrase
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
    {
        var phraseAbs = start + match.PhraseStartSeconds;
        if (_env.Vad != null)
        {
            var candidateRegion = candidate.VadRegion is { } cvr &&
                phraseAbs >= cvr.StartSeconds - JinglePhraseMatchToleranceSeconds &&
                phraseAbs <= cvr.EndSeconds + JinglePhraseMatchToleranceSeconds
                ? candidate.VadRegion : null;
            var (markSilence, markRegion) = ResolveJingleAnchor(
                phraseAbs, start + match.PhraseEndSeconds, start, _ctx.AllSilences,
                _ctx.NonSpeechRegions, candidateRegion, _ctx.SpeechSegments, trimmedAbs);
            if (markSilence == null && markRegion == null)
                markSilence = candidate.Silence;
            var time = RefineDefaultMark(
                Math.Max(0, ResolveDefaultPhraseOnset(phraseAbs, markRegion, _ctx.SpeechSegments) - DefaultMarkLeadSeconds),
                _ctx.SpeechSegments);
            return (time, markSilence, markRegion);
        }

        if (match.PhraseStartSeconds <= PhraseLatestStart)
            return (Math.Max(0, phraseAbs - DefaultMarkLeadSeconds), candidate.Silence, null);

        // Each of the three ways this can fail is named separately rather than folded into one
        // rejection: two of them point straight at a --min-silence-length that is too strict for
        // this book, which is exactly what someone chasing a missing chapter needs to be told.
        if (FindRealAnchorSilence(start, phraseAbs, _ctx.AllSilences) is not { } anchor)
            return RejectProbeMark(match, phraseAbs, "no silence precedes it inside the probe window");
        if (phraseAbs - anchor.EndSeconds > PhraseLatestStart)
            return RejectProbeMark(match, phraseAbs,
                $"the nearest silence ends {phraseAbs - anchor.EndSeconds:0.0} s before it, " +
                $"more than the {PhraseLatestStart:0.#} s allowed");
        if (anchor.EndSeconds - anchor.StartSeconds < _env.Options.MinSilenceSeconds)
            return RejectProbeMark(match, phraseAbs,
                $"the silence before it is only {anchor.EndSeconds - anchor.StartSeconds:0.00} s long, " +
                $"below --min-silence-length {_env.Options.MinSilenceSeconds:0.##} s");
        return (Math.Max(0, phraseAbs - DefaultMarkLeadSeconds), anchor, null);
    }

    /// <summary>Logs why <see cref="ResolveProbeMark"/> is dropping a chapter number the recognizer
    /// did hear, and returns the null that stands for "no mark". A missing chapter is far easier to
    /// chase when the log distinguishes "never heard" from "heard, but unanchorable, because
    /// &lt;reason&gt;".</summary>
    /// <param name="match">The rejected phrase match.</param>
    /// <param name="phraseAbs">Absolute phrase start time, for the log line's timestamp.</param>
    /// <param name="reason">Why no mark could be placed, phrased to follow "skipped chapter N at T - ".</param>
    private (double Time, Silence? MarkSilence, NonSpeechRegion? MarkRegion)? RejectProbeMark(
        PhraseMatch match, double phraseAbs, string reason)
    {
        _env.Log?.Invoke($"skipped chapter {match.Number} at {FormatTimestamp(phraseAbs)} - {reason}");
        return null;
    }

    /// <summary>
    /// Applies everything one probe's marks change about the region's running state: a sequence gap
    /// triggers the re-probe of everything skipped since the last mark, each mark's anchor silence
    /// may tighten the --min-silence-length auto threshold, and the last accepted number advances.
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
            if (_lastNumber is { } previousNumber && mark.Number > previousNumber + 1 &&
                _skippedSinceLastMark.Count > 0)
                await ReprobeSkippedAsync(previousNumber, mark.Number, ct);

            if (_env.Options.AutoMinSilence)
                TightenThreshold(mark);
            _skippedSinceLastMark.Clear();
            _lastNumber = mark.Number;
        }
    }

    /// <summary>
    /// Re-probes, unconditionally and at the full ceiling window, every candidate skipped since the
    /// last mark, because the chapter numbers just found leave a gap that one of them may have
    /// hidden. The skipped candidates form their own little window sequence, each end computed on
    /// the fly against its next skipped neighbor so adjacent re-probe windows get snapped shared
    /// borders too; the window width cannot change mid-re-probe (see <see cref="_reprobing"/>), so
    /// consecutive ends stay consistent for the whole sequence.
    /// </summary>
    /// <param name="previousNumber">The chapter number below the gap.</param>
    /// <param name="number">The chapter number above it, i.e. the mark that revealed the gap.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task ReprobeSkippedAsync(int previousNumber, int number, CancellationToken ct)
    {
        _env.Log?.Invoke($"sequence gap between chapter {previousNumber} and {number}, " +
                         $"re-probing {_skippedSinceLastMark.Count} skipped candidate(s) unconditionally");
        if (_env.Vad != null && _env.Options.AutoMaxJingle && _probeSeconds != _ctx.JingleCeilingSeconds)
        {
            _probeSeconds = _ctx.JingleCeilingSeconds;
            _env.Log?.Invoke($"jingle probe window reset to {_probeSeconds:0.#} s for the re-probe");
        }
        _reprobing = true;
        for (var si = 0; si < _skippedSinceLastMark.Count; si++)
        {
            var gapMarks = await ProbeAsync(
                _skippedSinceLastMark[si], WindowEndFor(_skippedSinceLastMark, si), ct);
            if (!_env.Options.AutoMinSilence)
                continue;
            // A gap mark's anchor silence was, by definition, short enough to have been skipped -
            // fold it into the running minimum so the threshold can never again sit above it. Only
            // genuine gap-fillers count; a duplicate or re-detection of this window's own mark
            // surfacing in a re-probe must not lower anything.
            foreach (var gapMark in gapMarks)
                if (gapMark.Number > previousNumber && gapMark.Number < number)
                    ProposeThreshold(gapMark.MarkSilence);
        }
        _reprobing = false;
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

        // The first set is a raise from the floor ("tightened"), everything after can only ever be
        // a lowering.
        var newThreshold = _adaptedThresholdSeconds ?? _env.Options.MinSilenceSeconds;
        if (newThreshold != _threshold)
            _env.Log?.Invoke($"threshold {(newThreshold > _threshold ? "tightened" : "lowered")} " +
                             $"to {newThreshold:0.##} s after chapter {mark.Number}");
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
