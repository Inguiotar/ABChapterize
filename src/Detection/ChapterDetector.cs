// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Audio;
using ABChapterize.Cli;
using ABChapterize.Language;
using ABChapterize.Processing;
using ABChapterize.Transcription;
using ABChapterize.Ui;
using ABChapterize.Vad;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Text;
using static ABChapterize.Detection.DetectionFormatting;
using static ABChapterize.Detection.DetectionTuning;
using static ABChapterize.Detection.GapPlanning;
using static ABChapterize.Detection.JingleGeometry;
using static ABChapterize.Detection.PhraseMatching;
using static ABChapterize.Detection.TranscriptTime;

namespace ABChapterize.Detection;

/// <summary>
/// Finds chapter starts in an audiobook. Fast path: detect longer-than-usual silences and
/// probe the audio following each silence with Whisper. If the resulting chapter numbers
/// contain sequence gaps, the audio between the mismatched markings is fully transcribed.
/// </summary>
public sealed class ChapterDetector
{
    private readonly CliOptions _options;
    private readonly IAudioSource _audio;
    private readonly ITranscriber _transcriber;

    /// <summary>Transcriber used for pass 3 (gap filling). The same instance as
    /// <see cref="_transcriber"/> unless <c>--pass3-model</c> selected a different model, in which
    /// case it is a <see cref="Pass3TranscriberProxy"/> onto the shared pass-3 model. Only which
    /// model recognizes the gap chunks changes; detection/marking/statistics are identical.</summary>
    private readonly ITranscriber _pass3Transcriber;

    private readonly IVoiceActivityDetector? _vad;

    /// <summary>Places every mark this detector's passes decide on, and holds the per-chapter
    /// silence/jingle measurements behind <see cref="DetectionStats"/>. Rebuilt per file once
    /// <see cref="_log"/> is known (see <see cref="SetLog"/>), since its constructor closes over
    /// this detector's own <see cref="TranscribeCountingAsync"/> so the corrections' transcriptions
    /// count toward the same per-file statistics - which also resets those measurements.</summary>
    private MarkPlacer? _marks;

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

    /// <summary>Whether the current file hit <see cref="DetectionTuning.MaxCustomMarksPerFile"/>.
    /// A field rather than a return value because every <see cref="RegionProber"/> of the file can
    /// set it, across passes 2 and 2.5 alike, and the answer belongs to the file rather than to any
    /// one of them. Reset per file, alongside the Whisper counters above.</summary>
    private bool _customLimitHit;

    /// <summary>Creates a detector bound to the given tools and options.</summary>
    /// <param name="options">Validated command line options.</param>
    /// <param name="audio">Audio source used for silence detection and PCM decoding.</param>
    /// <param name="transcriber">Loaded speech recognizer.</param>
    /// <param name="vad">Voice activity detector used for the full-file VAD pre-pass (finds
    /// jingle transitions with no detectable amplitude gap); null when
    /// <see cref="CliOptions.RunVadPrePass"/> is false, or in tests that don't exercise that
    /// path.</param>
    /// <param name="pass3Transcriber">Transcriber for pass 3 (gap filling) when
    /// <c>--pass3-model</c> asks for a model other than the main one; null (the default) makes
    /// pass 3 reuse <paramref name="transcriber"/>.</param>
    public ChapterDetector(CliOptions options, IAudioSource audio, ITranscriber transcriber,
        IVoiceActivityDetector? vad = null, ITranscriber? pass3Transcriber = null)
    {
        _options = options;
        _audio = audio;
        _transcriber = transcriber;
        _pass3Transcriber = pass3Transcriber ?? transcriber;
        _vad = vad;
    }

