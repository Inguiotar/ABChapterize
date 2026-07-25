// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Audio;
using ABChapterize.Cli;
using ABChapterize.Language;
using ABChapterize.Transcription;
using ABChapterize.Ui;
using ABChapterize.Vad;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Text;
using static ABChapterize.Detection.DetectionFormatting;
using static ABChapterize.Detection.DetectionTuning;
using static ABChapterize.Detection.GapPlanning;
using static ABChapterize.Detection.JingleGeometry;
using static ABChapterize.Detection.PhraseMatching;

namespace ABChapterize.Detection;

/// <summary>A detected chapter start: number plus position in the file.</summary>
/// <param name="Number">Chapter number as spoken/parsed.</param>
/// <param name="TimeSeconds">Position of the chapter marking in seconds.</param>
/// <param name="Confidence">Whisper's probability for the segment the chapter number was parsed
/// from (0-1); 1.0 when unknown. Below <see cref="DetectionTuning.LowConfidenceThreshold"/> the
/// number surfaces in <see cref="DetectionResult.LowConfidenceNumbers"/>.</param>
public readonly record struct DetectedChapter(int Number, double TimeSeconds, double Confidence = 1.0);

/// <summary>Per-file diagnostic statistics gathered during detection, surfaced per file under
/// --verbose (or --verbose-transcripts) and aggregated run-wide under --summary. The silence and
/// jingle extremes come in two flavours: one over every detected chapter, and an "inter-chapter"
/// one that excludes chapter 1 - whose intro-to-first-chapter transition is often atypically long
/// or short and would otherwise skew the picture of the book's regular chapter breaks.</summary>
/// <param name="MinPrecedingSilenceSeconds">The shortest silence found directly before a detected
/// chapter phrase - when the VAD pre-pass ran, the silence leading the jingle (a jingle framed
/// by two silences counts only its leading one); null when no chapter had a qualifying preceding
/// silence.</param>
/// <param name="MinInterChapterSilenceSeconds">As <paramref name="MinPrecedingSilenceSeconds"/>,
/// but excluding chapter 1; null when no chapter other than 1 had a qualifying silence.</param>
/// <param name="MaxJingleLengthSeconds">The longest jingle found before a detected chapter phrase
/// (only measured when the VAD pre-pass ran); null when the pre-pass did not run, or when no
/// jingle was measured.</param>
/// <param name="MaxInterChapterJingleSeconds">As <paramref name="MaxJingleLengthSeconds"/>, but
/// excluding chapter 1; null when no chapter other than 1 had a measured jingle.</param>
/// <param name="WhisperAudioSeconds">Total audio decoded and handed to Whisper during detection,
/// counting re-probed stretches each time they were transcribed; compare against the file's run
/// length for the fed-in share.</param>
/// <param name="WhisperTranscribeSeconds">Wall-clock time spent inside the Whisper transcription
/// calls themselves (not decoding). <see cref="WhisperAudioSeconds"/> divided by this is the
/// transcription speed relative to real time.</param>
public readonly record struct DetectionStats(
    double? MinPrecedingSilenceSeconds, double? MinInterChapterSilenceSeconds,
    double? MaxJingleLengthSeconds, double? MaxInterChapterJingleSeconds,
    double WhisperAudioSeconds, double WhisperTranscribeSeconds);

/// <summary>Outcome of chapter detection for one file.</summary>
/// <param name="Chapters">Detected chapters in chronological order; empty when none were found.</param>
/// <param name="GapRemains">True when a chapter sequence gap could not be resolved; the file must be left unchanged.</param>
/// <param name="MissingNumbers">The chapter numbers that could not be located (only when <paramref name="GapRemains"/>).</param>
/// <param name="LowConfidenceNumbers">Chapter numbers whose Whisper probability fell below
/// <see cref="DetectionTuning.LowConfidenceThreshold"/> - worth a manual spot-check.</param>
/// <param name="Profile">The language profile actually used for this file - the resolved
/// per-file profile with <c>--lang auto</c>, or the run's fixed <see cref="CliOptions.DefaultProfile"/>
/// otherwise.</param>
/// <param name="DetectedLanguage">Whisper's raw language guess with <c>--lang auto</c>; null
/// when auto-detection was not active, or was skipped because the file was too short to probe.</param>
/// <param name="DetectedProbability">Whisper's probability for <paramref name="DetectedLanguage"/>;
/// 0 when <paramref name="DetectedLanguage"/> is null. Note this may differ from
/// <see cref="Profile"/>'s language when the probability fell below
/// <see cref="DetectionTuning.AutoLanguageProbabilityThreshold"/> and the run fell back to English.</param>
/// <param name="Stats">Per-file diagnostic statistics (min preceding silence, max jingle, total
/// Whisper audio) for the --verbose and --summary reports.</param>
/// <param name="EarlyAborted">True when --early-abort cut detection short because no chapter
/// was found within its minute threshold; <paramref name="Chapters"/> is always empty in that
/// case, same as a completed scan that genuinely found nothing.</param>
/// <param name="BelowExpectedStartNumber">The chapter number Pass 2 found first, when
/// --expected-start-chapter aborted detection because it was numbered below that expectation;
/// null otherwise. <paramref name="Chapters"/> is always empty when this is set, same as
/// <paramref name="EarlyAborted"/>.</param>
/// <param name="LeadInHasSpeech">True unless the VAD pre-pass ran and found no speech at all
/// before the first chapter's own mark - i.e. the first words spoken anywhere in the file are
/// the chapter phrase itself, however much silence, music or a jingle precedes it. <see
/// cref="FileProcessor"/>'s intro-chapter insertion skips inserting one when this is false,
/// letting the mp4 muxer's own start-snapping fold that lead-in into chapter 1 instead of
/// giving it its own titled entry. Always true when the VAD pre-pass did not run (nothing to
/// check) or <paramref name="Chapters"/> is empty.</param>
public readonly record struct DetectionResult(
    IReadOnlyList<DetectedChapter> Chapters, bool GapRemains, IReadOnlyList<int> MissingNumbers,
    IReadOnlyList<int> LowConfidenceNumbers, LanguageProfile Profile,
    string? DetectedLanguage, double DetectedProbability, DetectionStats Stats, bool EarlyAborted = false,
    int? BelowExpectedStartNumber = null, bool LeadInHasSpeech = true);

/// <summary>Outcome of checking one pre-existing chapter marking against the audio, in file order -
/// the raw material <see cref="GapPlanning.BuildGapRegions"/> groups into gap-scoped
/// recovery regions for <see cref="ChapterDetector.DetectGapsAsync"/>.</summary>
/// <param name="StartSeconds">The marking's own pre-existing timestamp.</param>
/// <param name="ExpectedNumber">The chapter number parsed from the marking's title, or null when
/// its title had none (e.g. a prelude/intro entry) - such a marking counts neither as confirmed
/// nor as a gap boundary and is skipped when regions are built.</param>
/// <param name="Confirmed">True when Whisper found the expected phrase near this marking.</param>
public readonly record struct VerifyMarkingOutcome(double StartSeconds, int? ExpectedNumber, bool Confirmed);

/// <summary>Outcome of checking pre-existing chapter markings against the audio (--verify).</summary>
/// <param name="Passed">True when every checkable marking was confirmed; also true when none
/// of the file's markings had a parseable expected number (nothing to disprove).</param>
/// <param name="Checked">Number of markings that had a parseable expected number and were
/// actually probed. Markings without one (e.g. a prelude/intro entry) are not counted.</param>
/// <param name="Failed">Of <paramref name="Checked"/>, how many could not be confirmed.</param>
/// <param name="ConfirmedChapters">The confirmed markings, trusted and importable directly as
/// detected chapters - the seed <see cref="ChapterDetector.DetectGapsAsync"/> builds on instead
/// of redetecting them.</param>
/// <param name="Markings">Every marking's own outcome, in file order - the input to
/// <see cref="GapPlanning.BuildGapRegions"/>.</param>
/// <param name="Profile">The language profile resolved while verifying (or the run's fixed
/// <see cref="CliOptions.DefaultProfile"/> when nothing needed resolving); reused as-is by
/// <see cref="ChapterDetector.DetectGapsAsync"/> so gap recovery never re-resolves the language.</param>
/// <param name="DetectedLanguage">Whisper's raw language guess with <c>--lang auto</c>; null when
/// auto-detection was not active or every marking's window was empty.</param>
/// <param name="DetectedProbability">Whisper's probability for <paramref name="DetectedLanguage"/>;
/// 0 when <paramref name="DetectedLanguage"/> is null.</param>
public readonly record struct VerifyResult(
    bool Passed, int Checked, int Failed,
    IReadOnlyList<DetectedChapter> ConfirmedChapters, IReadOnlyList<VerifyMarkingOutcome> Markings,
    LanguageProfile Profile, string? DetectedLanguage, double DetectedProbability);

/// <summary>
/// Finds chapter starts in an audiobook. Fast path: detect longer-than-usual silences and
/// probe the audio following each silence with Whisper. If the resulting chapter numbers
/// contain sequence gaps, the audio between the mismatched markings is fully transcribed.
/// </summary>
public sealed class ChapterDetector
{
    /// <summary>Noise floor in dBFS for silence detection.</summary>

    private readonly CliOptions _options;
    private readonly IAudioSource _audio;
    private readonly ITranscriber _transcriber;

    /// <summary>Transcriber used for pass 3 (gap filling). The same instance as
    /// <see cref="_transcriber"/> unless <c>--pass3-model</c> selected a different model, in which
    /// case it is a <see cref="Pass3TranscriberProxy"/> onto the shared pass-3 model. Everything
    /// about the detection/marking/statistics logic is identical either way - only which model
    /// recognizes the gap chunks changes.</summary>
    private readonly ITranscriber _pass3Transcriber;

    private readonly IVoiceActivityDetector? _vad;

    /// <summary>Implements the --precise-mark correction; constructed once <see cref="_log"/> is
    /// known for the current file (see <see cref="DetectCoreAsync"/>), since its delegate-based
    /// constructor closes over this detector's own <see cref="TranscribeCountingAsync"/> so its
    /// transcriptions count toward the same per-file statistics.</summary>
    private PreciseMarkRefiner? _preciseMarkRefiner;

