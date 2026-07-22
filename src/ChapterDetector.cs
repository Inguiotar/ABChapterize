// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using System.Text;
using System.Text.RegularExpressions;

namespace ABChapterize;

/// <summary>A detected chapter start: number plus position in the file.</summary>
/// <param name="Number">Chapter number as spoken/parsed.</param>
/// <param name="TimeSeconds">Position of the chapter marking in seconds.</param>
/// <param name="Confidence">Whisper's probability for the segment the chapter number was parsed
/// from (0-1); 1.0 when unknown. Below <see cref="ChapterDetector.LowConfidenceThreshold"/> the
/// number surfaces in <see cref="DetectionResult.LowConfidenceNumbers"/>.</param>
public readonly record struct DetectedChapter(int Number, double TimeSeconds, double Confidence = 1.0);

/// <summary>Per-file diagnostic statistics gathered during detection, surfaced per file under
/// --verbose (or --verbose-transcripts) and aggregated run-wide under --summary.</summary>
/// <param name="MinPrecedingSilenceSeconds">The shortest silence found directly before a detected
/// chapter phrase - in --jingle mode the silence leading the jingle (a jingle framed by two
/// silences counts only its leading one); null when no chapter had a qualifying preceding
/// silence.</param>
/// <param name="MaxJingleLengthSeconds">The longest jingle found before a detected chapter phrase
/// (--jingle mode only); null in plain mode, or when no jingle was measured.</param>
/// <param name="WhisperAudioSeconds">Total audio decoded and handed to Whisper during detection,
/// counting re-probed stretches each time they were transcribed; compare against the file's run
/// length for the fed-in share.</param>
public readonly record struct DetectionStats(
    double? MinPrecedingSilenceSeconds, double? MaxJingleLengthSeconds, double WhisperAudioSeconds);

/// <summary>Outcome of chapter detection for one file.</summary>
/// <param name="Chapters">Detected chapters in chronological order; empty when none were found.</param>
/// <param name="GapRemains">True when a chapter sequence gap could not be resolved; the file must be left unchanged.</param>
/// <param name="MissingNumbers">The chapter numbers that could not be located (only when <paramref name="GapRemains"/>).</param>
/// <param name="LowConfidenceNumbers">Chapter numbers whose Whisper probability fell below
/// <see cref="ChapterDetector.LowConfidenceThreshold"/> - worth a manual spot-check.</param>
/// <param name="Profile">The language profile actually used for this file - the resolved
/// per-file profile with <c>--lang auto</c>, or the run's fixed <see cref="CliOptions.DefaultProfile"/>
/// otherwise.</param>
/// <param name="DetectedLanguage">Whisper's raw language guess with <c>--lang auto</c>; null
/// when auto-detection was not active, or was skipped because the file was too short to probe.</param>
/// <param name="DetectedProbability">Whisper's probability for <paramref name="DetectedLanguage"/>;
/// 0 when <paramref name="DetectedLanguage"/> is null. Note this may differ from
/// <see cref="Profile"/>'s language when the probability fell below
/// <see cref="ChapterDetector.AutoLanguageProbabilityThreshold"/> and the run fell back to English.</param>
/// <param name="Stats">Per-file diagnostic statistics (min preceding silence, max jingle, total
/// Whisper audio) for the --verbose and --summary reports.</param>
public readonly record struct DetectionResult(
    IReadOnlyList<DetectedChapter> Chapters, bool GapRemains, IReadOnlyList<int> MissingNumbers,
    IReadOnlyList<int> LowConfidenceNumbers, LanguageProfile Profile,
    string? DetectedLanguage, double DetectedProbability, DetectionStats Stats);

/// <summary>Outcome of checking pre-existing chapter markings against the audio (--verify).</summary>
/// <param name="Passed">True when every checkable marking was confirmed; also true when none
/// of the file's markings had a parseable expected number (nothing to disprove).</param>
/// <param name="Checked">Number of markings that had a parseable expected number and were
/// actually probed. Markings without one (e.g. a prelude/intro entry) are not counted.</param>
/// <param name="Failed">Of <paramref name="Checked"/>, how many could not be confirmed.</param>
public readonly record struct VerifyResult(bool Passed, int Checked, int Failed);

/// <summary>
/// Finds chapter starts in an audiobook. Fast path: detect longer-than-usual silences and
/// probe the audio following each silence with Whisper. If the resulting chapter numbers
/// contain sequence gaps, the audio between the mismatched markings is fully transcribed.
/// </summary>
public sealed class ChapterDetector
{
    /// <summary>Noise floor in dBFS for silence detection.</summary>
    private const int SilenceNoiseDb = -35;

    /// <summary>Probe window length in seconds when no jingle is expected.
    /// With --jingle the window is --max-jingle-length seconds instead.</summary>
    private const double ProbeSecondsPlain = 12;

    /// <summary>
    /// The shortest silence Pass 1 retains in memory (see the <c>allSilences</c>/<c>silences</c>
    /// split in <see cref="DetectAsync"/>) for use as a window-seam snap target (see
    /// <see cref="FindNearestSeam"/> and its callers: Pass 2's window plan, the reuse-time
    /// split, and Pass 3's chunk borders) and for pinpointing a mark at the silence directly
    /// preceding its phrase, regardless of how high --min-silence-length is set.
    /// Only silences at or above --min-silence-length are ever reported as Pass 2 candidates or
    /// logged; this lower floor exists purely so a silence-mid-point seam (or a mark anchor) is
    /// available even when the nearest real silence is shorter than the book's candidate
    /// threshold. Kept low enough to catch ordinary clause pauses without noticeably
    /// growing Pass 1's silence list.
    /// </summary>
    private const double MinStoredSilenceSeconds = 0.5;

    /// <summary>
    /// How far past a Pass 2 window's natural end <see cref="PlanWindowEnd"/> searches for a
    /// seam when that end does not lie inside the next window (no shared border to snap): the
    /// nearest silence - or, with --jingle, VAD non-speech region - mid-point within this many
    /// seconds after the natural end becomes the window's end, so even a stand-alone window
    /// stops at a word-safe cut instead of possibly mid-word (a mid-word tail is exactly what
    /// makes Whisper garble a window's final phrase). Extension only: a target before the
    /// natural end would shrink the window and could cut off the very phrase the probe exists
    /// to find. When no target lies within reach, the window keeps its natural length.
    /// </summary>
    private const double WindowEndSnapSearchSeconds = 5.0;

    /// <summary>Without a jingle the phrase must start within this many seconds after the silence.</summary>
    private const double PhraseLatestStart = 5.0;

    /// <summary>Flat margin added to --max-jingle-length so the phrase after the jingle
    /// still fits into the probe window.</summary>
    private const double PhraseMarginSeconds = 5.0;

    /// <summary>Chapter marks are placed this many seconds before a jingle (per specification).</summary>
    private const double JingleLeadSeconds = 0.5;

    /// <summary>
    /// Slack allowed when matching a VAD non-speech region (the jingle) to a Whisper phrase: the
    /// region's end is where VAD resumes detecting speech, which should coincide with the phrase
    /// start, but the two detectors time boundaries slightly differently - Whisper's segment
    /// timestamps can be a touch earlier (coarser) than VAD's frame-precise resume, leaving the
    /// region ending just <em>after</em> the phrase start. Without this slack such a region would
    /// be missed and a silence-less jingle would fall back to the (possibly false) nearest
    /// silence. Kept small - far below any real jingle length or inter-chapter spacing - so it
    /// only absorbs boundary jitter and can never grab an unrelated, later non-speech region.
    /// </summary>
    private const double JinglePhraseMatchToleranceSeconds = 0.5;

    /// <summary>
    /// Slack allowed when deciding a Whisper segment <em>starts with</em> a stored silence or VAD
    /// non-speech region (see <see cref="TrimLeadingNonSpeech"/>). Whisper timestamps a segment
    /// from where its decoded audio block begins, which can be a touch before silencedetect's or
    /// VAD's frame-precise onset; without this slack a silence starting a hair after the segment's
    /// timestamp would not be recognised as leading it. Kept small so it only absorbs that
    /// boundary jitter and never trims a segment that genuinely opens with speech.
    /// </summary>
    private const double SegmentLeadTrimToleranceSeconds = 0.5;

    /// <summary>
    /// The shortest span this codebase ever treats as "plausibly a real jingle". Used two ways:
    /// (1) a VAD non-speech region whose longest single contiguous run is shorter than this (see
    /// <see cref="ComputeNonSpeechRegions"/> for why the longest run, not the merged span) is
    /// dropped outright rather than ever becoming a candidate - too short to be a jingle at any
    /// book's pacing, more likely an in-narration breath pause VAD happened to classify as
    /// non-speech; (2) with
    /// --max-jingle-length auto, an observed phrase offset below this is treated as "this chapter
    /// had no jingle (or an ultra-short one)" and excluded from tightening the probe window: some
    /// audiobooks only play the jingle for some chapters, and such a chapter gives no information
    /// about how long the window needs to be for chapters that do have one - using it anyway
    /// could shrink the window before a later, genuinely full-length jingle is ever probed.
    /// </summary>
    private const double MinJingleObservationSeconds = 2.0;

    /// <summary>
    /// With --jingle, a VAD "speech" segment shorter than this, sandwiched between two non-speech
    /// regions, does not end the surrounding jingle - the two regions are merged and the blip is
    /// treated as VAD noise rather than a genuine return to narration. Silero VAD is not reliable
    /// on jingle music: a vocal-like transient or a strong rhythmic passage can cross its speech
    /// threshold for a fraction of a second in the middle of an otherwise instrumental jingle,
    /// which would otherwise fragment one continuous jingle into several too-short regions (see
    /// <see cref="ComputeNonSpeechRegions"/>). Deliberately well below any real inter-chapter
    /// narration gap, so a genuine speech resume is never merged away.
    /// </summary>
    private const double MergeShortSpeechGapSeconds = 1.0;

    /// <summary>
    /// With --max-jingle-length auto, the resized probe window is this factor times the
    /// longest jingle observed so far (plus <see cref="PhraseMarginSeconds"/>), leaving a 25 %
    /// safety margin above the longest observed jingle for normal length variation between
    /// chapters. Applied monotonically: after the first observation (at the second mark) sets
    /// the window, later observations can only widen it, never narrow it - a window below an
    /// already observed jingle length would, by definition, have been too short for that
    /// chapter's own jingle. The exact mirror of <see cref="AdaptiveTightenFactor"/>.
    /// </summary>
    private const double JingleObservationSafetyFactor = 1.25;

    /// <summary>
    /// With --min-silence-length auto, the Pass 2 probing threshold is this factor times a
    /// mark's anchor silence length, leaving a 25 % safety margin below the shortest observed
    /// inter-chapter break - matching <see cref="JingleObservationSafetyFactor"/>'s 25 % margin
    /// on the jingle side. Applied monotonically: the first qualifying mark (the second one
    /// found) raises the threshold from the floor; every later mark can only lower it again
    /// (when its anchor silence comes too close to the current threshold), never raise it - a
    /// threshold above an already observed inter-chapter silence would, by definition, skip
    /// the very kind of silence that has proven to precede this book's chapters.
    /// </summary>
    private const double AdaptiveTightenFactor = 0.75;

    /// <summary>Chunk length in seconds for full transcription of gap regions.</summary>
    private const double GapChunkSeconds = 600;

    /// <summary>Overlap between gap transcription chunks so no phrase is cut in half. Only
    /// applies to a chunk border that could not be snapped to a word-safe seam (see
    /// <see cref="Pass3SeamSearchSeconds"/>); snapped borders abut exactly and need no
    /// overlap redundancy.</summary>
    private const double GapChunkOverlapSeconds = 10;

    /// <summary>
    /// How far around a Pass 3 chunk's natural border the seam search reaches, in both
    /// directions: the border snaps to the nearest silence - or, with --jingle, VAD non-speech
    /// region - mid-point within this range, and the next chunk then starts exactly at that
    /// seam, with no overlap and nothing decoded twice. Bounded so a chunk can grow to at most
    /// <see cref="GapChunkSeconds"/> plus this: whisper.cpp has no hard input-length cap (it
    /// decodes any length in internal 30 s strides), but the decoded sample buffer scales with
    /// chunk length, so the growth is kept to a small fraction of the chunk.
    /// </summary>
    private const double Pass3SeamSearchSeconds = 30;

    /// <summary>
    /// At a snapped (overlap-free) Pass 3 seam, segments of the previous chunk ending within
    /// this many seconds before the seam are carried into the next chunk's phrase matching, so
    /// a chapter phrase straddling the seam itself - the narrator pausing mid-announcement
    /// right where the seam silence sits, e.g. between "Chapter" and its number - is still
    /// found even though neither chunk alone contains the whole phrase. Comfortably longer
    /// than any spoken chapter announcement. Irrelevant at unsnapped borders, where the
    /// <see cref="GapChunkOverlapSeconds"/> overlap provides the redundancy instead.
    /// </summary>
    private const double Pass3BridgeSeconds = 15;