    /// <summary>Sets the per-file --verbose log sink and rebuilds <see cref="_marks"/> around it, so
    /// its mark-placement log lines land in the same sink as the rest of this file's detection log
    /// and its per-chapter measurements start empty for the new file.</summary>
    /// <param name="log">Sink for --verbose log messages, or null when not verbose.</param>
    private void SetLog(Action<string>? log)
    {
        _log = log;
        _marks = new MarkPlacer(
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
        => DetectCoreAsync(file, info, work, log, [], [],
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
        return DetectCoreAsync(file, info, work, log, verify.ConfirmedChapters,
            verify.NamedMarks ?? [], plan.Regions,
            (verify.Profile, verify.DetectedLanguage, verify.DetectedProbability),
            plan.TrailingFrom is { } from ? (from, plan.TrailingTargets) : null, ct);
    }

    /// <summary>
    /// Auto-resumes a file <see cref="FileProcessor.MissingMarksPath"/> tagged after a previous run
    /// left a chapter-sequence gap unresolved. The committed markings are trusted verbatim, with no
    /// --verify-style re-check against the audio: unlike <see cref="DetectGapsAsync"/>'s confirmed
    /// markings these were never in doubt in the first place - they are exactly what pass 3 settled
    /// on last time. Only the gap(s) <see cref="FindGaps"/> still finds between them get their own
    /// gap-scoped Pass 2 plus the existing Pass 3 tail, exactly as <see cref="DetectGapsAsync"/>
    /// does after a --verify failure - which is what lets this reuse <see cref="DetectCoreAsync"/>
    /// directly instead of a bespoke pipeline. A trailing region can never need recovering here: a
    /// tag only ever names chapters <see cref="FindGaps"/> itself flagged, which always means a gap
    /// bounded by two confirmed chapters (or the file start), so the one case it structurally cannot
    /// flag - a still-missing trailing chapter - never produces a tag to resume in the first place.
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

        // Committed markings are trusted directly, never re-probed; only their chapter number
        // matters (parsed as --verify parses a marking's expected number). A marking with no
        // parseable number - the intro/prelude entry BuildChapters inserts - carries no chapter
        // identity and is dropped, exactly like an unparseable --verify marking.
        var confirmed = new List<DetectedChapter>();
        foreach (var marking in info.ExistingChapters)
            if (TryParseExpectedNumber(marking.Title, profile.Language, out var number))
                confirmed.Add(new DetectedChapter(number, marking.StartSeconds));
        confirmed = Normalize(confirmed);
        var namedSeed = CarryOverNamedMarkings(info, profile);

        // Gaps are re-derived from the committed markings rather than the tag's own number list, so
        // this always agrees with what FindGaps/MissingNumbersInGap would say about the file's
        // actual content right now. ExpectedStartChapter is passed through so a leading
        // missing-marks tag resolves to the same gap as the run that produced it.
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

        return await DetectCoreAsync(file, info, work, log, confirmed, namedSeed, regions,
            (profile, detectedLanguage, detectedProbability), null, ct);
    }

    /// <summary>
    /// Recovers the non-numbered marks from a file's existing markings by matching their titles
    /// against the ones this run would write. Both resume paths need it for the same reason: they
    /// rewrite the file's whole marking set from what detection hands back, and a named mark carries
    /// no chapter number - so unlike a chapter it would leave no hole behind, and nothing would ever
    /// notice it had been dropped. Matching on the title is what there is to match on: the marking's
    /// text is all a written chapter entry preserves, and this run's own titles are exactly what a
    /// previous run of the same command wrote there. A file marked by a different tool (or under
    /// different titles) simply yields nothing here, and its prologue is re-detected or lost exactly
    /// as before this existed.
    /// <para>
    /// A numbered marking and the intro entry are ruled out before any title is matched at all,
    /// because a --custom title made entirely of a capturing-group reference matches every string
    /// there is (see <see cref="NamedPhrase.TitleMatcher"/>) - without the exclusion, such a mapping
    /// would swallow the file's chapters into the named list and lose their numbers.
    /// </para>
    /// </summary>
    /// <param name="info">Probe result of the file, including its pre-existing chapter markings.</param>
    /// <param name="profile">The language profile resolved for this file, supplying the titles.</param>
    private static List<DetectedMark> CarryOverNamedMarkings(MediaInfo info, LanguageProfile profile)
    {
        var carried = new List<DetectedMark>();
        foreach (var marking in info.ExistingChapters)
        {
            var title = marking.Title.Trim();
            if (TryParseExpectedNumber(title, profile.Language, out _) ||
                string.Equals(title, profile.IntroTitle, StringComparison.OrdinalIgnoreCase))
                continue;
            if (profile.NamedPhrases.FirstOrDefault(p => p.TitleMatcher.IsMatch(title)) is { } phrase)
                carried.Add(new DetectedMark(
                    phrase.Kind, title, marking.StartSeconds,
                    PhraseTimeSeconds: marking.StartSeconds, Repeatable: phrase.Repeatable));
        }
        return carried;
    }

    /// <summary>
    /// The shared detection pipeline behind <see cref="DetectAsync"/> and <see
    /// cref="DetectGapsAsync"/>. Pass 1 always runs whole-file, even for a gap-scoped call: <see
    /// cref="IAudioSource"/> has no ranged silence/VAD scan, and redoing this one full-file decode
    /// is cheap next to the Whisper probing that follows. Pass 2 then runs once per entry in
    /// <paramref name="regions"/>, each with its own candidates (built only from silences/VAD
    /// regions starting inside that region) and its own adaptive-threshold/adaptive-jingle-window
    /// state starting completely fresh - a region is probed as if it were its own small file, not a
    /// continuation of whatever an earlier region's Pass 2 happened to learn. The sequence-gap
    /// Pass 3 tail (over the accumulated <c>chapters</c> and the file's full duration) is the final
    /// net for any interior gap regardless of how <c>chapters</c> was seeded;
    /// <paramref name="trailingFallback"/> and --trailing-scan exist only for the one case that
    /// tail structurally cannot catch - a still-missing chapter after the last one found, which
    /// nothing bounds from above to even notice.
    /// </summary>
    /// <param name="file">Path of the audio file.</param>
    /// <param name="info">The file's probed media info (duration, size, decoder).</param>
    /// <param name="work">Progress tracker for the phase/byte accounting.</param>
    /// <param name="log">Receives --verbose log lines, or null when logging is off.</param>
    /// <param name="confirmedSeed">Chapters trusted verbatim, with no Whisper re-check of their
    /// own - empty for a fresh <see cref="DetectAsync"/> run.</param>
    /// <param name="namedSeed">Prologue/epilogue marks carried over from the file's existing
    /// markings (see <see cref="CarryOverNamedMarkings"/>); empty for a fresh run.</param>
    /// <param name="regions">The independent Pass 2 region(s) to probe; a single whole-file region
    /// for <see cref="DetectAsync"/>, or the gap-scoped regions <see cref="BuildGapRegions"/> built
    /// for <see cref="DetectGapsAsync"/>.</param>
    /// <param name="known">The already-resolved language profile (from --verify) plus its own
    /// detected-language data to carry into the result verbatim, or null to resolve it lazily from
    /// the first probe's samples.</param>
    /// <param name="trailingFallback">The trailing region's start and expected chapter numbers,
    /// when <see cref="BuildGapRegions"/> found the last checkable --verify marking unconfirmed;
    /// null otherwise (including for a fresh <see cref="DetectAsync"/> run).</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<DetectionResult> DetectCoreAsync(
        string file, MediaInfo info, WorkTracker work, Action<string>? log,
        IReadOnlyList<DetectedChapter> confirmedSeed, IReadOnlyList<DetectedMark> namedSeed,
        IReadOnlyList<DetectionRegion> regions,
        (LanguageProfile Profile, string? DetectedLanguage, double DetectedProbability)? known,
        (double From, List<int> Targets)? trailingFallback, CancellationToken ct)
    {
        SetLog(log);
        _whisperAudioSeconds = 0;
        _whisperTranscribeSeconds = 0;
        _customLimitHit = false;
        var bytesPerSecond = info.DurationSeconds > 0 ? info.SizeBytes / info.DurationSeconds : 0;
        var jingleCeilingSeconds = _options.MaxJingleSeconds + PhraseMarginSeconds;

        var (allSilences, silences, nonSpeechRegions, speechSegments) =
            await RunPass1Async(file, info, work, bytesPerSecond, ct);

        // Pass 2 progress is position-based: the bar shows how far into the file's play time the
        // current candidate lies, not how many probes have run. Probe costs vary wildly (full
        // window decode vs. reused overlap vs. skipped candidate), so a fixed per-probe byte budget
        // drifts far off; position is honest about *where* the pass is, at the price of nonlinear -
        // and, during gap re-probes, briefly backwards - movement.
        work.BeginPhase("Pass 2", info.SizeBytes);

        // With --lang auto and a fresh DetectAsync run the language is resolved once per file, from
        // the first probe window's samples (at start 0, decoded below like any other window), then
        // fixed via ChangeLanguage rather than re-detected per probe. A gap-scoped run already
        // knows it from --verify, so `known` seeds it and no probe ever re-resolves it.
        var language = new LanguageState(
            known?.Profile, known?.DetectedLanguage, known?.DetectedProbability ?? 0.0);
        if (language.Profile != null)
            _transcriber.ChangeLanguage(language.Profile.Language);

        // Confirmed markings are trusted verbatim; new finds from every region below are added to
        // the same list, so Pass 3's existing gap tail (after the region loop) sees one seamless
        // sequence regardless of which numbers came from --verify and which from fresh probing.
        var found = new List<DetectedChapter>(confirmedSeed);
        // The named marks travel alongside rather than inside `found`: they have no chapter number,
        // and everything below - gaps, sequence progress, Pass 2.5's targets - reasons in numbers.
        var namedFound = new List<DetectedMark>(namedSeed);

        // --early-abort (0 disables it): once Pass 2 has probed this many minutes of play time
        // without finding a single chapter, further probing is pointless - give up rather than
        // transcribe the rest of a book that plainly will not yield any (wrong --chapter-phrase,
        // wrong --lang, or one that announces chapters differently). Only meaningful for a fresh
        // run: confirmedSeed is always non-empty for a --verify gap recovery or a ".missing-marks"
        // resume, and infinity disables the check outright for those.
        var earlyAbortSeconds = _options.EarlyAbortMinutes > 0 && confirmedSeed.Count == 0
            ? _options.EarlyAbortMinutes * 60
            : double.PositiveInfinity;
        var earlyAborted = false;

        // --expected-start-chapter's abort half, restricted to fresh runs for the same reason: with
        // a seeded chapter the "first chapter found" it guards is never the file's very first.
        // Null disables the check, as +infinity does above.
        var expectedStartChapter = confirmedSeed.Count == 0 ? _options.ExpectedStartChapter : null;
        int? belowExpectedStartNumber = null;

        var pass2Ctx = new Pass2Context(
            file, info, work, bytesPerSecond, jingleCeilingSeconds,
            allSilences, silences, nonSpeechRegions, speechSegments,
            earlyAbortSeconds, expectedStartChapter, _transcriber);

        foreach (var region in regions)
        {
            var prober = new RegionProber(
                BuildProbeEnvironment(), pass2Ctx, region, found, namedFound, language);
            await prober.RunAsync(ct);
            language = prober.Language;
            earlyAborted = prober.EarlyAborted;
            belowExpectedStartNumber = prober.BelowExpectedStartNumber;
            _customLimitHit |= prober.CustomLimitHit;

            if (earlyAborted || belowExpectedStartNumber != null)
                break;
        }

        var chapters = Normalize(found);
        _log?.Invoke("Pass 2 finished");

        // Passes 2.5 and 3 exist only to close holes in the chapter-number sequence, so with
        // --no-numbered-chapters there is nothing for either of them to chase: Pass 2 already
        // probed every candidate the file has, and no gap can be defined without numbers to be
        // missing from.
        var pass2Completed = !earlyAborted && belowExpectedStartNumber == null;
        if (_options.NumberedChapters)
        {
            if (pass2Completed)
                chapters = await RunPass25Async(file, info, work, chapters, namedFound, jingleCeilingSeconds,
                    allSilences, silences, nonSpeechRegions, speechSegments, bytesPerSecond, language.Profile!, ct);

            chapters = await RunPass3Async(file, info, work, chapters, allSilences, nonSpeechRegions,
                speechSegments, bytesPerSecond, language.Profile!, trailingFallback, pass2Completed, ct);
        }

        return BuildDetectionResult(
            chapters, namedFound, speechSegments, language.Profile!, language.DetectedLanguage,
            language.DetectedProbability, earlyAborted, belowExpectedStartNumber);
    }