    /// <summary>Per-file --verbose log sink set by <see cref="DetectAsync"/>; null when not verbose.</summary>
    private Action<string>? _log;

    /// <summary>Total seconds of audio actually decoded and handed to Whisper during the current
    /// file's detection (every probe window and gap chunk, counted each time it is transcribed -
    /// re-probed audio counts again, since Whisper processed it again). Reset per file, reported
    /// as a --verbose/--summary statistic.</summary>
    private double _whisperAudioSeconds;

    /// <summary>Wall-clock seconds spent inside the Whisper transcription calls for the current
    /// file (measured in <see cref="TranscribeCountingAsync"/>, decoding excluded). Reset per file;
    /// <see cref="_whisperAudioSeconds"/> over this is the transcription speed vs. real time.</summary>
    private double _whisperTranscribeSeconds;

    /// <summary>Per detected chapter number, the length of the silence that preceded its phrase
    /// (when the VAD pre-pass ran, the silence preceding the jingle - see
    /// <see cref="RecordChapterStats"/>). Keyed by number so a re-detection overwrites; filtered
    /// to the surviving chapters when the per-file minimum is computed. Reset per file.</summary>
    private readonly Dictionary<int, double> _chapterSilenceSeconds = [];

    /// <summary>Per detected chapter number, the length of the jingle that preceded its phrase
    /// (only measured when the VAD pre-pass ran). Reset per file; feeds the per-file
    /// maximum-jingle statistic.</summary>
    private readonly Dictionary<int, double> _chapterJingleSeconds = [];

    /// <summary>Creates a detector bound to the given tools and options.</summary>
    /// <param name="options">Validated command line options.</param>
    /// <param name="audio">Audio source used for silence detection and PCM decoding.</param>
    /// <param name="transcriber">Loaded speech recognizer.</param>
    /// <param name="vad">Voice activity detector used for the full-file VAD pre-pass (finds
    /// jingle transitions with no detectable amplitude gap); null when
    /// <see cref="CliOptions.RunVadPrePass"/> is false, or in tests that don't exercise that
    /// path.</param>
    /// <param name="pass3Transcriber">Transcriber to use for pass 3 (gap filling), when
    /// <c>--pass3-model</c> asks for a model other than the main one. Null (the default) means
    /// pass 3 reuses <paramref name="transcriber"/>, i.e. the same single-model behavior as
    /// before.</param>
    public ChapterDetector(CliOptions options, IAudioSource audio, ITranscriber transcriber,
        IVoiceActivityDetector? vad = null, ITranscriber? pass3Transcriber = null)
    {
        _options = options;
        _audio = audio;
        _transcriber = transcriber;
        _pass3Transcriber = pass3Transcriber ?? transcriber;
        _vad = vad;
    }

    /// <summary>Sets the per-file --verbose log sink and refreshes <see cref="_preciseMarkRefiner"/>
    /// to close over it, so its own --precise-mark log lines land in the same sink as the rest of
    /// this file's detection log.</summary>
    /// <param name="log">Sink for --verbose log messages, or null when not verbose.</param>
    private void SetLog(Action<string>? log)
    {
        _log = log;
        _preciseMarkRefiner = new PreciseMarkRefiner(
            _audio, _options, _log, (samples, ct) => TranscribeCountingAsync(samples, ct));
    }