    /// <summary>Whisper segment probability below which a chapter detection is flagged as
    /// low-confidence instead of being silently trusted. 0.5 was chosen as the point below
    /// which Whisper itself is, on average, more unsure than sure about the words it heard.</summary>
    internal const double LowConfidenceThreshold = 0.5;

    /// <summary>
    /// Whisper language-detection probability below which the result is treated as
    /// inconclusive and the run falls back to English for that file, with <c>--lang auto</c>.
    /// Reuses the same 0.5 cutoff as <see cref="LowConfidenceThreshold"/>: below it, Whisper
    /// itself is, on average, more unsure than sure about its own guess.
    /// </summary>
    internal const double AutoLanguageProbabilityThreshold = 0.5;

    /// <summary>How far before a pre-existing chapter marking's own timestamp --verify starts
    /// probing - the marking may sit slightly after the phrase actually started.</summary>
    private const double VerifyMarginBeforeSeconds = 5;

    /// <summary>Total length of the --verify probe window, starting <see
    /// cref="VerifyMarginBeforeSeconds"/> before the marking. Comparable in scale to the plain
    /// post-silence probe window (<see cref="ProbeSecondsPlain"/>).</summary>
    private const double VerifyWindowSeconds = 15;

    private readonly CliOptions _options;
    private readonly IAudioSource _audio;
    private readonly ITranscriber _transcriber;
    private readonly IVoiceActivityDetector? _vad;

    /// <summary>Per-file --verbose log sink set by <see cref="DetectAsync"/>; null when not verbose.</summary>
    private Action<string>? _log;

    /// <summary>Total seconds of audio actually decoded and handed to Whisper during the current
    /// file's detection (every probe window and gap chunk, counted each time it is transcribed -
    /// re-probed audio counts again, since Whisper processed it again). Reset per file, reported
    /// as a --verbose/--summary statistic.</summary>
    private double _whisperAudioSeconds;

    /// <summary>Per detected chapter number, the length of the silence that preceded its phrase
    /// (in --jingle mode, the silence preceding the jingle - see <see cref="RecordChapterStats"/>).
    /// Keyed by number so a re-detection overwrites; filtered to the surviving chapters when the
    /// per-file minimum is computed. Reset per file.</summary>
    private readonly Dictionary<int, double> _chapterSilenceSeconds = [];

    /// <summary>Per detected chapter number, the length of the jingle that preceded its phrase
    /// (--jingle mode only). Reset per file; feeds the per-file maximum-jingle statistic.</summary>
    private readonly Dictionary<int, double> _chapterJingleSeconds = [];

    /// <summary>Creates a detector bound to the given tools and options.</summary>
    /// <param name="options">Validated command line options.</param>
    /// <param name="audio">Audio source used for silence detection and PCM decoding.</param>
    /// <param name="transcriber">Loaded speech recognizer.</param>
    /// <param name="vad">Voice activity detector used for the --jingle full-file pre-pass
    /// (finds jingle transitions with no detectable amplitude gap); null when --jingle is not
    /// in effect, or in tests that don't exercise that path.</param>
    public ChapterDetector(CliOptions options, IAudioSource audio, ITranscriber transcriber, IVoiceActivityDetector? vad = null)
    {
        _options = options;
        _audio = audio;
        _transcriber = transcriber;
        _vad = vad;
    }