    /// <summary>
    /// Bundles the tools and detector-owned callbacks every <see cref="RegionProber"/> of the
    /// current file borrows. Built per region loop rather than held as a field because
    /// <see cref="_marks"/> and <see cref="_log"/> are themselves per-file (see
    /// <see cref="SetLog"/>), and a stale environment would hand a region the previous file's
    /// mark placer.
    /// </summary>
    private ProbeEnvironment BuildProbeEnvironment()
        => new(_options, _audio, _vad, _log, _marks!,
            (samples, ct, transcriber) => TranscribeCountingAsync(samples, ct, transcriber),
            ResolveLanguageAsync,
            language => _transcriber.ChangeLanguage(language),
            LogTranscript,
            (segments, profile, mergeBoundary) => FindCappedPhraseMatches(segments, profile, mergeBoundary));

    /// <summary>
    /// Pass 3 (only when needed): resolves sequence gaps by fully transcribing the regions between
    /// mismatched markings (and before the first marking, if it is not chapter 1, or below
    /// --expected-start-chapter). The same mechanism regardless of how <paramref name="chapters"/>
    /// was seeded - a gap-scoped <see cref="DetectGapsAsync"/> run's confirmed-plus-region-2
    /// chapters are covered exactly like a fresh <see cref="DetectAsync"/> run's own. Also runs the
    /// trailing-fallback recovery for a gap-scoped run whose last checkable --verify marking was
    /// unconfirmed - the one case the gap search cannot notice, since nothing bounds a
    /// still-missing trailing chapter from above to compare against.
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
    /// <param name="trailingScanAllowed">Whether --trailing-scan may run; see <see
    /// cref="ResolveTrailingRegion"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><paramref name="chapters"/> plus anything Pass 3 recovered.</returns>
    private async Task<List<DetectedChapter>> RunPass3Async(
        string file, MediaInfo info, WorkTracker work, List<DetectedChapter> chapters,
        List<Silence> allSilences, List<NonSpeechRegion> nonSpeechRegions, List<SpeechSegment> speechSegments,
        double bytesPerSecond, LanguageProfile profile, (double From, List<int> Targets)? trailingFallback,
        bool trailingScanAllowed, CancellationToken ct)
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