    /// <summary>
    /// Runs the complete detection pipeline for one file: a single Pass 2 region spanning the
    /// whole file, seeded with no prior knowledge. See <see cref="DetectGapsAsync"/> for the
    /// gap-scoped alternative run after a --verify failure.
    /// </summary>
    /// <param name="file">Path of the audio file.</param>
    /// <param name="info">Probe result of the file.</param>
    /// <param name="work">Progress tracker fed with processed bytes.</param>
    /// <param name="log">Sink for --verbose log messages, or null when not verbose.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task<DetectionResult> DetectAsync(
        string file, MediaInfo info, WorkTracker work, Action<string>? log, CancellationToken ct)
        => DetectCoreAsync(file, info, work, log, [],
            [new DetectionRegion(0, info.DurationSeconds, 0, null)], null, null, ct);

    /// <summary>
    /// Runs gap-scoped recovery after a --verify failure: <paramref name="verify"/>'s confirmed
    /// markings are trusted and imported directly, and only the region(s) <see
    /// cref="BuildGapRegions"/> builds around the unconfirmed one(s) get their own Pass 2 - the
    /// rest of the file is not re-scanned or re-transcribed at all.
    /// </summary>
    /// <param name="file">Path of the audio file.</param>
    /// <param name="info">Probe result of the file.</param>
    /// <param name="work">Progress tracker fed with processed bytes.</param>
    /// <param name="log">Sink for --verbose log messages, or null when not verbose.</param>
    /// <param name="verify">The --verify run's own result: <see cref="VerifyResult.ConfirmedChapters"/>
    /// seeds the result directly, <see cref="VerifyResult.Markings"/> is grouped into regions, and
    /// <see cref="VerifyResult.Profile"/>/<see cref="VerifyResult.DetectedLanguage"/>/<see
    /// cref="VerifyResult.DetectedProbability"/> are reused as-is so gap recovery never re-resolves
    /// the language.</param>
    /// <param name="ct">Cancellation token.</param>
    internal Task<DetectionResult> DetectGapsAsync(
        string file, MediaInfo info, WorkTracker work, Action<string>? log, VerifyResult verify, CancellationToken ct)
    {
        var plan = BuildGapRegions(verify.Markings, info.DurationSeconds);
        return DetectCoreAsync(file, info, work, log, verify.ConfirmedChapters, plan.Regions,
            (verify.Profile, verify.DetectedLanguage, verify.DetectedProbability),
            plan.TrailingFrom is { } from ? (from, plan.TrailingTargets) : null, ct);
    }

    /// <summary>
    /// Auto-resumes a file <see cref="FileProcessor.MissingMarksPath"/> tagged after a previous run
    /// left a chapter-sequence gap unresolved: the file's currently committed markings are trusted
    /// verbatim, with no --verify-style re-check against the audio (unlike <see
    /// cref="DetectGapsAsync"/>'s confirmed markings, these were never in doubt in the first place -
    /// they are exactly what pass 3 already settled on last time). Only the gap(s) <see
    /// cref="FindGaps"/> still finds between them get their own gap-scoped Pass 2 plus the existing
    /// Pass 3 tail, exactly as <see cref="DetectGapsAsync"/> does after a --verify failure - which is
    /// what lets this reuse <see cref="DetectCoreAsync"/> directly instead of a bespoke pipeline.
    /// There is never a trailing region to recover here: a missing-marks tag can only name chapters
    /// <see cref="FindGaps"/> itself flagged when the file was first tagged, and that always means a
    /// gap bounded by two confirmed chapters (or the file start) - the one case <see cref="FindGaps"/>
    /// structurally cannot flag, a still-missing trailing chapter, therefore never produces a tag to
    /// resume in the first place.
    /// </summary>
    /// <param name="file">Path of the audio file (still carrying its ".missing-marks-..." tag).</param>
    /// <param name="info">Probe result of the file, including its committed chapter markings.</param>
    /// <param name="work">Progress tracker fed with processed bytes.</param>
    /// <param name="log">Sink for --verbose log messages, or null when not verbose.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<DetectionResult> ResumeMissingMarksAsync(
        string file, MediaInfo info, WorkTracker work, Action<string>? log, CancellationToken ct)
    {
        SetLog(log);
        var (profile, detectedLanguage, detectedProbability) =
            await ResolveProfileFromMarkingsAsync(file, info, ct);
        _transcriber.ChangeLanguage(profile.Language);

        // Committed markings are trusted directly - only their own chapter number matters here
        // (parsed the same way --verify parses a marking's expected number), never re-probed. A
        // marking with no parseable number (the intro/prelude entry BuildChapters inserts) carries
        // no chapter identity and is silently dropped, exactly like an unparseable --verify marking.
        var confirmed = new List<DetectedChapter>();
        foreach (var marking in info.ExistingChapters)
            if (TryParseExpectedNumber(marking.Title, profile.Language, out var number))
                confirmed.Add(new DetectedChapter(number, marking.StartSeconds));
        confirmed = Normalize(confirmed);

        // Re-deriving the gaps from the committed markings themselves - rather than trusting the
        // tag's own number list - means this always agrees with what FindGaps/MissingNumbersInGap
        // would say right now, with no risk of drifting out of sync with the file's actual content.
        // _options.ExpectedStartChapter is passed through so a leading missing-marks tag (only
        // ever produced when that option was set to begin with) resolves to the same gap on
        // resume as the run that created it.
        var regions = FindGaps(confirmed, info.DurationSeconds, _options.ExpectedStartChapter)
            .Select(gap =>
            {
                var boundChapter = confirmed.FirstOrDefault(c => c.TimeSeconds == gap.FromSeconds);
                var lowerNumber = boundChapter.Number != 0 ? boundChapter.Number : (_options.ExpectedStartChapter ?? 1) - 1;
                return new DetectionRegion(
                    gap.FromSeconds, gap.ToSeconds, lowerNumber,
                    confirmed.First(c => c.TimeSeconds == gap.ToSeconds).Number);
            })
            .ToList();

        return await DetectCoreAsync(file, info, work, log, confirmed, regions,
            (profile, detectedLanguage, detectedProbability), null, ct);
    }

    /// <summary>
    /// The shared detection pipeline behind <see cref="DetectAsync"/> and <see
    /// cref="DetectGapsAsync"/>. Pass 1 always runs whole-file, even for a gap-scoped call -
    /// <see cref="IAudioSource"/> has no ranged silence/VAD scan, and redoing this one full-file
    /// decode is cheap next to the Whisper probing that follows. Pass 2 then runs once per entry
    /// in <paramref name="regions"/>, each with its own candidates (built only from silences/VAD
    /// regions starting inside that region) and its own adaptive-threshold/adaptive-jingle-window
    /// state starting completely fresh - a region is probed as if it were its own small file, not
    /// a continuation of whatever an earlier region's Pass 2 happened to learn. The existing
    /// sequence-gap Pass 3 (unchanged, over the accumulated <c>chapters</c> and the file's full
    /// duration) remains the final net for any interior gap regardless of how <c>chapters</c> was
    /// seeded; <paramref name="trailingFallback"/> exists only for the one case that tail
    /// structurally cannot catch - a still-missing chapter after the last one found, which
    /// nothing bounds from above to even notice.
    /// </summary>
    /// <param name="confirmedSeed">Chapters trusted verbatim, with no Whisper re-check of their
    /// own - empty for a fresh <see cref="DetectAsync"/> run.</param>
    /// <param name="regions">The independent Pass 2 region(s) to probe; a single whole-file region
    /// for <see cref="DetectAsync"/>, or the gap-scoped regions <see cref="BuildGapRegions"/> built
    /// for <see cref="DetectGapsAsync"/>.</param>
    /// <param name="known">The already-resolved language profile (from --verify) plus its own
    /// detected-language data to carry into the result verbatim, or null to resolve it lazily from
    /// the first probe's samples exactly as before this feature existed.</param>
    /// <param name="trailingFallback">The trailing region's start and expected chapter numbers,
    /// when <see cref="BuildGapRegions"/> found the last checkable --verify marking unconfirmed;
    /// null otherwise (including for a fresh <see cref="DetectAsync"/> run).</param>
    private async Task<DetectionResult> DetectCoreAsync(
        string file, MediaInfo info, WorkTracker work, Action<string>? log,
        IReadOnlyList<DetectedChapter> confirmedSeed, IReadOnlyList<DetectionRegion> regions,
        (LanguageProfile Profile, string? DetectedLanguage, double DetectedProbability)? known,
        (double From, List<int> Targets)? trailingFallback, CancellationToken ct)
    {
        SetLog(log);
        _whisperAudioSeconds = 0;
        _whisperTranscribeSeconds = 0;
        _chapterSilenceSeconds.Clear();
        _chapterJingleSeconds.Clear();
        var bytesPerSecond = info.DurationSeconds > 0 ? info.SizeBytes / info.DurationSeconds : 0;
        var jingleCeilingSeconds = _options.MaxJingleSeconds + PhraseMarginSeconds;

        var (allSilences, silences, nonSpeechRegions, speechSegments) =
            await RunPass1Async(file, info, work, bytesPerSecond, ct);

        // Pass 2 progress is position-based: the bar shows how far into the file's play time the
        // current candidate lies, not how many probes have run. Probe costs vary wildly (full
        // window decode vs. reused overlap vs. skipped candidate), so a fixed per-probe byte
        // budget drifts far off; position over total play time is honest about *where* the pass
        // is, at the price of nonlinear - and, during gap re-probes, briefly backwards - movement.
        work.BeginPhase("Pass 2", info.SizeBytes);

        // With --lang auto and a fresh DetectAsync run, the language is resolved once per file,
        // from the very first probe window's samples (always at start 0, decoded below like any
        // other window - no extra decode needed) - then fixed for the rest of the file via
        // ChangeLanguage, rather than re-detected per probe. A gap-scoped DetectGapsAsync run
        // instead already knows it (--verify resolved it), so `known` seeds it here and no probe
        // ever re-resolves it.
        LanguageProfile? profile = known?.Profile;
        if (profile != null)
            _transcriber.ChangeLanguage(profile.Language);
        string? detectedLanguage = known?.DetectedLanguage;
        var detectedProbability = known?.DetectedProbability ?? 0.0;

        // Confirmed markings are trusted verbatim; new finds from every region below are added to
        // the same list, so Pass 3's existing gap tail (after the region loop) sees one seamless
        // sequence regardless of which numbers came from --verify and which from fresh probing.
        var found = new List<DetectedChapter>(confirmedSeed);

        // --early-abort (0 disables it): once Pass 2 has probed this many minutes into the
        // file's play time without a single chapter found, further probing is pointless - abort
        // detection outright rather than transcribing the rest of a book that plainly is not
        // going to yield any (wrong --chapter-phrase, wrong --lang, or one that just announces
        // chapters differently). Only meaningful for a fresh, from-scratch run: confirmedSeed is
        // always non-empty for a --verify gap recovery or a ".missing-marks" resume, so this can
        // never fire for those - infinity below just disables the check outright for them.
        var earlyAbortSeconds = _options.EarlyAbortMinutes > 0 && confirmedSeed.Count == 0
            ? _options.EarlyAbortMinutes * 60
            : double.PositiveInfinity;
        var earlyAborted = false;

        // --expected-start-chapter's abort half: only meaningful for a fresh, from-scratch run,
        // same reasoning as earlyAbortSeconds above - a --verify gap recovery or a
        // ".missing-marks" resume always seeds at least one already-confirmed chapter, so the
        // "first chapter found" this guards can never be the very first of the whole file for
        // those. Null here disables the check outright for them, same as +infinity does above.
        var expectedStartChapter = confirmedSeed.Count == 0 ? _options.ExpectedStartChapter : null;
        int? belowExpectedStartNumber = null;

        var pass2Ctx = new Pass2Context(
            file, info, work, bytesPerSecond, jingleCeilingSeconds,
            allSilences, silences, nonSpeechRegions, speechSegments,
            earlyAbortSeconds, expectedStartChapter);

        foreach (var region in regions)
        {
            (profile, detectedLanguage, detectedProbability, earlyAborted, belowExpectedStartNumber) =
                await ProcessRegionAsync(pass2Ctx, region, found, profile, detectedLanguage, detectedProbability, ct);

            if (earlyAborted || belowExpectedStartNumber != null)
                break;
        }

        var chapters = Normalize(found);
        _log?.Invoke("Pass 2 finished");

        chapters = await RunPass3Async(file, info, work, chapters, allSilences, nonSpeechRegions,
            speechSegments, bytesPerSecond, profile!, trailingFallback, ct);

        return BuildDetectionResult(
            chapters, speechSegments, profile!, detectedLanguage, detectedProbability,
            earlyAborted, belowExpectedStartNumber);
    }

    /// <summary>
    /// Region-loop-invariant Pass 2 inputs, gathered here instead of threading each field through
    /// <see cref="ProcessRegionAsync"/>'s parameter list on its own.
    /// </summary>
    private readonly record struct Pass2Context(
        string File, MediaInfo Info, WorkTracker Work, double BytesPerSecond, double JingleCeilingSeconds,
        List<Silence> AllSilences, List<Silence> Silences, List<NonSpeechRegion> NonSpeechRegions,
        List<SpeechSegment> SpeechSegments, double EarlyAbortSeconds, int? ExpectedStartChapter);

    /// <summary>
    /// Runs Pass 2 candidate probing for a single detection region, appending every accepted
    /// chapter mark to <paramref name="found"/> in place. Every piece of per-region probe state -
    /// the probe window size and its adaptive resizing, the --min-silence-length auto threshold,
    /// the transcript-reuse cache, and the "last accepted number" - starts fresh on each call: a
    /// region is probed as if it were its own small file, not a continuation of whatever an
    /// earlier region happened to learn (see <see cref="DetectionRegion"/>'s remarks for why
    /// carrying it over would be wrong in both directions).
    /// </summary>
    /// <param name="ctx">Region-loop-invariant Pass 2 inputs.</param>
    /// <param name="region">The region to probe.</param>
    /// <param name="found">Accumulator of confirmed chapters across all regions; mutated in place
    /// as marks are accepted.</param>
    /// <param name="profile">The language profile resolved so far, or null if still unresolved -
    /// in which case this region's first full-window decode resolves it.</param>
    /// <param name="detectedLanguage">The language auto-detected so far, if any.</param>
    /// <param name="detectedProbability">Confidence of <paramref name="detectedLanguage"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The (possibly newly resolved) language profile/detection results, plus whether
    /// this region triggered an --early-abort or --expected-start-chapter abort.</returns>
    private async Task<(LanguageProfile? Profile, string? DetectedLanguage, double DetectedProbability,
        bool EarlyAborted, int? BelowExpectedStartNumber)> ProcessRegionAsync(
        Pass2Context ctx, DetectionRegion region, List<DetectedChapter> found,
        LanguageProfile? profile, string? detectedLanguage, double detectedProbability, CancellationToken ct)
    {
        var earlyAborted = false;
        int? belowExpectedStartNumber = null;

        // Every piece of state below - the probe window size and its adaptive resizing, the
        // --min-silence-length auto threshold, the transcript-reuse cache, and the "last
        // accepted number" - starts fresh for this region: it is probed as if it were its own
        // small file, not a continuation of whatever an earlier region happened to learn (see
        // DetectionRegion's remarks for why carrying it over would be wrong in both
        // directions). Declared here (rather than at ProcessRegionAsync's top) so ProbeAsync,
        // defined next, closes over this region's own instances.
        var probeSeconds = _options.MaxJingleSeconds > 0 ? ctx.JingleCeilingSeconds : ProbeSecondsPlain;
        // With --max-jingle-length auto, the adapted probe window: JingleObservationSafetyFactor
        // times the longest real inter-chapter jingle observed so far in this region, plus
        // PhraseMarginSeconds, capped at the ceiling. Null until the first qualifying
        // observation; monotonically increasing from then on (see JingleObservationSafetyFactor).
        double? adaptedWindowSeconds = null;
        // True while the sequence-gap recovery in the candidate loop below re-probes skipped
        // candidates at the full ceiling window: observations made during the re-probe still
        // feed adaptedWindowSeconds, but must not pull probeSeconds back down mid-re-probe -
        // the whole point of the reset is that every re-probe runs at the ceiling.
        var reprobing = false;
        // Set to region.LowerNumber when a chapter is already confirmed to precede this
        // region (an interior or trailing gap), or null for a from-file-start region (the
        // whole-file case, or a leading gap) - exactly DetectAsync's own original seeding,
        // generalized. Holds the previous value (not yet this window's) while a probe is in
        // flight, which is exactly what a gap re-probe needs to accept the in-between numbers.
        int? lastNumber = region.LowerNumber > 0 ? region.LowerNumber : null;

        // Candidates for this region only: the region's own start (mirroring the whole-file
        // case's start-of-file candidate), plus every silence/VAD non-speech region whose own
        // candidate position falls inside [FromSeconds, ToSeconds). A window can never decode
        // past ToSeconds regardless (see WindowEndFor's duration clamp below), so a region
        // boundary alone is enough containment - no extra check is needed here for that.
        var candidates = new List<(double Start, Silence? Silence, NonSpeechRegion? VadRegion)>
            { (region.FromSeconds, null, null) };
        candidates.AddRange(ctx.Silences
            .Where(s => s.EndSeconds >= region.FromSeconds && s.EndSeconds < region.ToSeconds - 1)
            .Select(s => ((double)s.EndSeconds, (Silence?)s, (NonSpeechRegion?)null)));
        if (_vad != null)
        {
            foreach (var vadRegion in ctx.NonSpeechRegions)
            {
                var jingleStart = JingleStart(vadRegion, ctx.Silences, ctx.SpeechSegments);
                if (jingleStart != vadRegion.StartSeconds)
                    continue;
                if (jingleStart < region.FromSeconds || jingleStart >= region.ToSeconds)
                    continue;
                var length = vadRegion.EndSeconds - jingleStart;
                if (length < MinJingleObservationSeconds || length > ctx.JingleCeilingSeconds)
                    continue;
                candidates.Add((jingleStart, null, vadRegion));
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
        // probes actually decoded. durationSeconds is region.ToSeconds, not the file's own
        // duration, so a window can never spill past this region's own boundary.
        double WindowEndFor(IReadOnlyList<(double Start, Silence? Silence, NonSpeechRegion? VadRegion)> list, int index)
            => PlanWindowEnd(list[index].Start,
                index + 1 < list.Count ? list[index + 1].Start : null,
                probeSeconds, region.ToSeconds, ctx.AllSilences, ctx.NonSpeechRegions, _vad != null);

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
        // of this region never counts as an overlap and always does a full transcribe.
        List<TranscriptSegment> cacheSegmentsAbs = [];
        var cacheFrom = region.FromSeconds;
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
            ctx.Work.SetPhaseProgress((long)(start * ctx.BytesPerSecond));

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
                        start, cacheTo, windowEnd, ctx.AllSilences, ctx.NonSpeechRegions, _vad != null,
                        allowBeyondBorder: false);
                    var samples = await _audio.DecodePcmAsync(ctx.File, splitPoint,
                        windowEnd - splitPoint, ctx.Info.InputDecoder, ct);
                    var fresh = await TranscribeCountingAsync(samples, ct);
                    var reused = cacheSegmentsAbs
                        .Where(s => s.StartSeconds >= start && s.StartSeconds < splitPoint).ToList();
                    windowSegmentsAbs = reused.Concat(ShiftSegments(fresh, splitPoint)).ToList();
                    mergeBoundarySegIndex = reused.Count;
                    cacheSegmentsAbs = windowSegmentsAbs;
                    cacheFrom = start;
                    cacheTo = windowEnd;
                    LogTranscript($"probe {windowEnd - splitPoint:0.0}s@{FormatTimestamp(splitPoint)} (tail)", fresh);
                }
            }
            else
            {
                // No usable overlap - transcribe the whole window. For a fresh DetectAsync run
                // this is also where --lang auto resolves the language, once, from the very
                // first probe's full samples; a gap-scoped run already has profile set from
                // `known`, so this never re-resolves it.
                var samples = await _audio.DecodePcmAsync(ctx.File, start,
                    windowEnd - start, ctx.Info.InputDecoder, ct);

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
                LogTranscript($"probe {windowEnd - start:0.0}s@{FormatTimestamp(start)}", fresh);
            }

            // Correct segment starts that Whisper timestamped from a leading silence/jingle
            // before shifting to window-relative time (the cache keeps the raw absolute timings
            // its reuse math relies on). FindPhraseMatches and the mark-placement math below
            // then work in window-relative time; the absolute trimmed transcript is kept for
            // ResolveJingleAnchor's narration-aware jingle edge adjustment.
            var trimmedAbs = TrimLeadingNonSpeech(
                windowSegmentsAbs, ctx.AllSilences, ctx.NonSpeechRegions, _vad != null);
            var segments = ShiftSegments(trimmedAbs, -start);

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
                // A snapped window can, near a gap region's own upper boundary, reach right up
                // against the next already-confirmed chapter's own announcement - reject a
                // match at or above it outright so gap recovery can never displace a chapter
                // --verify already trusts. Never set for the whole-file region (its
                // UpperNumber is always null), so this never fires for a fresh DetectAsync run.
                if (region.UpperNumber is { } upperBound && match.Number >= upperBound)
                    continue;

                var phraseAbs = start + match.PhraseStartSeconds;
                // The silence the mark is placed into (feeds the --min-silence-length auto
                // tightening; null when the mark sits on a VAD region or on nothing at all)
                // and, when the VAD pre-pass ran, the region it sits on (feeds
                // --max-jingle-length auto). Recorded for the auto mechanisms regardless of
                // --mark-before-jingle - only the final `time` below depends on that option.
                Silence? markSilence;
                NonSpeechRegion? markRegion = null;
                double time;
                if (_vad != null)
                {
                    // Anchor to the VAD jingle region ending at the phrase, not to whichever
                    // silence triggered this probe: a false in-text pause earlier in the
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
                        phraseAbs, start + match.PhraseEndSeconds, start, ctx.AllSilences,
                        ctx.NonSpeechRegions, candidateRegion, ctx.SpeechSegments, trimmedAbs);
                    if (markSilence == null && markRegion == null)
                        markSilence = candidate.Silence;
                    time = RefineDefaultMark(
                        Math.Max(0, ResolveDefaultPhraseOnset(phraseAbs, markRegion, ctx.SpeechSegments) - DefaultMarkLeadSeconds),
                        ctx.SpeechSegments);
                }
                else if (match.PhraseStartSeconds <= PhraseLatestStart)
                {
                    // The classic shape: the phrase directly follows the triggering silence.
                    // Without --mark-before-jingle the mark always goes DefaultMarkLeadSeconds
                    // before the phrase itself, regardless of what precedes it; markSilence is
                    // still recorded for the --min-silence-length auto tightening.
                    time = Math.Max(0, phraseAbs - DefaultMarkLeadSeconds);
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
                    var anchor = FindRealAnchorSilence(start, phraseAbs, ctx.AllSilences);
                    if (anchor is not { } a
                        || phraseAbs - a.EndSeconds > PhraseLatestStart
                        || a.EndSeconds - a.StartSeconds < _options.MinSilenceSeconds)
                        continue;
                    time = Math.Max(0, phraseAbs - DefaultMarkLeadSeconds);
                    markSilence = a;
                }

                if (_options.PreciseMark)
                    time = await _preciseMarkRefiner!.RefinePreciseMarkAsync(time, ctx.File, ctx.Info.InputDecoder, profile!, ctx.SpeechSegments, ct);
                if (_options.MarkBeforeJingle)
                    time = await ApplyMarkBeforeJingleAsync(time, ctx.AllSilences, ctx.SpeechSegments, ctx.File, ctx.Info.InputDecoder, ct);

                if (match.SpansMerge)
                    _log?.Invoke($"chapter {match.Number} detection spans the reused/fresh transcript " +
                                 "merge from Pass 2's overlap reuse - worth a spot check");

                found.Add(new DetectedChapter(match.Number, time, match.Confidence));
                marks.Add((match.Number, markSilence, match.Confidence));
                RecordChapterStats(match.Number, markSilence, markRegion, phraseAbs);
                windowLast = match.Number;
                var (highest, missingNumbers) = ChapterProgress(found, _options.ExpectedStartChapter);
                ctx.Work.HighestChapter = highest;
                ctx.Work.MissingChapters = missingNumbers.Count;
                _log?.Invoke($"chapter {match.Number} detected, mark placed at {FormatTimestamp(time)} " +
                             $"(confidence {match.Confidence:0.00}){LowConfidenceNote(match.Confidence)}" +
                             MissingNote(missingNumbers));

                if (_vad != null && _options.AutoMaxJingle && found.Count > 1)
                {
                    // found.Count > 1 means this is at least the second mark found overall
                    // (including any confirmedSeed entries), so its anchor is a real
                    // inter-chapter jingle - not the intro-to-chapter-1 gap, which can easily
                    // run longer (or shorter) than a book's regular jingles and would
                    // otherwise size the window off a one-off observation before any real
                    // jingle has even been seen. Same reasoning as the analogous
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
                        var proposed = Math.Min(ctx.JingleCeilingSeconds,
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
            ctx.Work.SetPhaseProgress((long)(candidate.Start * ctx.BytesPerSecond));

            if (candidate.Start >= ctx.EarlyAbortSeconds && found.Count == 0)
            {
                earlyAborted = true;
                _log?.Invoke($"early-abort: no chapter found within the first " +
                             $"{_options.EarlyAbortMinutes:0.#} minute(s) of play time " +
                             $"(stopped probing at {FormatTimestamp(candidate.Start)})");
                break;
            }

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
            if (candidate.VadRegion is { } candidateVadRegion &&
                candidateVadRegion.EndSeconds - candidate.Start > probeSeconds)
            {
                skippedSinceLastMark.Add(candidate);
                continue;
            }

            var foundNoneYet = found.Count == 0;
            var windowEnd = WindowEndFor(candidates, ci);
            var probeMarks = await ProbeAsync(candidate, windowEnd);

            // --expected-start-chapter: foundNoneYet means this probe is the one that found
            // the very first chapter of the whole (fresh) run, whether it added one match or
            // several - the one case the option cares about; a later, lower in-text mention
            // never reaches here at all, already rejected by the match.Number <= windowLast
            // check inside ProbeAsync. found[0] is that first chapter, whichever match
            // ProbeAsync accepted first.
            if (ctx.ExpectedStartChapter is { } expected && foundNoneYet && found.Count > 0 &&
                found[0].Number < expected)
            {
                belowExpectedStartNumber = found[0].Number;
                _log?.Invoke($"expected-start-chapter: first chapter found is {found[0].Number}, " +
                             $"below the expected start of {expected} - aborting detection for this file");
                found.Clear();
                break;
            }

            foreach (var (n, markSilence, _) in probeMarks)
            {
                // The gap re-probe runs regardless of --min-silence-length mode: with the
                // overlap-sequence skip below, candidates can be skipped even with an explicit
                // threshold, and a sequence gap is the signal that one of them hid a chapter.
                if (lastNumber is { } previousNumber && n > previousNumber + 1 && skippedSinceLastMark.Count > 0)
                {
                    _log?.Invoke($"sequence gap between chapter {previousNumber} and {n}, " +
                                 $"re-probing {skippedSinceLastMark.Count} skipped candidate(s) unconditionally");
                    if (_vad != null && _options.AutoMaxJingle && probeSeconds != ctx.JingleCeilingSeconds)
                    {
                        probeSeconds = ctx.JingleCeilingSeconds;
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
                    if (_vad != null && _options.AutoMaxJingle &&
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
                    _log?.Invoke($"{skipTo - ci} overlapping windows skipped");
                    for (var si = ci + 1; si <= skipTo; si++)
                        skippedSinceLastMark.Add(candidates[si]);
                    ci = skipTo;
                }
            }
        }

        return (profile, detectedLanguage, detectedProbability, earlyAborted, belowExpectedStartNumber);
    }

    /// <summary>
    /// Pass 3 (only when needed): resolves sequence gaps by fully transcribing the regions between
    /// mismatched markings (and before the first marking, if it is not chapter 1, or below
    /// --expected-start-chapter). This is the same, unmodified mechanism regardless of how <paramref
    /// name="chapters"/> was seeded - a gap-scoped <see cref="DetectGapsAsync"/> run's
    /// confirmed-plus-region-2 chapters are covered by it exactly like a fresh <see
    /// cref="DetectAsync"/> run's own chapters would be. Also runs the trailing-fallback recovery
    /// for a gap-scoped run whose last checkable --verify marking was unconfirmed - the one case
    /// the gap search itself cannot notice, since nothing bounds a still-missing trailing chapter
    /// from above to compare against.
    /// </summary>
    /// <param name="file">Path of the audio file.</param>
    /// <param name="info">Probe result of the file.</param>
    /// <param name="work">Progress tracker; begins its own "Pass 3" phase(s) as needed.</param>
    /// <param name="chapters">The chapters Pass 2 found, in chronological order.</param>
    /// <param name="allSilences">Every silence from <see cref="RunPass1Async"/>, used for gap-chunk
    /// seam snapping.</param>
    /// <param name="nonSpeechRegions">VAD non-speech regions from <see cref="RunPass1Async"/>.</param>
    /// <param name="speechSegments">VAD speech segments from <see cref="RunPass1Async"/>.</param>
    /// <param name="bytesPerSecond">The file's average byte rate, for progress reporting.</param>
    /// <param name="profile">The language profile resolved for this file.</param>
    /// <param name="trailingFallback">The trailing region's start and expected chapter numbers,
    /// when <see cref="BuildGapRegions"/> found the last checkable --verify marking unconfirmed;
    /// null otherwise (including for a fresh <see cref="DetectAsync"/> run).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><paramref name="chapters"/> plus anything Pass 3 recovered.</returns>
    private async Task<List<DetectedChapter>> RunPass3Async(
        string file, MediaInfo info, WorkTracker work, List<DetectedChapter> chapters,
        List<Silence> allSilences, List<NonSpeechRegion> nonSpeechRegions, List<SpeechSegment> speechSegments,
        double bytesPerSecond, LanguageProfile profile, (double From, List<int> Targets)? trailingFallback,
        CancellationToken ct)
    {
        var gaps = FindGaps(chapters, info.DurationSeconds, _options.ExpectedStartChapter);
        if (gaps.Count > 0)
        {
            work.BeginPhase("Pass 3",
                (long)(gaps.Sum(g => g.ToSeconds - g.FromSeconds) * bytesPerSecond));
            // A distinct --pass3-model needs its language set here; the pass-2 transcriber already
            // carries it, so the common (same-model) case leaves everything untouched.
            if (!ReferenceEquals(_pass3Transcriber, _transcriber))
                _pass3Transcriber.ChangeLanguage(profile.Language);
        }
        foreach (var gap in gaps)
        {
            _log?.Invoke($"transcribing suspicious region " +
                         $"{FormatTimestamp(gap.FromSeconds)} - {FormatTimestamp(gap.ToSeconds)}");
            // The chapter numbers this gap is expected to recover: everything strictly between
            // the numbers bounding it (or --expected-start-chapter, 1 when unset, up to the
            // first detected number, for a leading gap).
            var fills = await TranscribeRegionAsync(file, info, gap.FromSeconds, gap.ToSeconds,
                MissingNumbersInGap(chapters, gap, _options.ExpectedStartChapter),
                allSilences, nonSpeechRegions, speechSegments, bytesPerSecond, work, profile, chapters, ct);
            chapters = Normalize(chapters.Concat(fills).ToList());
            var (highest, missingNumbers) = ChapterProgress(chapters, _options.ExpectedStartChapter);
            work.HighestChapter = highest;
            work.MissingChapters = missingNumbers.Count;
        }
        if (gaps.Count > 0)
            _log?.Invoke("Pass 3 finished");

        // Trailing fallback: only for a gap-scoped DetectGapsAsync run whose last checkable
        // --verify marking was unconfirmed. FindGaps above has no way to notice a still-missing
        // trailing chapter (nothing bounds it from above to compare against), so this is the one
        // safety net that is not already covered by the untouched Pass 3 tail just above.
        if (trailingFallback is { } tf)
        {
            var stillMissing = tf.Targets.Where(n => !chapters.Any(c => c.Number == n)).ToList();
            if (stillMissing.Count > 0)
            {
                _log?.Invoke($"transcribing suspicious trailing region " +
                             $"{FormatTimestamp(tf.From)} - {FormatTimestamp(info.DurationSeconds)}");
                work.BeginPhase("Pass 3", (long)((info.DurationSeconds - tf.From) * bytesPerSecond));
                if (!ReferenceEquals(_pass3Transcriber, _transcriber))
                    _pass3Transcriber.ChangeLanguage(profile.Language);
                var fills = await TranscribeRegionAsync(file, info, tf.From, info.DurationSeconds,
                    stillMissing, allSilences, nonSpeechRegions, speechSegments, bytesPerSecond, work,
                    profile, chapters, ct);
                chapters = Normalize(chapters.Concat(fills).ToList());
                var (highest, missingNumbers) = ChapterProgress(chapters, _options.ExpectedStartChapter);
                work.HighestChapter = highest;
                work.MissingChapters = missingNumbers.Count;
                _log?.Invoke("Pass 3 finished (trailing)");
            }
        }
        return chapters;
    }

    /// <summary>
    /// Assembles the final <see cref="DetectionResult"/> once Pass 2 and Pass 3 are done: the
    /// remaining-gap consistency check, the low-confidence list, the lead-in speech check for
    /// <see cref="FileProcessor"/>'s intro-chapter insertion, and the per-file statistics.
    /// </summary>
    /// <param name="chapters">The final chapter list, after Pass 3.</param>
    /// <param name="speechSegments">The VAD speech segments from <see cref="RunPass1Async"/>
    /// (empty when the VAD pre-pass did not run).</param>
    /// <param name="profile">The language profile resolved for this file.</param>
    /// <param name="detectedLanguage">Whisper's raw language guess with --lang auto, or null.</param>
    /// <param name="detectedProbability">Whisper's probability for <paramref name="detectedLanguage"/>.</param>
    /// <param name="earlyAborted">True when --early-abort cut detection short.</param>
    /// <param name="belowExpectedStartNumber">The chapter number Pass 2 found first, when
    /// --expected-start-chapter aborted detection because it was numbered below that expectation.</param>
    private DetectionResult BuildDetectionResult(
        List<DetectedChapter> chapters, List<SpeechSegment> speechSegments, LanguageProfile profile,
        string? detectedLanguage, double detectedProbability, bool earlyAborted, int? belowExpectedStartNumber)
    {
        // Final consistency check: internal gaps that remain are fatal for this file, and so is a
        // leading gap Pass 3 above could not fully close - but only when --expected-start-chapter
        // actually named a number to hold it to; without that, there is nothing to be missing.
        var missing = new List<int>();
        if (_options.ExpectedStartChapter is { } expectedStart && chapters.Count > 0)
            for (var n = expectedStart; n < chapters[0].Number; n++)
                missing.Add(n);
        for (var i = 1; i < chapters.Count; i++)
            for (var n = chapters[i - 1].Number + 1; n < chapters[i].Number; n++)
                missing.Add(n);

        var lowConfidence = chapters
            .Where(c => c.Confidence < LowConfidenceThreshold)
            .Select(c => c.Number)
            .ToList();

        // Whether the very first chapter's mark is preceded by any VAD speech at all - lets
        // FileProcessor's intro-chapter insertion tell a real spoken prelude ("insert an Intro
        // entry") apart from just silence, music or a jingle before the phrase ("let chapter 1's
        // own mp4-muxer start-snap absorb the lead-in instead"). True by default: unknowable
        // without the VAD pre-pass, and irrelevant with no first chapter to check.
        var leadInHasSpeech = chapters.Count == 0 || _vad == null ||
            speechSegments.Any(s => s.StartSeconds < chapters[0].TimeSeconds);

        // Per-file statistics, aggregated over only the chapters that survived into the final
        // result (a detection that lost out to Normalize, or a spurious number, contributes
        // nothing). The silence/jingle dictionaries were filled at each mark placement. Each
        // extreme is computed twice: over all chapters, and over the "inter-chapter" subset that
        // excludes chapter 1 (whose intro transition is often atypical).
        double? MinSilence(IEnumerable<DetectedChapter> cs)
        {
            var vs = cs.Where(c => _chapterSilenceSeconds.ContainsKey(c.Number))
                .Select(c => _chapterSilenceSeconds[c.Number]).ToList();
            return vs.Count > 0 ? vs.Min() : null;
        }
        double? MaxJingle(IEnumerable<DetectedChapter> cs)
        {
            var vs = cs.Where(c => _chapterJingleSeconds.ContainsKey(c.Number))
                .Select(c => _chapterJingleSeconds[c.Number]).ToList();
            return vs.Count > 0 ? vs.Max() : null;
        }
        var interChapter = chapters.Where(c => c.Number != 1).ToList();
        var stats = new DetectionStats(
            MinSilence(chapters), MinSilence(interChapter),
            MaxJingle(chapters), MaxJingle(interChapter),
            _whisperAudioSeconds, _whisperTranscribeSeconds);

        return new DetectionResult(
            chapters, missing.Count > 0, missing, lowConfidence,
            profile, detectedLanguage, detectedProbability, stats, earlyAborted, belowExpectedStartNumber,
            leadInHasSpeech);
    }

    /// <summary>Result of <see cref="RunPass1Async"/>: every silence/VAD signal Pass 2 and Pass 3
    /// need, gathered in one full-file pass.</summary>
    /// <param name="AllSilences">Every silence down to <see cref="MinStoredSilenceSeconds"/>,
    /// regardless of --min-silence-length - used for seam snapping and mark anchoring.</param>
    /// <param name="Silences">The subset of <paramref name="AllSilences"/> at or above
    /// --min-silence-length - Pass 2's own candidate/logging silences.</param>
    /// <param name="NonSpeechRegions">Merged VAD non-speech regions (empty when the VAD pre-pass
    /// did not run) - see <see cref="ComputeNonSpeechRegions"/>.</param>
    /// <param name="SpeechSegments">The raw VAD speech segments behind <paramref
    /// name="NonSpeechRegions"/>, kept for the anchor-time jingle edge adjustment; empty when the
    /// VAD pre-pass did not run.</param>
    private readonly record struct Pass1Result(
        List<Silence> AllSilences, List<Silence> Silences,
        List<NonSpeechRegion> NonSpeechRegions, List<SpeechSegment> SpeechSegments);

    /// <summary>
    /// Pass 1: scans the whole file for silences and, when the VAD pre-pass is enabled (see
    /// <see cref="CliOptions.RunVadPrePass"/>), for VAD non-speech regions concurrently over the
    /// same decode - silencedetect alone never produces a Pass 2 candidate at a chapter transition
    /// where the jingle abuts speech on both sides with no amplitude gap; VAD sees that transition
    /// as a non-speech region (music, like silence, reads as non-speech to a speech detector)
    /// regardless of amplitude, so it can catch what silencedetect misses. See <see
    /// cref="JingleGeometry.ComputeMarkBeforeJingle"/> for how the two detectors' findings
    /// combine to place the mark with --mark-before-jingle.
    /// </summary>
    /// <param name="file">Path of the audio file.</param>
    /// <param name="info">Probe result of the file.</param>
    /// <param name="work">Progress tracker; begins the "Pass 1" phase itself.</param>
    /// <param name="bytesPerSecond">The file's average byte rate, for progress reporting.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<Pass1Result> RunPass1Async(
        string file, MediaInfo info, WorkTracker work, double bytesPerSecond, CancellationToken ct)
    {
        work.BeginPhase("Pass 1", info.SizeBytes);
        // The scan itself always goes down to MinStoredSilenceSeconds (or --min-silence-length
        // itself, if that is lower still) so short silences are available for overlap-border
        // snapping (see FindOverlapSplitPoint); allSilences holds every one of those, while
        // `silences` - used everywhere else below exactly as before this feature existed - keeps
        // only the ones at or above --min-silence-length.
        var storedSilenceFloor = Math.Min(_options.MinSilenceSeconds, MinStoredSilenceSeconds);
        List<Silence> allSilences;
        var nonSpeechRegions = new List<NonSpeechRegion>();
        // The raw VAD speech segments behind nonSpeechRegions, kept for the anchor-time jingle
        // edge adjustment (see AdjustJingleRegion): the merged regions alone no longer say where
        // the speech blips inside them lie. Empty when VAD is off.
        var speechSegments = new List<SpeechSegment>();
        if (_vad is { } vad)
        {
            allSilences = await _audio.DetectSilencesAndStreamPcmAsync(
                file, info.DurationSeconds, storedSilenceFloor, SilenceNoiseDb,
                async (pcm, innerCt) => speechSegments = await vad.DetectSpeechAsync(pcm, innerCt),
                seconds => work.SetPhaseProgress((long)(seconds * bytesPerSecond)), info.InputDecoder, ct);
            nonSpeechRegions = ComputeNonSpeechRegions(speechSegments);
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
        if (_vad != null)
            // speechSegments.Count and nonSpeechRegions.Count always differ by exactly one (a
            // non-speech region is the gap between two consecutive speech segments) before the
            // merge/filter cleanup below can drop some, and always differ by at most one afterwards - the
            // region count alone is the actionable number, so only it is logged.
            _log?.Invoke($"Pass 1: {nonSpeechRegions.Count} non-speech region(s) found");

        return new Pass1Result(allSilences, silences, nonSpeechRegions, speechSegments);
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
        SetLog(log);
        var (profile, detectedLanguage, detectedProbability) =
            await ResolveProfileFromMarkingsAsync(file, info, ct);
        _transcriber.ChangeLanguage(profile.Language);

        var checkedCount = 0;
        var failed = 0;
        // Mirrors Pass 2/3's found-chapters list, but of confirmed markings rather than fresh
        // detections, so the same ChapterProgress/bar display applies: the highest confirmed
        // number, with any lower unconfirmed one shown as a "(-N)" gap beneath it.
        var confirmedChapters = new List<DetectedChapter>();
        // Every marking's own outcome, in file order - the input BuildGapRegions groups into
        // DetectGapsAsync's recovery regions; a marking skipped below (empty window or no
        // parseable number) is still recorded, as null/false, so a run of unconfirmed markings on
        // either side of it is not wrongly treated as broken by an unrelated, unparseable one.
        var markings = new List<VerifyMarkingOutcome>();

        work.BeginPhase("Verify", info.ExistingChapters.Count);
        foreach (var marking in info.ExistingChapters)
        {
            ct.ThrowIfCancellationRequested();

            var windowStart = Math.Max(0, marking.StartSeconds - VerifyMarginBeforeSeconds);
            var windowLen = Math.Min(VerifyWindowSeconds, info.DurationSeconds - windowStart);
            if (windowLen <= 0)
            {
                markings.Add(new VerifyMarkingOutcome(marking.StartSeconds, null, false));
                work.Advance(1);
                continue;
            }

            if (!TryParseExpectedNumber(marking.Title, profile.Language, out var expected))
            {
                markings.Add(new VerifyMarkingOutcome(marking.StartSeconds, null, false));
                work.Advance(1);
                continue;
            }

            var samples = await _audio.DecodePcmAsync(file, windowStart, windowLen, info.InputDecoder, ct);
            var segments = await _transcriber.TranscribeAsync(samples, ct);
            LogTranscript($"verify @{FormatTimestamp(marking.StartSeconds)}", segments);

            checkedCount++;
            var confirmed = FindPhraseMatches(segments, profile).Any(m => m.Number == expected);
            if (!confirmed)
                confirmed = await TryConfirmViaGapRetranscribeAsync(
                    file, info, windowStart, windowLen, segments, profile, expected, ct);
            _log?.Invoke(confirmed
                ? $"chapter {expected} marking at {FormatTimestamp(marking.StartSeconds)} confirmed"
                : $"chapter {expected} marking at {FormatTimestamp(marking.StartSeconds)} NOT confirmed - phrase not found nearby");
            markings.Add(new VerifyMarkingOutcome(marking.StartSeconds, expected, confirmed));
            if (!confirmed)
                failed++;
            else
                confirmedChapters.Add(new DetectedChapter(expected, marking.StartSeconds));
            var (highest, missingNumbers) = ChapterProgress(confirmedChapters, _options.ExpectedStartChapter);
            work.HighestChapter = highest;
            work.MissingChapters = missingNumbers.Count;
            work.Advance(1);
        }

        return new VerifyResult(failed == 0, checkedCount, failed, confirmedChapters, markings,
            profile, detectedLanguage, detectedProbability);
    }

    /// <summary>
    /// Resolves the language profile to use for a file from its pre-existing chapter markings,
    /// shared by <see cref="VerifyExistingChaptersAsync"/> and <see cref="ResumeMissingMarksAsync"/>:
    /// with an explicit --lang, <see cref="CliOptions.DefaultProfile"/> is returned immediately with
    /// no decode at all; with --lang auto, the first marking with a decodable window (<see
    /// cref="VerifyMarginBeforeSeconds"/> before its own timestamp, <see cref="VerifyWindowSeconds"/>
    /// long) is used to resolve it via <see cref="ResolveLanguageAsync"/>. Does not itself call
    /// <see cref="ITranscriber.ChangeLanguage"/> - every caller needs that applied at a slightly
    /// different point, so it is left to them.
    /// </summary>
    /// <param name="file">Path of the audio file.</param>
    /// <param name="info">Probe result of the file, including its pre-existing chapter markings.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<(LanguageProfile Profile, string? DetectedLanguage, double DetectedProbability)>
        ResolveProfileFromMarkingsAsync(string file, MediaInfo info, CancellationToken ct)
    {
        if (!_options.AutoLanguage)
            return (_options.DefaultProfile, null, 0);
        foreach (var marking in info.ExistingChapters)
        {
            var windowStart = Math.Max(0, marking.StartSeconds - VerifyMarginBeforeSeconds);
            var windowLen = Math.Min(VerifyWindowSeconds, info.DurationSeconds - windowStart);
            if (windowLen <= 0)
                continue;
            var samples = await _audio.DecodePcmAsync(file, windowStart, windowLen, info.InputDecoder, ct);
            return await ResolveLanguageAsync(samples, ct);
        }
        return (_options.DefaultProfile, null, 0);
    }

    /// <summary>
    /// Second-chance confirmation for a --verify window whose first-pass transcript missed the
    /// expected phrase: every gap of at least <see cref="GapRetryThresholdSeconds"/> between
    /// transcribed segments (including before the first and after the last one) is padded by
    /// <see cref="GapRetryPaddingSeconds"/> on each side and re-scanned in short, overlapping
    /// <see cref="GapRetryChunkSeconds"/> sub-chunks, each independently re-decoded and
    /// re-transcribed and checked for the phrase - stopping at the first chunk that confirms it.
    /// Scanning in small chunks rather than re-transcribing the whole padded gap in one call
    /// matters: a single call spanning a long, mostly non-speech stretch (silence or a jingle
    /// around a short phrase) risks the very same failure it exists to recover from, since
    /// Whisper can judge that whole call's audio as non-speech on average and return only a
    /// token leading segment - as observed in practice - even though the same audio, decoded on
    /// its own at a scale close to a single phrase, transcribes it correctly, exactly as
    /// detection's own original run over this same audio already did.
    /// </summary>
    /// <param name="file">Path of the audio file.</param>
    /// <param name="info">Probe result of the file, for its duration and input decoder.</param>
    /// <param name="windowStart">Absolute start of the --verify window already transcribed.</param>
    /// <param name="windowLen">Length of that window in seconds.</param>
    /// <param name="segments">That window's first-pass transcript segments, window-relative.</param>
    /// <param name="profile">Language profile for phrase/number matching.</param>
    /// <param name="expected">The chapter number this marking is expected to confirm.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<bool> TryConfirmViaGapRetranscribeAsync(
        string file, MediaInfo info, double windowStart, double windowLen,
        List<TranscriptSegment> segments, LanguageProfile profile, int expected, CancellationToken ct)
    {
        var boundaries = new List<double> { 0 };
        foreach (var s in segments.OrderBy(s => s.StartSeconds))
        {
            boundaries.Add(s.StartSeconds);
            boundaries.Add(s.EndSeconds);
        }
        boundaries.Add(windowLen);

        // Consecutive pairs at even indices are the gaps between segments (odd indices are the
        // segments themselves): [0, seg0.Start], [seg0.End, seg1.Start], ..., [segN.End, windowLen].
        for (var i = 0; i + 1 < boundaries.Count; i += 2)
        {
            var gapStart = boundaries[i];
            var gapEnd = boundaries[i + 1];
            if (gapEnd - gapStart < GapRetryThresholdSeconds)
                continue;

            var sliceStart = Math.Max(0, gapStart - GapRetryPaddingSeconds);
            var sliceEnd = Math.Min(windowLen, gapEnd + GapRetryPaddingSeconds);

            var chunkStep = GapRetryChunkSeconds - GapRetryChunkOverlapSeconds;
            for (var chunkStart = sliceStart; chunkStart < sliceEnd; chunkStart += chunkStep)
            {
                var absStart = windowStart + chunkStart;
                var len = Math.Min(
                    Math.Min(GapRetryChunkSeconds, sliceEnd - chunkStart), info.DurationSeconds - absStart);
                if (len <= 0)
                    continue;

                var gapSamples = await _audio.DecodePcmAsync(file, absStart, len, info.InputDecoder, ct);
                var gapSegments = await _transcriber.TranscribeAsync(gapSamples, ct);
                LogTranscript($"verify gap retry {len:0.0}s@{FormatTimestamp(absStart)}", gapSegments);
                if (FindPhraseMatches(gapSegments, profile).Any(m => m.Number == expected))
                    return true;
            }
        }
        return false;
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

    /// <summary>
    /// Applies --mark-before-jingle on top of a mark default-mode placement (optionally already
    /// corrected by --precise-mark) already computed: <see
    /// cref="JingleGeometry.ComputeMarkBeforeJingle"/> walks it backward to the jingle's true
    /// leading edge (or leaves it unchanged when VAD finds no jingle there at all), then the same
    /// backward-only quiet-point snap --precise-mark's own final step applies runs on the result,
    /// so a player seeking to a --mark-before-jingle mark starts in near-silence just as it would
    /// for any other mark.
    /// </summary>
    /// <param name="mark">The mark to walk backward from.</param>
    /// <param name="allSilences">Every silence Pass 1 stored, for the backward walk.</param>
    /// <param name="speechSegments">Raw VAD speech segments for the whole file, for the backward
    /// walk.</param>
    /// <param name="file">Path of the audio file, for the final quiet-point snap's own decode.</param>
    /// <param name="inputDecoder">Explicit input decoder to force, or null.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<double> ApplyMarkBeforeJingleAsync(
        double mark, List<Silence> allSilences, List<SpeechSegment> speechSegments,
        string file, string? inputDecoder, CancellationToken ct)
    {
        var walked = ComputeMarkBeforeJingle(mark, allSilences, speechSegments);
        var quietest = await _preciseMarkRefiner!.SnapToQuietestPointAsync(walked, file, inputDecoder, ct);
        if (walked != mark)
            _log?.Invoke($"--mark-before-jingle: walked mark back from {FormatTimestamp(mark)} to {FormatTimestamp(walked)}");
        if (quietest != walked)
            _log?.Invoke($"--mark-before-jingle: nudged {FormatTimestamp(walked)} to quieter {FormatTimestamp(quietest)}");
        return quietest;
    }

    /// <summary>
    /// Records, for one detected chapter, the length of the silence and (when the VAD pre-pass
    /// ran) the jingle that preceded its phrase - the raw material for the per-file
    /// minimum-silence and maximum-jingle statistics. This runs regardless of
    /// --mark-before-jingle, since the auto mechanisms and statistics stay meaningful even when
    /// marks are placed at the default fixed offset. Keyed by chapter number, so a later
    /// re-detection of the same chapter simply overwrites; the per-file aggregate is then taken
    /// over only the chapters that survive into the final result. The silence recorded is the
    /// one the mark anchored to: without a VAD pre-pass, the silence directly before the phrase;
    /// with one, the silence leading the jingle (a jingle framed between two silences
    /// contributes only its <em>leading</em> one, per <see cref="ResolveJingleAnchor"/>), or none
    /// for a silence-less jingle. The jingle length is measured from its true start (the leading
    /// silence's end, else the region start) up to the phrase, clipped at the region end so an
    /// announcement spoken inside the jingle - or a merge-inflated region end - never overstates
    /// it, matching the --max-jingle-length auto observation.
    /// </summary>
    /// <param name="number">The detected chapter number.</param>
    /// <param name="precedingSilence">The silence the mark anchored to, or null when none.</param>
    /// <param name="jingleRegion">The jingle region preceding the phrase, or null (always null
    /// when the VAD pre-pass did not run).</param>
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
    /// <param name="transcriber">Recognizer to use; defaults to the pass-2 transcriber. Pass 3
    /// passes <see cref="_pass3Transcriber"/> so a distinct <c>--pass3-model</c> can do the gap
    /// work while the audio and time still count toward the same statistics.</param>
    private async Task<List<TranscriptSegment>> TranscribeCountingAsync(
        float[] samples, CancellationToken ct, ITranscriber? transcriber = null)
    {
        _whisperAudioSeconds += samples.Length / (double)FfmpegClient.SampleRate;
        var watch = Stopwatch.StartNew();
        var segments = await (transcriber ?? _transcriber).TranscribeAsync(samples, ct);
        _whisperTranscribeSeconds += watch.Elapsed.TotalSeconds;
        return segments;
    }

    /// <summary>
    /// Fully transcribes a region of the file and returns all chapter starts found in it. Used
    /// to close sequence gaps left by the silence-probe fast path (Pass 3). Every chunk border
    /// is snapped to the nearest silence (or, when the VAD pre-pass ran, VAD non-speech region) mid-point
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
    /// <param name="speechSegments">The raw VAD speech segments behind
    /// <paramref name="nonSpeechRegions"/> (empty when VAD is off), for the jingle edge
    /// adjustment inside <see cref="ResolveJingleAnchor"/>.</param>
    /// <param name="knownChapters">Chapters already detected outside this region, so the
    /// per-mark progress numbers and still-missing log notes reflect the whole file rather
    /// than just this region's finds.</param>
    private async Task<List<DetectedChapter>> TranscribeRegionAsync(
        string file, MediaInfo info, double fromSeconds, double toSeconds,
        IReadOnlyList<int> expectedNumbers,
        List<Silence> allSilences, List<NonSpeechRegion> nonSpeechRegions,
        List<SpeechSegment> speechSegments, double bytesPerSecond,
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
                    allSilences, nonSpeechRegions, _vad != null)
                : null;
            var chunkEnd = seam ?? naturalEnd;

            var samples = await _audio.DecodePcmAsync(file, chunkStart, chunkEnd - chunkStart, info.InputDecoder, ct);
            var segments = await TranscribeCountingAsync(samples, ct, _pass3Transcriber);
            LogTranscript($"transcribed gap chunk @{FormatTimestamp(chunkStart)}", segments);
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
            matchSegments = TrimLeadingNonSpeech(matchSegments, allSilences, nonSpeechRegions, _vad != null);

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
                await RecordGapChapterMatch(match, matchSegments, found, remaining, knownChapters,
                    allSilences, nonSpeechRegions, speechSegments, work, file, info.InputDecoder, profile, ct);
            }

            // A chunk whose normal transcript still leaves some expected number(s) unaccounted
            // for gets one more look: long inner gaps that line up with a real silence/jingle
            // (not just an ordinary narration pause) are re-scanned in small chunks, the same
            // fallback --verify uses for the same underlying Whisper failure mode.
            if (remaining.Count > 0)
                await ScanGapRetriesAsync(file, info, chunkStart, chunkEnd, freshAbs, profile,
                    found, remaining, knownChapters, allSilences, nonSpeechRegions, speechSegments, work, ct);

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
    /// Records one phrase match found while scanning a Pass 3 gap chunk (its normal transcript,
    /// or <see cref="ScanGapRetriesAsync"/>'s fallback) as a detected chapter: resolves where the
    /// mark itself goes (a fixed offset before the phrase by default, further walked back to a
    /// jingle's own start with --mark-before-jingle), records the chapter's per-file statistics, updates
    /// <paramref name="found"/>/<paramref name="remaining"/> and the progress bar's chapter
    /// state, and logs it. Shared between both callers so the mark-placement logic - the same
    /// rules <see cref="TranscribeRegionAsync"/>'s doc comment describes - stays in exactly one
    /// place.
    /// </summary>
    /// <param name="match">The confirmed phrase match, in absolute file time.</param>
    /// <param name="matchSegments">The transcript the match was found in (absolute file time),
    /// for the VAD edge adjustment inside <see cref="ResolveJingleAnchor"/>.</param>
    /// <param name="found">Chapters found in this gap so far; appended to.</param>
    /// <param name="remaining">Still-missing chapter numbers for this gap; the match's number is
    /// removed from it.</param>
    /// <param name="knownChapters">Chapters already detected outside this gap, so the progress
    /// bar's chapter state reflects the whole file.</param>
    /// <param name="allSilences">Every silence Pass 1 stored, for anchor resolution.</param>
    /// <param name="nonSpeechRegions">VAD non-speech regions (empty when the VAD pre-pass did
    /// not run), for the jingle anchor resolution.</param>
    /// <param name="speechSegments">Raw VAD speech segments, for the jingle edge adjustment and,
    /// with --precise-mark, as its candidate positions.</param>
    /// <param name="work">The file's progress tracker.</param>
    /// <param name="file">Path of the audio file, for --precise-mark's own extra transcriptions.</param>
    /// <param name="inputDecoder">Explicit input decoder to force, or null.</param>
    /// <param name="profile">Language profile supplying the phrase --precise-mark looks for.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task RecordGapChapterMatch(
        PhraseMatch match, List<TranscriptSegment> matchSegments,
        List<DetectedChapter> found, HashSet<int> remaining, IReadOnlyList<DetectedChapter> knownChapters,
        List<Silence> allSilences, List<NonSpeechRegion> nonSpeechRegions, List<SpeechSegment> speechSegments,
        WorkTracker work, string file, string? inputDecoder, LanguageProfile profile, CancellationToken ct)
    {
        var phraseAbs = match.PhraseStartSeconds;
        double time;
        // The silence/jingle the mark anchored to, hoisted out of the branches below so the
        // per-file statistics can be recorded once, uniformly (see RecordChapterStats).
        Silence? statSilence = null;
        NonSpeechRegion? statRegion = null;
        if (_vad != null)
        {
            // Same VAD-region-primary anchor resolution as Pass 2 (ResolveJingleAnchor), just
            // against a fixed lookback since a gap chunk has no meaningful probe window start of
            // its own - used here for default-mode placement and the auto-mechanism statistics;
            // --mark-before-jingle's own correction (below) does not consume it, resolving
            // instead from whatever default-mode mark this computes.
            var lookback = _options.MaxJingleSeconds + PhraseMarginSeconds;
            var (anchorSilence, vadRegion) = ResolveJingleAnchor(
                phraseAbs, match.PhraseEndSeconds, phraseAbs - lookback, allSilences,
                nonSpeechRegions, candidateVadRegion: null, speechSegments, matchSegments);
            time = RefineDefaultMark(
                Math.Max(0, ResolveDefaultPhraseOnset(phraseAbs, vadRegion, speechSegments) - DefaultMarkLeadSeconds),
                speechSegments);
            (statSilence, statRegion) = (anchorSilence, vadRegion);
        }
        else
        {
            // Without a VAD pre-pass, the mark always goes DefaultMarkLeadSeconds before the
            // phrase itself; the preceding silence (if any close enough) is still located
            // purely to feed the --min-silence-length auto tightening via RecordChapterStats.
            var anchor = FindRealAnchorSilence(phraseAbs - PhraseLatestStart, phraseAbs, allSilences);
            time = Math.Max(0, phraseAbs - DefaultMarkLeadSeconds);
            statSilence = anchor;
        }
        if (_options.PreciseMark)
            time = await _preciseMarkRefiner!.RefinePreciseMarkAsync(time, file, inputDecoder, profile, speechSegments, ct);
        if (_options.MarkBeforeJingle)
            time = await ApplyMarkBeforeJingleAsync(time, allSilences, speechSegments, file, inputDecoder, ct);
        found.Add(new DetectedChapter(match.Number, time, match.Confidence));
        RecordChapterStats(match.Number, statSilence, statRegion, phraseAbs);
        remaining.Remove(match.Number);
        var (highest, missingNumbers) = ChapterProgress(knownChapters.Concat(found), _options.ExpectedStartChapter);
        work.HighestChapter = highest;
        work.MissingChapters = missingNumbers.Count;
        _log?.Invoke($"chapter {match.Number} found in gap, mark placed at {FormatTimestamp(time)} " +
                     $"(confidence {match.Confidence:0.00}){LowConfidenceNote(match.Confidence)}" +
                     MissingNote(missingNumbers));
    }

    /// <summary>
    /// Second-chance scan for a Pass 3 gap chunk that, after its normal transcript, still has
    /// missing chapter numbers (<paramref name="remaining"/>): every stored silence, or, when
    /// the VAD pre-pass ran, also every VAD non-speech region, at least <see cref="GapRetryThresholdSeconds"/>
    /// long and entirely inside this chunk that <em>none</em> of the chunk's own fresh segments
    /// (not the bridged tail carried in from the previous chunk, already covered by its own
    /// pass) actually covers - i.e. Whisper produced no speech at all over that stretch - is
    /// padded by <see cref="GapRetryPaddingSeconds"/> on each side and re-scanned in short,
    /// overlapping <see cref="GapRetryChunkSeconds"/> sub-chunks, the same technique --verify
    /// uses to recover a phrase Whisper silently dropped from a single call spanning a mostly
    /// non-speech stretch. Scoped to the silence/region's own bounds rather than the whole raw
    /// stretch between the two segments bracketing it: that stretch can span most of a 600 s
    /// Pass 3 chunk when narration is sparse, and re-scanning all of it in small sub-chunks would
    /// make an already-expensive fallback far more expensive still, whereas a genuine jingle or
    /// scene-transition silence is normally just seconds to at most tens of seconds long.
    /// Confirmed matches are recorded via <see cref="RecordGapChapterMatch"/> exactly like the
    /// chunk's normal ones; scanning stops as soon as nothing is left to find.
    /// </summary>
    /// <param name="file">Path of the audio file.</param>
    /// <param name="info">Probe result of the file, for its duration and input decoder.</param>
    /// <param name="chunkStart">Absolute start of the Pass 3 chunk just transcribed.</param>
    /// <param name="chunkEnd">Absolute end of that chunk.</param>
    /// <param name="freshAbs">That chunk's own transcript segments (absolute file time),
    /// excluding any bridged tail from the previous chunk.</param>
    /// <param name="profile">Language profile for phrase/number matching.</param>
    /// <param name="found">Chapters found in this gap so far; appended to via <see
    /// cref="RecordGapChapterMatch"/>.</param>
    /// <param name="remaining">Still-missing chapter numbers for this gap.</param>
    /// <param name="knownChapters">Chapters already detected outside this gap.</param>
    /// <param name="allSilences">Every silence Pass 1 stored - both for anchor resolution and as
    /// retry candidates.</param>
    /// <param name="nonSpeechRegions">VAD non-speech regions (empty when the VAD pre-pass did not run).</param>
    /// <param name="speechSegments">Raw VAD speech segments, for the jingle edge adjustment.</param>
    /// <param name="work">The file's progress tracker.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task ScanGapRetriesAsync(
        string file, MediaInfo info, double chunkStart, double chunkEnd,
        List<TranscriptSegment> freshAbs, LanguageProfile profile,
        List<DetectedChapter> found, HashSet<int> remaining, IReadOnlyList<DetectedChapter> knownChapters,
        List<Silence> allSilences, List<NonSpeechRegion> nonSpeechRegions, List<SpeechSegment> speechSegments,
        WorkTracker work, CancellationToken ct)
    {
        IEnumerable<(double Start, double End)> candidates = allSilences
            .Where(s => s.EndSeconds - s.StartSeconds >= GapRetryThresholdSeconds &&
                        s.StartSeconds >= chunkStart && s.EndSeconds <= chunkEnd)
            .Select(s => (s.StartSeconds, s.EndSeconds));
        if (_vad != null)
            candidates = candidates.Concat(nonSpeechRegions
                .Where(r => r.EndSeconds - r.StartSeconds >= GapRetryThresholdSeconds &&
                            r.StartSeconds >= chunkStart && r.EndSeconds <= chunkEnd)
                .Select(r => (r.StartSeconds, r.EndSeconds)));

        foreach (var (silStart, silEnd) in candidates.OrderBy(c => c.Start))
        {
            if (remaining.Count == 0)
                break;
            // An ordinary sentence that merely straddles a real pause still has its own segment
            // covering the pause and needs no second look - only a stretch with nothing
            // transcribed over it at all is a candidate for having been dropped outright.
            if (freshAbs.Any(s => s.StartSeconds < silEnd && s.EndSeconds > silStart))
                continue;

            var sliceStart = Math.Max(chunkStart, silStart - GapRetryPaddingSeconds);
            var sliceEnd = Math.Min(chunkEnd, silEnd + GapRetryPaddingSeconds);
            var subStep = GapRetryChunkSeconds - GapRetryChunkOverlapSeconds;
            for (var subStart = sliceStart; subStart < sliceEnd && remaining.Count > 0; subStart += subStep)
            {
                var len = Math.Min(
                    Math.Min(GapRetryChunkSeconds, sliceEnd - subStart), info.DurationSeconds - subStart);
                if (len <= 0)
                    continue;

                var subSamples = await _audio.DecodePcmAsync(file, subStart, len, info.InputDecoder, ct);
                var subSegments = await TranscribeCountingAsync(subSamples, ct, _pass3Transcriber);
                LogTranscript($"gap retry {len:0.0}s@{FormatTimestamp(subStart)}", subSegments);
                var subAbs = TrimLeadingNonSpeech(
                    ShiftSegments(subSegments, subStart), allSilences, nonSpeechRegions, _vad != null);

                foreach (var match in FindPhraseMatches(subAbs, profile))
                {
                    if (!remaining.Contains(match.Number) || knownChapters.Any(k => k.Number == match.Number))
                        continue;
                    await RecordGapChapterMatch(match, subAbs, found, remaining, knownChapters,
                        allSilences, nonSpeechRegions, speechSegments, work, file, info.InputDecoder, profile, ct);
                    if (remaining.Count == 0)
                        break;
                }
            }
        }
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

}