    /// <summary>
    /// Runs the complete detection pipeline for one file.
    /// </summary>
    /// <param name="file">Path of the audio file.</param>
    /// <param name="info">Probe result of the file.</param>
    /// <param name="work">Progress tracker fed with processed bytes.</param>
    /// <param name="log">Sink for --verbose log messages, or null when not verbose.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<DetectionResult> DetectAsync(
        string file, MediaInfo info, WorkTracker work, Action<string>? log, CancellationToken ct)
    {
        _log = log;
        _whisperAudioSeconds = 0;
        _chapterSilenceSeconds.Clear();
        _chapterJingleSeconds.Clear();
        var bytesPerSecond = info.DurationSeconds > 0 ? info.SizeBytes / info.DurationSeconds : 0;
        var jingleCeilingSeconds = _options.MaxJingleSeconds + PhraseMarginSeconds;
        var probeSeconds = _options.Jingle ? jingleCeilingSeconds : ProbeSecondsPlain;

        // With --max-jingle-length auto, the adapted probe window: JingleObservationSafetyFactor
        // times the longest real inter-chapter jingle observed so far, plus PhraseMarginSeconds,
        // capped at the ceiling. Null until the first qualifying observation (from the second
        // mark found - see the AutoMinSilence precedent below for why the first is excluded;
        // chapters with no/an ultra-short jingle are excluded too, see
        // MinJingleObservationSeconds); monotonically increasing from then on (see
        // JingleObservationSafetyFactor). probeSeconds (captured by ProbeAsync below) follows it.
        double? adaptedWindowSeconds = null;

        // True while the sequence-gap recovery in the candidate loop below re-probes skipped
        // candidates at the full ceiling window: observations made during the re-probe still
        // feed adaptedWindowSeconds, but must not pull probeSeconds back down mid-re-probe -
        // the whole point of the reset is that every re-probe runs at the ceiling.
        var reprobing = false;

        // Pass 1: silence scan (one full pass over the file). With --jingle, a VAD pre-pass
        // runs concurrently over the very same decode (see DetectSilencesAndStreamPcmAsync) -
        // silencedetect alone never produces a Pass 2 candidate at a chapter transition where
        // the jingle abuts speech on both sides with no amplitude gap; VAD sees that transition
        // as a non-speech region (music, like silence, reads as non-speech to a speech
        // detector) regardless of amplitude, so it can catch what silencedetect misses. See
        // ComputeJingleMark for how the two detectors' findings combine to place the mark.
        work.BeginPhase("Pass 1", info.SizeBytes);
        // The scan itself always goes down to MinStoredSilenceSeconds (or --min-silence-length
        // itself, if that is lower still) so short silences are available for overlap-border
        // snapping (see FindOverlapSplitPoint); allSilences holds every one of those, while
        // `silences` - used everywhere else below exactly as before this feature existed - keeps
        // only the ones at or above --min-silence-length.
        var storedSilenceFloor = Math.Min(_options.MinSilenceSeconds, MinStoredSilenceSeconds);
        List<Silence> allSilences;
        var nonSpeechRegions = new List<NonSpeechRegion>();
        if (_options.Jingle && _vad is { } vad)
        {
            List<SpeechSegment> speech = [];
            allSilences = await _audio.DetectSilencesAndStreamPcmAsync(
                file, info.DurationSeconds, storedSilenceFloor, SilenceNoiseDb,
                async (pcm, innerCt) => speech = await vad.DetectSpeechAsync(pcm, innerCt),
                seconds => work.SetPhaseProgress((long)(seconds * bytesPerSecond)), info.InputDecoder, ct);
            nonSpeechRegions = ComputeNonSpeechRegions(speech);
        }
        else
        {
            allSilences = await _audio.DetectSilencesAsync(
                file, info.DurationSeconds, storedSilenceFloor, SilenceNoiseDb,
                seconds => work.SetPhaseProgress((long)(seconds * bytesPerSecond)), info.InputDecoder, ct);
        }
        var silences = allSilences
            .Where(s => s.EndSeconds - s.StartSeconds >= _options.MinSilenceSeconds).ToList();

        _log?.Invoke($"Pass 1: {silences.Count} silence(s) of >= " +
                     $"{_options.MinSilenceSeconds:0.#} s found" + (_options.AutoMinSilence ? " (adaptive threshold)" : ""));
        if (_options.Jingle && _vad != null)
            // speech.Count and nonSpeechRegions.Count always differ by exactly one (a non-speech
            // region is the gap between two consecutive speech segments) before the merge/filter
            // cleanup below can drop some, and always differ by at most one afterwards - the
            // region count alone is the actionable number, so only it is logged.
            _log?.Invoke($"Pass 1: {nonSpeechRegions.Count} non-speech region(s) found");

        // Pass 2: probe the beginning of the file and the end of every silence. With
        // --min-silence-length auto, ProbeThresholdTightening below can skip some of these
        // candidates instead of probing every one.
        var candidates = new List<(double Start, Silence? Silence, NonSpeechRegion? VadRegion)> { (0, null, null) };
        candidates.AddRange(silences
            .Where(s => s.EndSeconds < info.DurationSeconds - 1)
            .Select(s => ((double)s.EndSeconds, (Silence?)s, (NonSpeechRegion?)null)));

        // Add a VAD candidate for every silence-less non-speech region: a silencedetect
        // silence already leading the region means the existing silence candidate above
        // probes the same transition, so skip it there (dedup - the silence path stays
        // primary). The lower length bound is already guaranteed by ComputeNonSpeechRegions'
        // own MinJingleObservationSeconds filter, kept here as well as a defensive invariant;
        // the upper bound (regions too long to ever be this book's jingle) is rechecked against
        // the (possibly since-narrowed) probe window inside the Pass 2 loop below.
        if (_options.Jingle)
        {
            foreach (var region in nonSpeechRegions)
            {
                var jingleStart = JingleStart(region, silences);
                if (jingleStart != region.StartSeconds)
                    continue;
                var length = region.EndSeconds - jingleStart;
                if (length < MinJingleObservationSeconds || length > jingleCeilingSeconds)
                    continue;
                candidates.Add((jingleStart, null, region));
            }
            candidates = candidates.OrderBy(c => c.Start).ToList();
        }

        // Each probe window's end is computed on the fly, right before its probe runs (see
        // PlanWindowEnd): an overlapping neighbor gets the shared border snapped to a silence
        // mid-point, moving this window's decode end itself - possibly beyond its natural end -
        // rather than merely choosing where to stop reusing cache after the fact. Deciding per
        // window instead of pre-planning the whole list keeps every end consistent with the
        // probeSeconds in effect at that moment - the adaptive resizes below apply to the very
        // next window, with no stale bulk plan to recompute and drift away from what earlier
        // probes actually decoded.
        double WindowEndFor(IReadOnlyList<(double Start, Silence? Silence, NonSpeechRegion? VadRegion)> list, int index)
            => PlanWindowEnd(list[index].Start,
                index + 1 < list.Count ? list[index + 1].Start : null,
                probeSeconds, info.DurationSeconds, allSilences, nonSpeechRegions, _options.Jingle);

        // Pass 2 progress is position-based: the bar shows how far into the file's play time the
        // current candidate lies, not how many probes have run. Probe costs vary wildly (full
        // window decode vs. reused overlap vs. skipped candidate), so a fixed per-probe byte
        // budget drifts far off; position over total play time is honest about *where* the pass
        // is, at the price of nonlinear - and, during gap re-probes, briefly backwards - movement.
        work.BeginPhase("Pass 2", info.SizeBytes);

        // With --lang auto, the language is resolved once per file, from the very first probe
        // window's samples (always at start 0, decoded below like any other window - no extra
        // decode needed) - then fixed for the rest of the file via ChangeLanguage, rather than
        // re-detected per probe, which would be both slower and inconsistent within one file.
        LanguageProfile? profile = null;
        string? detectedLanguage = null;
        var detectedProbability = 0.0;

        var found = new List<DetectedChapter>();
        // Declared here (rather than with the rest of the adaptive-threshold state below) so
        // ProbeAsync, defined next, can read it - set to the latest accepted chapter mark's
        // number by the main candidate loop, still holding the previous value (not yet this
        // window's) while a probe is in flight, which is exactly what a gap re-probe needs to
        // accept the in-between chapter numbers.
        int? lastNumber = null;

        // Transcript cache for Pass 2's overlapping-window reuse. Holds the previous probe's full
        // window transcript in absolute file time, together with the absolute [from, to) span that
        // transcript actually covers. When the next candidate's window overlaps this span, the
        // overlapping segments are reused verbatim instead of being re-run through Whisper - only
        // the fresh tail beyond the planned seam is decoded. The cache-span test below (start
        // inside [cacheFrom, cacheTo)) doubles as the seam-stitching check: it holds exactly when
        // the previous window really was decoded up to the seam this window's plan relies on, and
        // when it does not (e.g. that window was skipped by the adaptive threshold), the probe
        // falls back to decoding its full window from the candidate start - nothing is ever left
        // covered by neither decode. cacheTo starts at negative infinity so the very first probe
        // (start 0) never counts as an overlap and always does a full transcribe - which is also
        // where --lang auto resolves the language from full samples.
        List<TranscriptSegment> cacheSegmentsAbs = [];
        var cacheFrom = 0.0;
        var cacheTo = double.NegativeInfinity;

        // Probes a single window and appends every chapter mark found in it to `found`. Since
        // segment timestamps plus the full stored silence list let every detection be
        // pinpointed independently of the triggering candidate, one window can yield several
        // marks (e.g. a wide --jingle window covering two transitions) - there is no
        // one-chapter-per-window early return anymore. Returns the accepted marks in window
        // order, each with the silence its mark falls into - null when the mark sits on a VAD
        // region (or nothing at all) - for the --min-silence-length auto tightening, plus
        // Whisper's confidence for the candidate loop's sequence-skip decision. windowEnd is
        // the window's *planned* end (see PlanWindowEnd) - possibly snapped away from the
        // natural start + probeSeconds - while the candidate start stays the semantic anchor
        // for the phrase-timing rule and progress, both of which are relative to the
        // triggering silence, not to whatever seam the plan chose.
        async Task<List<(int Number, Silence? MarkSilence, double Confidence)>> ProbeAsync(
            (double Start, Silence? Silence, NonSpeechRegion? VadRegion) candidate, double windowEnd)
        {
            var start = candidate.Start;
            ct.ThrowIfCancellationRequested();
            // Position-based Pass 2 progress (see BeginPhase above); reported here rather than
            // only in the candidate loop so gap re-probes show their (backwards) position too.
            work.SetPhaseProgress((long)(start * bytesPerSecond));

            // This window's full transcript in absolute file time, assembled from the previous
            // window's cache (overlap reuse), a fresh Whisper decode, or a mix. The whole window
            // is always represented, so nothing a reuse-only "search just the new tail" scheme
            // would silently drop - e.g. a phrase the previous probe rejected for want of a
            // qualifying anchor that this window can anchor - is ever lost.
            //
            // --verbose logging only ever shows what Whisper actually transcribed just now, at its
            // own (0-based) timestamps - never the reused portion restated at window-relative time,
            // which would make every probe look like a fresh full-window decode even when most of it
            // was cache. Segments used for phrase matching below (`segments`) are unaffected; only
            // what gets logged changes.
            List<TranscriptSegment> windowSegmentsAbs;
            // Set only by the partial-overlap branch below, to the count of reused segments that
            // precede the fresh tail in windowSegmentsAbs - i.e. the index of the first fresh
            // segment. Passed to FindPhraseMatches so it can flag a detection that draws on text
            // from both sides of the cache/fresh boundary (see PhraseMatch.SpansMerge).
            int? mergeBoundarySegIndex = null;

            // A window whose start falls inside the cached span overlaps the previous one.
            if (start >= cacheFrom && start < cacheTo)
            {
                if (windowEnd <= cacheTo)
                {
                    // Fully contained in the previous window: reuse its transcript wholesale, no
                    // Whisper at all. The (larger) cache is deliberately left untouched so a later
                    // candidate starting within it can keep reusing it too.
                    windowSegmentsAbs = cacheSegmentsAbs
                        .Where(s => s.StartSeconds >= start && s.StartSeconds < windowEnd).ToList();
                    _log?.Invoke($"probe @{FormatTimestamp(start)}: fully reused, no new transcription");
                }
                else
                {
                    // Partial overlap: cut between the reused cache and the fresh tail decode.
                    // The previous window's end was planned as a seam snapped to a silence
                    // mid-point inside this window (see PlanWindowEnd), so the cache normally
                    // ends exactly at that seam and this restricted search simply re-finds it
                    // at distance zero - the fresh decode starts right where the previous
                    // window's decode stopped, stitching the two transcripts together
                    // word-safely with nothing re-decoded and nothing dropped. It genuinely
                    // decides only for overlaps that plan did not anticipate (a probe-window
                    // resize since the previous window was probed), where it snaps to the best
                    // seam still covered by the cache; the border fallback means no seam
                    // exists, which also means no chapter transition sits in the overlap.
                    var splitPoint = FindOverlapSplitPoint(
                        start, cacheTo, windowEnd, allSilences, nonSpeechRegions, _options.Jingle,
                        allowBeyondBorder: false);
                    var samples = await _audio.DecodePcmAsync(file, splitPoint,
                        windowEnd - splitPoint, info.InputDecoder, ct);
                    var fresh = await TranscribeCountingAsync(samples, ct);
                    var reused = cacheSegmentsAbs
                        .Where(s => s.StartSeconds >= start && s.StartSeconds < splitPoint).ToList();
                    windowSegmentsAbs = reused.Concat(ShiftSegments(fresh, splitPoint)).ToList();
                    mergeBoundarySegIndex = reused.Count;
                    cacheSegmentsAbs = windowSegmentsAbs;
                    cacheFrom = start;
                    cacheTo = windowEnd;
                    LogTranscript($"probe {windowEnd - splitPoint:0.#}s@{FormatTimestamp(splitPoint)} (tail)", fresh);
                }
            }
            else
            {
                // No usable overlap - transcribe the whole window. This is also where --lang auto
                // resolves the language, once, from the very first probe's full samples.
                var samples = await _audio.DecodePcmAsync(file, start,
                    windowEnd - start, info.InputDecoder, ct);

                if (profile == null)
                {
                    (profile, detectedLanguage, detectedProbability) = await ResolveLanguageAsync(samples, ct);
                    _transcriber.ChangeLanguage(profile.Language);
                }

                var fresh = await TranscribeCountingAsync(samples, ct);
                windowSegmentsAbs = ShiftSegments(fresh, start);
                cacheSegmentsAbs = windowSegmentsAbs;
                cacheFrom = start;
                cacheTo = windowEnd;
                LogTranscript($"probe {windowEnd - start:0.#}s@{FormatTimestamp(start)}", fresh);
            }

            // Correct segment starts that Whisper timestamped from a leading silence/jingle
            // before shifting to window-relative time (the cache keeps the raw absolute timings
            // its reuse math relies on). FindPhraseMatches and the mark-placement math below
            // then work in window-relative time.
            var segments = ShiftSegments(
                TrimLeadingNonSpeech(windowSegmentsAbs, allSilences, nonSpeechRegions, _options.Jingle),
                -start);

            var marks = new List<(int Number, Silence? MarkSilence, double Confidence)>();
            // Window-local continuation of lastNumber: several accepted marks within one
            // window must each top the previous one, exactly as consecutive windows' marks do.
            var windowLast = lastNumber ?? 0;

            // profile is resolved on the first probe, which is always a full decode (the cache is
            // empty then), so it is non-null by the time any transcript-reuse branch above runs.
            foreach (var match in FindPhraseMatches(segments, profile!, mergeBoundarySegIndex))
            {
                // A duplicate or regression (an in-text mention like "as seen in chapter
                // three", or a re-detection of an already-marked chapter) does not end the
                // window - skip it and keep scanning, so a real announcement later in the
                // same window is still found.
                if (match.Number <= windowLast)
                    continue;

                var phraseAbs = start + match.PhraseStartSeconds;
                // The silence the mark is placed into (feeds the --min-silence-length auto
                // tightening; null when the mark sits on a VAD region or on nothing at all)
                // and, with --jingle, the region it sits on (feeds --max-jingle-length auto).
                Silence? markSilence;
                NonSpeechRegion? markRegion = null;
                double time;
                if (_options.Jingle)
                {
                    // With --jingle, anchor to the VAD jingle region ending at the phrase, not to
                    // whichever silence triggered this probe: a false in-text pause earlier in the
                    // previous chapter does not lead that region, so it must not become the anchor
                    // (which would mark at the pause and feed the auto mechanisms a bogus jingle
                    // length). See ResolveJingleAnchor. The candidate's own VAD region is used
                    // directly only when this phrase is plausibly attached to it - a second
                    // announcement further along the window belongs to a different transition
                    // and must re-derive its own anchor. When neither a region nor a closer
                    // silence was found, fall back to this probe's own triggering silence.
                    var candidateRegion = candidate.VadRegion is { } cvr &&
                        phraseAbs >= cvr.StartSeconds - JinglePhraseMatchToleranceSeconds &&
                        phraseAbs <= cvr.EndSeconds + JinglePhraseMatchToleranceSeconds
                        ? candidate.VadRegion : null;
                    (markSilence, markRegion) = ResolveJingleAnchor(
                        phraseAbs, start, allSilences, nonSpeechRegions, candidateRegion);
                    if (markSilence == null && markRegion == null)
                        markSilence = candidate.Silence;
                    time = ComputeJingleMark(phraseAbs, markSilence, markRegion?.StartSeconds);
                }
                else if (match.PhraseStartSeconds <= PhraseLatestStart)
                {
                    // The classic shape: the phrase directly follows the triggering silence,
                    // whose end (the candidate start) is where the mark belongs. A match at
                    // the very start of the file has no triggering silence; the phrase
                    // position itself is the mark there.
                    time = Math.Max(0, start + (start == 0 ? match.PhraseStartSeconds : 0));
                    markSilence = candidate.Silence;
                }
                else
                {
                    // The phrase sits deeper in the window than the timing rule allows for the
                    // triggering silence - but with segment timestamps and the full stored
                    // silence list, it can still be accepted right away (no need to wait for a
                    // later candidate's own window to re-find it) when a candidate-grade
                    // silence directly precedes it: the phrase must follow that silence within
                    // the same 5 s the classic rule grants, and the silence must be at least
                    // --min-silence-length long - a shorter breath pause in front of an in-text
                    // mention ("Chapter eight had been hard.") must not qualify as an anchor.
                    // The mark is pinpointed at that silence's end, exactly where a probe
                    // triggered by the silence itself would have placed it.
                    var anchor = FindRealAnchorSilence(start, phraseAbs, allSilences);
                    if (anchor is not { } a
                        || phraseAbs - a.EndSeconds > PhraseLatestStart
                        || a.EndSeconds - a.StartSeconds < _options.MinSilenceSeconds)
                        continue;
                    time = a.EndSeconds;
                    markSilence = a;
                }

                if (match.SpansMerge)
                    _log?.Invoke($"chapter {match.Number} detection spans the reused/fresh transcript " +
                                 "merge from Pass 2's overlap reuse - worth a spot check");

                found.Add(new DetectedChapter(match.Number, time, match.Confidence));
                marks.Add((match.Number, markSilence, match.Confidence));
                RecordChapterStats(match.Number, markSilence, markRegion, phraseAbs);
                windowLast = match.Number;
                var (highest, missingNumbers) = ChapterProgress(found);
                work.HighestChapter = highest;
                work.MissingChapters = missingNumbers.Count;
                _log?.Invoke($"chapter {match.Number} detected, mark placed at {FormatTimestamp(time)} " +
                             $"(confidence {match.Confidence:0.00}){LowConfidenceNote(match.Confidence)}" +
                             MissingNote(missingNumbers));

                if (_options.Jingle && _options.AutoMaxJingle && found.Count > 1)
                {
                    // found.Count > 1 means this is at least the second mark found, so its
                    // anchor is a real inter-chapter jingle - not the intro-to-chapter-1 gap,
                    // which can easily run longer (or shorter) than a book's regular jingles
                    // and would otherwise size the window off a one-off observation before
                    // any real jingle has even been seen. Same reasoning as the analogous
                    // --min-silence-length auto tightening in the candidate loop below.
                    // The jingle length is measured from the silence/region the mark actually
                    // falls into (see above) - using the raw offset from this probe's own
                    // window start would inflate the observation whenever a false, earlier
                    // in-text pause was what actually triggered this probe. When the anchor is
                    // a VAD region (no leading silence), the length runs from the region start
                    // to the phrase, clipped at the region end: the announcement is often spoken
                    // inside the jingle (so the phrase precedes the region end), and the region
                    // end can itself be inflated when ComputeNonSpeechRegions' short-speech-gap
                    // merge swallowed that announcement - either way the phrase bounds the jingle.
                    var observedLength = markSilence is { } ras
                        ? phraseAbs - ras.EndSeconds
                        : markRegion is { } rvr
                            ? Math.Min(rvr.EndSeconds, phraseAbs) - rvr.StartSeconds
                            : phraseAbs - start;
                    if (observedLength >= MinJingleObservationSeconds)
                    {
                        // The window this observation asks for; the adapted window is the
                        // running maximum of these (monotonically increasing after the first
                        // set - see JingleObservationSafetyFactor), capped at the original
                        // ceiling so an outlier can never make the window wider than what
                        // --max-jingle-length was given (or its 45 s default) would allow.
                        // During a gap re-probe only the running maximum is updated;
                        // probeSeconds stays at the ceiling until the re-probe is done.
                        var proposed = Math.Min(jingleCeilingSeconds,
                            JingleObservationSafetyFactor * observedLength + PhraseMarginSeconds);
                        adaptedWindowSeconds = Math.Max(adaptedWindowSeconds ?? proposed, proposed);
                        if (!reprobing && adaptedWindowSeconds.Value != probeSeconds)
                        {
                            probeSeconds = adaptedWindowSeconds.Value;
                            _log?.Invoke($"jingle probe window resized to {probeSeconds:0.#} s");
                        }
                    }
                }
            }
            return marks;
        }

        // Adaptive threshold state (--min-silence-length auto only; otherwise every candidate
        // is probed unconditionally, same as before this feature existed). Probing proceeds
        // unthrottled until the second mark is found (its anchor silence is the first real
        // inter-chapter break - the silence before the first mark is typically the intro/title
        // silence, often longer, so it must not be used to tighten). From there, each mark's
        // anchor silence proposes AdaptiveTightenFactor times its own length, and the adapted
        // threshold is the running *minimum* of those proposals - the first one raises it from
        // the floor, every later one can only lower it (see AdaptiveTightenFactor for why a
        // raise is never safe). A sequence gap re-probes everything skipped since the last
        // mark unconditionally and folds the gap marks' own anchor silences into the running
        // minimum, so gap-filling stays inside Pass 2 where possible and the threshold can
        // never again sit above a silence that has proven to precede a chapter.
        //
        // skippedSinceLastMark also collects the windows the overlap-sequence skip below
        // passes over (in every mode, not just auto), so the same gap re-probe covers the
        // unlikely case of a skipped sequence window having hidden a second transition.
        double? adaptedThresholdSeconds = null;
        var threshold = _options.MinSilenceSeconds;
        var skippedSinceLastMark = new List<(double Start, Silence? Silence, NonSpeechRegion? VadRegion)>();

        for (var ci = 0; ci < candidates.Count; ci++)
        {
            var candidate = candidates[ci];
            work.SetPhaseProgress((long)(candidate.Start * bytesPerSecond));

            if (_options.AutoMinSilence && candidate.Silence is { } candidateSilence &&
                candidateSilence.EndSeconds - candidateSilence.StartSeconds < threshold)
            {
                skippedSinceLastMark.Add(candidate);
                continue;
            }

            // A VAD candidate qualified against the probe window at merge time, but that
            // window can since have narrowed (--max-jingle-length auto) once a baseline is
            // known - recheck here so probing keeps skipping regions too long to be this
            // book's jingle, same as the merge-time filter intends after the baseline exists.
            if (candidate.VadRegion is { } region && region.EndSeconds - candidate.Start > probeSeconds)
            {
                skippedSinceLastMark.Add(candidate);
                continue;
            }

            var windowEnd = WindowEndFor(candidates, ci);
            var probeMarks = await ProbeAsync(candidate, windowEnd);

            foreach (var (n, markSilence, _) in probeMarks)
            {
                // The gap re-probe runs regardless of --min-silence-length mode: with the
                // overlap-sequence skip below, candidates can be skipped even with an explicit
                // threshold, and a sequence gap is the signal that one of them hid a chapter.
                if (lastNumber is { } previousNumber && n > previousNumber + 1 && skippedSinceLastMark.Count > 0)
                {
                    _log?.Invoke($"sequence gap between chapter {previousNumber} and {n}, " +
                                 $"re-probing {skippedSinceLastMark.Count} skipped candidate(s) unconditionally");
                    if (_options.Jingle && _options.AutoMaxJingle && probeSeconds != jingleCeilingSeconds)
                    {
                        probeSeconds = jingleCeilingSeconds;
                        _log?.Invoke($"jingle probe window reset to {probeSeconds:0.#} s for the re-probe");
                    }
                    reprobing = true;
                    // The skipped candidates form their own little window sequence at the
                    // (possibly ceiling-reset) width, each end computed on the fly against its
                    // next skipped neighbor so adjacent re-probe windows get snapped shared
                    // borders too. probeSeconds cannot change mid-re-probe (the resize inside
                    // ProbeAsync is gated on !reprobing), so consecutive ends stay consistent
                    // for the whole sequence.
                    for (var si = 0; si < skippedSinceLastMark.Count; si++)
                    {
                        var gapMarks = await ProbeAsync(skippedSinceLastMark[si], WindowEndFor(skippedSinceLastMark, si));
                        if (!_options.AutoMinSilence)
                            continue;
                        // A gap mark's anchor silence was, by definition, short enough to have
                        // been skipped - fold it into the running minimum so the threshold can
                        // never again sit above it. Only genuine gap-fillers count; a duplicate
                        // or re-detection of this window's own mark surfacing in a re-probe
                        // must not lower anything.
                        foreach (var gapMark in gapMarks)
                        {
                            if (gapMark.Number > previousNumber && gapMark.Number < n &&
                                gapMark.MarkSilence is { } gapSilence)
                            {
                                adaptedThresholdSeconds = Math.Min(
                                    adaptedThresholdSeconds ?? double.MaxValue,
                                    Math.Max(_options.MinSilenceSeconds,
                                        AdaptiveTightenFactor * (gapSilence.EndSeconds - gapSilence.StartSeconds)));
                            }
                        }
                    }
                    reprobing = false;
                    // Re-probing done: bring the jingle window back down from the ceiling to the
                    // adapted value, including anything the re-probed marks just taught us.
                    if (_options.Jingle && _options.AutoMaxJingle &&
                        adaptedWindowSeconds is { } restoredWindow && probeSeconds != restoredWindow)
                    {
                        probeSeconds = restoredWindow;
                        _log?.Invoke($"jingle probe window restored to {probeSeconds:0.#} s");
                    }
                }

                if (_options.AutoMinSilence)
                {
                    // markSilence, when present, is the silence the mark actually falls into
                    // (resolved inside ProbeAsync - the triggering silence for a classic
                    // detection, the pinpointed anchor otherwise). lastNumber.HasValue means
                    // this is at least the second mark found, so that silence is a real
                    // inter-chapter break - not the intro-to-chapter-1 silence, which is
                    // routinely longer than that and would otherwise over-tighten the
                    // threshold from the very first mark. Never below the MinSilenceSeconds
                    // floor: Pass 1 never reports candidates shorter than the floor in the
                    // first place, so a threshold below it would skip nothing at all.
                    if (lastNumber.HasValue && markSilence is { } anchorSilence)
                    {
                        var proposed = Math.Max(_options.MinSilenceSeconds,
                            AdaptiveTightenFactor * (anchorSilence.EndSeconds - anchorSilence.StartSeconds));
                        adaptedThresholdSeconds = Math.Min(adaptedThresholdSeconds ?? proposed, proposed);
                    }

                    // Only announce an actual change; the first set is a raise from the floor
                    // ("tightened"), everything after can only ever be a lowering.
                    var newThreshold = adaptedThresholdSeconds ?? _options.MinSilenceSeconds;
                    if (newThreshold != threshold)
                        _log?.Invoke($"threshold {(newThreshold > threshold ? "tightened" : "lowered")} " +
                                     $"to {newThreshold:0.##} s after chapter {n}");
                    threshold = newThreshold;
                }
                skippedSinceLastMark.Clear();
                lastNumber = n;
            }

            // A confident mark settles its whole overlapping window sequence (consecutive
            // candidates whose windows each overlap the next): the remaining windows of the
            // sequence cover the same continuous stretch of audio around the found
            // transition, and a single sequence spanning two chapter transitions is highly
            // unlikely - so they are skipped outright instead of probed. They still go into
            // skippedSinceLastMark, so the gap re-probe above recovers the unlikely case
            // after all (and Pass 3 remains the final net). A low-confidence mark does not
            // skip anything: the remaining windows keep their chance to re-detect the
            // transition it may have gotten wrong. The chain starts from this window's
            // *actual* probed end - a mid-probe resize (--max-jingle-length auto) must not
            // retroactively pretend the window was narrower than what was really decoded -
            // while the links beyond it use ends computed at the current width, the same
            // ends those windows would be probed with.
            if (probeMarks.Count > 0 && probeMarks[^1].Confidence >= LowConfidenceThreshold)
            {
                var skipTo = ci;
                var reach = windowEnd;
                while (skipTo + 1 < candidates.Count && reach > candidates[skipTo + 1].Start)
                {
                    skipTo++;
                    reach = WindowEndFor(candidates, skipTo);
                }
                if (skipTo > ci)
                {
                    _log?.Invoke($"chapter {lastNumber} settles current overlapping window " +
                                 $"sequence - skipping its remaining {skipTo - ci} window(s)");
                    for (var si = ci + 1; si <= skipTo; si++)
                        skippedSinceLastMark.Add(candidates[si]);
                    ci = skipTo;
                }
            }
        }

        var chapters = Normalize(found);
        _log?.Invoke("Pass 2 finished");

        // Pass 3 (only when needed): resolve sequence gaps by fully transcribing the regions
        // between mismatched markings (and before the first marking, if it is not chapter 1).
        var gaps = FindGaps(chapters, info.DurationSeconds);
        if (gaps.Count > 0)
        {
            work.BeginPhase("Pass 3",
                (long)(gaps.Sum(g => g.ToSeconds - g.FromSeconds) * bytesPerSecond));
        }
        foreach (var gap in gaps)
        {
            _log?.Invoke($"transcribing suspicious region " +
                         $"{FormatTimestamp(gap.FromSeconds)} - {FormatTimestamp(gap.ToSeconds)}");
            // The chapter numbers this gap is expected to recover: everything strictly between
            // the numbers bounding it (or 1 up to the first detected number, for a leading gap).
            var fills = await TranscribeRegionAsync(file, info, gap.FromSeconds, gap.ToSeconds,
                MissingNumbersInGap(chapters, gap),
                allSilences, nonSpeechRegions, bytesPerSecond, work, profile!, chapters, ct);
            chapters = Normalize(chapters.Concat(fills).ToList());
            var (highest, missingNumbers) = ChapterProgress(chapters);
            work.HighestChapter = highest;
            work.MissingChapters = missingNumbers.Count;
        }
        if (gaps.Count > 0)
            _log?.Invoke("Pass 3 finished");

        // Final consistency check: internal gaps that remain are fatal for this file.
        var missing = new List<int>();
        for (var i = 1; i < chapters.Count; i++)
            for (var n = chapters[i - 1].Number + 1; n < chapters[i].Number; n++)
                missing.Add(n);

        var lowConfidence = chapters
            .Where(c => c.Confidence < LowConfidenceThreshold)
            .Select(c => c.Number)
            .ToList();

        // Per-file statistics, aggregated over only the chapters that survived into the final
        // result (a detection that lost out to Normalize, or a spurious number, contributes
        // nothing). The silence/jingle dictionaries were filled at each mark placement.
        var finalSilences = chapters
            .Where(c => _chapterSilenceSeconds.ContainsKey(c.Number))
            .Select(c => _chapterSilenceSeconds[c.Number]).ToList();
        var finalJingles = chapters
            .Where(c => _chapterJingleSeconds.ContainsKey(c.Number))
            .Select(c => _chapterJingleSeconds[c.Number]).ToList();
        var stats = new DetectionStats(
            finalSilences.Count > 0 ? finalSilences.Min() : null,
            finalJingles.Count > 0 ? finalJingles.Max() : null,
            _whisperAudioSeconds);

        return new DetectionResult(
            chapters, missing.Count > 0, missing, lowConfidence,
            profile!, detectedLanguage, detectedProbability, stats);
    }

    /// <summary>
    /// Checks pre-existing chapter markings against the audio (--verify), far cheaper than the
    /// full silence-scan/probe pipeline since only the markings' own timestamps are visited:
    /// for every marking whose title yields a parseable expected chapter number, a short window
    /// around its timestamp is probed with Whisper and checked for a phrase match with that
    /// number. A marking whose title has no parseable number (e.g. a prelude/intro entry
    /// without one) cannot be checked and does not count against or for the result; if none of
    /// a file's markings have a parseable number, verification trivially passes - there is
    /// nothing to disprove, so the file is left alone rather than needlessly re-detected.
    /// </summary>
    /// <param name="file">Path of the audio file.</param>
    /// <param name="info">Probe result of the file, including its pre-existing chapter markings.</param>
    /// <param name="work">Progress tracker, advanced once per marking (checked or skipped).</param>
    /// <param name="log">Sink for --verbose log messages, or null when not verbose.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<VerifyResult> VerifyExistingChaptersAsync(
        string file, MediaInfo info, WorkTracker work, Action<string>? log, CancellationToken ct)
    {
        _log = log;
        // With an explicit --lang, the profile is known upfront - no probing needed to resolve
        // it, so title parsing below always uses the real language. With --lang auto it stays
        // null until the first marking that actually gets decoded, resolved the same way
        // DetectAsync resolves its own first probe window.
        LanguageProfile? profile = _options.AutoLanguage ? null : _options.DefaultProfile;
        if (profile != null)
            _transcriber.ChangeLanguage(profile.Language);
        var checkedCount = 0;
        var failed = 0;

        work.BeginPhase("Verify", info.ExistingChapters.Count);
        foreach (var marking in info.ExistingChapters)
        {
            ct.ThrowIfCancellationRequested();

            var windowStart = Math.Max(0, marking.StartSeconds - VerifyMarginBeforeSeconds);
            var windowLen = Math.Min(VerifyWindowSeconds, info.DurationSeconds - windowStart);
            if (windowLen <= 0)
            {
                work.Advance(1);
                continue;
            }

            var placeholderLanguage = profile?.Language ?? "en";
            if (!TryParseExpectedNumber(marking.Title, placeholderLanguage, out var expected))
            {
                work.Advance(1);
                continue;
            }

            var samples = await _audio.DecodePcmAsync(file, windowStart, windowLen, info.InputDecoder, ct);
            if (profile == null)
            {
                (profile, _, _) = await ResolveLanguageAsync(samples, ct);
                _transcriber.ChangeLanguage(profile.Language);
                // The number may have been parsed above using "en" as a placeholder before the
                // real language was known; re-parse now in case that made a difference.
                if (!TryParseExpectedNumber(marking.Title, profile.Language, out expected))
                {
                    work.Advance(1);
                    continue;
                }
            }

            var segments = await _transcriber.TranscribeAsync(samples, ct);
            LogTranscript($"verify @{FormatTimestamp(marking.StartSeconds)}", segments);

            checkedCount++;
            var confirmed = FindPhraseMatches(segments, profile).Any(m => m.Number == expected);
            _log?.Invoke(confirmed
                ? $"chapter {expected} marking at {FormatTimestamp(marking.StartSeconds)} confirmed"
                : $"chapter {expected} marking at {FormatTimestamp(marking.StartSeconds)} NOT confirmed - phrase not found nearby");
            if (!confirmed)
                failed++;
            work.Advance(1);
        }

        return new VerifyResult(failed == 0, checkedCount, failed);
    }

    /// <summary>
    /// Extracts an expected chapter number from a pre-existing marking's title: a plain digit
    /// sequence first (works regardless of language, and covers titles from other tools like
    /// "Chapter 05" or "05 - Title"), then a spelled-out number via <see cref="NumberWordParser"/>
    /// for the given language. Returns false when the title has no parseable number at all.
    /// </summary>
    private static bool TryParseExpectedNumber(string title, string language, out int number)
    {
        var digits = Regex.Match(title, @"\d+");
        if (digits.Success && int.TryParse(digits.Value, out number))
            return true;
        return NumberWordParser.TryExtractNumber(title, language, out number);
    }

    /// <summary>
    /// Resolves the language profile to use for this file: with an explicit --lang, always
    /// <see cref="CliOptions.DefaultProfile"/> (no detection call at all); with --lang auto,
    /// runs Whisper's language detector on a short clip and applies
    /// <see cref="AutoLanguageProbabilityThreshold"/>, falling back to English when the
    /// detection is inconclusive or the clip is too short to probe.
    /// </summary>
    /// <param name="samples">Decoded samples of the first probe window (start of the file).</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<(LanguageProfile Profile, string? DetectedLanguage, double DetectedProbability)> ResolveLanguageAsync(
        float[] samples, CancellationToken ct)
    {
        if (!_options.AutoLanguage)
            return (_options.DefaultProfile, null, 0);

        if (samples.Length < FfmpegClient.SampleRate / 2)
        {
            _log?.Invoke("language auto-detection skipped (clip too short); using en");
            return (_options.ResolveProfile("en"), null, 0);
        }

        var (detected, probability) = await _transcriber.DetectLanguageWithProbability(samples, ct);
        var conclusive = probability >= AutoLanguageProbabilityThreshold && !string.IsNullOrWhiteSpace(detected);
        var effective = conclusive ? detected.ToLowerInvariant() : "en";
        _log?.Invoke(conclusive
            ? $"language auto-detected: {effective} (p={probability:0.00})"
            : $"language auto-detection inconclusive ({detected ?? "?"} p={probability:0.00}); falling back to en");
        return (_options.ResolveProfile(effective), detected, probability);
    }

    /// <summary>A time region suspected to contain undetected chapter starts.</summary>
    /// <param name="FromSeconds">Region start.</param>
    /// <param name="ToSeconds">Region end.</param>
    internal readonly record struct GapRegion(double FromSeconds, double ToSeconds);

    /// <summary>
    /// A gap between two consecutive <see cref="SpeechSegment"/>s found by the VAD pre-pass -
    /// i.e. a region VAD considers non-speech, flanked by speech on both sides. With --jingle,
    /// a silence-less jingle transition shows up as one of these (music, like silence, reads
    /// as non-speech to a speech detector). Deliberately does not cover leading/trailing
    /// non-speech at the very start/end of the file - the synthetic file-start candidate
    /// (Start = 0) already covers a jingle-before-chapter-1 edge case without it.
    /// </summary>
    /// <param name="StartSeconds">Where VAD stopped detecting speech.</param>
    /// <param name="EndSeconds">Where VAD resumed detecting speech.</param>
    internal readonly record struct NonSpeechRegion(double StartSeconds, double EndSeconds);

    /// <summary>
    /// Inverts consecutive VAD speech segments into the non-speech gaps between them, then cleans
    /// up two things Silero VAD is not reliable about on real jingle music: a "speech" blip
    /// shorter than <see cref="MergeShortSpeechGapSeconds"/> (a vocal-like transient or a strong
    /// rhythmic passage inside otherwise instrumental music) does not end a jingle - the non-speech
    /// regions on either side of it are merged into one, rather than fragmenting one continuous
    /// jingle into several too-short regions; and any region whose longest single <em>contiguous</em>
    /// non-speech run falls short of <see cref="MinJingleObservationSeconds"/> is dropped outright.
    /// <para>
    /// The floor is deliberately checked against the longest contiguous run, not the merged
    /// region's wall-clock span: a jingle is defined by containing one genuinely long, unbroken
    /// music block (surviving fragmentation by a brief misfire because the pieces between misfires
    /// are still long), whereas ordinary narration cadence produces only short inter-word/clause
    /// pauses - individually well under the floor, but able to chain-merge across the equally short
    /// speech between them into a span that clears the floor even though no real jingle-length
    /// silence exists anywhere in it. Measuring the span there would resurface exactly the
    /// breath-pause false positives the floor is meant to suppress; measuring the longest run
    /// keeps genuine (even mildly fragmented) jingles while rejecting stitched-together narration.
    /// </para>
    /// Internal for unit testing.
    /// </summary>
    internal static List<NonSpeechRegion> ComputeNonSpeechRegions(List<SpeechSegment> speech)
    {
        var merged = new List<NonSpeechRegion>();
        // The longest single contiguous non-speech run within each merged region (i.e. the
        // longest raw gap it was built from, before any speech blips were merged across),
        // parallel to `merged` by index - kept alongside rather than on NonSpeechRegion itself
        // so the record's shape stays purely (Start, End) for downstream use and equality.
        var longestRun = new List<double>();
        for (var i = 1; i < speech.Count; i++)
        {
            var start = speech[i - 1].EndSeconds;
            var end = speech[i].StartSeconds;
            var run = end - start;
            if (merged.Count > 0 && start - merged[^1].EndSeconds < MergeShortSpeechGapSeconds)
            {
                merged[^1] = merged[^1] with { EndSeconds = end };
                longestRun[^1] = Math.Max(longestRun[^1], run);
            }
            else
            {
                merged.Add(new NonSpeechRegion(start, end));
                longestRun.Add(run);
            }
        }
        return merged.Where((_, i) => longestRun[i] >= MinJingleObservationSeconds).ToList();
    }

    /// <summary>
    /// The silencedetect silence that leads a VAD non-speech region - the low-amplitude part
    /// before the jingle's music starts, whose end lies inside the region - or null when the
    /// region has none (a silence-less jingle, or an in-text pause that ends before the region
    /// rather than inside it). Picks the earliest-ending silence when more than one overlaps the
    /// region's leading edge. This is the geometry that distinguishes a genuine "silence then
    /// jingle" transition from a false in-text pause that merely triggered a probe.
    /// </summary>
    private static Silence? LeadingSilence(NonSpeechRegion region, List<Silence> silences)
        => silences
            .Where(s => s.EndSeconds > region.StartSeconds && s.EndSeconds <= region.EndSeconds)
            .OrderBy(s => s.EndSeconds)
            .Cast<Silence?>()
            .FirstOrDefault();

    /// <summary>
    /// The true start of the jingle within a VAD non-speech region: the end of a
    /// <see cref="LeadingSilence"/> (when present), or the region's own start when no such
    /// silence exists - see "Why both detectors are required" in the design notes.
    /// </summary>
    private static double JingleStart(NonSpeechRegion region, List<Silence> silences)
        => LeadingSilence(region, silences)?.EndSeconds ?? region.StartSeconds;

    /// <summary>
    /// Resolves the anchor for placing a --jingle chapter mark, independent of whichever silence
    /// happened to trigger the probe. The jingle is the VAD non-speech region the phrase belongs
    /// to (see <see cref="FindJingleRegionForPhrase"/> - by containment, so the announcement being
    /// spoken <em>inside</em> the jingle does not lose the region); a silencedetect silence is
    /// accepted as the anchor <em>only</em> when it <see cref="LeadingSilence">leads that region</see>
    /// (its end lies inside it) - the classic "silence then jingle" transition, where the mark goes
    /// 0.5 s before the silence. When the region has no leading silence (a silence-less jingle) the
    /// region itself is the anchor and the mark goes at the jingle start with no lead.
    /// <para>
    /// Crucially, a false in-text pause earlier in the previous chapter's narration does
    /// <em>not</em> lead the jingle region, so it is never mistaken for the anchor even when it
    /// is the candidate that triggered this probe. That prevents a silence-less jingle transition
    /// from being marked at the pause instead of the jingle, and stops the pause's length from
    /// feeding the --min-silence-length / --max-jingle-length auto mechanisms with a bogus
    /// (inflated) observation. Only when VAD found no region near the phrase at all (VAD off, or a
    /// transition with neither a jingle nor a VAD-registered silence) does this fall back to the
    /// nearest preceding silence.
    /// </para>
    /// </summary>
    /// <param name="phraseAbs">Absolute phrase start time.</param>
    /// <param name="earliestAnchor">Earliest time an anchor may lie at: the probe window start
    /// (Pass 2) or <c>phraseAbs - lookback</c> (Pass 3).</param>
    /// <param name="silences">Every silence Pass 1 stored, down to
    /// <see cref="MinStoredSilenceSeconds"/> - even a sub-threshold silence leading the jingle
    /// is the more accurate anchor (and jingle-length reference) than the region alone.</param>
    /// <param name="nonSpeechRegions">All VAD non-speech regions (empty when VAD is off).</param>
    /// <param name="candidateVadRegion">The region a VAD candidate carries, if this probe was
    /// triggered by one; used directly instead of re-deriving it. Null for silence candidates
    /// and for Pass 3.</param>
    /// <returns><c>AnchorSilence</c>: the silence leading the jingle (or, when no jingle region
    /// was found, the silence directly preceding the phrase), or null for a silence-less jingle.
    /// <c>VadRegion</c>: the jingle region the phrase belongs to, or null when none was found.
    /// The region is returned even when <c>AnchorSilence</c> also is (the "silence then jingle"
    /// shape), so a caller can measure the jingle; the mark itself is unaffected because
    /// <see cref="ComputeJingleMark"/> already prefers the silence over the region.</returns>
    private static (Silence? AnchorSilence, NonSpeechRegion? VadRegion) ResolveJingleAnchor(
        double phraseAbs, double earliestAnchor, List<Silence> silences,
        List<NonSpeechRegion> nonSpeechRegions, NonSpeechRegion? candidateVadRegion)
    {
        var jingleRegion = candidateVadRegion ?? FindJingleRegionForPhrase(earliestAnchor, phraseAbs, nonSpeechRegions);
        if (jingleRegion is { } jr)
            return (LeadingSilence(jr, silences), jr);
        return (FindRealAnchorSilence(earliestAnchor, phraseAbs, silences), null);
    }

    /// <summary>
    /// Records, for one detected chapter, the length of the silence and (in --jingle mode) the
    /// jingle that preceded its phrase - the raw material for the per-file minimum-silence and
    /// maximum-jingle statistics. Keyed by chapter number, so a later re-detection of the same
    /// chapter simply overwrites; the per-file aggregate is then taken over only the chapters that
    /// survive into the final result. The silence recorded is the one the mark anchored to: in
    /// plain mode the silence directly before the phrase; in --jingle mode the silence leading the
    /// jingle (a jingle framed between two silences contributes only its <em>leading</em> one, per
    /// <see cref="ResolveJingleAnchor"/>), or none for a silence-less jingle. The jingle length is
    /// measured from its true start (the leading silence's end, else the region start) up to the
    /// phrase, clipped at the region end so an announcement spoken inside the jingle - or a
    /// merge-inflated region end - never overstates it, matching the --max-jingle-length auto
    /// observation.
    /// </summary>
    /// <param name="number">The detected chapter number.</param>
    /// <param name="precedingSilence">The silence the mark anchored to, or null when none.</param>
    /// <param name="jingleRegion">The jingle region preceding the phrase, or null (always null
    /// outside --jingle mode).</param>
    /// <param name="phraseAbs">Absolute phrase start time, the clip point for the jingle length.</param>
    private void RecordChapterStats(
        int number, Silence? precedingSilence, NonSpeechRegion? jingleRegion, double phraseAbs)
    {
        if (precedingSilence is { } s && s.EndSeconds > s.StartSeconds)
            _chapterSilenceSeconds[number] = s.EndSeconds - s.StartSeconds;
        if (jingleRegion is { } r)
        {
            var jingleStart = precedingSilence?.EndSeconds ?? r.StartSeconds;
            _chapterJingleSeconds[number] = Math.Max(0, Math.Min(r.EndSeconds, phraseAbs) - jingleStart);
        }
    }

    /// <summary>
    /// Transcribes decoded PCM and tallies its length toward the per-file Whisper-audio statistic
    /// (<see cref="_whisperAudioSeconds"/>). All detection-path recognition routes through here so
    /// the tally stays complete and counts re-probed audio each time it is decoded; the --verify
    /// path calls the transcriber directly, as its audio is not part of a detection run's stat.
    /// </summary>
    /// <param name="samples">16 kHz mono PCM for one probe window or gap chunk.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<List<TranscriptSegment>> TranscribeCountingAsync(float[] samples, CancellationToken ct)
    {
        _whisperAudioSeconds += samples.Length / (double)FfmpegClient.SampleRate;
        return await _transcriber.TranscribeAsync(samples, ct);
    }

    /// <summary>
    /// Computes where to place a --jingle chapter mark, given the phrase time and the
    /// silence/VAD non-speech region the caller has already resolved to truly precede it (Pass
    /// 2's ProbeAsync and Pass 3's TranscribeRegionAsync each resolve these their own way, but
    /// share this decision): a preceding silence takes priority (mark 0.5 s before it, clamped
    /// to the silence's own length so the lead can never overshoot into the previous chapter's
    /// trailing narration); absent one, a preceding VAD non-speech region places the mark at
    /// its start with no lead (a lead here would cut into the previous chapter's narration,
    /// since there is no absorbable silence to place it in); absent both, a last-resort
    /// fallback backs up from the phrase itself.
    /// </summary>
    /// <param name="phraseAbs">Absolute phrase start time.</param>
    /// <param name="silence">The silence immediately preceding the phrase, if any.</param>
    /// <param name="vadRegionStart">Start of the VAD non-speech region immediately preceding
    /// the phrase, if any; only consulted when <paramref name="silence"/> is null.</param>
    private static double ComputeJingleMark(double phraseAbs, Silence? silence, double? vadRegionStart)
    {
        if (silence is { } s)
        {
            var lead = Math.Min(JingleLeadSeconds, s.EndSeconds - s.StartSeconds);
            return Math.Max(0, s.EndSeconds - lead);
        }
        if (vadRegionStart is { } vs)
            return Math.Max(0, vs);
        return Math.Max(0, phraseAbs - JingleLeadSeconds);
    }

    /// <summary>
    /// Finds the VAD non-speech region (the jingle) a matched phrase belongs to, by
    /// <em>containment</em> rather than end-alignment: the last region that contains the phrase
    /// (<c>Start &lt;= phrase &lt;= End</c>) or brackets it within
    /// <see cref="JinglePhraseMatchToleranceSeconds"/> at either edge (VAD and Whisper time their
    /// boundaries slightly differently). This is deliberately robust to the "Kapitel N"
    /// announcement being spoken <em>inside</em> the jingle - Whisper then timestamps the phrase
    /// before the VAD region ends, so an end-alignment test would drop the region and the mark
    /// would fall back onto an unrelated earlier in-text pause, landing the chapter seconds early
    /// (the failure that motivated this). Because the mark is taken from the region's
    /// <see cref="JingleStart">start</see>, where the region <em>ends</em> - possibly inflated by
    /// <see cref="ComputeNonSpeechRegions"/>'s short-speech-gap merge swallowing the announcement -
    /// never affects placement. A region that starts after the phrase (a post-announcement pause)
    /// is excluded. Returns null when no region qualifies within the window.
    /// </summary>
    /// <param name="windowStart">Earliest a qualifying region may end (the probe window start or
    /// the Pass 3 lookback start); a region entirely before it is ignored.</param>
    /// <param name="phraseAbsSeconds">Absolute phrase start in seconds.</param>
    /// <param name="regions">All VAD non-speech regions, in chronological order.</param>
    private static NonSpeechRegion? FindJingleRegionForPhrase(
        double windowStart, double phraseAbsSeconds, List<NonSpeechRegion> regions)
    {
        var latestStart = phraseAbsSeconds + JinglePhraseMatchToleranceSeconds;
        var earliestEnd = phraseAbsSeconds - JinglePhraseMatchToleranceSeconds;
        NonSpeechRegion? found = null;
        foreach (var r in regions)
            if (r.EndSeconds > windowStart && r.StartSeconds <= latestStart && r.EndSeconds >= earliestEnd)
                found = r;
        return found;
    }

    /// <summary>
    /// Determines the regions to fully transcribe: between every pair of consecutive detected
    /// chapters whose numbers are not consecutive, and before the first chapter when its
    /// number is greater than 1. Internal for unit testing.
    /// </summary>
    internal static List<GapRegion> FindGaps(List<DetectedChapter> chapters, double duration)
    {
        var gaps = new List<GapRegion>();
        if (chapters.Count == 0)
            return gaps;
        if (chapters[0].Number > 1 && chapters[0].TimeSeconds > 30)
            gaps.Add(new GapRegion(0, chapters[0].TimeSeconds));
        for (var i = 1; i < chapters.Count; i++)
        {
            if (chapters[i].Number > chapters[i - 1].Number + 1)
                gaps.Add(new GapRegion(chapters[i - 1].TimeSeconds, chapters[i].TimeSeconds));
        }
        return gaps;
    }

    /// <summary>
    /// The chapter numbers a gap is expected to recover: every number strictly between the
    /// detected chapters bounding it, or 1 up to the first detected number for a leading gap
    /// (whose start is 0, before any chapter). The bounding chapters are located by their exact
    /// timestamps, which <see cref="FindGaps"/> copied verbatim into the gap, so the float match
    /// is exact. Pass 3 uses this to stop transcribing a gap the moment all of them are found.
    /// Internal for unit testing.
    /// </summary>
    /// <param name="chapters">The currently known chapters, in chronological order.</param>
    /// <param name="gap">A gap produced by <see cref="FindGaps"/> over these chapters.</param>
    internal static List<int> MissingNumbersInGap(List<DetectedChapter> chapters, GapRegion gap)
    {
        var upper = chapters.First(c => c.TimeSeconds == gap.ToSeconds).Number;
        // A leading gap starts at 0 with no chapter there; FirstOrDefault yields Number 0, so the
        // expected set becomes 1..upper-1 exactly as intended.
        var lower = chapters.FirstOrDefault(c => c.TimeSeconds == gap.FromSeconds).Number;
        var missing = new List<int>();
        for (var n = lower + 1; n < upper; n++)
            missing.Add(n);
        return missing;
    }

    /// <summary>
    /// Sorts detections chronologically, removes duplicates of the same chapter number
    /// (keeping the earliest) and drops out-of-order regressions, which are typically
    /// in-text mentions like "as seen in chapter three". Internal for unit testing.
    /// </summary>
    internal static List<DetectedChapter> Normalize(List<DetectedChapter> found)
    {
        var result = new List<DetectedChapter>();
        foreach (var c in found.OrderBy(c => c.TimeSeconds).ThenBy(c => c.Number))
        {
            if (result.Count == 0 || c.Number > result[^1].Number)
                result.Add(c);
        }
        return result;
    }

    /// <summary>
    /// Finds the silence that truly precedes a matched phrase, independent of which candidate
    /// silence actually triggered the probe. A probe window can span the trailing speech of the
    /// previous chapter, an unrelated in-text pause long enough to itself have passed the
    /// --min-silence-length threshold, the real inter-chapter silence, the jingle (with
    /// --jingle) and finally the phrase - so trusting the probe's own triggering silence, both
    /// for the jingle-mode mark position and for the --min-silence-length/--max-jingle-length
    /// auto mechanisms, would anchor to the wrong (earlier, false) silence whenever that
    /// happens. Returns null when no silence between the window start and the phrase was found,
    /// meaning the triggering silence (ending exactly at windowStart) was the real one after all.
    /// </summary>
    /// <param name="windowStart">Absolute start of the probe window (or of the lookback range)
    /// in seconds.</param>
    /// <param name="phraseAbsSeconds">Absolute phrase start in seconds.</param>
    /// <param name="silences">The silences to search - callers pass the full stored list
    /// (every silence down to <see cref="MinStoredSilenceSeconds"/>).</param>
    private static Silence? FindRealAnchorSilence(double windowStart, double phraseAbsSeconds, List<Silence> silences)
    {
        var silence = silences.LastOrDefault(s => s.EndSeconds > windowStart && s.EndSeconds <= phraseAbsSeconds);
        return silence == default ? null : silence;
    }

    /// <summary>
    /// Finds where to cut between two adjacent Pass 2 probe windows so the seam never falls
    /// mid-word: the mid-point of the nearest qualifying silence, falling back to a VAD
    /// non-speech region under the same rules in --jingle mode when no silence qualifies, and
    /// finally to the border itself (no snap) when neither exists - which almost certainly
    /// means there is no chapter transition near the border to begin with, so a mid-word cut
    /// there is not a real risk. A candidate target's mid-point must lie inside window 2 -
    /// strictly after <paramref name="windowStart"/>, and before <paramref name="windowEnd"/>
    /// (inclusive at planning time, where a seam at window 2's very end just means window 1
    /// swallows it whole; strict at reuse time, so the fresh tail decode is never empty).
    /// <para>
    /// Two callers with different rules, selected via <paramref name="allowBeyondBorder"/>.
    /// <see cref="PlanWindowEnd"/> (true) plans window 1's end before window 1 is decoded, so
    /// it may place the seam anywhere within window 2 - window 1's decode is simply extended
    /// (or shortened) to end exactly at it. The reuse-time call inside a probe (false) runs
    /// after window 1 is already decoded: everything left of the seam is served from
    /// window 1's cached transcript, which cannot be extended retroactively, so there the
    /// target must <em>start</em> at or before the border. A target merely straddling the
    /// border is still fine (the stretch past the border is inside the silence itself, so no
    /// speech is lost), but one entirely beyond it would leave [border, seam) in neither
    /// transcript. The border normally <em>is</em> window 1's planned seam already, which the
    /// restricted search then re-finds at distance zero; it only genuinely decides for
    /// overlaps that plan did not anticipate (a probe-window resize since window 1 was
    /// probed).
    /// </para>
    /// </summary>
    /// <param name="windowStart">Start of window 2 (the later window's candidate start).</param>
    /// <param name="border">The unsnapped border - window 1's (planned or decoded) end.</param>
    /// <param name="windowEnd">End of window 2.</param>
    /// <param name="allSilences">Every silence Pass 1 found, down to <see
    /// cref="MinStoredSilenceSeconds"/> - not just the ones at or above --min-silence-length.</param>
    /// <param name="nonSpeechRegions">VAD non-speech regions; empty when --jingle is off.</param>
    /// <param name="jingle">True when --jingle is in effect, enabling the VAD region fallback.</param>
    /// <param name="allowBeyondBorder">True at planning time (the border itself moves to the
    /// seam); false at reuse time (the cache ends at the border, see above).</param>
    private static double FindOverlapSplitPoint(
        double windowStart, double border, double windowEnd,
        List<Silence> allSilences, List<NonSpeechRegion> nonSpeechRegions, bool jingle,
        bool allowBeyondBorder)
    {
        // At planning time a seam exactly at windowEnd is allowed: window 1 then swallows
        // window 2 whole, and window 2 is served entirely from its cache. At reuse time the
        // bound stays strict so the fresh tail decode [seam, windowEnd) can never be empty.
        return FindNearestSeam(border, windowStart, windowEnd,
            upperInclusive: allowBeyondBorder,
            targetStartAtOrBefore: allowBeyondBorder ? null : border,
            allSilences, nonSpeechRegions, jingle) ?? border;
    }

    /// <summary>
    /// The nearest word-safe seam to <paramref name="border"/>: the mid-point of a silence -
    /// or, with --jingle, of a VAD non-speech region when no silence qualifies - within
    /// (<paramref name="earliestExclusive"/>, <paramref name="latestInclusive"/>], or null when
    /// neither kind of target has its mid-point in that range. No word straddles the mid-point
    /// of a silence, which is what makes it the safest place to cut audio that is transcribed
    /// in separate pieces. The single seam search behind every border decision in the
    /// pipeline: Pass 2's shared-border and stand-alone window-end snaps
    /// (<see cref="PlanWindowEnd"/>), the reuse-time split (both via
    /// <see cref="FindOverlapSplitPoint"/>), and Pass 3's chunk borders
    /// (<see cref="TranscribeRegionAsync"/>).
    /// </summary>
    /// <param name="border">The unsnapped border the seam should stay closest to.</param>
    /// <param name="earliestExclusive">Lower bound (exclusive) for the seam.</param>
    /// <param name="latestInclusive">Upper bound for the seam; inclusive when
    /// <paramref name="upperInclusive"/>, exclusive otherwise.</param>
    /// <param name="upperInclusive">Whether a seam exactly at <paramref name="latestInclusive"/>
    /// is acceptable (see <see cref="FindOverlapSplitPoint"/> for the one caller that must
    /// keep the bound strict).</param>
    /// <param name="targetStartAtOrBefore">When set, only targets that <em>start</em> at or
    /// before this position qualify - the reuse-time restriction, where everything left of the
    /// seam must already be covered by a cached transcript.</param>
    /// <param name="allSilences">Every silence Pass 1 stored, down to
    /// <see cref="MinStoredSilenceSeconds"/>.</param>
    /// <param name="nonSpeechRegions">VAD non-speech regions; empty when --jingle is off.</param>
    /// <param name="jingle">True when --jingle is in effect, enabling the VAD region fallback.</param>
    private static double? FindNearestSeam(
        double border, double earliestExclusive, double latestInclusive, bool upperInclusive,
        double? targetStartAtOrBefore,
        List<Silence> allSilences, List<NonSpeechRegion> nonSpeechRegions, bool jingle)
    {
        double? Nearest(IEnumerable<(double Start, double End)> targets) => targets
            .Where(t => targetStartAtOrBefore is not { } cap || t.Start <= cap)
            .Select(t => (double?)((t.Start + t.End) / 2))
            .Where(mid => mid > earliestExclusive &&
                          (upperInclusive ? mid <= latestInclusive : mid < latestInclusive))
            .OrderBy(mid => Math.Abs(mid!.Value - border))
            .FirstOrDefault();

        var seam = Nearest(allSilences.Select(s => (s.StartSeconds, s.EndSeconds)));
        if (seam is null && jingle)
            seam = Nearest(nonSpeechRegions.Select(r => (r.StartSeconds, r.EndSeconds)));
        return seam;
    }

    /// <summary>
    /// Plans a single Pass 2 probe window's end, called right before that window is probed -
    /// on the fly, so the end always reflects the <paramref name="probeSeconds"/> in effect at
    /// that moment (the adaptive --max-jingle-length resizes apply to the very next window,
    /// with no pre-computed bulk plan to go stale). The window naturally spans
    /// <paramref name="probeSeconds"/> from its candidate start (clamped to the file end), but
    /// when the next candidate's window overlaps it, their shared border is snapped to the
    /// nearest silence (or, with --jingle, VAD non-speech region) mid-point anywhere within
    /// that next window's natural span - see <see cref="FindOverlapSplitPoint"/> - and this
    /// window's decode ends exactly there, be that before or beyond its natural end. The next
    /// probe's fresh decode then starts at the very same seam (its cached-transcript reuse
    /// re-finds the seam as the cache's end), so consecutive decodes stitch together
    /// word-safely at a mid-silence cut with no dead (never-transcribed) stretch and no
    /// re-decoded overlap between them. A raw-border joint remains only where the next window
    /// contains no snap target at all - and no silence there means no chapter transition near
    /// the border either, so a mid-word cut there costs nothing.
    /// <para>
    /// A window end that does <em>not</em> lie inside the next window (stand-alone windows,
    /// the last window, and a next window fully contained in this one) is snapped too, in a
    /// more limited way: to the nearest seam within <see cref="WindowEndSnapSearchSeconds"/>
    /// <em>after</em> the natural end (extension only), so even an isolated window's decode
    /// stops at a word-safe cut. Without a target in reach it keeps its natural length.
    /// Internal for unit testing.
    /// </para>
    /// </summary>
    /// <param name="start">This window's candidate start.</param>
    /// <param name="nextStart">The next candidate's start, or null for the last window.</param>
    /// <param name="probeSeconds">Current probe window length in seconds.</param>
    /// <param name="durationSeconds">Total play time; window ends are clamped to it.</param>
    /// <param name="allSilences">Every silence Pass 1 found, down to <see
    /// cref="MinStoredSilenceSeconds"/>.</param>
    /// <param name="nonSpeechRegions">VAD non-speech regions; empty when --jingle is off.</param>
    /// <param name="jingle">True when --jingle is in effect, enabling the VAD region fallback.</param>
    internal static double PlanWindowEnd(
        double start, double? nextStart, double probeSeconds, double durationSeconds,
        List<Silence> allSilences, List<NonSpeechRegion> nonSpeechRegions, bool jingle)
    {
        var naturalEnd = Math.Min(start + probeSeconds, durationSeconds);
        if (nextStart is { } ns && ns < naturalEnd)
        {
            var nextNaturalEnd = Math.Min(ns + probeSeconds, durationSeconds);
            if (nextNaturalEnd > naturalEnd)
            {
                // Shared border inside the next window: snap it to a seam anywhere in there.
                var seam = FindOverlapSplitPoint(ns, naturalEnd, nextNaturalEnd,
                    allSilences, nonSpeechRegions, jingle, allowBeyondBorder: true);
                return seam > start ? seam : naturalEnd;
            }
            // The next window ends at or before this one's natural end (possible near the
            // file end): no shared border to snap - it will be served wholesale from this
            // window's cached transcript instead; fall through to the stand-alone snap.
        }

        // Stand-alone end: extend to the nearest seam within the short forward search so the
        // decode never stops mid-word (see WindowEndSnapSearchSeconds). Should this reach past
        // the next window's start, the reuse-time split simply re-finds the very same seam as
        // the cache's end - a clean stitch either way.
        return FindNearestSeam(naturalEnd, naturalEnd,
            Math.Min(naturalEnd + WindowEndSnapSearchSeconds, durationSeconds),
            upperInclusive: true, targetStartAtOrBefore: null,
            allSilences, nonSpeechRegions, jingle) ?? naturalEnd;
    }

    /// <summary>
    /// Computes the chapter numbers shown in the progress bar and in detection log lines from a
    /// detection list: the highest chapter number found so far, and which numbers below it are
    /// still undetected (the gaps Pass 3 would have to chase). Runs the input through
    /// <see cref="Normalize"/> first so in-text mentions of earlier chapters (regressions that
    /// Normalize drops anyway) cannot make a genuinely missing chapter look found.
    /// Internal for unit testing.
    /// </summary>
    internal static (int Highest, List<int> Missing) ChapterProgress(IEnumerable<DetectedChapter> found)
    {
        var numbers = Normalize(found.ToList()).Select(c => c.Number).ToHashSet();
        var highest = numbers.Count == 0 ? 0 : numbers.Max();
        var missing = Enumerable.Range(1, Math.Max(0, highest)).Where(n => !numbers.Contains(n)).ToList();
        return (highest, missing);
    }

    /// <summary>Trailing note for a detection log line listing the chapter numbers still missing
    /// below the highest found; empty when the sequence so far is complete.</summary>
    private static string MissingNote(List<int> missing)
        => missing.Count > 0 ? $" - still missing: {string.Join(", ", missing)}" : "";

    /// <summary>A phrase match inside a transcribed window.</summary>
    /// <param name="Number">Parsed chapter number.</param>
    /// <param name="PhraseStartSeconds">Phrase start relative to the window start.</param>
    /// <param name="Confidence">Whisper's probability for the segment the match was found in.</param>
    /// <param name="SpansMerge">True when the text actually used to find the phrase and parse its
    /// number straddles a Pass 2 overlap's cache/fresh boundary - see <see cref="FindPhraseMatches"/>'s
    /// <c>mergeBoundarySegIndex</c> parameter.</param>
    private readonly record struct PhraseMatch(
        int Number, double PhraseStartSeconds, double Confidence, bool SpansMerge = false);

    /// <summary>
    /// Searches the transcribed segments for the chapter phrase and parses the chapter number,
    /// either from the regexp capturing group or from the words following the phrase
    /// ("Chapter Seven"); when neither yields a number, the words directly preceding the
    /// phrase are tried ("Erstes Kapitel", "Birinci Bölüm").
    /// </summary>
    /// <param name="segments">The window's transcript segments, in window-relative time.</param>
    /// <param name="profile">Language profile supplying the chapter phrase and number parsing.</param>
    /// <param name="mergeBoundarySegIndex">For a window assembled by Pass 2's overlap reuse (see
    /// ProbeAsync), the index of the first segment that came from the fresh tail decode rather
    /// than the reused cache; null for a window that is entirely one or the other (a plain probe,
    /// a fully-reused window, a gap chunk, or a --verify window). Used only to flag
    /// <see cref="PhraseMatch.SpansMerge"/> - it does not affect which matches are found.</param>
    private static IEnumerable<PhraseMatch> FindPhraseMatches(
        List<TranscriptSegment> segments, LanguageProfile profile, int? mergeBoundarySegIndex = null)
    {
        if (segments.Count == 0)
            yield break;

        // Concatenate all segment texts and remember which character belongs to which segment
        // so a match position can be mapped back to a time.
        var sb = new StringBuilder();
        var segStartChar = new int[segments.Count];
        for (var i = 0; i < segments.Count; i++)
        {
            segStartChar[i] = sb.Length;
            sb.Append(segments[i].Text);
            sb.Append(' ');
        }
        var text = sb.ToString();
        var mergeBoundaryChar = mergeBoundarySegIndex is { } idx && idx > 0 && idx < segments.Count
            ? segStartChar[idx] : (int?)null;

        foreach (Match m in profile.PhraseRegex.Matches(text))
        {
            int number;
            // The exact character range actually consulted to find the phrase and parse its
            // number - just the match itself unless a head/tail slice contributed too - used
            // below to tell whether this detection drew on text from both sides of a Pass 2
            // overlap's cache/fresh boundary.
            var consumedStart = m.Index;
            var consumedEnd = m.Index + m.Length;
            if (profile.PhraseHasNumberGroup && m.Groups.Count > 1 && m.Groups[1].Success)
            {
                if (!int.TryParse(m.Groups[1].Value, out number))
                    continue;
            }
            else
            {
                var tail = text[(m.Index + m.Length)..];
                if (tail.Length > 80)
                    tail = tail[..80];
                if (NumberWordParser.TryExtractNumber(tail, profile.Language, out number))
                {
                    consumedEnd += tail.Length;
                }
                else
                {
                    // No number after the phrase - try the ordinal-first announcement
                    // order ("Erstes Kapitel", "2. Kapitel", "Birinci Bölüm").
                    var head = text[..m.Index];
                    if (head.Length > 80)
                        head = head[^80..];
                    if (!NumberWordParser.TryExtractNumberBefore(head, profile.Language, out number))
                        continue;
                    consumedStart -= head.Length;
                }
            }

            // Map the match position back to the segment that contains it.
            var segIndex = 0;
            for (var i = 0; i < segments.Count; i++)
            {
                if (segStartChar[i] <= m.Index)
                    segIndex = i;
                else
                    break;
            }
            var spansMerge = mergeBoundaryChar is { } b && consumedStart < b && b < consumedEnd;
            yield return new PhraseMatch(
                number, segments[segIndex].StartSeconds, segments[segIndex].Probability, spansMerge);
        }
    }

    /// <summary>
    /// Fully transcribes a region of the file and returns all chapter starts found in it. Used
    /// to close sequence gaps left by the silence-probe fast path (Pass 3). Every chunk border
    /// is snapped to the nearest silence (or, with --jingle, VAD non-speech region) mid-point
    /// within <see cref="Pass3SeamSearchSeconds"/> of its natural position; consecutive chunks
    /// then abut exactly at that word-safe seam - no overlap, nothing decoded twice - and a
    /// phrase straddling the seam itself is still found by carrying the previous chunk's
    /// trailing segments (<see cref="Pass3BridgeSeconds"/>) into the next chunk's matching.
    /// Only when no seam target exists near a border does the old scheme remain for that
    /// joint: a raw cut with <see cref="GapChunkOverlapSeconds"/> of overlap as redundancy
    /// against the possible mid-word cut.
    /// </summary>
    /// <param name="expectedNumbers">The chapter numbers this gap exists to recover (see
    /// <see cref="MissingNumbersInGap"/>). Transcription stops as soon as all of them are found -
    /// continuing would only re-scan audio that cannot yield anything new - so the caller can
    /// advance to the next gap (or finish Pass 3) immediately.</param>
    /// <param name="allSilences">Every silence Pass 1 stored, down to
    /// <see cref="MinStoredSilenceSeconds"/> - used both as seam targets and to pinpoint each
    /// mark at the end of the silence directly preceding its phrase.</param>
    /// <param name="knownChapters">Chapters already detected outside this region, so the
    /// per-mark progress numbers and still-missing log notes reflect the whole file rather
    /// than just this region's finds.</param>
    private async Task<List<DetectedChapter>> TranscribeRegionAsync(
        string file, MediaInfo info, double fromSeconds, double toSeconds,
        IReadOnlyList<int> expectedNumbers,
        List<Silence> allSilences, List<NonSpeechRegion> nonSpeechRegions, double bytesPerSecond,
        WorkTracker work, LanguageProfile profile, IReadOnlyList<DetectedChapter> knownChapters,
        CancellationToken ct)
    {
        var found = new List<DetectedChapter>();
        // The still-missing chapter numbers of this gap; emptied as they are found, at which
        // point there is nothing left to recover here and transcription can stop early.
        var remaining = new HashSet<int>(expectedNumbers);
        // The previous chunk's own transcript in absolute file time, and whether the seam it
        // ends at was snapped (overlap-free) - the inputs to the cross-chunk bridging below.
        List<TranscriptSegment> previousChunkAbs = [];
        var previousSeamSnapped = false;
        var chunkStart = fromSeconds;
        while (chunkStart < toSeconds)
        {
            ct.ThrowIfCancellationRequested();
            var naturalEnd = Math.Min(chunkStart + GapChunkSeconds, toSeconds);
            var seam = naturalEnd < toSeconds
                ? FindNearestSeam(naturalEnd,
                    Math.Max(chunkStart, naturalEnd - Pass3SeamSearchSeconds),
                    Math.Min(naturalEnd + Pass3SeamSearchSeconds, toSeconds),
                    upperInclusive: true, targetStartAtOrBefore: null,
                    allSilences, nonSpeechRegions, _options.Jingle)
                : null;
            var chunkEnd = seam ?? naturalEnd;

            var samples = await _audio.DecodePcmAsync(file, chunkStart, chunkEnd - chunkStart, info.InputDecoder, ct);
            var segments = await TranscribeCountingAsync(samples, ct);
            LogTranscript($"gap chunk @{FormatTimestamp(chunkStart)}", segments);
            var freshAbs = ShiftSegments(segments, chunkStart);

            // At a snapped seam the chunks share no audio, so a phrase straddling the seam
            // exists in neither chunk alone - bridge it by prepending the previous chunk's
            // trailing segments to this chunk's matching input. Unsnapped borders overlap
            // instead and need no bridge; bridging there would only duplicate the overlap's
            // text and risk parsing a number across the duplicated join.
            List<TranscriptSegment> carried = previousSeamSnapped
                ? previousChunkAbs.Where(s => s.EndSeconds > chunkStart - Pass3BridgeSeconds).ToList()
                : [];
            List<TranscriptSegment> matchSegments = carried.Count > 0 ? [.. carried, .. freshAbs] : freshAbs;
            // Same leading silence/jingle correction Pass 2 applies, so a phrase Whisper
            // timestamped from the pause before it is anchored from its real onset here too.
            matchSegments = TrimLeadingNonSpeech(matchSegments, allSilences, nonSpeechRegions, _options.Jingle);

            // Unlike Pass 2 there is no window-relative timing rule here, so matching simply
            // runs in absolute file time: a match's PhraseStartSeconds is already absolute.
            foreach (var match in FindPhraseMatches(matchSegments, profile,
                         carried.Count > 0 ? carried.Count : null))
            {
                var phraseAbs = match.PhraseStartSeconds;
                // A match entirely inside the carried tail was already found (and reported) by
                // the previous chunk's own pass; only a seam-straddling detection is news here.
                if (phraseAbs < chunkStart && !match.SpansMerge)
                    continue;
                // The chapter bounding this gap's start or end is already known (that is what
                // made this a gap in the first place) and can resurface right at a chunk
                // border - its own announcement sitting just inside the scanned range - without
                // being new information. Leave the existing mark alone and stay silent about it,
                // rather than risking Normalize picking this re-detection's timestamp instead.
                if (knownChapters.Any(k => k.Number == match.Number))
                    continue;
                if (match.SpansMerge)
                    _log?.Invoke($"chapter {match.Number} detection spans a Pass 3 chunk seam " +
                                 "(bridged from the previous chunk) - worth a spot check");
                double time;
                // The silence/jingle the mark anchored to, hoisted out of the branches below so
                // the per-file statistics can be recorded once, uniformly (see RecordChapterStats).
                Silence? statSilence = null;
                NonSpeechRegion? statRegion = null;
                if (_options.Jingle)
                {
                    // Same VAD-region-primary anchor resolution as Pass 2 (ResolveJingleAnchor),
                    // just against a fixed lookback since a gap chunk has no meaningful probe
                    // window start of its own; ComputeJingleMark then decides the mark exactly as
                    // Pass 2 would. Resolving from the region rather than the nearest silence keeps
                    // Pass 3 from anchoring a silence-less jingle transition to a false in-text
                    // pause that merely happens to fall within the lookback.
                    var lookback = _options.MaxJingleSeconds + PhraseMarginSeconds;
                    var (anchorSilence, vadRegion) = ResolveJingleAnchor(
                        phraseAbs, phraseAbs - lookback, allSilences, nonSpeechRegions, candidateVadRegion: null);
                    time = ComputeJingleMark(phraseAbs, anchorSilence, vadRegion?.StartSeconds);
                    (statSilence, statRegion) = (anchorSilence, vadRegion);
                }
                else
                {
                    // Pinpoint the mark at the end of the silence directly preceding the
                    // phrase - the same "the chapter starts where the break ends" placement
                    // Pass 2 uses - falling back to the phrase start when none is close enough.
                    var anchor = FindRealAnchorSilence(phraseAbs - PhraseLatestStart, phraseAbs, allSilences);
                    time = anchor?.EndSeconds ?? phraseAbs;
                    statSilence = anchor;
                }
                found.Add(new DetectedChapter(match.Number, time, match.Confidence));
                RecordChapterStats(match.Number, statSilence, statRegion, phraseAbs);
                remaining.Remove(match.Number);
                var (highest, missingNumbers) = ChapterProgress(knownChapters.Concat(found));
                work.HighestChapter = highest;
                work.MissingChapters = missingNumbers.Count;
                _log?.Invoke($"chapter {match.Number} found in gap, mark placed at {FormatTimestamp(time)} " +
                             $"(confidence {match.Confidence:0.00}){LowConfidenceNote(match.Confidence)}" +
                             MissingNote(missingNumbers));
            }
            work.Advance((long)((chunkEnd - chunkStart) * bytesPerSecond));

            // Every chapter this gap was meant to recover is found - the rest of the region can
            // only hold audio already accounted for by the chapters bounding it, so stop here and
            // let the caller move on to the next gap. The unscanned remainder still counts as this
            // gap's work done, so advance it to keep the Pass 3 progress bar honest.
            if (remaining.Count == 0)
            {
                _log?.Invoke("gap complete - all expected chapters found");
                if (chunkEnd < toSeconds)
                    work.Advance((long)((toSeconds - chunkEnd) * bytesPerSecond));
                break;
            }
            if (chunkEnd >= toSeconds)
                break;
            previousChunkAbs = freshAbs;
            previousSeamSnapped = seam.HasValue;
            // A snapped border needs no overlap - the next decode starts exactly at the seam;
            // an unsnapped one keeps the redundancy overlap against its possible mid-word cut.
            chunkStart = seam ?? chunkEnd - GapChunkOverlapSeconds;
        }
        return found;
    }

    /// <summary>
    /// Logs a Whisper transcript's header line and, only with --verbose-transcripts, the segments
    /// themselves (each with its start/end time relative to the decoded window). Under plain
    /// --verbose just the header - the "&lt;length&gt;@&lt;timestamp&gt;" context - is printed, so
    /// the log stays readable without the full recognizer output. Does nothing when not verbose.
    /// </summary>
    /// <param name="context">Description of the decoded window, e.g. "probe 50s@0:12:34.00".</param>
    /// <param name="segments">The transcribed segments.</param>
    private void LogTranscript(string context, List<TranscriptSegment> segments)
    {
        if (!_options.VerboseTranscripts)
        {
            _log?.Invoke(context);
            return;
        }
        _log?.Invoke(segments.Count == 0
            ? $"{context}: (no speech recognized)"
            : $"{context}: " + string.Join(" | ",
                segments.Select(s =>
                    $"{s.StartSeconds:0.0}-{s.EndSeconds:0.0} (p={s.Probability:0.00}) \"{s.Text.Trim()}\"")));
    }

    /// <summary>
    /// Returns copies of <paramref name="segments"/> with every timestamp shifted by
    /// <paramref name="delta"/> seconds. Used to move a probe's transcript between window-relative
    /// time (what Whisper emits and <c>FindPhraseMatches</c> expects) and absolute file time (how
    /// Pass 2's overlap cache stores it): a positive delta of the window start makes segments
    /// absolute, a negative delta makes them window-relative again.
    /// </summary>
    /// <param name="segments">The segments to shift.</param>
    /// <param name="delta">Seconds to add to each segment's start and end time.</param>
    private static List<TranscriptSegment> ShiftSegments(IEnumerable<TranscriptSegment> segments, double delta) =>
        segments.Select(s => s with
        {
            StartSeconds = s.StartSeconds + delta,
            EndSeconds = s.EndSeconds + delta,
        }).ToList();

    /// <summary>
    /// Advances each transcript segment's start past any run of silence and/or jingle (VAD
    /// non-speech) that Whisper lumped into the head of the segment, so the timestamp points at
    /// the actual speech onset. Whisper timestamps a segment from where its decoded audio block
    /// begins; for the segment that carries a chapter announcement after a pause and/or a jingle,
    /// that is the start of the leading non-speech, not of the spoken phrase. Left uncorrected,
    /// the phrase's apparent start sits back in the previous chapter's trailing audio, which both
    /// mis-places the mark (the anchor logic keys off the phrase start) and feeds the
    /// --min-silence-length / --max-jingle-length auto mechanisms a mis-measured (wrong, usually
    /// shorter) silence. Both detectors' findings are available here - silencedetect down to
    /// <see cref="MinStoredSilenceSeconds"/>, plus VAD regions with --jingle - so the real onset
    /// is the far end of the contiguous run of non-speech intervals that begins at (or a hair
    /// before, see <see cref="SegmentLeadTrimToleranceSeconds"/>) the segment's timestamp, chained
    /// through directly abutting intervals (a silence immediately followed by its jingle). The run
    /// is never followed past the segment's own end - a segment that matched a phrase always has
    /// some speech in it, so a leading run consuming the whole segment would be spurious.
    /// Segments are in absolute file time, matching the silence/region lists. Internal for unit
    /// testing.
    /// </summary>
    /// <param name="segmentsAbs">The window's transcript segments, in absolute file time.</param>
    /// <param name="allSilences">Every silence Pass 1 stored, down to
    /// <see cref="MinStoredSilenceSeconds"/>.</param>
    /// <param name="nonSpeechRegions">VAD non-speech regions; empty when --jingle is off.</param>
    /// <param name="jingle">True when --jingle is in effect, enabling the region intervals.</param>
    internal static List<TranscriptSegment> TrimLeadingNonSpeech(
        List<TranscriptSegment> segmentsAbs, List<Silence> allSilences,
        List<NonSpeechRegion> nonSpeechRegions, bool jingle)
    {
        // The non-speech intervals a segment start can be advanced through: every stored silence
        // plus, with --jingle, every VAD non-speech region.
        var intervals = allSilences.Select(s => (s.StartSeconds, s.EndSeconds));
        if (jingle)
            intervals = intervals.Concat(nonSpeechRegions.Select(r => (r.StartSeconds, r.EndSeconds)));
        var nonSpeech = intervals.ToList();

        return segmentsAbs.Select(seg =>
        {
            var onset = seg.StartSeconds;
            // Chase the run: any interval that begins at or just before the current onset and
            // extends past it (without spilling beyond the segment) pushes the onset to its end.
            // Re-scan until stable so a silence directly abutting a jingle is chained through.
            bool advanced;
            do
            {
                advanced = false;
                foreach (var (from, to) in nonSpeech)
                {
                    if (from <= onset + SegmentLeadTrimToleranceSeconds
                        && to > onset + SegmentLeadTrimToleranceSeconds
                        && to <= seg.EndSeconds)
                    {
                        onset = to;
                        advanced = true;
                    }
                }
            } while (advanced);
            return onset > seg.StartSeconds ? seg with { StartSeconds = onset } : seg;
        }).ToList();
    }

    /// <summary>Trailing note appended to a --verbose detection log line when the segment
    /// confidence is below <see cref="LowConfidenceThreshold"/>.</summary>
    private static string LowConfidenceNote(double confidence)
        => confidence < LowConfidenceThreshold ? " - LOW CONFIDENCE, worth a manual check" : "";

    /// <summary>Formats a position in the file as h:mm:ss.ff for log messages.</summary>
    /// <param name="seconds">Position in seconds.</param>
    private static string FormatTimestamp(double seconds)
        => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"h\:mm\:ss\.ff");
}