        // The trailing region - the one thing FindGaps above structurally cannot flag, since
        // nothing bounds a still-missing last chapter from above to compare against. Two
        // independent things ask for it (see ResolveTrailingRegion), and both end up here.
        if (ResolveTrailingRegion(trailingFallback, chapters, trailingScanAllowed) is { } trailing)
        {
            // "suspicious" only fits the --verify fallback, which is chasing specific numbers it
            // has reason to believe are there; a --trailing-scan sweep is speculative by design.
            var what = trailing.Targets is null ? "trailing region" : "suspicious trailing region";
            _log?.Invoke($"transcribing {what} " +
                         $"{FormatTimestamp(trailing.From)} - {FormatTimestamp(info.DurationSeconds)}");
            work.BeginPhase("Pass 3", (long)((info.DurationSeconds - trailing.From) * bytesPerSecond));
            if (!ReferenceEquals(_pass3Transcriber, _transcriber))
                _pass3Transcriber.ChangeLanguage(profile.Language);
            var fills = await TranscribeRegionAsync(file, info, trailing.From, info.DurationSeconds,
                trailing.Targets, allSilences, nonSpeechRegions, speechSegments, bytesPerSecond, work,
                profile, chapters, ct);
            chapters = Normalize(chapters.Concat(fills).ToList());
            var (highest, missingNumbers) = ChapterProgress(chapters, _options.ExpectedStartChapter);
            work.HighestChapter = highest;
            work.MissingChapters = missingNumbers.Count;
            _log?.Invoke("Pass 3 finished (trailing)");
        }
        return chapters;
    }

    /// <summary>
    /// Decides whether Pass 3 gets a trailing region to transcribe, and of which kind. Two
    /// independent things ask for one:
    /// <list type="bullet">
    /// <item><description>the --verify fallback, for a gap-scoped <see cref="DetectGapsAsync"/> run
    /// whose last checkable marking was unconfirmed: it knows exactly which numbers it is after, so
    /// it is skipped entirely once they have all turned up elsewhere;</description></item>
    /// <item><description>--trailing-scan, which sweeps from the last detected chapter to the end of
    /// the file with no expectation of what it will find - the only way to catch a chapter after the
    /// last one detected, which nothing bounds from above.</description></item>
    /// </list>
    /// --trailing-scan subsumes the fallback when both apply: an open-ended scan accepts everything
    /// the targeted one would and starts no later, so the two are merged into a single sweep rather
    /// than transcribing the tail twice.
    /// </summary>
    /// <param name="verifyFallback">The --verify fallback's region start and expected numbers, or
    /// null when this is not a gap-scoped run (or its last marking was confirmed).</param>
    /// <param name="chapters">Everything detected so far, in chronological order.</param>
    /// <param name="trailingScanAllowed">Whether --trailing-scan may run at all - false once Pass 2
    /// aborted, since a run that gave up on the file has no meaningful "last chapter" to sweep from.</param>
    /// <returns>The region's start and its expected chapter numbers (null for an open-ended
    /// --trailing-scan sweep), or null when no trailing region is needed.</returns>
    private (double From, IReadOnlyList<int>? Targets)? ResolveTrailingRegion(
        (double From, List<int> Targets)? verifyFallback, List<DetectedChapter> chapters,
        bool trailingScanAllowed)
    {
        // Nothing found at all means no anchor to sweep from - the whole file would be "the
        // trailing region", which is Pass 2's job, not this one's.
        if (_options.TrailingScan && trailingScanAllowed && chapters.Count > 0)
            return (Math.Min(chapters[^1].TimeSeconds, verifyFallback?.From ?? double.MaxValue), null);
        if (verifyFallback is not { } tf)
            return null;
        var stillMissing = tf.Targets.Where(n => !chapters.Any(c => c.Number == n)).ToList();
        return stillMissing.Count > 0 ? (tf.From, stillMissing) : null;
    }

    /// <summary>
    /// Pass 2.5: before Pass 3 resorts to transcribing a whole gap region end to end, re-probes it
    /// with Pass 2's own cheap candidate logic - the same silence/jingle-anchored windows, adaptive
    /// resizing and transcript reuse - on the <c>--pass3-model</c> recognizer instead of the pass-2
    /// one. The premise: most gaps are not "the announcement is unprobeable" but "the pass-2 model
    /// misheard it" - the window was probed, the audio was right there, and a better model would
    /// have read the number correctly. Retrying just those windows can close the gap without
    /// transcribing the region at all.
    /// <para>
    /// The cost is <em>not</em> guaranteed to be small, and scales with the gap's candidate count
    /// rather than its length: a region dense in qualifying silences can decode about as much audio
    /// as the full transcription it is avoiding, and when it finds nothing Pass 3 still runs after
    /// it. Measured on real audio (2026-07-26, --model tiny --pass3-model large): a 56-minute gap
    /// took ~40 minutes of re-probing and recovered nothing. A favourable bet only where candidates
    /// are sparse - hence opt-in behind a deliberately chosen heavier --pass3-model.
    /// </para>
    /// <para>
    /// Runs only when <see cref="CliOptions.Pass3ModelIsUpgrade"/> holds (a lighter or equal pass-3
    /// model would re-probe the same audio to the same conclusion) and a distinct pass-3 recognizer
    /// actually exists to probe with. Never after an --early-abort or --expected-start-chapter
    /// abort: both mean the file is being given up on, not gap-filled.
    /// </para>
    /// <para>
    /// Each gap becomes a <see cref="DetectionRegion"/> bounded by the chapter numbers around it,
    /// exactly as a --verify gap recovery builds its regions, so a re-probe can never accept a
    /// number outside the gap or displace a chapter already found. Mark placement for anything
    /// recovered is unchanged - it refines on the pass-2 model like every other mark, including
    /// Pass 3's own (see <see cref="Pass2Context.Transcriber"/>).
    /// </para>
    /// </summary>
    /// <param name="file">Path of the audio file.</param>
    /// <param name="info">Probe result of the file.</param>
    /// <param name="work">Progress tracker; begins its own "Pass 2.5" phase when there is work.</param>
    /// <param name="chapters">The chapters Pass 2 found, in chronological order.</param>
    /// <param name="namedFound">The file's prologue/epilogue accumulator, passed through so a
    /// re-probe on the better model can still notice an announcement Pass 2's model missed.</param>
    /// <param name="jingleCeilingSeconds">The probe window ceiling Pass 2 was run with.</param>
    /// <param name="allSilences">Every silence from <see cref="RunPass1Async"/>.</param>
    /// <param name="silences">The --min-silence-length subset - Pass 2's own candidates.</param>
    /// <param name="nonSpeechRegions">VAD non-speech regions from <see cref="RunPass1Async"/>.</param>
    /// <param name="speechSegments">VAD speech segments from <see cref="RunPass1Async"/>.</param>
    /// <param name="bytesPerSecond">The file's average byte rate, for progress reporting.</param>
    /// <param name="profile">The language profile resolved for this file.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><paramref name="chapters"/> plus anything the re-probe recovered.</returns>
    private async Task<List<DetectedChapter>> RunPass25Async(
        string file, MediaInfo info, WorkTracker work, List<DetectedChapter> chapters,
        List<DetectedMark> namedFound,
        double jingleCeilingSeconds, List<Silence> allSilences, List<Silence> silences,
        List<NonSpeechRegion> nonSpeechRegions, List<SpeechSegment> speechSegments,
        double bytesPerSecond, LanguageProfile profile, CancellationToken ct)
    {
        if (!_options.Pass3ModelIsUpgrade || ReferenceEquals(_pass3Transcriber, _transcriber))
            return chapters;

        // Only the gaps that actually name a missing chapter are worth re-probing, and only those
        // are budgeted for below - a gap whose numbers are all accounted for would otherwise inflate
        // the phase total and stop the bar from ever reaching 100 %.
        var work25 = FindGaps(chapters, info.DurationSeconds, _options.ExpectedStartChapter)
            .Select(gap => (Gap: gap, Missing: MissingNumbersInGap(chapters, gap, _options.ExpectedStartChapter)))
            .Where(g => g.Missing.Count > 0)
            .ToList();
        if (work25.Count == 0)
            return chapters;

        work.BeginPhase("Pass 2.5", (long)(work25.Sum(g => g.Gap.ToSeconds - g.Gap.FromSeconds) * bytesPerSecond));
        _pass3Transcriber.ChangeLanguage(profile.Language);

        // --early-abort and --expected-start-chapter are both disabled for these regions (infinity
        // and null): they exist to give up on a file that is yielding nothing at all, which is not
        // a question a bounded gap re-probe of an already-productive file gets to reopen.
        var ctx = new Pass2Context(
            file, info, work, bytesPerSecond, jingleCeilingSeconds,
            allSilences, silences, nonSpeechRegions, speechSegments,
            double.PositiveInfinity, null, _pass3Transcriber);

        // Seeded with what is already known, exactly as DetectCoreAsync seeds Pass 2 proper:
        // RegionProber reports per-mark progress and "still missing" notes off this list, and gates
        // the --max-jingle-length auto observation on it not being the file's very first mark - all
        // nonsense on a list holding only this pass's own finds.
        var found = new List<DetectedChapter>(chapters);
        var knownCount = found.Count;
        // Seconds of gap already behind us; each gap's probing is reported relative to it, so the
        // bar runs monotonically 0-100 % across the whole pass rather than measuring a whole-file
        // position against a gap-sized budget.
        var gapSecondsDone = 0.0;
        foreach (var (gap, missing) in work25)
        {
            _log?.Invoke(
                $"pass 2.5: re-probing {FormatTimestamp(gap.FromSeconds)} - {FormatTimestamp(gap.ToSeconds)} " +
                $"for chapter{(missing.Count > 1 ? "s" : "")} {string.Join(", ", missing)} with the pass 3 model");
            var region = new DetectionRegion(gap.FromSeconds, gap.ToSeconds, missing[0] - 1, missing[^1] + 1);
            var prober = new RegionProber(
                BuildProbeEnvironment(), ctx, region, found, namedFound,
                new LanguageState(profile, null, 0),
                gapSecondsDone - gap.FromSeconds);
            await prober.RunAsync(ct);
            _customLimitHit |= prober.CustomLimitHit;
            gapSecondsDone += gap.ToSeconds - gap.FromSeconds;
            work.SetPhaseProgress((long)(gapSecondsDone * bytesPerSecond));
        }

        var recovered = found.Count - knownCount;
        chapters = Normalize(found);
        var (highest, missingNumbers) = ChapterProgress(chapters, _options.ExpectedStartChapter);
        work.HighestChapter = highest;
        work.MissingChapters = missingNumbers.Count;
        _log?.Invoke(recovered > 0
            ? $"Pass 2.5 finished - recovered {recovered} chapter(s) without a full transcription"
            : "Pass 2.5 finished - nothing recovered, falling through to pass 3");
        return chapters;
    }

    /// <summary>
    /// Assembles the final <see cref="DetectionResult"/> once Pass 2 and Pass 3 are done: the
    /// remaining-gap consistency check, the low-confidence list, the lead-in speech check for
    /// <see cref="FileProcessor"/>'s intro-chapter insertion, and the per-file statistics.
    /// </summary>
    /// <param name="chapters">The final chapter list, after Pass 3.</param>
    /// <param name="namedMarks">The file's prologue/epilogue marks, at most one of each.</param>
    /// <param name="speechSegments">The VAD speech segments from <see cref="RunPass1Async"/>
    /// (empty when the VAD pre-pass did not run).</param>
    /// <param name="profile">The language profile resolved for this file.</param>
    /// <param name="detectedLanguage">Whisper's raw language guess with --lang auto, or null.</param>
    /// <param name="detectedProbability">Whisper's probability for <paramref name="detectedLanguage"/>.</param>
    /// <param name="earlyAborted">True when --early-abort cut detection short.</param>
    /// <param name="belowExpectedStartNumber">The chapter number Pass 2 found first, when
    /// --expected-start-chapter aborted detection because it was numbered below that expectation.</param>
    private DetectionResult BuildDetectionResult(
        List<DetectedChapter> chapters, List<DetectedMark> namedMarks,
        List<SpeechSegment> speechSegments, LanguageProfile profile,
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

        // A file that yielded no chapter at all is left unchanged by FileProcessor, and a lone
        // prologue or epilogue must not be what makes it worth rewriting: a book whose chapter
        // announcements were never heard is a failed detection, not a two-mark book. With
        // --no-numbered-chapters that reasoning inverts - the named marks are the entire point of
        // the run, and there is no chapter whose absence could condemn them.
        var named = chapters.Count > 0 || !_options.NumberedChapters
            ? namedMarks.OrderBy(m => m.TimeSeconds).ToList()
            : [];

        // Whether the very first mark is preceded by any VAD speech at all - lets FileProcessor's
        // intro-chapter insertion tell a real spoken prelude ("insert an Intro entry") apart from
        // just silence, music or a jingle before the phrase ("let the first mark's own mp4-muxer
        // start-snap absorb the lead-in instead"). Measured against the earliest mark of either
        // kind, since a prologue ahead of chapter 1 is what the intro would have to precede. True
        // by default: unknowable without the VAD pre-pass, and irrelevant with no mark to check.
        var firstMark = chapters.Count == 0 && named.Count == 0
            ? (double?)null
            : Math.Min(
                chapters.Count > 0 ? chapters[0].TimeSeconds : double.MaxValue,
                named.Count > 0 ? named[0].TimeSeconds : double.MaxValue);
        var leadInHasSpeech = firstMark is not { } first || _vad == null ||
            speechSegments.Any(s => s.StartSeconds < first);

        // Per-file statistics over only the chapters that survived into the final result (anything
        // Normalize dropped contributes nothing - see MarkPlacer, which recorded them at mark
        // placement). Each extreme is computed twice: over all chapters, and over the
        // "inter-chapter" subset excluding chapter 1, whose intro transition is often atypical.
        var interChapter = chapters.Where(c => c.Number != 1).ToList();
        var stats = new DetectionStats(
            _marks!.MinSilenceSeconds(chapters), _marks.MinSilenceSeconds(interChapter),
            _marks.MaxJingleSeconds(chapters), _marks.MaxJingleSeconds(interChapter),
            _whisperAudioSeconds, _whisperTranscribeSeconds);

        return new DetectionResult(
            chapters, named, missing.Count > 0, missing, lowConfidence,
            profile, detectedLanguage, detectedProbability, stats, earlyAborted, belowExpectedStartNumber,
            leadInHasSpeech, _customLimitHit);
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
        // The scan always goes down to MinStoredSilenceSeconds (or --min-silence-length, if lower
        // still) so short silences are available for overlap-border snapping (see
        // FindOverlapSplitPoint). allSilences holds all of those; `silences` keeps only the ones at
        // or above --min-silence-length.
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
            // The speech-segment count carries no extra information (a non-speech region is just
            // the gap between two consecutive speech segments), so only the regions are logged.
            _log?.Invoke($"Pass 1: {nonSpeechRegions.Count} non-speech region(s) found");

        return new Pass1Result(allSilences, silences, nonSpeechRegions, speechSegments);
    }

    /// <summary>
    /// Checks pre-existing chapter markings against the audio (--verify) - far quicker than the
    /// full silence-scan/probe pipeline, since only the markings' own timestamps are visited. For
    /// every marking whose title yields a parseable expected chapter number, a short window around
    /// its timestamp is probed with Whisper and checked for a phrase match with that number. A
    /// marking whose title has no parseable number (e.g. a prelude/intro entry) cannot be checked
    /// and counts neither for nor against the result; when none of a file's markings have one,
    /// verification trivially passes - there is nothing to disprove, so the file is left alone
    /// rather than needlessly re-detected.
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
        // Every marking's outcome in file order - the input BuildGapRegions groups into
        // DetectGapsAsync's recovery regions. A skipped marking (empty window or no parseable
        // number) is still recorded, as null/false, so it cannot split a run of unconfirmed
        // markings around it into two.
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
            var confirmed = FindCappedPhraseMatches(segments, profile).Any(m => m.Number == expected);
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
            profile, detectedLanguage, detectedProbability, CarryOverNamedMarkings(info, profile));
    }

    /// <summary>
    /// Resolves the language profile for a file from its pre-existing chapter markings, shared by
    /// <see cref="VerifyExistingChaptersAsync"/> and <see cref="ResumeMissingMarksAsync"/>: with an
    /// explicit --lang, <see cref="CliOptions.DefaultProfile"/> comes straight back with no decode
    /// at all; with --lang auto, the first marking with a decodable window
    /// (<see cref="VerifyMarginBeforeSeconds"/> before its own timestamp,
    /// <see cref="VerifyWindowSeconds"/> long) resolves it via <see cref="ResolveLanguageAsync"/>.
    /// Does not itself call <see cref="ITranscriber.ChangeLanguage"/> - every caller needs that
    /// applied at a slightly different point, so it is left to them.
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
    /// <see cref="GapRetryChunkSeconds"/> sub-chunks, each independently re-decoded, re-transcribed
    /// and checked for the phrase - stopping at the first chunk that confirms it. Small chunks
    /// rather than one call over the whole padded gap matters: a single call spanning a long, mostly
    /// non-speech stretch (silence, or a jingle around a short phrase) risks the very failure this
    /// exists to recover from, since Whisper can judge that audio as non-speech on average and
    /// return only a token leading segment - observed in practice - while the same audio decoded at
    /// a scale close to a single phrase transcribes correctly.
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
                if (FindCappedPhraseMatches(gapSegments, profile).Any(m => m.Number == expected))
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
        if (digits.Success && int.TryParse(digits.Value, NumberStyles.None, CultureInfo.InvariantCulture, out number))
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
    /// Fully transcribes a region of the file and returns all chapter starts found in it - Pass 3's
    /// way of closing sequence gaps the silence-probe fast path left. Every chunk border is snapped
    /// to the nearest silence (or, when the VAD pre-pass ran, VAD non-speech region) mid-point
    /// within <see cref="Pass3SeamSearchSeconds"/> of its natural position; consecutive chunks then
    /// abut exactly at that word-safe seam - no overlap, nothing decoded twice - and a phrase
    /// straddling the seam is still found by carrying the previous chunk's trailing segments
    /// (<see cref="Pass3BridgeSeconds"/>) into the next chunk's matching. Only where no seam target
    /// exists near a border does that joint fall back to a raw cut with
    /// <see cref="GapChunkOverlapSeconds"/> of overlap as redundancy against a possible mid-word cut.
    /// </summary>
    /// <param name="file">Path of the audio file.</param>
    /// <param name="info">The file's probed media info (duration, size, decoder).</param>
    /// <param name="fromSeconds">Start of the region to transcribe, in seconds.</param>
    /// <param name="toSeconds">End of the region to transcribe, in seconds.</param>
    /// <param name="expectedNumbers">The chapter numbers this gap exists to recover (see
    /// <see cref="MissingNumbersInGap"/>). Transcription stops as soon as all of them are found -
    /// continuing would only re-scan audio that cannot yield anything new - so the caller can
    /// advance to the next gap (or finish Pass 3) immediately.
    /// <para>
    /// Null instead runs the region <em>open-ended</em>, as --trailing-scan needs: there is no
    /// known set of numbers to satisfy, so nothing can ever be complete and the region is always
    /// scanned through to its end. With no target list to filter by, the only thing that makes a
    /// match new is being numbered above every chapter already known - otherwise an in-text
    /// mention of an earlier chapter would be reported as a find and merely dropped later by
    /// <see cref="Normalize"/>.
    /// </para></param>
    /// <param name="allSilences">Every silence Pass 1 stored, down to
    /// <see cref="MinStoredSilenceSeconds"/> - used both as seam targets and to pinpoint each
    /// mark at the end of the silence directly preceding its phrase.</param>
    /// <param name="nonSpeechRegions">The VAD pre-pass's non-speech regions (empty when it did not
    /// run), used as chunk-border seam targets alongside the silences.</param>
    /// <param name="speechSegments">The raw VAD speech segments behind
    /// <paramref name="nonSpeechRegions"/> (empty when VAD is off), for the jingle edge
    /// adjustment inside <see cref="ResolveJingleAnchor"/>.</param>
    /// <param name="bytesPerSecond">The file's average bytes per second of play time, used to
    /// convert transcribed play time into the byte-based progress the bar counts in.</param>
    /// <param name="work">Progress tracker for the phase/byte accounting.</param>
    /// <param name="profile">The language profile this file resolved to.</param>
    /// <param name="knownChapters">Chapters already detected outside this region, so the
    /// per-mark progress numbers and still-missing log notes reflect the whole file rather
    /// than just this region's finds.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<List<DetectedChapter>> TranscribeRegionAsync(
        string file, MediaInfo info, double fromSeconds, double toSeconds,
        IReadOnlyList<int>? expectedNumbers,
        List<Silence> allSilences, List<NonSpeechRegion> nonSpeechRegions,
        List<SpeechSegment> speechSegments, double bytesPerSecond,
        WorkTracker work, LanguageProfile profile, IReadOnlyList<DetectedChapter> knownChapters,
        CancellationToken ct)
    {
        var found = new List<DetectedChapter>();
        // The still-missing chapter numbers of this gap; emptied as they are found, at which
        // point there is nothing left to recover here and transcription can stop early. Null for
        // an open-ended --trailing-scan region, which has no such list and therefore no way to
        // finish early - see the expectedNumbers doc above.
        var remaining = expectedNumbers is null ? null : new HashSet<int>(expectedNumbers);
        // Inputs to the cross-chunk bridging below: the previous chunk's transcript in absolute
        // file time, and whether the seam it ends at was snapped (overlap-free).
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
            foreach (var match in FindCappedPhraseMatches(matchSegments, profile,
                         carried.Count > 0 ? carried.Count : null))
            {
                var phraseAbs = match.PhraseStartSeconds;
                // A match entirely inside the carried tail was already found (and reported) by
                // the previous chunk's own pass; only a seam-straddling detection is news here.
                if (phraseAbs < chunkStart && !match.SpansMerge)
                    continue;
                // A chapter bounding this gap is already known and can resurface right at a chunk
                // border, its announcement sitting just inside the scanned range, without being
                // news. Leave its existing mark alone rather than risk Normalize preferring this
                // re-detection's timestamp.
                if (knownChapters.Any(k => k.Number == match.Number))
                    continue;
                // An open-ended region has no expected-number list, so what makes a match new is
                // topping every chapter already known. Without this an in-text mention of an
                // earlier number would be reported as a find and then dropped by Normalize.
                if (remaining is null && !IsAboveEveryKnownChapter(match.Number, knownChapters, found))
                {
                    _log?.Invoke($"skipped chapter {match.Number} at {FormatTimestamp(phraseAbs)} - " +
                                 "not above every chapter already found (in-text mention?)");
                    continue;
                }
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
            if (remaining is null or { Count: > 0 })
                await ScanGapRetriesAsync(file, info, chunkStart, chunkEnd, freshAbs, profile,
                    found, remaining, knownChapters, allSilences, nonSpeechRegions, speechSegments, work, ct);

            work.Advance((long)((chunkEnd - chunkStart) * bytesPerSecond));

            // Everything this gap was meant to recover is found, so stop and let the caller move
            // on. The unscanned remainder still counts as this gap's work done - advance it, or
            // the Pass 3 bar never reaches its budget.
            if (remaining is { Count: 0 })
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
    /// Whether a phrase match found in an <em>open-ended</em> Pass 3 region (see the null
    /// <c>expectedNumbers</c> case of <see cref="TranscribeRegionAsync"/>) is genuinely new. Such a
    /// region has no expected-number list to test against, so the only usable criterion is the one
    /// <see cref="Normalize"/> would apply anyway: the number has to top every chapter already
    /// known, both the ones detected elsewhere and the ones this region has found so far. Anything
    /// at or below that is a repeat or an in-text mention, not a chapter this scan recovered.
    /// </summary>
    /// <param name="number">The matched chapter number.</param>
    /// <param name="knownChapters">Chapters already detected outside this region.</param>
    /// <param name="found">Chapters this region has found so far.</param>
    private static bool IsAboveEveryKnownChapter(
        int number, IReadOnlyList<DetectedChapter> knownChapters, IReadOnlyList<DetectedChapter> found)
        => knownChapters.Concat(found).All(c => number > c.Number);

    /// <summary>
    /// Records one phrase match found while scanning a Pass 3 gap chunk (its normal transcript, or
    /// <see cref="ScanGapRetriesAsync"/>'s fallback) as a detected chapter: resolves the
    /// default-mode mark - a fixed offset before the phrase - hands it to <see cref="MarkPlacer"/>
    /// for the corrections and statistics every pass shares, then updates <paramref name="found"/>,
    /// <paramref name="remaining"/> and the progress bar's chapter state, and logs it. Shared
    /// between both callers so this stays in exactly one place.
    /// </summary>
    /// <param name="match">The confirmed phrase match, in absolute file time.</param>
    /// <param name="matchSegments">The transcript the match was found in (absolute file time),
    /// for the VAD edge adjustment inside <see cref="ResolveJingleAnchor"/>.</param>
    /// <param name="found">Chapters found in this gap so far; appended to.</param>
    /// <param name="remaining">Still-missing chapter numbers for this gap; the match's number is
    /// removed from it. Null for an open-ended region, which keeps no such list.</param>
    /// <param name="knownChapters">Chapters already detected outside this gap, so the progress
    /// bar's chapter state reflects the whole file.</param>
    /// <param name="allSilences">Every silence Pass 1 stored, for anchor resolution.</param>
    /// <param name="nonSpeechRegions">VAD non-speech regions (empty when the VAD pre-pass did
    /// not run), for the jingle anchor resolution.</param>
    /// <param name="speechSegments">Raw VAD speech segments, for the jingle edge adjustment and,
    /// with precise marking, as its candidate positions.</param>
    /// <param name="work">The file's progress tracker.</param>
    /// <param name="file">Path of the audio file, for precise marking's own extra transcriptions.</param>
    /// <param name="inputDecoder">Explicit input decoder to force, or null.</param>
    /// <param name="profile">Language profile supplying the phrase precise marking looks for.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task RecordGapChapterMatch(
        PhraseMatch match, List<TranscriptSegment> matchSegments,
        List<DetectedChapter> found, HashSet<int>? remaining, IReadOnlyList<DetectedChapter> knownChapters,
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
            // Same VAD-region-primary anchor resolution as Pass 2, just against a fixed lookback
            // since a gap chunk has no probe window start of its own. Feeds default-mode placement
            // and the auto-mechanism statistics; --mark-before-jingle resolves from the
            // default-mode mark instead and does not consume it.
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
            // purely to feed the --min-silence-length auto tightening via MarkPlacer's statistics.
            var anchor = FindRealAnchorSilence(phraseAbs - PhraseLatestStart, phraseAbs, allSilences);
            time = Math.Max(0, phraseAbs - DefaultMarkLeadSeconds);
            statSilence = anchor;
        }
        var markCtx = new MarkContext(
            file, inputDecoder, profile.PhraseRegex, allSilences, speechSegments, matchSegments);
        time = await _marks!.PlaceAsync(match.Number, time, phraseAbs, statSilence, statRegion, markCtx, ct);
        found.Add(new DetectedChapter(match.Number, time, match.Confidence));
        remaining?.Remove(match.Number);
        var (highest, missingNumbers) = ChapterProgress(knownChapters.Concat(found), _options.ExpectedStartChapter);
        work.HighestChapter = highest;
        work.MissingChapters = missingNumbers.Count;
        _log?.Invoke($"chapter {match.Number} found in gap, mark placed at {FormatTimestamp(time)} " +
                     $"(confidence {match.Confidence:0.00}" +
                     await _marks.LoudnessNoteAsync(time, markCtx, ct) +
                     $"){LowConfidenceNote(match.Confidence)}" +
                     MissingNote(missingNumbers));
    }

    /// <summary>
    /// Second-chance scan for a Pass 3 gap chunk that, after its normal transcript, still has
    /// missing chapter numbers (<paramref name="remaining"/>). Every stored silence - and, when the
    /// VAD pre-pass ran, every VAD non-speech region - at least <see cref="GapRetryThresholdSeconds"/>
    /// long, entirely inside this chunk, and covered by <em>none</em> of the chunk's own fresh
    /// segments (not the bridged tail carried in from the previous chunk, already covered by its own
    /// pass), i.e. one Whisper produced no speech at all over, is padded by
    /// <see cref="GapRetryPaddingSeconds"/> on each side and re-scanned in short, overlapping
    /// <see cref="GapRetryChunkSeconds"/> sub-chunks - the same technique --verify uses to recover a
    /// phrase Whisper silently dropped from a single call spanning a mostly non-speech stretch.
    /// Scoped to the silence/region's own bounds rather than the whole raw stretch between the
    /// segments bracketing it: with sparse narration that stretch can span most of a 600 s Pass 3
    /// chunk, making an already time-consuming fallback far more so, whereas a genuine jingle or
    /// scene-transition silence runs seconds to at most tens of seconds. Confirmed matches are
    /// recorded via <see cref="RecordGapChapterMatch"/> like the chunk's normal ones; scanning
    /// stops as soon as nothing is left to find.
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
    /// <param name="remaining">Still-missing chapter numbers for this gap, or null for an
    /// open-ended region - which cannot filter by expected number, and so re-scans every candidate
    /// stretch and accepts anything numbered above every chapter already known.</param>
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
        List<DetectedChapter> found, HashSet<int>? remaining, IReadOnlyList<DetectedChapter> knownChapters,
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
            if (remaining is { Count: 0 })
                break;
            // An ordinary sentence that merely straddles a real pause still has its own segment
            // covering the pause and needs no second look - only a stretch with nothing
            // transcribed over it at all is a candidate for having been dropped outright.
            if (freshAbs.Any(s => s.StartSeconds < silEnd && s.EndSeconds > silStart))
                continue;

            var sliceStart = Math.Max(chunkStart, silStart - GapRetryPaddingSeconds);
            var sliceEnd = Math.Min(chunkEnd, silEnd + GapRetryPaddingSeconds);
            var subStep = GapRetryChunkSeconds - GapRetryChunkOverlapSeconds;
            for (var subStart = sliceStart;
                 subStart < sliceEnd && remaining is null or { Count: > 0 };
                 subStart += subStep)
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

                foreach (var match in FindCappedPhraseMatches(subAbs, profile))
                {
                    var wanted = remaining is null
                        ? IsAboveEveryKnownChapter(match.Number, knownChapters, found)
                        : remaining.Contains(match.Number);
                    if (!wanted || knownChapters.Any(k => k.Number == match.Number))
                        continue;
                    await RecordGapChapterMatch(match, subAbs, found, remaining, knownChapters,
                        allSilences, nonSpeechRegions, speechSegments, work, file, info.InputDecoder, profile, ct);
                    if (remaining is { Count: 0 })
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
    /// <see cref="PhraseMatching.FindPhraseMatches"/> with <see cref="CliOptions.MaxChapterNumber"/>
    /// applied: a match whose parsed number sits above the cap is dropped (and logged under
    /// --verbose) rather than handed on. Every pass funnels its matching through here - Pass 2,
    /// Pass 3, the gap chunk scan and --verify alike - so an implausible number can enter the
    /// chapter sequence by no route, neither as a mark of its own nor as the upper bound that turns
    /// everything below it into a gap to hunt for. Without a cap this is exactly
    /// <see cref="PhraseMatching.FindPhraseMatches"/>.
    /// </summary>
    /// <param name="segments">The transcript segments to search, in whatever time base the caller
    /// works in (this method neither reads nor rewrites the timings).</param>
    /// <param name="profile">Language profile supplying the chapter phrase and number parsing.</param>
    /// <param name="mergeBoundarySegIndex">Passed straight through to
    /// <see cref="PhraseMatching.FindPhraseMatches"/>.</param>
    private IEnumerable<PhraseMatch> FindCappedPhraseMatches(
        List<TranscriptSegment> segments, LanguageProfile profile, int? mergeBoundarySegIndex = null)
    {
        foreach (var match in FindPhraseMatches(segments, profile, mergeBoundarySegIndex))
        {
            if (_options.MaxChapterNumber is { } cap && match.Number > cap)
            {
                _log?.Invoke($"discarded chapter {match.Number} - above the --max-chapter-number cap of {cap}");
                continue;
            }
            yield return match;
        }
    }

}
