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
public readonly record struct DetectionResult(
    IReadOnlyList<DetectedChapter> Chapters, bool GapRemains, IReadOnlyList<int> MissingNumbers,
    IReadOnlyList<int> LowConfidenceNumbers, LanguageProfile Profile,
    string? DetectedLanguage, double DetectedProbability);

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
    /// When one probe window overlaps the previous one, the overlapping portion is not
    /// re-transcribed with Whisper - the prior window's cached transcript is reused for it (see
    /// the reuse logic in ProbeAsync). Only the not-yet-transcribed tail is decoded and sent to
    /// Whisper, but starting this many seconds <em>before</em> the overlap border rather than
    /// exactly at it: Whisper's accuracy near the very start of a decode is poorer (it has no
    /// left-hand acoustic context), so the tail decode reaches back a little into the already
    /// cached region as pure context. Segments produced within that reached-back margin are
    /// discarded in favor of the cached ones, so the margin only improves the fresh tail's
    /// transcription without double-counting the overlap.
    /// </summary>
    private const double ProbeReuseContextSeconds = 2.0;

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
    /// With --max-jingle-length auto, each resized probe window is this factor times the
    /// longest jingle observed so far (plus <see cref="PhraseMarginSeconds"/>), giving a bit of
    /// headroom for normal length variation between chapters instead of sizing the window to
    /// exactly the longest jingle seen yet.
    /// </summary>
    private const double JingleObservationSafetyFactor = 1.25;

    /// <summary>
    /// With --min-silence-length auto, each chapter mark tightens the Pass 2 probing threshold
    /// to this factor times the length of the silence that triggered it, so probing keeps
    /// following silences close to the length of recent inter-chapter breaks (allowing a bit
    /// of slack below it) while skipping clearly shorter in-chapter pauses.
    /// </summary>
    private const double AdaptiveTightenFactor = 0.9;

    /// <summary>Chunk length in seconds for full transcription of gap regions.</summary>
    private const double GapChunkSeconds = 600;

    /// <summary>Overlap between gap transcription chunks so no phrase is cut in half.</summary>
    private const double GapChunkOverlapSeconds = 10;

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
        var bytesPerSecond = info.DurationSeconds > 0 ? info.SizeBytes / info.DurationSeconds : 0;
        var jingleCeilingSeconds = _options.MaxJingleSeconds + PhraseMarginSeconds;
        var probeSeconds = _options.Jingle ? jingleCeilingSeconds : ProbeSecondsPlain;

        // With --max-jingle-length auto, the raw length of the longest jingle observed so far
        // (from the second mark found - see the AutoMinSilence precedent below for why the
        // first is excluded; chapters with no/an ultra-short jingle are excluded too, see
        // MinJingleObservationSeconds). probeSeconds (captured by ProbeAsync below) is resized
        // to JingleObservationSafetyFactor times this plus margin, never past the original
        // ceiling, so later probes decode less audio once a real jingle length is known.
        var observedMaxJingleSeconds = 0.0;

        // Pass 1: silence scan (one full pass over the file). With --jingle, a VAD pre-pass
        // runs concurrently over the very same decode (see DetectSilencesAndStreamPcmAsync) -
        // silencedetect alone never produces a Pass 2 candidate at a chapter transition where
        // the jingle abuts speech on both sides with no amplitude gap; VAD sees that transition
        // as a non-speech region (music, like silence, reads as non-speech to a speech
        // detector) regardless of amplitude, so it can catch what silencedetect misses. See
        // ComputeJingleMark for how the two detectors' findings combine to place the mark.
        work.BeginPhase("Pass 1", info.SizeBytes);
        List<Silence> silences;
        var nonSpeechRegions = new List<NonSpeechRegion>();
        if (_options.Jingle && _vad is { } vad)
        {
            List<SpeechSegment> speech = [];
            silences = await _audio.DetectSilencesAndStreamPcmAsync(
                file, info.DurationSeconds, _options.MinSilenceSeconds, SilenceNoiseDb,
                async (pcm, innerCt) => speech = await vad.DetectSpeechAsync(pcm, innerCt),
                seconds => work.SetPhaseProgress((long)(seconds * bytesPerSecond)), info.InputDecoder, ct);
            nonSpeechRegions = ComputeNonSpeechRegions(speech);
        }
        else
        {
            silences = await _audio.DetectSilencesAsync(
                file, info.DurationSeconds, _options.MinSilenceSeconds, SilenceNoiseDb,
                seconds => work.SetPhaseProgress((long)(seconds * bytesPerSecond)), info.InputDecoder, ct);
        }

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

        var probeBytes = (long)(probeSeconds * bytesPerSecond);
        work.BeginPhase("Pass 2", probeBytes * candidates.Count);

        // With --lang auto, the language is resolved once per file, from the very first probe
        // window's samples (always at start 0, decoded below like any other window - no extra
        // decode needed) - then fixed for the rest of the file via ChangeLanguage, rather than
        // re-detected per probe, which would be both slower and inconsistent within one file.
        LanguageProfile? profile = null;
        string? detectedLanguage = null;
        var detectedProbability = 0.0;

        var found = new List<DetectedChapter>();
        // Declared here (rather than with the rest of the adaptive-threshold state below) so
        // ProbeAsync, defined next, can read it - set to the previous distinct chapter mark's
        // number by the main candidate loop, still holding that value (not yet this mark's)
        // while a probe is in flight.
        int? lastNumber = null;

        // Transcript cache for Pass 2's overlapping-window reuse. Holds the previous probe's full
        // window transcript in absolute file time, together with the absolute [from, to) span that
        // transcript actually covers. When the next candidate's window overlaps this span, the
        // overlapping segments are reused verbatim instead of being re-run through Whisper - only
        // the fresh tail is decoded (see ProbeReuseContextSeconds). cacheTo starts at negative
        // infinity so the very first probe (start 0) never counts as an overlap and always does a
        // full transcribe - which is also where --lang auto resolves the language from full samples.
        List<TranscriptSegment> cacheSegmentsAbs = [];
        var cacheFrom = 0.0;
        var cacheTo = double.NegativeInfinity;

        // Probes a single window and appends any chapter mark found in it to `found`.
        // Returns the chapter number found (or null when the phrase was not found), together
        // with the real silence found to immediately precede the phrase - see
        // FindRealAnchorSilence - for the caller to use when tightening --min-silence-length
        // instead of blindly trusting this probe's own triggering candidate.
        async Task<(int? Number, Silence? RealAnchorSilence)> ProbeAsync(
            (double Start, Silence? Silence, NonSpeechRegion? VadRegion) candidate)
        {
            var start = candidate.Start;
            ct.ThrowIfCancellationRequested();
            var windowEnd = Math.Min(start + probeSeconds, info.DurationSeconds);

            // This window's full transcript in absolute file time, assembled from the previous
            // window's cache (overlap reuse), a fresh Whisper decode, or a mix. The whole window is
            // always represented - both cases of what a reuse-only "search just the new tail" scheme
            // would silently drop are avoided: a phrase the previous probe rejected under the
            // per-silence 5 s rule but this window accepts, and a second phrase the previous probe's
            // one-mark-per-window early return never reached.
            List<TranscriptSegment> windowSegmentsAbs;
            string logLabel;

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
                    logLabel = $"probe @{FormatTimestamp(start)} (reused)";
                }
                else
                {
                    // Partial overlap: reuse cached segments up to the overlap border and transcribe
                    // only the fresh tail, reaching ProbeReuseContextSeconds back into the cache as
                    // Whisper context (those reached-back segments are dropped in favor of the cache).
                    var decodeFrom = Math.Max(start, cacheTo - ProbeReuseContextSeconds);
                    var samples = await _audio.DecodePcmAsync(file, decodeFrom,
                        windowEnd - decodeFrom, info.InputDecoder, ct);
                    var fresh = await _transcriber.TranscribeAsync(samples, ct);
                    windowSegmentsAbs = cacheSegmentsAbs
                        .Where(s => s.StartSeconds >= start && s.StartSeconds < decodeFrom)
                        .Concat(ShiftSegments(fresh, decodeFrom))
                        .ToList();
                    cacheSegmentsAbs = windowSegmentsAbs;
                    cacheFrom = start;
                    cacheTo = windowEnd;
                    logLabel = $"probe @{FormatTimestamp(start)} (tail from {FormatTimestamp(decodeFrom)})";
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

                var fresh = await _transcriber.TranscribeAsync(samples, ct);
                windowSegmentsAbs = ShiftSegments(fresh, start);
                cacheSegmentsAbs = windowSegmentsAbs;
                cacheFrom = start;
                cacheTo = windowEnd;
                logLabel = $"probe @{FormatTimestamp(start)}";
            }

            // FindPhraseMatches and the mark-placement math below work in window-relative time.
            var segments = ShiftSegments(windowSegmentsAbs, -start);
            LogTranscript(logLabel, segments);

            // profile is resolved on the first probe, which is always a full decode (the cache is
            // empty then), so it is non-null by the time any transcript-reuse branch above runs.
            foreach (var match in FindPhraseMatches(segments, profile!))
            {
                if (!_options.Jingle && match.PhraseStartSeconds > PhraseLatestStart)
                    continue; // without a jingle the phrase must directly follow the silence

                var phraseAbs = start + match.PhraseStartSeconds;
                // An in-text pause long enough to itself pass the --min-silence-length
                // threshold can trigger a probe whose window - especially the wide one used
                // with --jingle - still reaches the real chapter transition further along, so
                // this probe's own triggering silence (at `start`) is not necessarily the one
                // immediately preceding the phrase.
                Silence? realAnchorSilence;
                NonSpeechRegion? realAnchorVadRegion = null;
                if (_options.Jingle)
                {
                    // With --jingle, anchor to the VAD jingle region ending at the phrase, not to
                    // whichever silence triggered this probe: a false in-text pause earlier in the
                    // previous chapter does not lead that region, so it must not become the anchor
                    // (which would mark at the pause and feed the auto mechanisms a bogus jingle
                    // length). See ResolveJingleAnchor. When neither a region nor a closer silence
                    // was found, fall back to this probe's own triggering silence.
                    (realAnchorSilence, realAnchorVadRegion) = ResolveJingleAnchor(
                        phraseAbs, start, silences, nonSpeechRegions, candidate.VadRegion);
                    if (realAnchorSilence == null && realAnchorVadRegion == null)
                        realAnchorSilence = candidate.Silence;
                }
                else
                {
                    // Without a jingle, re-derive the real preceding silence from the full list,
                    // falling back to this probe's own triggering silence when none was found
                    // between the window start and the phrase.
                    realAnchorSilence = FindRealAnchorSilence(start, phraseAbs, silences) ?? candidate.Silence;
                }

                var time = _options.Jingle
                    ? ComputeJingleMark(phraseAbs, realAnchorSilence, realAnchorVadRegion?.StartSeconds)
                    : Math.Max(0, start + (start == 0 ? match.PhraseStartSeconds : 0));
                _log?.Invoke($"chapter {match.Number} detected, mark placed at {FormatTimestamp(time)} " +
                             $"(confidence {match.Confidence:0.00}){LowConfidenceNote(match.Confidence)}");
                found.Add(new DetectedChapter(match.Number, time, match.Confidence));
                work.ChaptersFound = CountDistinct(found);

                if (_options.Jingle && _options.AutoMaxJingle && lastNumber.HasValue)
                {
                    // lastNumber.HasValue means this is at least the second mark found, so its
                    // triggering candidate is a real inter-chapter jingle - not the
                    // intro-to-chapter-1 gap, which can easily run longer (or shorter) than a
                    // book's regular jingles and would otherwise size the window off a
                    // one-off observation before any real jingle has even been seen. Same
                    // reasoning as the analogous --min-silence-length auto tightening below.
                    // The real jingle length is the gap between the real preceding anchor (not
                    // necessarily this probe's own triggering candidate, see above) and the
                    // phrase - using the raw offset from this probe's own window start would
                    // inflate the observation whenever a false, earlier in-text pause was what
                    // actually triggered this probe. When the anchor is a VAD region (no
                    // leading silence), its own boundaries give a more accurate jingle length
                    // than the phrase-relative estimate, since the phrase can start a moment
                    // after the jingle actually ends.
                    var observedLength = realAnchorSilence is { } ras
                        ? phraseAbs - ras.EndSeconds
                        : realAnchorVadRegion is { } rvr
                            ? rvr.EndSeconds - rvr.StartSeconds
                            : phraseAbs - start;
                    if (observedLength >= MinJingleObservationSeconds && observedLength > observedMaxJingleSeconds)
                    {
                        // Track the longest jingle seen so far and resize future probe windows
                        // to it plus a safety margin, capped at the original ceiling so an
                        // outlier can never make the window wider than what --max-jingle-length
                        // was given (or its 45 s default) would allow. Not shrink-only: a later,
                        // genuinely longer jingle must widen the window back out too, or the
                        // safety margin could never do its job.
                        observedMaxJingleSeconds = observedLength;
                        var resized = Math.Min(jingleCeilingSeconds,
                            JingleObservationSafetyFactor * observedMaxJingleSeconds + PhraseMarginSeconds);
                        if (resized != probeSeconds)
                        {
                            probeSeconds = resized;
                            _log?.Invoke($"Pass 2: jingle probe window resized to {probeSeconds:0.#} s");
                        }
                    }
                }
                return (match.Number, realAnchorSilence); // one chapter per probe window
            }
            return (null, null);
        }

        // Adaptive threshold state (--min-silence-length auto only; otherwise every candidate
        // is probed unconditionally, same as before this feature existed). Probing proceeds
        // unthrottled until the second mark is found (its triggering silence is the first
        // real inter-chapter break - the silence before the first mark is typically the
        // intro/title silence, often longer, so it must not be used to tighten). From there,
        // every new mark tightens the threshold to AdaptiveTightenFactor * its own triggering
        // silence's length; a sequence gap resets it to the 1.5 s floor and re-probes
        // everything skipped since the last mark, so gap-filling stays inside Pass 2 where
        // possible and Pass 3's full transcription is only needed if that still fails.
        var threshold = _options.MinSilenceSeconds;
        var skippedSinceLastMark = new List<(double Start, Silence? Silence, NonSpeechRegion? VadRegion)>();

        foreach (var candidate in candidates)
        {
            if (_options.AutoMinSilence && candidate.Silence is { } candidateSilence &&
                candidateSilence.EndSeconds - candidateSilence.StartSeconds < threshold)
            {
                skippedSinceLastMark.Add(candidate);
                work.Advance(probeBytes);
                continue;
            }

            // A VAD candidate qualified against the probe window at merge time, but that
            // window can since have narrowed (--max-jingle-length auto) once a baseline is
            // known - recheck here so probing keeps skipping regions too long to be this
            // book's jingle, same as the merge-time filter intends after the baseline exists.
            if (candidate.VadRegion is { } region && region.EndSeconds - candidate.Start > probeSeconds)
            {
                skippedSinceLastMark.Add(candidate);
                work.Advance(probeBytes);
                continue;
            }

            var (number, realAnchorSilence) = await ProbeAsync(candidate);
            work.Advance(probeBytes);

            if (number is not { } n || n <= (lastNumber ?? 0))
                continue; // no match, or a duplicate/regression (e.g. an in-text mention)

            if (_options.AutoMinSilence)
            {
                if (lastNumber.HasValue && n > lastNumber.Value + 1 && skippedSinceLastMark.Count > 0)
                {
                    _log?.Invoke($"Pass 2: sequence gap between chapter {lastNumber} and {n}, " +
                                 $"re-probing {skippedSinceLastMark.Count} skipped candidate(s) at the " +
                                 $"{_options.MinSilenceSeconds:0.#} s floor");
                    threshold = _options.MinSilenceSeconds;
                    if (_options.Jingle && _options.AutoMaxJingle && probeSeconds != jingleCeilingSeconds)
                    {
                        probeSeconds = jingleCeilingSeconds;
                        _log?.Invoke($"Pass 2: jingle probe window reset to {probeSeconds:0.#} s");
                    }
                    foreach (var skipped in skippedSinceLastMark)
                        await ProbeAsync(skipped);
                }
                // realAnchorSilence, when present, is the silence that truly precedes the
                // phrase (already defaulted to this probe's own triggering silence inside
                // ProbeAsync when no closer one was found - see FindRealAnchorSilence there).
                else if (lastNumber.HasValue && realAnchorSilence is { } triggeringSilence)
                {
                    // lastNumber.HasValue means this is at least the second mark found, so
                    // its triggering silence is a real inter-chapter break - not the
                    // intro-to-chapter-1 silence, which is routinely longer than that and
                    // would otherwise over-tighten the threshold from the very first mark.
                    // Never below the MinSilenceSeconds floor: Pass 1's silence scan never
                    // detects anything shorter than that floor in the first place, so every
                    // candidate is already >= it - a threshold below the floor would skip
                    // nothing at all and silently defeat the whole point of tightening.
                    var tightened = Math.Max(_options.MinSilenceSeconds,
                        AdaptiveTightenFactor * (triggeringSilence.EndSeconds - triggeringSilence.StartSeconds));
                    // Only announce an actual change: when the threshold is already at the floor
                    // and this mark's silence would clamp it right back to the floor, nothing was
                    // tightened, so claiming so in the log would be misleading noise.
                    if (tightened != threshold)
                        _log?.Invoke($"Pass 2: threshold tightened to {tightened:0.##} s after chapter {n}");
                    threshold = tightened;
                }
                skippedSinceLastMark.Clear();
            }
            lastNumber = n;
        }

        var chapters = Normalize(found);

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
            _log?.Invoke($"Pass 3: transcribing suspicious region " +
                         $"{FormatTimestamp(gap.FromSeconds)} - {FormatTimestamp(gap.ToSeconds)}");
            var fills = await TranscribeRegionAsync(file, info, gap.FromSeconds, gap.ToSeconds,
                silences, nonSpeechRegions, bytesPerSecond, work, profile!, ct);
            chapters = Normalize(chapters.Concat(fills).ToList());
            work.ChaptersFound = chapters.Count;
        }

        // Final consistency check: internal gaps that remain are fatal for this file.
        var missing = new List<int>();
        for (var i = 1; i < chapters.Count; i++)
            for (var n = chapters[i - 1].Number + 1; n < chapters[i].Number; n++)
                missing.Add(n);

        var lowConfidence = chapters
            .Where(c => c.Confidence < LowConfidenceThreshold)
            .Select(c => c.Number)
            .ToList();
        return new DetectionResult(
            chapters, missing.Count > 0, missing, lowConfidence,
            profile!, detectedLanguage, detectedProbability);
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
    /// happened to trigger the probe. The jingle is the VAD non-speech region ending at the
    /// phrase; a silencedetect silence is accepted as the anchor <em>only</em> when it
    /// <see cref="LeadingSilence">leads that region</see> (its end lies inside it) - the classic
    /// "silence then jingle" transition, where the mark goes 0.5 s before the silence. When the
    /// region has no leading silence (a silence-less jingle) the region itself is the anchor and
    /// the mark goes at the jingle start with no lead.
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
    /// <param name="silences">All silences found by the silence scan.</param>
    /// <param name="nonSpeechRegions">All VAD non-speech regions (empty when VAD is off).</param>
    /// <param name="candidateVadRegion">The region a VAD candidate carries, if this probe was
    /// triggered by one; used directly instead of re-deriving it. Null for silence candidates
    /// and for Pass 3.</param>
    private static (Silence? AnchorSilence, NonSpeechRegion? VadRegion) ResolveJingleAnchor(
        double phraseAbs, double earliestAnchor, List<Silence> silences,
        List<NonSpeechRegion> nonSpeechRegions, NonSpeechRegion? candidateVadRegion)
    {
        var jingleRegion = candidateVadRegion ?? FindLastRegionEndingWithin(earliestAnchor, phraseAbs, nonSpeechRegions);
        if (jingleRegion is { } jr)
        {
            var leading = LeadingSilence(jr, silences);
            return leading is { } ls ? (ls, null) : (null, jr);
        }
        return (FindRealAnchorSilence(earliestAnchor, phraseAbs, silences), null);
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
    /// Finds the VAD non-speech region (the jingle) that ends at a matched phrase, the same way
    /// <see cref="FindRealAnchorSilence"/> does for silencedetect silences. The region's end is
    /// matched against the phrase with <see cref="JinglePhraseMatchToleranceSeconds"/> of slack,
    /// so a region ending a hair after the phrase (VAD and Whisper time boundaries slightly
    /// differently) is still recognised rather than missed. Returns null when none was found in
    /// the window.
    /// </summary>
    private static NonSpeechRegion? FindLastRegionEndingWithin(
        double windowStart, double phraseAbsSeconds, List<NonSpeechRegion> regions)
    {
        var latestEnd = phraseAbsSeconds + JinglePhraseMatchToleranceSeconds;
        var region = regions.LastOrDefault(r => r.EndSeconds > windowStart && r.EndSeconds <= latestEnd);
        return region == default ? null : region;
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
    /// <param name="windowStart">Absolute start of the probe window in seconds.</param>
    /// <param name="phraseAbsSeconds">Absolute phrase start in seconds.</param>
    /// <param name="silences">All silences found by the silence scan.</param>
    private static Silence? FindRealAnchorSilence(double windowStart, double phraseAbsSeconds, List<Silence> silences)
    {
        var silence = silences.LastOrDefault(s => s.EndSeconds > windowStart && s.EndSeconds <= phraseAbsSeconds);
        return silence == default ? null : silence;
    }

    /// <summary>Counts distinct chapter numbers in a raw detection list (for progress display).</summary>
    private static int CountDistinct(List<DetectedChapter> found)
        => found.Select(c => c.Number).Distinct().Count();

    /// <summary>A phrase match inside a transcribed window.</summary>
    /// <param name="Number">Parsed chapter number.</param>
    /// <param name="PhraseStartSeconds">Phrase start relative to the window start.</param>
    /// <param name="Confidence">Whisper's probability for the segment the match was found in.</param>
    private readonly record struct PhraseMatch(int Number, double PhraseStartSeconds, double Confidence);

    /// <summary>
    /// Searches the transcribed segments for the chapter phrase and parses the chapter number,
    /// either from the regexp capturing group or from the words following the phrase
    /// ("Chapter Seven"); when neither yields a number, the words directly preceding the
    /// phrase are tried ("Erstes Kapitel", "Birinci Bölüm").
    /// </summary>
    private static IEnumerable<PhraseMatch> FindPhraseMatches(List<TranscriptSegment> segments, LanguageProfile profile)
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

        foreach (Match m in profile.PhraseRegex.Matches(text))
        {
            int number;
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
                if (!NumberWordParser.TryExtractNumber(tail, profile.Language, out number))
                {
                    // No number after the phrase - try the ordinal-first announcement
                    // order ("Erstes Kapitel", "2. Kapitel", "Birinci Bölüm").
                    var head = text[..m.Index];
                    if (head.Length > 80)
                        head = head[^80..];
                    if (!NumberWordParser.TryExtractNumberBefore(head, profile.Language, out number))
                        continue;
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
            yield return new PhraseMatch(number, segments[segIndex].StartSeconds, segments[segIndex].Probability);
        }
    }

    /// <summary>
    /// Fully transcribes a region of the file in overlapping chunks and returns all chapter
    /// starts found in it. Used to close sequence gaps left by the silence-probe fast path.
    /// </summary>
    private async Task<List<DetectedChapter>> TranscribeRegionAsync(
        string file, MediaInfo info, double fromSeconds, double toSeconds,
        List<Silence> silences, List<NonSpeechRegion> nonSpeechRegions, double bytesPerSecond,
        WorkTracker work, LanguageProfile profile, CancellationToken ct)
    {
        var found = new List<DetectedChapter>();
        for (var chunkStart = fromSeconds; chunkStart < toSeconds; chunkStart += GapChunkSeconds - GapChunkOverlapSeconds)
        {
            ct.ThrowIfCancellationRequested();
            var chunkLen = Math.Min(GapChunkSeconds, toSeconds - chunkStart);
            var samples = await _audio.DecodePcmAsync(file, chunkStart, chunkLen, info.InputDecoder, ct);
            var segments = await _transcriber.TranscribeAsync(samples, ct);
            LogTranscript($"gap chunk @{FormatTimestamp(chunkStart)}", segments);

            foreach (var match in FindPhraseMatches(segments, profile))
            {
                var phraseAbs = chunkStart + match.PhraseStartSeconds;
                double time;
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
                        phraseAbs, phraseAbs - lookback, silences, nonSpeechRegions, candidateVadRegion: null);
                    time = ComputeJingleMark(phraseAbs, anchorSilence, vadRegion?.StartSeconds);
                }
                else
                {
                    time = phraseAbs;
                }
                _log?.Invoke($"chapter {match.Number} found in gap, mark placed at {FormatTimestamp(time)} " +
                             $"(confidence {match.Confidence:0.00}){LowConfidenceNote(match.Confidence)}");
                found.Add(new DetectedChapter(match.Number, time, match.Confidence));
            }
            work.Advance((long)(chunkLen * bytesPerSecond));
        }
        return found;
    }

    /// <summary>
    /// Logs a Whisper transcript in --verbose mode: every segment with its start/end time
    /// relative to the decoded window. Does nothing when not verbose.
    /// </summary>
    /// <param name="context">Description of the decoded window, e.g. "probe @0:12:34.00".</param>
    /// <param name="segments">The transcribed segments.</param>
    private void LogTranscript(string context, List<TranscriptSegment> segments)
    {
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

    /// <summary>Trailing note appended to a --verbose detection log line when the segment
    /// confidence is below <see cref="LowConfidenceThreshold"/>.</summary>
    private static string LowConfidenceNote(double confidence)
        => confidence < LowConfidenceThreshold ? " - LOW CONFIDENCE, worth a manual check" : "";

    /// <summary>Formats a position in the file as h:mm:ss.ff for log messages.</summary>
    /// <param name="seconds">Position in seconds.</param>
    private static string FormatTimestamp(double seconds)
        => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"h\:mm\:ss\.ff");
}
