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
using static ABChapterize.Language.NumberWordParser;
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
/// contain sequence gaps, the audio between the mismatched marks is fully transcribed.
/// </summary>
public sealed class ChapterDetector
{
    private readonly CliOptions _options;
    private readonly IAudioSource _audio;
    private readonly ITranscriber _transcriber;

    /// <summary>Transcriber used for Scan (gap filling). The same instance as
    /// <see cref="_transcriber"/> unless <c>--upgrade-model</c> selected a different model, in which
    /// case it is a <see cref="Transcription.UpgradeTranscriber"/>. Reference equality with
    /// <see cref="_transcriber"/> is what the code below tests to tell the two cases apart. Only
    /// which model recognizes the gap chunks changes; detection/mark/statistics are
    /// identical.</summary>
    private readonly ITranscriber _upgradeTranscriber;

    private readonly IVoiceActivityDetector? _vad;

    /// <summary>Places every mark this detector's passes decide on, and holds the per-chapter
    /// silence/jingle measurements behind <see cref="DetectionStats"/>. Rebuilt per file once
    /// <see cref="_log"/> is known (see <see cref="SetLog"/>), since its constructor closes over
    /// this detector's own <see cref="TranscribeCountingAsync"/> so the corrections' transcriptions
    /// count toward the same per-file statistics - which also resets those measurements.</summary>
    private MarkPlacer? _marks;

    /// <summary>The speech denoiser this file's probes may fall back on, or null when the run
    /// switched it off or the file sounded clean enough not to need it. Resolved on first demand by
    /// <see cref="DenoiserForFileAsync"/> and disposed with the file, since an ONNX session is worth
    /// neither holding across a whole batch nor building per window.</summary>
    private SpeechDenoiser? _denoiser;

    /// <summary>Whether <see cref="DenoiserForFileAsync"/> has already decided for this file, so a
    /// second garbled window neither re-measures nor re-opens the session. A flag of its own rather
    /// than a null check, since "measured and refused" is a real answer.</summary>
    private bool _denoiserDecided;

    /// <summary>What the fidelity check needs to sample this file, captured where the detection core
    /// still has it. Held rather than threaded through <see cref="BuildProbeEnvironment"/> because
    /// the resolver is reached from four different passes, none of which has it in scope.</summary>
    private (string File, double DurationSeconds, string? Decoder)? _denoiseSource;

    /// <summary>Per-file log sink set by <see cref="SetLog"/>, reaching every destination there is;
    /// null when nothing is listening. What a detection line is normally written to.</summary>
    private Action<string>? _log;

    /// <summary>Per-file <c>--debug</c> sink alone, for the bulk detail that would drown
    /// <see cref="_log"/> - see <see cref="DetectionLog"/>. Null unless <c>--debug</c> was given.</summary>
    private Action<string>? _debug;

    /// <summary>Per-file ordinary sink alone (console and <c>--log-file</c>), for the one line that
    /// reaches the debug file in a fuller form - see <see cref="LogTranscript"/>.</summary>
    private Action<string>? _plainLog;

    /// <summary>
    /// How far back this file's music reaches (<see cref="JingleCensus.ReachSeconds"/>), set once
    /// Analyze has counted the jingles and read by everything that has to look back over music:
    /// Scan's anchor lookback here, and <see cref="MarkPlacer"/>'s refiner. Starts at the bare margin,
    /// which is what a run with no VAD pre-pass and a book with no jingles both come to.
    /// </summary>
    private double _jingleReachSeconds = PhraseMarginSeconds;

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
    /// set it, across Probe and Re-probe alike, and the answer belongs to the file rather than to any
    /// one of them. Reset per file, alongside the Whisper counters above.</summary>
    private bool _customLimitHit;

    /// <summary>How many announcements the current file lost to a restarting chapter sequence,
    /// accumulated across every <see cref="RegionProber"/> of the file for the same reason as
    /// <see cref="_customLimitHit"/> above. Reset per file.</summary>
    private int _sequenceRestartSkips;

    /// <summary>The current file's named marks, as a live view rather than a snapshot: the very
    /// list every <see cref="RegionProber"/> of the file appends to, so
    /// <see cref="ExpectedStartChapter"/> answers with what is known at the moment it is asked. Set
    /// per file, and empty until then.</summary>
    private IReadOnlyList<DetectedMark> _namedMarks = [];

    /// <summary>The chapter number this file's sequence is expected to start at - see
    /// <see cref="GapPlanning.ExpectedStartFor"/>, which is where the rule lives. Read instead of
    /// <see cref="CliOptions.ExpectedStartChapter"/> everywhere below that plans a gap or counts
    /// what is missing; the option itself is what Probe's abort half still gets, for the reason
    /// given there.</summary>
    private int? ExpectedStartChapter => ExpectedStartFor(_options, _namedMarks);

    /// <summary>The options this detector reads, which for a file under a per-folder
    /// <c>.abchapterize-config</c> are not the run's own (see <see cref="FolderConfig"/>). Exposed
    /// so whatever reports what detection did - the debug log's header above all - reports the
    /// settings that were actually in force rather than the ones the command line asked for.</summary>
    public CliOptions Options => _options;

    /// <summary>Creates a detector bound to the given tools and options.</summary>
    /// <param name="options">Validated command line options.</param>
    /// <param name="audio">Audio source used for silence detection and PCM decoding.</param>
    /// <param name="transcriber">Loaded speech recognizer.</param>
    /// <param name="vad">Voice activity detector used for the full-file VAD pre-pass (finds
    /// jingle transitions with no detectable amplitude gap). Every real run has one since 0.12.0;
    /// null is for the tests that do not exercise that path.</param>
    /// <param name="upgradeTranscriber">Transcriber for Scan (gap filling) when
    /// <c>--upgrade-model</c> asks for a model other than the main one; null (the default) makes
    /// Scan reuse <paramref name="transcriber"/>.</param>
    public ChapterDetector(CliOptions options, IAudioSource audio, ITranscriber transcriber,
        IVoiceActivityDetector? vad = null, ITranscriber? upgradeTranscriber = null)
    {
        _options = options;
        _audio = audio;
        _transcriber = transcriber;
        _upgradeTranscriber = upgradeTranscriber ?? transcriber;
        _vad = vad;
    }

    /// <summary>Sets the per-file log sinks and rebuilds <see cref="_marks"/> around them, so its
    /// mark-placement log lines land in the same sinks as the rest of this file's detection log and
    /// its per-chapter measurements start empty for the new file. Every entry point into the
    /// detector passes through here, which is what makes it the place to clear per-file state that
    /// is not a counter (see <see cref="_namedMarks"/>).</summary>
    /// <param name="log">This file's log sinks; default when nothing is listening.</param>
    private void SetLog(DetectionLog log)
    {
        // Emptied here rather than in each entry point, so no path into the detector can carry the
        // previous file's prologue into this one's expected-start question.
        _namedMarks = [];
        _log = log.Fanout();
        _plainLog = log.Plain;
        _debug = log.Debug;
        _marks = new MarkPlacer(
            _audio, _options, log, (samples, ct) => TranscribeCountingAsync(samples, ct),
            // Same gate the suspect-number re-read uses: a --upgrade-model worth a second opinion is
            // one that outclasses the probing model and is a separate recognizer at all.
            _options.UpgradeModelIsBetter && !ReferenceEquals(_upgradeTranscriber, _transcriber)
                ? SecondOpinionAsync
                : null,
            // The refinement's number re-read looks at probe windows that open on the announcement
            // itself, so the narrow --chapter-phrase none reading is the one that fits: the wider
            // one would only offer it numbers from the surrounding prose.
            (segments, profile, mergeBoundary) => FindCappedPhraseMatches(
                segments, profile, mergeBoundary, BareNumberReading.SpokenAloneAtSegmentStart));
    }

    /// <summary>
    /// Runs the complete detection pipeline for one file: a single Probe region spanning the
    /// whole file, seeded with no prior knowledge. See <see cref="DetectGapsAsync"/> for the
    /// gap-scoped alternative run after a --verify failure.
    /// </summary>
    /// <param name="file">Path of the audio file.</param>
    /// <param name="info">Probe result of the file.</param>
    /// <param name="work">Progress tracker fed with processed bytes.</param>
    /// <param name="log">This file's log sinks; default when nothing is listening.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task<DetectionResult> DetectAsync(
        string file, MediaInfo info, WorkTracker work, DetectionLog log, CancellationToken ct)
        => DetectCoreAsync(file, info, work, log, [], [],
            [new DetectionRegion(0, info.DurationSeconds, 0, null)], null, null, ct);

    /// <summary>
    /// Runs gap-scoped recovery after a --verify failure: <paramref name="verify"/>'s confirmed
    /// marks are trusted and imported directly, and only the region(s) <see
    /// cref="BuildGapRegions"/> builds around the unconfirmed one(s) get their own Probe - the
    /// rest of the file is not re-scanned or re-transcribed at all.
    /// </summary>
    /// <param name="file">Path of the audio file.</param>
    /// <param name="info">Probe result of the file.</param>
    /// <param name="work">Progress tracker fed with processed bytes.</param>
    /// <param name="log">This file's log sinks; default when nothing is listening.</param>
    /// <param name="verify">The --verify run's own result: <see cref="VerifyResult.ConfirmedChapters"/>
    /// seeds the result directly, <see cref="VerifyResult.Outcomes"/> is grouped into regions, and
    /// <see cref="VerifyResult.Profile"/>/<see cref="VerifyResult.DetectedLanguage"/>/<see
    /// cref="VerifyResult.DetectedProbability"/> are reused as-is so gap recovery never re-resolves
    /// the language.</param>
    /// <param name="ct">Cancellation token.</param>
    internal Task<DetectionResult> DetectGapsAsync(
        string file, MediaInfo info, WorkTracker work, DetectionLog log, VerifyResult verify, CancellationToken ct)
    {
        var plan = BuildGapRegions(verify.Outcomes, info.DurationSeconds);
        return DetectCoreAsync(file, info, work, log, verify.ConfirmedChapters,
            verify.NamedMarks ?? [], plan.Regions,
            new LanguageState(verify.Profile, verify.DetectedLanguage, verify.DetectedProbability),
            plan.TrailingFrom is { } from ? (from, plan.TrailingTargets) : null, ct);
    }

    /// <summary>
    /// Auto-resumes a file <see cref="MissingMarksTag.PathFor"/> tagged after a previous run
    /// left a chapter-sequence gap unresolved. The committed marks are trusted verbatim, with no
    /// --verify-style re-check against the audio: unlike <see cref="DetectGapsAsync"/>'s confirmed
    /// marks these were never in doubt in the first place - they are exactly what Scan settled
    /// on last time. Only the gap(s) <see cref="FindGaps"/> still finds between them get their own
    /// gap-scoped Probe plus the existing Scan tail, exactly as <see cref="DetectGapsAsync"/>
    /// does after a --verify failure - which is what lets this reuse <see cref="DetectCoreAsync"/>
    /// directly instead of a bespoke pipeline. A trailing region can never need recovering here: a
    /// tag only ever names chapters <see cref="FindGaps"/> itself flagged, which always means a gap
    /// bounded by two confirmed chapters (or the file start), so the one case it structurally cannot
    /// flag - a still-missing trailing chapter - never produces a tag to resume in the first place.
    /// </summary>
    /// <param name="file">Path of the audio file (still carrying its ".missing-marks-..." tag).</param>
    /// <param name="info">Probe result of the file, including its committed chapter marks.</param>
    /// <param name="work">Progress tracker fed with processed bytes.</param>
    /// <param name="log">This file's log sinks; default when nothing is listening.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<DetectionResult> ResumeMissingMarksAsync(
        string file, MediaInfo info, WorkTracker work, DetectionLog log, CancellationToken ct)
    {
        SetLog(log);
        var language = await ResolveProfileFromExistingMarksAsync(file, info, ct);
        var profile = language.Profile;
        _transcriber.ChangeLanguage(profile.Language);

        // Committed marks are trusted directly, never re-probed; only their chapter number
        // matters (parsed as --verify parses a mark's expected number). A mark with no
        // parseable number - the intro/prelude entry BuildChapters inserts - carries no chapter
        // identity and is dropped, exactly like an unparseable --verify mark.
        var confirmed = new List<DetectedChapter>();
        foreach (var mark in info.ExistingChapters)
            if (TryParseExpectedNumber(mark.Title, profile, out var number))
                confirmed.Add(new DetectedChapter(
                    number, mark.StartSeconds, Sequence: PartOf(mark.Title, profile)));
        confirmed = Normalize(confirmed);
        var namedSeed = CarryOverNamedMarks(info, profile);
        // Before the gap search below, which asks where the sequence starts: a prologue the previous
        // run wrote into the file speaks for that just as one detected fresh does.
        _namedMarks = namedSeed;

        // Gaps are re-derived from the committed marks rather than the tag's own number list, so
        // this always agrees with what FindGaps/MissingNumbersInGap would say about the file's
        // actual content right now. ExpectedStartChapter is passed through so a leading
        // missing-marks tag resolves to the same gap as the run that produced it.
        var regions = FindGaps(confirmed, info.DurationSeconds, ExpectedStartChapter)
            .Select(gap => new DetectionRegion(
                gap.FromSeconds, gap.ToSeconds,
                // Literally the rule MissingNumbersInGap reasons in, called rather than restated:
                // a gap opened by a restart is bounded below by the previous part's last chapter,
                // which is no lower bound at all for this part's numbering.
                LowerBoundNumber(confirmed, gap, ExpectedStartChapter),
                confirmed.First(c => c.TimeSeconds == gap.ToSeconds).Number, gap.Sequence))
            .ToList();

        return await DetectCoreAsync(
            file, info, work, log, confirmed, namedSeed, regions, language, null, ct);
    }

    /// <summary>
    /// Recovers the non-numbered marks a file already carries, by matching their titles
    /// against the ones this run would write. Both resume paths need it for the same reason: they
    /// rewrite the file's whole mark set from what detection hands back, and a named mark carries
    /// no chapter number - so unlike a chapter it would leave no hole behind, and nothing would ever
    /// notice it had been dropped. Matching on the title is what there is to match on: the mark's
    /// text is all a written chapter entry preserves, and this run's own titles are exactly what a
    /// previous run of the same command wrote there. A file marked by a different tool (or under
    /// different titles) simply yields nothing here, and its prologue is re-detected or lost exactly
    /// as before this existed.
    /// <para>
    /// A numbered mark and the intro entry are ruled out before any title is matched at all,
    /// because a --custom title made entirely of a capturing-group reference matches every string
    /// there is (see <see cref="NamedPhrase.TitleMatcher"/>) - without the exclusion, such a mapping
    /// would swallow the file's chapters into the named list and lose their numbers.
    /// </para>
    /// </summary>
    /// <param name="info">Probe result of the file, including its pre-existing chapter marks.</param>
    /// <param name="profile">The language profile resolved for this file, supplying the titles.</param>
    private static List<DetectedMark> CarryOverNamedMarks(MediaInfo info, LanguageProfile profile)
    {
        var carried = new List<DetectedMark>();
        foreach (var mark in info.ExistingChapters)
        {
            var title = mark.Title.Trim();
            if (TryParseExpectedNumber(title, profile, out _) ||
                string.Equals(title, profile.IntroTitle, StringComparison.OrdinalIgnoreCase))
                continue;
            if (profile.NamedPhrases.FirstOrDefault(p => p.TitleMatcher.IsMatch(title)) is { } phrase)
                carried.Add(new DetectedMark(
                    phrase.Kind, title, mark.StartSeconds,
                    PhraseTimeSeconds: mark.StartSeconds, Repeatable: phrase.Repeatable));
        }
        return carried;
    }

    /// <summary>
    /// The shared detection pipeline behind <see cref="DetectAsync"/> and <see
    /// cref="DetectGapsAsync"/>. Analyze always runs whole-file, even for a gap-scoped call: <see
    /// cref="IAudioSource"/> has no ranged silence/VAD scan, and redoing this one full-file decode
    /// is cheap next to the Whisper probing that follows. Probe then runs once per entry in
    /// <paramref name="regions"/>, each with its own candidates (built only from silences/VAD
    /// regions starting inside that region) and its own adaptive-threshold/adaptive-jingle-window
    /// state starting completely fresh - a region is probed as if it were its own small file, not a
    /// continuation of whatever an earlier region's Probe happened to learn. The sequence-gap
    /// Scan tail (over the accumulated <c>chapters</c> and the file's full duration) is the final
    /// net for any interior gap regardless of how <c>chapters</c> was seeded;
    /// <paramref name="trailingFallback"/> and the trailing scan exist only for the one case that
    /// tail structurally cannot catch - a still-missing chapter after the last one found, which
    /// nothing bounds from above to even notice.
    /// </summary>
    /// <param name="file">Path of the audio file.</param>
    /// <param name="info">The file's probed media info (duration, size, decoder).</param>
    /// <param name="work">Progress tracker for the phase/byte accounting.</param>
    /// <param name="log">This file's log sinks; default when nothing is listening.</param>
    /// <param name="confirmedSeed">Chapters trusted verbatim, with no Whisper re-check of their
    /// own - empty for a fresh <see cref="DetectAsync"/> run.</param>
    /// <param name="namedSeed">Prologue/epilogue marks carried over from the file's existing
    /// marks (see <see cref="CarryOverNamedMarks"/>); empty for a fresh run.</param>
    /// <param name="regions">The independent Probe region(s) to probe; a single whole-file region
    /// for <see cref="DetectAsync"/>, or the gap-scoped regions <see cref="BuildGapRegions"/> built
    /// for <see cref="DetectGapsAsync"/>.</param>
    /// <param name="known">The language resolution --verify already paid for, carried into the
    /// result verbatim; null to resolve this file's own from Analyze's speech segments.</param>
    /// <param name="trailingFallback">The trailing region's start and expected chapter numbers,
    /// when <see cref="BuildGapRegions"/> found the last checkable --verify mark unconfirmed;
    /// null otherwise (including for a fresh <see cref="DetectAsync"/> run).</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<DetectionResult> DetectCoreAsync(
        string file, MediaInfo info, WorkTracker work, DetectionLog log,
        IReadOnlyList<DetectedChapter> confirmedSeed, IReadOnlyList<DetectedMark> namedSeed,
        IReadOnlyList<DetectionRegion> regions, LanguageState? known,
        (double From, List<int> Targets)? trailingFallback, CancellationToken ct)
    {
        SetLog(log);
        _whisperAudioSeconds = 0;
        _whisperTranscribeSeconds = 0;
        _customLimitHit = false;
        _sequenceRestartSkips = 0;
        var bytesPerSecond = info.DurationSeconds > 0 ? info.SizeBytes / info.DurationSeconds : 0;

        var (allSilences, silences, nonSpeechRegions, speechSegments, jingles) =
            await RunAnalysisAsync(file, info, work, bytesPerSecond, ct);

        // Probing progress is position-based: the bar shows how far into the file's play time the
        // current candidate lies, not how many probes have run. Probe costs vary wildly (full
        // window decode vs. reused overlap vs. skipped candidate), so a fixed per-probe byte budget
        // drifts far off; position is honest about *where* the pass is, at the price of nonlinear -
        // and, during gap re-probes, briefly backwards - movement.
        //
        // Begun before the language is resolved, which reports no progress of its own: the bar
        // would otherwise sit at a finished Analyze for the several seconds that takes. A
        // jingle-first file relabels this to J-probe below, once the shape is known.
        //
        // The census stands in for the candidate list, which does not exist yet: a walk with no
        // music in it is named for what it does read (see PhaseNames.ChronologicalProbe). The two
        // are built by different rules - the census bridges its stretches across short silences,
        // candidates come one per non-speech region - so they could in principle disagree about a
        // marginal file. Only the label is at stake, and this is the last moment before the several
        // seconds of silence the bar would otherwise sit through.
        //
        // Which pieces of the book this Probe covers: all of it on a fresh run (so no stretch is
        // marked out at all), the gaps alone on a --verify or missing-marks recovery. Held in one
        // variable because the two shape-specific re-begins below have to hand the bar exactly the
        // same stretches - a re-begin that quietly dropped them would leave a recovery's gaps
        // unmarked.
        var probeSpans = BarSpans(
            [.. regions.Select(r => (r.FromSeconds, r.ToSeconds))], info.DurationSeconds, bytesPerSecond);
        work.BeginPhase(
            jingles.Count > 0 ? PhaseNames.Probe : PhaseNames.ChronologicalProbe,
            info.SizeBytes, probeSpans);

        // The language is settled here, before a single probe runs, and fixed via ChangeLanguage
        // rather than re-detected per window - it belongs to the file, not to a region. Resolving it
        // needs Analyze's speech segments, which is the whole reason it happens at this exact point:
        // sampling narration instead of the file's opening seconds is what LanguageResolver is for.
        // A gap-scoped run already knows the answer from --verify, so `known` seeds it unprobed.
        var language = known ?? await NewLanguageResolver().ResolveAsync(
            file, info, LanguageResolver.SpeechPositions(speechSegments, info.DurationSeconds), ct);
        _transcriber.ChangeLanguage(language.Profile.Language);

        // Confirmed marks are trusted verbatim; new finds from every region below are added to
        // the same list, so Scan's existing gap tail (after the region loop) sees one seamless
        // sequence regardless of which numbers came from --verify and which from fresh probing.
        var found = new List<DetectedChapter>(confirmedSeed);
        // The named marks travel alongside rather than inside `found`: they have no chapter number,
        // and everything below - gaps, sequence progress, Re-probe's targets - reasons in numbers.
        var namedFound = new List<DetectedMark>(namedSeed);
        // The list itself, not a copy: ExpectedStartChapter reads it whenever it is asked, and a
        // prologue found halfway through Probe has to count from that moment on.
        _namedMarks = namedFound;

        // --early-abort (0 disables it): once Probe has probed this many minutes of play time
        // without finding a single chapter, further probing is pointless - give up rather than
        // transcribe the rest of a book that plainly will not yield any (wrong --chapter-phrase,
        // wrong --lang, or one that announces chapters differently). Only meaningful for a fresh
        // run: confirmedSeed is always non-empty for a --verify gap recovery or a ".missing-marks"
        // resume, and infinity disables the check outright for those.
        var earlyAbortSeconds = _options.EarlyAbortMinutes > 0 && confirmedSeed.Count == 0
            ? _options.EarlyAbortMinutes * 60
            : double.PositiveInfinity;

        // --expected-start-chapter's abort half, restricted to fresh runs for the same reason: with
        // a seeded chapter the "first chapter found" it guards is never the file's very first.
        // Null disables the check, as +infinity does above.
        var expectedStartChapter = confirmedSeed.Count == 0 ? _options.ExpectedStartChapter : null;

        // Only what the fidelity check would need, and no measuring yet: most files never garble an
        // announcement, and the ones that do not should not pay to be told so.
        _denoiser?.Dispose();
        _denoiser = null;
        _denoiserDecided = false;
        _denoiseSource = (file, info.DurationSeconds, info.InputDecoder);

        var probeCtx = new ProbeContext(
            file, info, work, bytesPerSecond,
            allSilences, silences, nonSpeechRegions, speechSegments, jingles,
            earlyAbortSeconds, expectedStartChapter, _transcriber,
            _options.AdaptiveFloorSeconds);

        // Whether this file's music is enough of its structure to be read on its own first, and its
        // pauses only where the sequence still wants one - see JingleFirstScan, which also decides
        // what to say about it in the log.
        var jingleFirst = JingleFirstScan.Decide(
            _options, jingles, info.DurationSeconds, language.Profile,
            freshRun: confirmedSeed.Count == 0 && regions.Count == 1);
        if (jingleFirst.Note is { } shapeNote)
            _log?.Invoke(shapeNote);

        // A file the music-first shape turned down may still have its longest pauses read first,
        // which is the same trade over the other candidate class - see DescendingSilenceScan.
        var descending = DescendingSilenceScan.Decide(
            _options, language.Profile,
            freshRun: confirmedSeed.Count == 0 && regions.Count == 1, jingleFirst.Run);
        if (descending.Note is { } pauseNote)
            _log?.Invoke(pauseNote);

        // The label is the shape, which is the one thing about this pass a watcher cannot
        // otherwise see: a jingle-first file reads its music under J-probe and the pauses it
        // deferred under S-probe, a descending one skims its longest pauses under SD-probe before
        // the walk itself. An ordinary file keeps the label begun above throughout. Re-beginning
        // resets the bar to zero, which is where it already is.
        if (jingleFirst.Run)
            work.BeginPhase(PhaseNames.JingleProbe, info.SizeBytes, probeSpans);
        else if (descending.Run)
            work.BeginPhase(PhaseNames.DescendingProbe, info.SizeBytes, probeSpans);

        // The shortest chapter break any region measured, which is what decides whether the sweep
        // below has anything to do. Taken across regions rather than per region: it is a statement
        // about the narrator, and a region that found only one mark measured nothing at all.
        var scan = await ProbeRegionsAsync(
            probeCtx, regions, found, namedFound, language,
            jingleFirst.Run ? ProbeShape.JinglesOnly
                : descending.Run ? ProbeShape.SilencesDescending
                : ProbeShape.Everything,
            ct);

        // The pauses the jingle half deferred, over the stretches it left unsettled. Not run after
        // an abort: both of them mean this file is not being detected at all, and the pauses cannot
        // change that verdict - --early-abort's own is the one this half's head stretch would
        // deliver anyway. The descending shape needs none of this: it defers nothing, it only
        // decides which candidates its own one walk bothers to read (see RegionProber's
        // GatherThenResolveAsync, and why it is one walk rather than two).
        if (jingleFirst.Run && !scan.EarlyAborted && scan.BelowExpectedStartNumber == null)
            scan = scan.And(await ProbePausesAfterJinglesAsync(
                probeCtx, regions[0], found, namedFound, language, bytesPerSecond, ct));

        var (earlyAborted, belowExpectedStartNumber, measuredBreakSeconds) = scan;

        if (!earlyAborted && belowExpectedStartNumber == null && !_options.IgnoreChapterNumbers)
            await SweepAdaptiveSubFloorAsync(
                probeCtx, found, namedFound, language, measuredBreakSeconds, ct);

        var chapters = await ReconcileSequenceAsync(found, namedFound, probeCtx, language.Profile, ct);
        // The stage drops marks as well as renumbering them, and what it leaves is what gap planning
        // is about to measure the book against - so the bar should be showing that sequence and not
        // the one the last mark placement reported.
        RefreshChapterProgress(work, chapters);
        _log?.Invoke("Probe finished");

        // The Re-probe and Scan passes exist only to close holes in the chapter-number sequence, so with
        // --ignore-chapter-numbers there is nothing for either of them to chase: Probe already
        // probed every candidate the file has, and no gap can be defined without numbers to be
        // missing from.
        var probeCompleted = !earlyAborted && belowExpectedStartNumber == null;
        if (!_options.IgnoreChapterNumbers)
        {
            if (probeCompleted)
                chapters = await RunReprobeAsync(file, info, work, chapters, namedFound,
                    allSilences, silences, nonSpeechRegions, speechSegments, jingles, bytesPerSecond,
                    language.Profile, ct);

            chapters = await RunScanAsync(file, info, work, chapters, allSilences, nonSpeechRegions,
                speechSegments, bytesPerSecond, language.Profile, trailingFallback, probeCompleted, ct);

            // Again here, having already run at the end of Probe (see ReconcileSequenceAsync):
            // this is the only point that can compare what every pass made of the same
            // announcement, and two passes reading one announcement under two different numbers
            // leave two marks on top of each other that are only now both in the same list.
            chapters = await ReconcileCollidingMarksAsync(
                chapters, namedFound, probeCtx, language.Profile, ct);
        }
        // Last of all, because settling a collision removes a mark and the numbers under it may
        // have become missing: nothing after this point reports progress, so without it the bar's
        // final reading would be the one taken before the sequence was whole.
        RefreshChapterProgress(work, chapters);

        // The ONNX session belongs to this file: a batch would otherwise carry one file's session
        // through every later file that never asks for one.
        _denoiser?.Dispose();
        _denoiser = null;

        return BuildDetectionResult(
            chapters, namedFound, speechSegments, language.Profile, language.DetectedLanguage,
            language.DetectedProbability, earlyAborted, belowExpectedStartNumber);
    }

    /// <summary>
    /// What one sweep of Probe regions reported back - the two verdicts that end detection for a
    /// file, plus the one measurement the sub-floor sweep after Probe reads. Gathered into a record
    /// because Probe now runs the same loop twice on a jingle-first file, and the second run has to
    /// combine its answers with the first's rather than replace them.
    /// </summary>
    /// <param name="EarlyAborted">Whether --early-abort fired.</param>
    /// <param name="BelowExpectedStartNumber">The first chapter number found, when it sat below
    /// --expected-start-chapter and detection was abandoned; null otherwise.</param>
    /// <param name="MeasuredBreakSeconds">The shortest chapter break any region measured, or null
    /// where nothing qualified.</param>
    private readonly record struct ProbeOutcome(
        bool EarlyAborted, int? BelowExpectedStartNumber, double? MeasuredBreakSeconds)
    {
        /// <summary>This outcome together with a later sweep's, for the file as a whole: either
        /// verdict stands wherever it was reached, and the measurement is the shorter of the two -
        /// it is a statement about the narrator and belongs to the file, not to the sweep that
        /// happened to hear it.</summary>
        /// <param name="next">The later sweep's outcome.</param>
        internal ProbeOutcome And(ProbeOutcome next)
            => new(EarlyAborted || next.EarlyAborted,
                   BelowExpectedStartNumber ?? next.BelowExpectedStartNumber,
                   MeasuredBreakSeconds is { } mine
                       ? next.MeasuredBreakSeconds is { } theirs ? Math.Min(mine, theirs) : mine
                       : next.MeasuredBreakSeconds);
    }

    /// <summary>
    /// Probes a list of Probe regions in order, stopping at the first one that gives up on the
    /// file. The marks land in the accumulators; what comes back is what the caller has to act on.
    /// </summary>
    /// <param name="ctx">The file's Probe context.</param>
    /// <param name="regions">The regions to probe, in file order.</param>
    /// <param name="found">Accumulator of confirmed chapters.</param>
    /// <param name="namedFound">Accumulator of the file's named marks.</param>
    /// <param name="language">The file's settled language resolution.</param>
    /// <param name="shape">Which of each region's candidates to walk; see <see cref="ProbeShape"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<ProbeOutcome> ProbeRegionsAsync(
        ProbeContext ctx, IReadOnlyList<DetectionRegion> regions,
        List<DetectedChapter> found, List<DetectedMark> namedFound, LanguageState language,
        ProbeShape shape, CancellationToken ct)
    {
        var outcome = new ProbeOutcome(false, null, null);
        foreach (var region in regions)
        {
            var prober = new RegionProber(
                BuildProbeEnvironment(), ctx, region, found, namedFound, language, shape: shape);
            await prober.RunAsync(ct);
            _customLimitHit |= prober.CustomLimitHit;
            _sequenceRestartSkips += prober.SequenceRestartSkips;
            outcome = outcome.And(new ProbeOutcome(
                prober.EarlyAborted, prober.BelowExpectedStartNumber, prober.AdaptedThresholdSeconds));

            if (outcome.EarlyAborted || outcome.BelowExpectedStartNumber != null)
                break;
        }
        return outcome;
    }

    /// <summary>
    /// The second half of a jingle-first Probe: the pauses of every stretch the jingle half left
    /// unsettled - the head of the file, any hole in the chapter numbering, and the tail. See
    /// <see cref="JingleFirstScan"/> for why the pauses in between are the ones that can be skipped.
    /// <para>
    /// The descending shape has no counterpart to this and wants none: it defers no conclusions, so
    /// there is never a stretch left unsettled by it - see
    /// <see cref="DescendingSilenceScan"/>.
    /// </para>
    /// <para>
    /// A phase of its own, because the run really is looking at the book a second time, over a
    /// fraction of it - which is what the bar shows: the stretches marked out on it, and the fill
    /// back at the first of them.
    /// </para>
    /// <para>
    /// Each stretch is walked by an ordinary prober in the primary scan's own framing, not a
    /// recovery pass's trimmed one. Nothing has read these pauses yet, and the trimmed framing exists
    /// to ask a <em>differently</em> framed question of audio that already came back empty (see
    /// <see cref="DetectionTuning.RecoveryLeadInTrimSeconds"/>) - the wrong instrument for a first
    /// look, and one that would quietly downgrade every pause-announced chapter in the file.
    /// </para>
    /// <para>
    /// What the split does cost is the transcript overlap cache, which belongs to one prober: a
    /// pause window neighbouring a jingle window no longer picks up what that decode read ahead, and
    /// the audio at such a junction is read twice. Small against what the shape saves - a jingle book
    /// has one junction per chapter and thousands of pauses - and it also shows up as a mark's
    /// confidence differing from the one the ordinary shape reports for the same position, the
    /// window around it having been framed differently.
    /// </para>
    /// </summary>
    /// <param name="ctx">The file's Probe context.</param>
    /// <param name="region">The region both halves walk - the whole file, this shape being
    /// restricted to a fresh run.</param>
    /// <param name="found">Accumulator of confirmed chapters, holding the jingle half's finds.</param>
    /// <param name="namedFound">Accumulator of the file's named marks.</param>
    /// <param name="language">The file's settled language resolution.</param>
    /// <param name="bytesPerSecond">The file's play time to progress-byte rate.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<ProbeOutcome> ProbePausesAfterJinglesAsync(
        ProbeContext ctx, DetectionRegion region,
        List<DetectedChapter> found, List<DetectedMark> namedFound, LanguageState language,
        double bytesPerSecond, CancellationToken ct)
    {
        var stretches = JingleFirstScan.UnsettledStretches(
            found, region, _options.ExpectedStartChapter ?? 1);
        var stretchSeconds = stretches.Sum(s => s.ToSeconds - s.FromSeconds);
        _log?.Invoke(
            $"J-probe finished, {found.Count} chapter(s) out of the music - " +
            $"now the pauses of {stretches.Count} stretch(es), " +
            $"{FormatLength(stretchSeconds)} of {FormatLength(region.ToSeconds - region.FromSeconds)}");
        foreach (var stretch in stretches)
            _log?.Invoke(
                $"S-probe: {FormatTimestamp(stretch.FromSeconds)}-{FormatTimestamp(stretch.ToSeconds)}, " +
                (stretch.LowerNumber > 0
                    ? $"above chapter {stretch.LowerNumber}"
                    : "before every chapter found") +
                (stretch.UpperNumber is { } upper
                    ? $" and below chapter {upper}"
                    : " and open at the top"));
        if (stretchSeconds <= 0)
            return new ProbeOutcome(false, null, null);

        ctx.Work.BeginPhase(
            PhaseNames.SilenceProbe, ctx.Info.SizeBytes,
            BarSpans([.. stretches.Select(s => (s.FromSeconds, s.ToSeconds))],
                     ctx.Info.DurationSeconds, bytesPerSecond));
        return await ProbeRegionsAsync(
            ctx, stretches, found, namedFound, language, ProbeShape.SilencesOnly, ct);
    }

    /// <summary>
    /// Decides, once per file and only when something asks, whether a garbled announcement here may
    /// be re-read through the speech denoiser.
    /// <para>
    /// The check is a permission, not a diagnosis. What actually asks is a probe window that heard a
    /// chapter number without the word beside it
    /// (<see cref="RegionProber.RereadDenoisedAsync"/>); this only decides whether that request may
    /// be granted, and it exists so a book with plenty of treble - where the failure has never been
    /// observed - does not pay for the possibility. The threshold is therefore set well clear of the
    /// books that motivated the work rather than between them: granting it to a clean file costs
    /// nothing, since the trigger still has to fire, while refusing a dark one silently loses a
    /// chapter. See <see cref="AudioFidelity"/> for what the measure is and what it is not.
    /// </para>
    /// <para>
    /// Lazy rather than run up front, which is what keeps it free: a file whose announcements all
    /// come through cleanly never reaches this at all, and so never spends the
    /// <see cref="FidelityExcerpts"/> decodes on being told it did not need to. The decision is
    /// cached either way, so a book with many garbled windows measures once.
    /// </para>
    /// <para>
    /// Sampled at <see cref="FidelityExcerpts"/> positions spread over the file rather than one
    /// stretch: within a single book the measure moves several-fold between excerpts, so one look
    /// decides nothing and the median of several is the smallest honest reading.
    /// </para>
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The denoiser to re-read through, or null when this file may not be denoised.</returns>
    private async Task<SpeechDenoiser?> DenoiserForFileAsync(CancellationToken ct)
    {
        if (_denoiserDecided)
            return _denoiser;
        _denoiserDecided = true;
        if (!_options.Denoise || _denoiseSource is not { } source || source.DurationSeconds <= 0)
            return null;

        var excerpts = new List<double?>();
        for (var i = 0; i < FidelityExcerpts; i++)
        {
            // Spread over the middle 80%, keeping front and back matter - credits, a publisher's
            // card, closing music - out of a measurement meant to describe the narration.
            var at = source.DurationSeconds * (0.10 + 0.80 * i / (FidelityExcerpts - 1.0));
            var length = Math.Min(FidelityExcerptSeconds, source.DurationSeconds - at);
            if (length < 1)
                continue;
            var samples = await _audio.DecodePcmAsync(source.File, at, length, source.Decoder, ct);
            excerpts.Add(AudioFidelity.Measure(samples, SpeechDenoiser.SampleRate));
        }

        var fidelity = AudioFidelity.Combine(excerpts);
        if (fidelity is not { } measured)
        {
            _log?.Invoke("denoiser: could not measure this file's fidelity - not denoising");
            return null;
        }
        if (measured >= AudioFidelity.Threshold)
        {
            _log?.Invoke(
                $"denoiser: high-frequency ratio {measured:0.#####} at or above " +
                $"{AudioFidelity.Threshold:0.#####} - clear enough, not denoising");
            return null;
        }
        _log?.Invoke(
            $"denoiser: high-frequency ratio {measured:0.#####} below " +
            $"{AudioFidelity.Threshold:0.#####} - a garbled announcement may be re-read denoised");
        return _denoiser = new SpeechDenoiser();
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
            LogTranscript,
            (segments, profile, mergeBoundary, reading) =>
                FindCappedPhraseReadings(segments, profile, mergeBoundary, reading),
            _options.UpgradeModelIsBetter && !ReferenceEquals(_upgradeTranscriber, _transcriber)
                ? SecondOpinionAsync
                : null,
            DenoiserForFileAsync);

    /// <summary>
    /// The same for every <see cref="RegionScanner"/> of the current file, and built per pass for
    /// the same reason: <see cref="_marks"/> and <see cref="_log"/> are per-file.
    /// </summary>
    private ScanEnvironment BuildScanEnvironment()
        => new(_options, _audio, _vad, _log, _marks!, _upgradeTranscriber,
            TranscribeCountingAsync, LogTranscript, FindCappedPhraseMatches);

    /// <summary>
    /// The file's measurements as <see cref="RegionScanner"/> reads them, gathered once per Scan
    /// pass rather than threaded through every region.
    /// </summary>
    /// <remarks>
    /// <see cref="ExpectedStartChapter"/> is a live property everywhere else, because a prologue
    /// found halfway through Probe has to count from that moment on (see <see cref="_namedMarks"/>).
    /// Reading it into the context freezes it, which is sound only because every
    /// <see cref="RegionProber"/> - the one thing that can accept a named mark - has finished by the
    /// time Scan begins; Scan itself records numbered chapters and nothing else. Should a later pass
    /// ever gain a named-mark route, this is the line that has to become live again.
    /// </remarks>
    /// <param name="file">Path of the audio file.</param>
    /// <param name="info">Probe result of the file.</param>
    /// <param name="work">Progress tracker.</param>
    /// <param name="bytesPerSecond">The file's average byte rate, for progress reporting.</param>
    /// <param name="allSilences">Every silence Analyze retained.</param>
    /// <param name="nonSpeechRegions">VAD non-speech regions, empty when the pre-pass did not run.</param>
    /// <param name="speechSegments">VAD speech segments, empty when the pre-pass did not run.</param>
    /// <param name="profile">The language profile resolved for this file.</param>
    private ScanContext BuildScanContext(
        string file, MediaInfo info, WorkTracker work, double bytesPerSecond,
        List<Silence> allSilences, List<NonSpeechRegion> nonSpeechRegions,
        List<SpeechSegment> speechSegments, LanguageProfile profile)
        => new(file, info, work, bytesPerSecond, allSilences, nonSpeechRegions, speechSegments,
            profile, _jingleReachSeconds, ExpectedStartChapter);

    /// <summary>
    /// Re-reads the progress line's two chapter counts off the sequence as it now stands. Every
    /// step that can change which chapter numbers exist ends in this call, which is the point of
    /// its being one: the counts are derived from the sequence rather than tallied as marks arrive,
    /// so a step that removes or renumbers a mark and forgets to refresh leaves the bar reporting a
    /// book that no longer exists - and a mark <em>removed</em> after the last refresh cannot be
    /// noticed at all, the display having nothing to count down from.
    /// </summary>
    /// <param name="work">The file's progress tracker.</param>
    /// <param name="chapters">The chapter sequence as it now stands.</param>
    private void RefreshChapterProgress(WorkTracker work, List<DetectedChapter> chapters)
    {
        var (highest, missingNumbers) = ChapterProgress(chapters, ExpectedStartChapter);
        work.HighestChapters = highest;
        work.MissingChapters = missingNumbers.Count;
    }

    /// <summary>
    /// Puts <c>--verify</c>'s tally of confirmed named marks on the progress line, where it shows
    /// as the same "(+N)" a detection run's extra marks do.
    /// </summary>
    /// <param name="work">The file's progress tracker.</param>
    /// <param name="confirmed">How many named marks this file has confirmed so far.</param>
    /// <remarks>
    /// <para>
    /// It exists because the bar is the only thing a long verification says while it runs. A user
    /// watching a prologue, an epilogue or a <c>--custom</c> mark they know to be good go past with
    /// the counts unmoved has nothing to distinguish a confirmation from a failure, and will assume
    /// the worse of the two.
    /// </para>
    /// <para>
    /// <b>Confirmations only, and never a "(-N)".</b> A named mark that cannot be confirmed leaves
    /// the count exactly where it was: subtracting would make a book with one bad epilogue report
    /// fewer good marks than it has, and that bracket's negative half counts chapters missing from
    /// a sequence, which gap recovery goes on to chase - a named mark opens no gap and nothing will
    /// be done about it, so borrowing the notation would promise work that is not coming (see
    /// <see cref="NamedMarkOutcome"/>). This changes what the line shows and nothing else:
    /// <see cref="VerifyResult.Checked"/>, <see cref="VerifyResult.Failed"/> and the
    /// wholesale-failure ratio are all as they were.
    /// </para>
    /// <para>
    /// Both counters move together because every named mark <c>--verify</c> can confirm is an extra
    /// one: <see cref="CheckNamedMarkAsync"/> recognizes a mark only through
    /// <see cref="LanguageProfile.NamedPhrases"/>, which never holds the chapter announcement
    /// (<see cref="LanguageProfile.ChapterAnnouncement"/> is a separate thing Probe uses for
    /// <c>--ignore-chapter-numbers</c>). Keeping them equal preserves
    /// <see cref="WorkTracker.ExtraMarks"/>'s meaning as a subset of
    /// <see cref="WorkTracker.NamedMarks"/>.
    /// </para>
    /// <para>
    /// A gap recovery that follows cannot make the number fall back: <see cref="RegionProber"/>
    /// recomputes both counts off a list seeded with <see cref="CarryOverNamedMarks"/>, which
    /// recognizes marks by the same title matcher this counted them by - so what was confirmed here
    /// is always among what is seeded there.
    /// </para>
    /// </remarks>
    private static void RefreshNamedProgress(WorkTracker work, int confirmed)
    {
        work.NamedMarks = confirmed;
        work.ExtraMarks = confirmed;
    }

    /// <summary>
    /// The stretches a pass is about to work, as the spans its phase is begun with (see
    /// <see cref="WorkTracker.PhaseSpans"/>) - or null where they cover the book end to end, a bar
    /// tinted from edge to edge saying nothing a plain one does not.
    /// </summary>
    /// <param name="stretches">The stretches of play time, in file order.</param>
    /// <param name="durationSeconds">The file's play time, against which "the whole book" is
    /// decided.</param>
    /// <param name="bytesPerSecond">The file's play time to progress-byte rate.</param>
    /// <remarks>
    /// The whole-book exemption is the rule <see cref="RegionProber"/> applies to the current-region
    /// highlight, restated here for the phase's own stretches so the two cannot disagree about
    /// whether a pass is working a piece of the book. Probe is the pass that needs it: its regions
    /// are the whole file on a fresh run and the gaps on a --verify recovery, from the one call.
    /// </remarks>
    private static IReadOnlyList<(long FromBytes, long ToBytes)>? BarSpans(
        IReadOnlyList<(double FromSeconds, double ToSeconds)> stretches,
        double durationSeconds, double bytesPerSecond)
        => stretches.Count == 1 &&
           stretches[0].FromSeconds <= 0 && stretches[0].ToSeconds >= durationSeconds
            ? null
            : [.. stretches.Select(s => WorkTracker.Span(s.FromSeconds, s.ToSeconds, bytesPerSecond))];

    /// <summary>
    /// The pipeline stage between Probe and Re-probe: settles the marks that landed on top of each
    /// other, hands <see cref="RepairSequenceOutliersAsync"/> a re-read backed by this file's decoder
    /// and recognizer, then clears out <see cref="DropNamedMarkEchoes"/>'s phantoms. Everything each
    /// step actually decides lives there.
    /// <para>
    /// The echo sweep runs here as well as after Scan because this is the last moment it is free:
    /// a phantom left in the sequence at this point is what gap planning measures the book against,
    /// and one implausible number is enough to commit Re-probe and Scan to transcribing everything
    /// behind it.
    /// </para>
    /// <para>
    /// <b>Collisions are settled here as well as after Scan for a stronger reason than cost, and
    /// settled first within this stage.</b> One Probe window can produce two marks on its own - a
    /// recognizer that repeats an announcement under the next number leaves both at the same onset -
    /// and from that moment the phantom holds a chapter number the real chapter still has to be
    /// found under. It bars that chapter from Probe ("not above the last accepted"), from Scan (the
    /// number is already known), and from <see cref="GapPlanning.Normalize"/>, whose filter must drop
    /// one of two marks carrying the same number and resolves the tie toward the earlier one. Waiting
    /// until after Scan means every one of those has already happened. Settling before
    /// <see cref="RepairSequenceOutliersAsync"/> matters for the same reason: the repair passes over
    /// a dropped mark whose number a kept one still holds, so with the phantom gone first it is the
    /// real chapter that survives the filter rather than the one that has to be repaired back in.
    /// </para>
    /// </summary>
    /// <remarks>Notes: the book that lost a chapter to a hallucinated duplicate, and the three
    /// rescues the duplicate defeated.
    /// <include file='../../notes/Detection/ChapterDetector.xml' path='doc/member[@name="ReconcileSequenceAsync"]/*' /></remarks>
    /// <param name="found">Probe's raw detections, in any order.</param>
    /// <param name="named">The file's prologue/epilogue/--custom marks, complete once Probe has
    /// finished every region.</param>
    /// <param name="ctx">The file's Probe context, for the re-read's decoding and recognition.</param>
    /// <param name="profile">The file's resolved language profile, or null when --lang auto never
    /// got as far as resolving one (nothing was probed, so nothing was transcribed). The repair's
    /// sequence arithmetic still runs then; only the step that would re-read audio is withheld,
    /// since there is no phrase to look for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The ascending chapter sequence, including whatever was repaired back into it.</returns>
    private async Task<List<DetectedChapter>> ReconcileSequenceAsync(
        List<DetectedChapter> found, IReadOnlyList<DetectedMark> named, ProbeContext ctx,
        LanguageProfile? profile, CancellationToken ct)
    {
        // Bound once and shared: both rules ask the audio the same question through the same
        // mender, and building a second one per stage would decode with a second set of state.
        var reread = ReReadAtMark(ctx, profile);
        // Ordered rather than normalized, because settling has to happen before anything filters:
        // ordering is all SettleCollidingMarksAsync needs, and by the same key Normalize uses, so
        // a colliding pair meets it in the order it would meet the filter in.
        var settled = await SettleCollidingMarksAsync(
            found.OrderBy(c => c.TimeSeconds).ThenBy(c => c.Number).ToList(),
            ExpectedStartChapter, _log, reread, ct);
        var repaired = await RepairSequenceOutliersAsync(
            settled, ExpectedStartChapter, _log, reread, ct);
        return DropNamedMarkEchoes(repaired, named, ExpectedStartChapter, _log);
    }

    /// <summary>
    /// The "ask the audio what number this mark really carries" delegate both reconciliation stages
    /// hand their rule, bound to this file's decoder and recognizer. A whole-file region: the
    /// re-framings clip themselves to it, a mark in doubt can sit anywhere, and what actually
    /// constrains a re-read is the bounds passed per mark.
    /// </summary>
    /// <param name="ctx">The file's Probe context, for decoding and recognition.</param>
    /// <param name="profile">The file's resolved language profile, or null when nothing was ever
    /// transcribed - there is then no phrase to look for, so the delegate answers nothing and the
    /// stages fall back on sequence arithmetic alone.</param>
    private Func<DetectedChapter, NumberBounds, CancellationToken, Task<int?>> ReReadAtMark(
        ProbeContext ctx, LanguageProfile? profile)
    {
        if (profile is not { } resolved)
            return (_, _, _) => Task.FromResult((int?)null);

        var mender = new SuspectNumberMender(
            BuildProbeEnvironment(), ctx,
            new DetectionRegion(0, ctx.Info.DurationSeconds, 0, null));
        return (mark, bounds, token) => mender.ReReadAtMarkAsync(
            mark.TimeSeconds, _options.MarkLeadSeconds, resolved, bounds, mark.Number, token);
    }

    /// <summary>
    /// The pipeline stage after Scan: hands <see cref="SettleCollidingMarksAsync"/> a re-read
    /// backed by this file's decoder and recognizer, exactly as
    /// <see cref="ReconcileSequenceAsync"/> does for the sequence repair.
    /// </summary>
    /// <param name="chapters">The chapter sequence every pass has finished with.</param>
    /// <param name="named">The file's prologue/epilogue/--custom marks.</param>
    /// <param name="ctx">The file's Probe context, for the re-read's decoding and recognition.</param>
    /// <param name="profile">The file's resolved language profile, or null when nothing was ever
    /// transcribed - in which case there is no phrase to re-read and the confidence tiebreak decides
    /// alone.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<List<DetectedChapter>> ReconcileCollidingMarksAsync(
        List<DetectedChapter> chapters, IReadOnlyList<DetectedMark> named, ProbeContext ctx,
        LanguageProfile? profile, CancellationToken ct)
    {
        var settled = await SettleCollidingMarksAsync(
            chapters, ExpectedStartChapter, _log, ReReadAtMark(ctx, profile), ct);
        // Again rather than only before Re-probe, because Scan places marks of its own and a gap
        // reaching into a named announcement is exactly where it would place one.
        return DropNamedMarkEchoes(settled, named, ExpectedStartChapter, _log);
    }

    /// <summary>
    /// Settles two chapter marks that landed on top of each other, which is one announcement read
    /// under two different numbers rather than two chapters
    /// (see <see cref="DetectionTuning.CollidingChapterMarkSeconds"/> for the case on record).
    /// <para>
    /// This is the one misreading none of the other four defences can touch, and the reason is worth
    /// stating: they all reason about whether a number <em>fits the sequence</em>, and a number
    /// misheard as its own neighbour fits perfectly. A chapter 13 read as 12 continues the sequence,
    /// so <see cref="SuspectNumberMender"/> is never invoked, the longest-increasing-subsequence
    /// filter in <see cref="GapPlanning.Normalize"/> keeps both marks (12 then 13 ascends in both
    /// time and number), and <see cref="RepairSequenceOutliersAsync"/> sees no outlier to repair.
    /// What gives it away is not arithmetic at all but geometry: two chapters cannot begin a
    /// hundredth of a second apart.
    /// </para>
    /// <para>
    /// Which of the two numbers is right cannot be decided from the sequence either - both sit
    /// between the same neighbours, so both are admissible - and it cannot be decided by which pass
    /// found it, since the mirror case swaps the roles: read 12 as 13 instead and it is Probe that
    /// is wrong, the real chapter 13 further on is rejected as "not above the last accepted", and
    /// every mark after it carries a number one too high. So the audio is asked again, in the tightly
    /// framed windows <see cref="SuspectNumberMender.ReReadAtMarkAsync"/> uses, with the pair's own
    /// neighbours as the acceptance rule.
    /// </para>
    /// <para>
    /// When the re-read settles nothing, the pair's own bounds are asked first: where they admit one
    /// of the two numbers and not the other, the pair is not two readings of one announcement but a
    /// real chapter with something spurious beside it, and the answer is the number the book can
    /// hold. Only when both are admissible - the shape this rule exists for, since both sit between
    /// the same neighbours - does the higher-confidence reading survive. That last step is a tiebreak
    /// and not evidence, and it is documented as one, but it beats keeping both: that leaves a player
    /// two chapter entries at the same position and hands the file a chapter number the rest of the
    /// book then has to live with.
    /// </para>
    /// <para>
    /// The one step that touches audio is a delegate rather than a call, so the rule itself can be
    /// tested without a decoder, a recognizer or a file. Internal for that reason.
    /// </para>
    /// </summary>
    /// <param name="chapters">The chapter sequence in time order - as Probe leaves it, and again
    /// as every pass leaves it (see <see cref="ReconcileSequenceAsync"/> for why both).</param>
    /// <param name="expectedStartChapter">--expected-start-chapter, or null; the lower bound for a
    /// collision with no chapter before it.</param>
    /// <param name="log">Sink for --verbose log messages, or null when not verbose.</param>
    /// <param name="reread">Asks the audio which number the announcement at the given mark really
    /// carries, holding the answer to the given bounds; called at most
    /// <see cref="DetectionTuning.MaxSequenceRepairsPerFile"/> times per invocation - the budget
    /// is per call, and a file runs this twice.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The sequence with every collision reduced to one mark.</returns>
    internal static async Task<List<DetectedChapter>> SettleCollidingMarksAsync(
        List<DetectedChapter> chapters, int? expectedStartChapter, Action<string>? log,
        Func<DetectedChapter, NumberBounds, CancellationToken, Task<int?>> reread,
        CancellationToken ct)
    {
        var settled = new List<DetectedChapter>(chapters);
        var rereads = 0;
        for (var i = 1; i < settled.Count; i++)
        {
            var (first, second) = (settled[i - 1], settled[i]);
            if (second.TimeSeconds - first.TimeSeconds >= CollidingChapterMarkSeconds)
                continue;
            // Two marks in different parts are two announcements whatever their spacing: the whole
            // premise here is "one announcement read as two numbers", and two numbers from two
            // different counts were never readings of each other. There is also no bound that could
            // settle them, both being admissible in their own part.
            if (first.Sequence != second.Sequence)
                continue;

            ct.ThrowIfCancellationRequested();
            log?.Invoke(
                $"chapters {first.Number} and {second.Number} " +
                $"{second.TimeSeconds - first.TimeSeconds:0.00} s apart at " +
                $"{FormatTimestamp(first.TimeSeconds)} - one announcement read two ways");

            // The pair's own members are excluded so the bounds describe the room the rest of the
            // book leaves at this position, which is what both candidates have to fit into.
            var others = settled.Where((_, index) => index != i && index != i - 1).ToList();
            var bounds = BracketingBounds(
                first.TimeSeconds, others, [], expectedStartChapter, first.Sequence);

            int? settledNumber = null;
            if (rereads < MaxSequenceRepairsPerFile)
            {
                rereads++;
                settledNumber = await reread(first, bounds, ct);
            }

            DetectedChapter winner;
            if (settledNumber is { } number && (number == first.Number || number == second.Number))
                winner = number == first.Number ? first : second;
            // Before the confidence tiebreak, and ahead of it because it is evidence rather than a
            // coin toss: where the rest of the book leaves room for one of the two numbers and not
            // the other, the pair is not two readings of one announcement at all but a real chapter
            // with something spurious beside it - an in-text mention, a misheard year - and the
            // spurious one is the one nothing can hold. This is also the reading
            // <see cref="GapPlanning.Normalize"/> would arrive at, which matters now that this runs
            // ahead of it (see ReconcileSequenceAsync): without it the weaker rule could discard a
            // mark the stronger one would have kept.
            else if (bounds.Admits(first.Number) != bounds.Admits(second.Number))
            {
                winner = bounds.Admits(first.Number) ? first : second;
                log?.Invoke($"re-reading settled nothing - keeping chapter {winner.Number}, " +
                            $"the only one of the two {bounds.Describe()} can hold");
            }
            else
            {
                winner = second.Confidence > first.Confidence ? second : first;
                log?.Invoke("re-reading settled nothing - keeping chapter " +
                            $"{winner.Number}, heard with more confidence");
            }
            settled.RemoveAt(i);
            settled[i - 1] = winner;
            // Re-examine the winner against whatever now follows it: three marks can collide, and
            // dropping one of them must not hide the next pair.
            i--;
        }
        return settled;
    }

    /// <summary>
    /// Drops a chapter mark that is no chapter at all but a line of a named announcement's own
    /// heading: one sitting within <see cref="DetectionTuning.CollidingChapterMarkSeconds"/> of a
    /// prologue, epilogue or <c>--custom</c> mark and carrying a number the chapters around it
    /// cannot hold.
    /// <para>
    /// Both halves are load-bearing. Proximity alone would take a real first chapter that follows a
    /// short prologue; an ill-fitting number alone is what every other defence already works on, and
    /// one that survives all of them is likelier a real chapter with several undetected ones in front
    /// of it than a phantom - which is why such a mark is otherwise kept (see
    /// <see cref="DetectedChapter.NumberUnverified"/>). Together they say something much narrower: a
    /// book does not start a chapter three seconds into its epilogue, and if it did, that chapter's
    /// number would continue the sequence.
    /// </para>
    /// <para>
    /// Nothing about such a number gives it away; the geometry does, exactly as it does for two
    /// numbered marks on one announcement (<see cref="SettleCollidingMarksAsync"/>), and it borrows
    /// that rule's threshold for the same reason: five seconds sits far above the spread between two
    /// lines of one heading and far below the shortest chapter anyone writes.
    /// </para>
    /// <para>
    /// The sequence's own first chapter is never dropped, however badly it fits. Its lower bound is
    /// an assumption rather than a chapter - <c>--expected-start-chapter</c>, or 1 - so a book
    /// legitimately beginning at chapter 12 fails <see cref="NumberBounds.Admits"/> on that
    /// assumption alone, and the split-book parts this tool is routinely pointed at are exactly that
    /// case.
    /// </para>
    /// <para>
    /// Pure arithmetic and geometry: no audio is consulted, so this needs neither a decoder nor a
    /// recognizer to test. Internal for that reason.
    /// </para>
    /// </summary>
    /// <remarks>Notes: the epilogue heading that became a chapter, and why its threshold is borrowed.
    /// <include file='../../notes/Detection/ChapterDetector.xml' path='doc/member[@name="DropNamedMarkEchoes"]/*' /></remarks>
    /// <param name="chapters">The chapter sequence, ascending in time.</param>
    /// <param name="named">The file's prologue/epilogue/--custom marks, in any order.</param>
    /// <param name="expectedStartChapter">--expected-start-chapter, or null.</param>
    /// <param name="log">Sink for --verbose log messages, or null when not verbose.</param>
    /// <returns>The sequence with its named-mark echoes removed.</returns>
    internal static List<DetectedChapter> DropNamedMarkEchoes(
        List<DetectedChapter> chapters, IReadOnlyList<DetectedMark> named, int? expectedStartChapter,
        Action<string>? log)
    {
        if (named.Count == 0)
            return chapters;

        var kept = new List<DetectedChapter>(chapters.Count);
        for (var i = 0; i < chapters.Count; i++)
        {
            var chapter = chapters[i];
            // Proximity first: it is the cheap half, and on any real book it rules out all but a
            // handful of chapters before the fit test has to rebuild the sequence around one.
            if (NamedMarkBeside(chapter.TimeSeconds, named) is not { } mark ||
                !FitsNowhereInTheSequence(chapters, i, expectedStartChapter))
            {
                kept.Add(chapter);
                continue;
            }
            log?.Invoke(
                $"chapter {chapter.Number} at {FormatTimestamp(chapter.TimeSeconds)}, " +
                $"{Math.Abs(chapter.TimeSeconds - mark.TimeSeconds):0.00} s from the {mark.Kind} mark " +
                "and fitting nowhere in the sequence - dropped as part of that announcement");
        }
        return kept;
    }

    /// <summary>The named mark a chapter mark is close enough to be another line of the same heading
    /// as, or null when there is none.</summary>
    /// <param name="timeSeconds">The chapter mark's position.</param>
    /// <param name="named">The file's named marks.</param>
    private static DetectedMark? NamedMarkBeside(double timeSeconds, IReadOnlyList<DetectedMark> named)
    {
        foreach (var mark in named)
            if (Math.Abs(mark.TimeSeconds - timeSeconds) < CollidingChapterMarkSeconds)
                return mark;
        return null;
    }

    /// <summary>
    /// Whether the chapter at <paramref name="index"/> carries a number the rest of the sequence
    /// cannot hold where it sits. Measured against every <em>other</em> chapter, so a phantom can
    /// never vouch for itself, and always false for the sequence's own first chapter - see
    /// <see cref="DropNamedMarkEchoes"/> for why an assumed lower bound may not condemn a mark.
    /// </summary>
    /// <param name="chapters">The sequence, ascending in time.</param>
    /// <param name="index">Which of them is in question.</param>
    /// <param name="expectedStartChapter">--expected-start-chapter, or null.</param>
    private static bool FitsNowhereInTheSequence(
        List<DetectedChapter> chapters, int index, int? expectedStartChapter)
    {
        var chapter = chapters[index];
        var others = chapters.Where((_, i) => i != index && chapters[i].Sequence == chapter.Sequence)
            .ToList();
        return others.Any(c => c.TimeSeconds <= chapter.TimeSeconds) &&
               !BracketingBounds(
                       chapter.TimeSeconds, others, [], expectedStartChapter, chapter.Sequence)
                   .Admits(chapter.Number);
    }

    /// <summary>
    /// Reconciles Probe's raw finds into an ascending chapter sequence and tries to win back the
    /// marks that reconciliation would otherwise cost - the last line of defence against a misheard
    /// chapter number, and the only one that gets to use evidence which did not exist when the
    /// number was read.
    /// <para>
    /// That evidence is the rest of the book. Chapter numbers ascend with time, so a mark whose
    /// number contradicts the marks around it is provably wrong, and the chapters that were detected
    /// <em>after</em> it - later in the file, and often later in the run - pin down what it should
    /// have been - and typically neither bounding chapter was known when the misreading happened.
    /// </para>
    /// <para>
    /// Two ways to settle an outlier, in order of how much they cost. When the chapters bracketing
    /// it leave exactly one number unaccounted for (<see cref="NumberBounds.SoleCandidate"/>), that
    /// number is the answer and no audio need be consulted at all. When they leave several, the
    /// audio is asked again with the bracket as the acceptance rule
    /// (<see cref="SuspectNumberMender.ReReadAtMarkAsync"/>), which is a far tighter question than
    /// the one that could be asked at detection time - back then nothing was yet known to follow.
    /// An outlier neither settles keeps the number it has and stays out of the sequence, exactly as
    /// <see cref="GapPlanning.Normalize"/> would have left it.
    /// </para>
    /// <para>
    /// Duplicates are not outliers and are silently left alone: a second detection of a number
    /// already in the sequence is one announcement heard by two overlapping windows, which
    /// <see cref="GapPlanning.Normalize"/> is right to drop and which there is nothing to repair
    /// about.
    /// </para>
    /// <para>
    /// Runs once, between Probe and Re-probe, which is where it pays for itself twice over: the
    /// repaired sequence is also what the missing-chapter list is computed from, so a book no longer
    /// sends Re-probe and Scan hunting through hours of audio for chapters that were never missing.
    /// </para>
    /// <para>
    /// The one step that touches audio is a delegate rather than a call, so everything here -
    /// which is all sequence arithmetic - can be tested without a decoder, a recognizer or a file.
    /// Internal for that reason.
    /// </para>
    /// </summary>
    /// <remarks>Notes: the worked example, and the hunting time the repair saves downstream.
    /// <include file='../../notes/Detection/ChapterDetector.xml' path='doc/member[@name="RepairSequenceOutliersAsync"]/*' /></remarks>
    /// <param name="found">The raw detections, in any order.</param>
    /// <param name="expectedStartChapter">--expected-start-chapter, or null; the lower bound for an
    /// outlier that has no chapter before it.</param>
    /// <param name="log">Sink for --verbose log messages, or null when not verbose.</param>
    /// <param name="reread">Asks the audio what number the given outlier really carries, holding the
    /// answer to the given bounds; only called when the sequence leaves more than one possibility,
    /// and at most <see cref="MaxSequenceRepairsPerFile"/> times.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The ascending chapter sequence, including whatever was repaired back into it.</returns>
    internal static async Task<List<DetectedChapter>> RepairSequenceOutliersAsync(
        List<DetectedChapter> found, int? expectedStartChapter, Action<string>? log,
        Func<DetectedChapter, NumberBounds, CancellationToken, Task<int?>> reread,
        CancellationToken ct)
    {
        var (kept, dropped) = NormalizeWithOutliers(found);
        // Identity is (part, number), not number alone: part 2's chapter 3 dropped as an outlier is
        // not "already in the sequence" just because part 1 has a chapter 3 of its own.
        var outliers = dropped
            .Where(c => !kept.Any(k => k.Sequence == c.Sequence && k.Number == c.Number))
            .ToList();
        if (outliers.Count == 0)
            return kept;

        var repaired = new List<DetectedChapter>(kept);
        var rereads = 0;

        foreach (var outlier in outliers)
        {
            ct.ThrowIfCancellationRequested();
            // Recomputed per outlier rather than carried, since a repaired mark joins `repaired` and
            // the next outlier of the same part has to see it. Cheap: a book has tens of chapters
            // and MaxSequenceRepairsPerFile bounds the loop itself.
            var taken = repaired.Where(c => c.Sequence == outlier.Sequence)
                .Select(c => c.Number).ToHashSet();
            var bounds = BracketingBounds(
                outlier.TimeSeconds, repaired, [], expectedStartChapter, outlier.Sequence);
            log?.Invoke(
                $"chapter {outlier.Number} at {FormatTimestamp(outlier.TimeSeconds)} contradicts " +
                $"its neighbours - they leave room only in {bounds.Describe()}");

            var repairedNumber = bounds.SoleCandidate(taken);
            if (repairedNumber is { } sole)
                log?.Invoke($"chapter {sole} the only number that fits - renumbering the mark");
            else if (rereads < MaxSequenceRepairsPerFile)
            {
                rereads++;
                repairedNumber = await reread(outlier, bounds, ct);
            }

            if (repairedNumber is not { } number || taken.Contains(number))
            {
                log?.Invoke($"chapter {outlier.Number} at {FormatTimestamp(outlier.TimeSeconds)} " +
                            "could not be placed in the sequence - dropping the mark");
                continue;
            }
            repaired.Add(new DetectedChapter(
                number, outlier.TimeSeconds, outlier.Confidence, Sequence: outlier.Sequence));
        }

        // Through Normalize once more so the repaired entries land in chronological order and any
        // that still do not fit (nothing observed, but a re-read is not infallible) fall out here
        // rather than downstream.
        return Normalize(repaired);
    }

    /// <summary>
    /// Re-transcribes samples with the <c>--upgrade-model</c> recognizer, in the file's own language,
    /// for the Probe steps that want a better opinion than the probing model's:
    /// <see cref="SuspectNumberMender"/>'s re-read of an implausible chapter number and
    /// <see cref="RegionProber.RereadJingleSpeechAsync"/>'s second look at a lost announcement.
    /// Routed through <see cref="TranscribeCountingAsync"/> like every other recognition, so the
    /// extra work shows up in the file's Whisper statistics rather than vanishing.
    /// <para>
    /// This is how the heavier model can be reached before a gap has been declared, which means a
    /// run that would otherwise never have loaded it may now do so (it is loaded lazily, on first
    /// use - see <see cref="Transcription.UpgradeTranscriber"/>). That is the trade both callers
    /// make: one model load against a Scan over hours of audio that a misheard - or unheard -
    /// announcement would otherwise mandate.
    /// </para>
    /// </summary>
    /// <param name="samples">16 kHz mono PCM of the window to re-read.</param>
    /// <param name="language">The file's resolved language.</param>
    /// <param name="ct">Cancellation token.</param>
    private Task<List<TranscriptSegment>> SecondOpinionAsync(
        float[] samples, string language, CancellationToken ct)
    {
        _upgradeTranscriber.ChangeLanguage(language);
        return TranscribeCountingAsync(samples, ct, _upgradeTranscriber);
    }

    /// <summary>
    /// Scan (only when needed): resolves sequence gaps by fully transcribing the regions between
    /// mismatched marks (and before the first mark, if it is not chapter 1, or below
    /// --expected-start-chapter). The same mechanism regardless of how <paramref name="chapters"/>
    /// was seeded - a gap-scoped <see cref="DetectGapsAsync"/> run's confirmed-plus-region-2
    /// chapters are covered exactly like a fresh <see cref="DetectAsync"/> run's own. Also runs the
    /// trailing-fallback recovery for a gap-scoped run whose last checkable --verify mark was
    /// unconfirmed - the one case the gap search cannot notice, since nothing bounds a
    /// still-missing trailing chapter from above to compare against.
    /// </summary>
    /// <param name="file">Path of the audio file.</param>
    /// <param name="info">Probe result of the file.</param>
    /// <param name="work">Progress tracker; begins its own "Scan" phase(s) as needed.</param>
    /// <param name="chapters">The chapters Probe found, in chronological order.</param>
    /// <param name="allSilences">Every silence from <see cref="RunAnalysisAsync"/>, used for gap-chunk
    /// seam snapping.</param>
    /// <param name="nonSpeechRegions">VAD non-speech regions from <see cref="RunAnalysisAsync"/>.</param>
    /// <param name="speechSegments">VAD speech segments from <see cref="RunAnalysisAsync"/>.</param>
    /// <param name="bytesPerSecond">The file's average byte rate, for progress reporting.</param>
    /// <param name="profile">The language profile resolved for this file.</param>
    /// <param name="trailingFallback">The trailing region's start and expected chapter numbers,
    /// when <see cref="BuildGapRegions"/> found the last checkable --verify mark unconfirmed;
    /// null otherwise (including for a fresh <see cref="DetectAsync"/> run).</param>
    /// <param name="trailingScanAllowed">Whether the trailing scan may run; see <see
    /// cref="ResolveTrailingRegion"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><paramref name="chapters"/> plus anything Scan recovered.</returns>
    private async Task<List<DetectedChapter>> RunScanAsync(
        string file, MediaInfo info, WorkTracker work, List<DetectedChapter> chapters,
        List<Silence> allSilences, List<NonSpeechRegion> nonSpeechRegions, List<SpeechSegment> speechSegments,
        double bytesPerSecond, LanguageProfile profile, (double From, List<int> Targets)? trailingFallback,
        bool trailingScanAllowed, CancellationToken ct)
    {
        var env = BuildScanEnvironment();
        var ctx = BuildScanContext(file, info, work, bytesPerSecond, allSilences, nonSpeechRegions,
            speechSegments, profile);
        var gaps = FindGaps(chapters, info.DurationSeconds, ExpectedStartChapter);
        if (gaps.Count > 0)
        {
            work.BeginPhase(
                PhaseNames.Scan, info.SizeBytes,
                BarSpans([.. gaps.Select(g => (g.FromSeconds, g.ToSeconds))],
                         info.DurationSeconds, bytesPerSecond));
            // A distinct --upgrade-model needs its language set here; the probe transcriber already
            // carries it, so the common (same-model) case leaves everything untouched.
            if (!ReferenceEquals(_upgradeTranscriber, _transcriber))
                _upgradeTranscriber.ChangeLanguage(profile.Language);
        }
        foreach (var gap in gaps)
        {
            _log?.Invoke($"transcribing suspicious region " +
                         $"{FormatTimestamp(gap.FromSeconds)} - {FormatTimestamp(gap.ToSeconds)}");
            var fills = await new RegionScanner(
                env, ctx, gap.FromSeconds, gap.ToSeconds,
                MissingNumbersInGap(chapters, gap, ExpectedStartChapter),
                // Whatever this leaves open goes to the shifted re-read below, unless a downgraded
                // --upgrade-model has switched that off - so the seams go too. See snapSeams.
                snapSeams: _options.UpgradeModelIsWorse,
                chapters, gap.Sequence).RunAsync(ct);
            chapters = Normalize(chapters.Concat(fills).ToList());
            RefreshChapterProgress(work, chapters);
        }
        if (gaps.Count > 0)
            _log?.Invoke("Scan finished");

        chapters = await RescanShiftedAsync(env, ctx, chapters, ct);

        // The trailing region - the one thing FindGaps above structurally cannot flag, since
        // nothing bounds a still-missing last chapter from above to compare against. Two
        // independent things ask for it (see ResolveTrailingRegion), and both end up here.
        if (ResolveTrailingRegion(trailingFallback, chapters, trailingScanAllowed) is { } trailing)
        {
            // "suspicious" only fits a targeted sweep, which is chasing specific numbers it has
            // reason to believe are there; the open-ended scan is speculative by design.
            var what = trailing.Targets is null ? "trailing region" : "suspicious trailing region";
            // Whether a shifted re-read may follow at all, which decides both this transcription's
            // chunk borders (see snapSeams) and, below, whether one actually runs. A targeted sweep
            // knows what it is after and goes by the same rule the gaps do. The open-ended one gets
            // a single pass instead: it now runs on every file by default, and a second reading of
            // audio nothing suspects would double that standing cost for the whole library. It is
            // not left unprotected against the boundary problem the re-read exists for - with no
            // re-read to follow, its own seams snap to silences (see snapSeams), which is what keeps
            // an announcement off a chunk border in the first place.
            var rereadPossible = trailing.Targets is not null && !_options.UpgradeModelIsWorse;
            _log?.Invoke($"transcribing {what} " +
                         $"{FormatTimestamp(trailing.From)} - {FormatTimestamp(info.DurationSeconds)}");
            work.BeginPhase(
                PhaseNames.Scan, info.SizeBytes,
                BarSpans([(trailing.From, info.DurationSeconds)], info.DurationSeconds, bytesPerSecond));
            if (!ReferenceEquals(_upgradeTranscriber, _transcriber))
                _upgradeTranscriber.ChangeLanguage(profile.Language);
            var fills = await new RegionScanner(
                env, ctx, trailing.From, info.DurationSeconds,
                trailing.Targets, snapSeams: !rereadPossible,
                chapters, trailing.Sequence).RunAsync(ct);
            chapters = Normalize(chapters.Concat(fills).ToList());
            RefreshChapterProgress(work, chapters);
            _log?.Invoke("Scan finished (trailing)");

            if (rereadPossible &&
                (trailing.Targets is null ||
                 trailing.Targets.Any(n => chapters.All(
                     c => c.Sequence != trailing.Sequence || c.Number != n))))
                chapters = await RescanRegionShiftedAsync(
                    env, ctx, chapters, trailing.From, info.DurationSeconds, trailing.Targets,
                    $"{what} ", trailing.Sequence, ct);
        }
        return chapters;
    }

    /// <summary>
    /// Scan's last resort, for the gaps its own full transcription left open: reads them again with
    /// every decode displaced by <see cref="DetectionTuning.RescanShiftSeconds"/>, half of Whisper's
    /// internal decode window.
    /// <para>
    /// The premise is that a gap surviving a complete transcription is not audio nobody looked at -
    /// every second of it was transcribed - but audio the recognizer read wrongly, and the single
    /// likeliest reason for that is where the announcement fell inside
    /// <see cref="DetectionTuning.WhisperChunkSeconds"/>. An announcement landing just after a window
    /// boundary can vanish from the transcript entirely while the timeline stays contiguous, so the
    /// the text reads as if nothing were missing. Half a window is the displacement that moves
    /// whatever sat on a boundary as far from one as it can get.
    /// </para>
    /// <para>
    /// Shifting the region's start rather than re-planning its chunks is what makes this cheap and
    /// hole-free: the first attempt already covered every second, so the head this skips is not
    /// unread, merely not re-read - and re-reading it in the framing that already failed would buy
    /// nothing. Shifting the start alone only guaranteed a new framing for the region's <em>first</em>
    /// chunk, though - which is the whole of it for any remainder under
    /// <see cref="DetectionTuning.GapChunkSeconds"/>, but past that, seam snapping could land a later
    /// chunk back on its original border and hand the re-read exactly the framing that had already
    /// failed. So neither attempt snaps its seams where the other one may run: see
    /// <see cref="RegionScanner._snapSeams"/>, which is what turns "probably a
    /// different framing" into "one shift later, every chunk".
    /// </para>
    /// <para>
    /// Only when <c>--upgrade-model</c> is not a deliberate downgrade. A lighter upgrade model is the one
    /// unambiguous statement that this file's stragglers are not worth more time, and doubling the
    /// cost of the gap it just failed on would be exactly the opposite.
    /// </para>
    /// </summary>
    /// <remarks>Notes: the chapter that vanished from a full transcription at every chunk length tried, and reappeared 15 s later.
    /// <include file='../../notes/Detection/ChapterDetector.xml' path='doc/member[@name="RescanShiftedAsync"]/*' /></remarks>
    /// <param name="env">The tools a <see cref="RegionScanner"/> borrows.</param>
    /// <param name="ctx">The file being scanned.</param>
    /// <param name="chapters">The chapters known after Scan's own transcription.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><paramref name="chapters"/> plus anything the re-scan recovered.</returns>
    private async Task<List<DetectedChapter>> RescanShiftedAsync(
        ScanEnvironment env, ScanContext ctx, List<DetectedChapter> chapters, CancellationToken ct)
    {
        if (_options.UpgradeModelIsWorse)
            return chapters;

        // Recomputed rather than carried over from the loop above: a gap Scan closed only in part
        // has become one or more narrower gaps around what it did find, and those remainders - not
        // the original span - are what is left to read again.
        foreach (var gap in FindGaps(chapters, ctx.Info.DurationSeconds, ExpectedStartChapter))
            chapters = await RescanRegionShiftedAsync(
                env, ctx, chapters, gap.FromSeconds, gap.ToSeconds,
                MissingNumbersInGap(chapters, gap, ExpectedStartChapter), "", gap.Sequence, ct);
        return chapters;
    }

    /// <summary>
    /// Transcribes one still-open region a second time with every decode displaced by
    /// <see cref="DetectionTuning.RescanShiftSeconds"/> - see <see cref="RescanShiftedAsync"/> for why.
    /// Skips a region with no room left for the shift, where the displaced start would be at or past
    /// the end.
    /// </summary>
    /// <param name="env">The tools a <see cref="RegionScanner"/> borrows.</param>
    /// <param name="ctx">The file being scanned.</param>
    /// <param name="chapters">The chapters known so far.</param>
    /// <param name="fromSeconds">Start of the region as the first attempt read it.</param>
    /// <param name="toSeconds">End of the region.</param>
    /// <param name="expectedNumbers">The numbers this region is expected to yield, or null for an
    /// open-ended trailing sweep.</param>
    /// <param name="what">What the region is called in the log line, ending in a space, or empty
    /// for an ordinary gap.</param>
    /// <param name="sequence">Which chapter sequence this region lies in; see
    /// <see cref="RegionScanner"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<List<DetectedChapter>> RescanRegionShiftedAsync(
        ScanEnvironment env, ScanContext ctx, List<DetectedChapter> chapters,
        double fromSeconds, double toSeconds, IReadOnlyList<int>? expectedNumbers,
        string what, int sequence, CancellationToken ct)
    {
        var from = fromSeconds + RescanShiftSeconds;
        if (from >= toSeconds)
            return chapters;

        _log?.Invoke(
            $"Re-scan: {what}{FormatTimestamp(fromSeconds)} - {FormatTimestamp(toSeconds)} " +
            $"from {FormatTimestamp(from)}, half a decode window later");
        ctx.Work.BeginPhase(
            PhaseNames.Rescan, ctx.Info.SizeBytes,
            BarSpans([(from, toSeconds)], ctx.Info.DurationSeconds, ctx.BytesPerSecond));
        if (!ReferenceEquals(_upgradeTranscriber, _transcriber))
            _upgradeTranscriber.ChangeLanguage(ctx.Profile.Language);

        var fills = await new RegionScanner(
            env, ctx, from, toSeconds, expectedNumbers,
            // Never snapped here either: a re-read that snapped could land a later chunk back on the
            // border the first attempt used, which is the one framing already known to fail.
            snapSeams: false,
            chapters, sequence).RunAsync(ct);
        chapters = Normalize(chapters.Concat(fills).ToList());
        RefreshChapterProgress(ctx.Work, chapters);
        _log?.Invoke(fills.Count > 0
            ? $"Re-scan recovered {fills.Count} chapter(s)"
            : "Re-scan found nothing further");
        return chapters;
    }

    /// <summary>
    /// Decides whether Scan gets a trailing region to transcribe, and of which kind. Two
    /// independent things ask for one:
    /// <list type="bullet">
    /// <item><description>the --verify fallback, for a gap-scoped <see cref="DetectGapsAsync"/> run
    /// whose last checkable mark was unconfirmed: it knows exactly which numbers it is after, so
    /// it is skipped entirely once they have all turned up elsewhere;</description></item>
    /// <item><description>--chapter-count, which states how many numbered chapters the book has and
    /// therefore names exactly which ones above the last find are still owed;</description></item>
    /// <item><description>the trailing scan (on unless --no-trailing-scan), which sweeps from the
    /// last detected chapter to the end of the file with no expectation of what it will find - the
    /// only way to catch a chapter after the last one detected when nothing has said how many there
    /// are.</description></item>
    /// </list>
    /// The two targeted kinds merge with each other, since each knows numbers the other does not,
    /// and the mere presence of either suppresses the open-ended sweep - including when it has
    /// nothing left to look for, which is exactly when a --chapter-count run should be doing no
    /// trailing work at all. That precedence is the opposite of what it was while the scan was
    /// opt-in, where asking for it was a request for the broadest possible search; now that it is
    /// the default, letting it win would mean --chapter-count silently paid for a sweep to the end
    /// of the file instead of the early-stopping search it exists to provide.
    /// </summary>
    /// <param name="verifyFallback">The --verify fallback's region start and expected numbers, or
    /// null when this is not a gap-scoped run (or its last mark was confirmed).</param>
    /// <param name="chapters">Everything detected so far, in chronological order.</param>
    /// <param name="trailingScanAllowed">Whether the trailing scan may run at all - false once Probe
    /// aborted, since a run that gave up on the file has no meaningful "last chapter" to sweep from.</param>
    /// <returns>The region's start, its expected chapter numbers (null for an open-ended
    /// sweep) and the sequence it lies in, or null when no trailing region is needed.</returns>
    private (double From, IReadOnlyList<int>? Targets, int Sequence)? ResolveTrailingRegion(
        (double From, List<int> Targets)? verifyFallback, List<DetectedChapter> chapters,
        bool trailingScanAllowed)
    {
        var targets = new List<int>();
        var from = double.MaxValue;
        // The tail of a file is the tail of its last part, whatever the parts before it did.
        var sequence = chapters.Count > 0 ? chapters[^1].Sequence : 0;
        if (verifyFallback is { } tf)
        {
            var stillMissing = tf.Targets
                .Where(n => chapters.All(c => c.Sequence != sequence || c.Number != n)).ToList();
            if (stillMissing.Count > 0)
            {
                targets.AddRange(stillMissing);
                from = tf.From;
            }
        }
        var declared = DeclaredTrailingNumbers(chapters);
        if (declared.Count > 0)
        {
            targets.AddRange(declared);
            from = Math.Min(from, chapters[^1].TimeSeconds);
        }
        if (targets.Count > 0)
            return (from, targets.Distinct().Order().ToList(), sequence);

        // Nothing left to aim at, so sweep on spec - but only where nothing better informed is
        // driving this file. Both targeted mechanisms suppress the open-ended scan outright, not
        // merely outrank it: "the book has twelve chapters and twelve are marked" is a statement
        // that there is nothing in the tail, not an invitation to go and look, and --chapter-count
        // caps the numbering as well, so anything a sweep did find above the count would be
        // discarded anyway. Letting the scan win here would take the whole saving that option exists
        // for away from every run that uses it. Nothing found at all means no anchor to sweep from
        // either; the whole file would be "the trailing region", which is Probe's job, not this one's.
        var somethingTargeted = verifyFallback != null || _options.LastExpectedChapter != null;
        return _options.TrailingScan && !somethingTargeted && trailingScanAllowed && chapters.Count > 0
            ? (chapters[^1].TimeSeconds, null, sequence)
            : null;
    }

    /// <summary>
    /// The chapter numbers --chapter-count says this book has and detection has not reached: every
    /// number from just above the last find up to <see cref="CliOptions.LastExpectedChapter"/>.
    /// Everything above the last find is missing by definition, the chapter list being sorted and
    /// free of duplicates by the time it gets here.
    /// </summary>
    /// <param name="chapters">Everything detected so far, in chronological order.</param>
    /// <returns>The still-owed numbers, or an empty list when no count was given, nothing was found
    /// to count from, or the declared last chapter has already been reached.</returns>
    private List<int> DeclaredTrailingNumbers(List<DetectedChapter> chapters)
    {
        if (_options.LastExpectedChapter is not { } last || chapters.Count == 0)
            return [];
        // The last part only: a count is a statement about how far the numbering runs, and on a book
        // divided into parts the numbering that is still running is the last one's. Applying it to
        // the whole list would demand chapters 16..N of a book whose final part is on chapter 9.
        var lastPart = chapters.Where(c => c.Sequence == chapters[^1].Sequence).ToList();
        // Counted from the last number that stands for something: an unverified one would otherwise
        // satisfy any --chapter-count at all simply by being large (see
        // DetectedChapter.NumberUnverified), which is the exact opposite of what the option is for.
        var highest = lastPart.LastOrDefault(c => !c.NumberUnverified, lastPart[^1]).Number;
        return highest < last ? [.. Enumerable.Range(highest + 1, last - highest)] : [];
    }

    /// <summary>
    /// Re-probe: before Scan resorts to transcribing a whole gap region end to end, re-probes it
    /// with Probe's own cheap candidate logic - the same silence/jingle-anchored windows, adaptive
    /// resizing and transcript reuse - on the <c>--upgrade-model</c> recognizer instead of the Probe
    /// one. The premise: most gaps are not "the announcement is unprobeable" but "the probe model
    /// misheard it" - the window was probed, the audio was right there, and a better model would
    /// have read the number correctly. Retrying just those windows can close the gap without
    /// transcribing the region at all.
    /// <para>
    /// A gap that survives that gets a second, differently aimed attempt before Scan is called in:
    /// the sub-floor silence sweep (<see cref="SweepSubFloorSilencesAsync"/>), which answers the
    /// other half of "why was the announcement not found" - not "the model misread it" but "nothing
    /// ever probed there".
    /// </para>
    /// <para>
    /// The cost is <em>not</em> guaranteed to be small, and scales with the gap's candidate count
    /// rather than its length: a region dense in qualifying silences can decode about as much audio
    /// as the full transcription it is avoiding, and when it finds nothing Scan still runs after
    /// it - a long gap dense in candidates has been measured spending most of an hour to recover
    /// nothing. A favourable bet only where candidates
    /// are sparse - hence gated behind a upgrade model heavier than the probing one, which since
    /// 0.11.0 the default small/turbo pair is, so this runs unless --model says otherwise.
    /// </para>
    /// <para>
    /// Runs only when <see cref="CliOptions.UpgradeModelIsBetter"/> holds (a lighter or equal Scan
    /// model would re-probe the same audio to the same conclusion) and a distinct upgrade recognizer
    /// actually exists to probe with. Never after an --early-abort or --expected-start-chapter
    /// abort: both mean the file is being given up on, not gap-filled.
    /// </para>
    /// <para>
    /// Each gap becomes a <see cref="DetectionRegion"/> bounded by the chapter numbers around it,
    /// exactly as a --verify gap recovery builds its regions, so a re-probe can never accept a
    /// number outside the gap or displace a chapter already found. Mark placement for anything
    /// recovered is unchanged - it refines on the probe model like every other mark, including
    /// Scan's own (see <see cref="ProbeContext.Transcriber"/>).
    /// </para>
    /// <para>
    /// The one pass that asks <see cref="GapPlanning.FindGaps"/> for the gaps under a written-off
    /// number too (<c>beneathUnverified</c>), and the only one that may: it re-reads a stretch's own
    /// candidate pauses rather than transcribing it, so looking where the doubt may be unfounded
    /// costs minutes instead of the hours that made the write-off worth having. Everything that
    /// <em>reports</em> a hole still declines to believe in this one until the re-probe has filled
    /// it, at which point <see cref="VindicateGapBound"/> retires the doubt as well.
    /// </para>
    /// </summary>
    /// <remarks>Notes: the gap that re-probed for most of an hour and recovered nothing.
    /// <include file='../../notes/Detection/ChapterDetector.xml' path='doc/member[@name="RunReprobeAsync"]/*' /></remarks>
    /// <param name="file">Path of the audio file.</param>
    /// <param name="info">Probe result of the file.</param>
    /// <param name="work">Progress tracker; begins its own "Re-probe" phase when there is work.</param>
    /// <param name="chapters">The chapters Probe found, in chronological order.</param>
    /// <param name="namedFound">The file's prologue/epilogue accumulator, passed through so a
    /// re-probe on the better model can still notice an announcement Probe's model missed.</param>
    /// <param name="allSilences">Every silence from <see cref="RunAnalysisAsync"/>.</param>
    /// <param name="silences">The --min-silence-length subset - Probe's own candidates.</param>
    /// <param name="nonSpeechRegions">VAD non-speech regions from <see cref="RunAnalysisAsync"/>.</param>
    /// <param name="speechSegments">VAD speech segments from <see cref="RunAnalysisAsync"/>.</param>
    /// <param name="jingles">The file's jingle census, carried through only because
    /// <see cref="ProbeContext"/> holds it; this pass's own windows keep their legacy sizing.</param>
    /// <param name="bytesPerSecond">The file's average byte rate, for progress reporting.</param>
    /// <param name="profile">The language profile resolved for this file.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><paramref name="chapters"/> plus anything the re-probe recovered.</returns>
    private async Task<List<DetectedChapter>> RunReprobeAsync(
        string file, MediaInfo info, WorkTracker work, List<DetectedChapter> chapters,
        List<DetectedMark> namedFound,
        List<Silence> allSilences, List<Silence> silences,
        List<NonSpeechRegion> nonSpeechRegions, List<SpeechSegment> speechSegments,
        List<Jingle> jingles, double bytesPerSecond, LanguageProfile profile, CancellationToken ct)
    {
        if (!_options.UpgradeModelIsBetter || ReferenceEquals(_upgradeTranscriber, _transcriber))
            return chapters;

        // Only the gaps that actually name a missing chapter are worth re-probing, and only those
        // are marked out on the bar below - a gap whose numbers are all accounted for would
        // otherwise be highlighted as work this pass is going to do and then silently skipped.
        var reprobeWork = FindGaps(chapters, info.DurationSeconds, ExpectedStartChapter,
                                   beneathUnverified: true)
            .Select(gap => (Gap: gap, Missing: MissingNumbersInGap(chapters, gap, ExpectedStartChapter)))
            .Where(g => g.Missing.Count > 0)
            .ToList();
        if (reprobeWork.Count == 0)
            return chapters;

        work.BeginPhase(
            PhaseNames.Reprobe, info.SizeBytes,
            BarSpans([.. reprobeWork.Select(g => (g.Gap.FromSeconds, g.Gap.ToSeconds))],
                     info.DurationSeconds, bytesPerSecond));
        _upgradeTranscriber.ChangeLanguage(profile.Language);

        // --early-abort and --expected-start-chapter are both disabled for these regions (infinity
        // and null): they exist to give up on a file that is yielding nothing at all, which is not
        // a question a bounded gap re-probe of an already-productive file gets to reopen.
        var ctx = new ProbeContext(
            file, info, work, bytesPerSecond,
            allSilences, silences, nonSpeechRegions, speechSegments, jingles,
            double.PositiveInfinity, null, _upgradeTranscriber, SecondGuessNumbers: false);

        // No second opinion to ask: every window below already decodes through the upgrade model, so
        // re-reading one with it would be the same recognizer on the same audio in the same framing.
        // What is left for an unreadable number here is the re-framing half (see SuspectNumberMender).
        var env = BuildProbeEnvironment() with { SecondOpinion = null };
        // Seeded with what is already known, exactly as DetectCoreAsync seeds Probe proper:
        // RegionProber reports per-mark progress and "still missing" notes off this list, and gates
        // the --min-silence-length auto observation on it not being the file's very first mark - all
        // nonsense on a list holding only this pass's own finds.
        var found = new List<DetectedChapter>(chapters);
        var knownCount = found.Count;
        foreach (var (gap, missing) in reprobeWork)
        {
            _log?.Invoke(
                $"Re-probe: {FormatTimestamp(gap.FromSeconds)} - {FormatTimestamp(gap.ToSeconds)} " +
                $"for chapter{(missing.Count > 1 ? "s" : "")} {string.Join(", ", missing)} with the upgrade model");
            var region = new DetectionRegion(
                gap.FromSeconds, gap.ToSeconds, missing[0] - 1, missing[^1] + 1, gap.Sequence);
            // Unclassified windows on purpose: this pass re-reads a stretch the primary scan's own
            // expectations already came up empty on, so the one thing it must not do is apply them
            // a second time. Its value is the heavier model over the same audio, seen whole.
            var prober = new RegionProber(
                env, ctx, region, found, namedFound,
                new LanguageState(profile, null, 0), recovery: true, hunting: missing);
            await prober.RunAsync(ct);
            _customLimitHit |= prober.CustomLimitHit;
            _sequenceRestartSkips += prober.SequenceRestartSkips;
            await SweepSubFloorSilencesAsync(
                env, ctx, gap, missing, found, namedFound, profile, allSilences, ct);
            // The prober reports its position as it goes, but it stops at the last candidate rather
            // than at the gap's end, and a gap yielding no candidates at all reports nothing - so
            // the gap is booked as passed here, or the bar would sit behind the pass by up to a
            // whole gap.
            work.SetPhaseProgress(WorkTracker.Position(gap.ToSeconds, bytesPerSecond));
            VindicateGapBound(found, gap, missing);
        }

        var recovered = found.Count - knownCount;
        chapters = Normalize(found);
        RefreshChapterProgress(work, chapters);
        _log?.Invoke(recovered > 0
            ? $"Re-probe finished - recovered {recovered} chapter(s) without a full transcription"
            : "Re-probe finished - nothing recovered, falling through to the Scan pass");
        return chapters;
    }

    /// <summary>
    /// Clears <see cref="DetectedChapter.NumberUnverified"/> on the chapter closing a gap the
    /// re-probe has just filled completely. A number is written off for leaving a hole under it
    /// that nothing corroborates; once every number in that hole has been found and placed, the
    /// hole is gone and the reason with it - the chapter now has an immediate predecessor, which is
    /// the whole of what it was doubted for.
    /// <para>
    /// Only where the gap was filled <em>entirely</em>, and only for the chapter that bounds it
    /// from above. A partially filled hole is still a hole, and a number still standing over one has
    /// lost none of its doubt. Nothing re-derives the sequence rule here on purpose: the question
    /// "does this number fit" belongs to <see cref="RegionProber"/> and asking it a second time in
    /// its own words is how two rules end up wearing one name. This asks a different and far
    /// narrower question - "is the specific hole this flag was raised over now closed?" - which the
    /// gap's own missing list answers outright.
    /// </para>
    /// <para>
    /// Almost always cosmetic, and worth doing anyway: the flag's live effect is to withhold a
    /// number from the corroborated set <see cref="GapPlanning.Normalize"/> takes its highest from,
    /// which changes an answer only where the doubted chapter is the highest one found. Leaving a
    /// vindicated number marked doubtful would be a state that contradicts the list it sits in.
    /// </para>
    /// </summary>
    /// <param name="found">The chapters known so far, updated in place.</param>
    /// <param name="gap">The gap just re-probed.</param>
    /// <param name="missing">The numbers that gap was asked to recover.</param>
    private static void VindicateGapBound(
        List<DetectedChapter> found, GapPlanning.GapRegion gap, List<int> missing)
    {
        if (!missing.All(n => found.Any(c => c.Number == n && c.Sequence == gap.Sequence)))
            return;
        var i = found.FindIndex(c => c.TimeSeconds == gap.ToSeconds && c.NumberUnverified);
        if (i >= 0)
            found[i] = found[i] with { NumberUnverified = false };
    }

    /// <summary>
    /// Re-probe's second half, for a gap its ordinary re-probe left open: sweeps the silences that
    /// sit just <em>below</em> --min-silence-length, one narrow band at a time and longest first,
    /// stopping the moment the gap's last missing chapter turns up. Silences that short are what
    /// Probe reaches last or not at all: its threshold opens at --min-silence-length and only comes
    /// down as far as this book's own marked-up breaks argue it down (see
    /// <see cref="DetectionTuning.AdaptiveSilenceFloorSeconds"/>), so a gap left open is by
    /// definition one where that argument was never made. Analyze stored them all regardless
    /// (<see cref="DetectionTuning.MinStoredSilenceSeconds"/>), so the material is already in hand
    /// and each band costs a handful of probe windows.
    /// <para>
    /// The case this exists for is a narrator whose chapter break simply lands on the floor - every
    /// chapter Probe missed preceded by a pause a hundredth or two under the demand. Some such
    /// chapters are eventually recovered by Scan transcribing whole gaps; one was lost outright
    /// because Whisper's long-form decode swallowed the announcement, a long chunk producing a
    /// contiguous transcript with the words simply absent from it, reproducibly, while any short
    /// window aimed at the same audio read them cleanly. A targeted probe is not merely the cheaper
    /// way to find such a chapter, it is sometimes the only way.
    /// </para>
    /// <para>
    /// Band by band rather than one sweep down to the bottom, because the yield is concentrated at
    /// the top: a book whose breaks fall just under the floor has all of them within a band or two,
    /// while each further step down roughly doubles the candidate count for a steadily smaller
    /// chance of a real break. Stopping at the first band that closes the gap is what keeps the
    /// common case to one cheap sweep.
    /// </para>
    /// <para>
    /// Bounded by a fraction of what it is avoiding, counted in
    /// <see cref="DetectionTuning.WhisperChunkSeconds"/> decode windows because that is the unit
    /// recognition is actually billed in - a 12 s probe and a 30 s slice of a Scan chunk cost the
    /// same one window. A band that would take the sweep past
    /// <see cref="SubFloorSweepBudgetFraction"/> of what transcribing the whole gap would cost ends
    /// it instead of starting, so the sweep is always the cheaper bet even when it finds nothing and
    /// Scan runs in full afterwards. Without a bound, a long gap dense in short pauses could spend
    /// more than Scan and still come back empty - the shape <see cref="RunReprobeAsync"/>'s own
    /// notes already record.
    /// </para>
    /// </summary>
    /// <remarks>Notes: the book whose breaks all landed on the floor, and what the sweeps came to on it when re-run.
    /// <include file='../../notes/Detection/ChapterDetector.xml' path='doc/member[@name="SweepSubFloorSilencesAsync"]/*' /></remarks>
    /// <param name="env">The probe environment the gap's ordinary re-probe used.</param>
    /// <param name="ctx">The Re-probe probe context, whose silence list each band replaces.</param>
    /// <param name="gap">The gap being recovered.</param>
    /// <param name="missing">The chapter numbers that gap was expected to yield.</param>
    /// <param name="found">Accumulator of chapters, holding whatever the ordinary re-probe added.</param>
    /// <param name="namedFound">The file's prologue/epilogue accumulator.</param>
    /// <param name="profile">The language profile resolved for this file.</param>
    /// <param name="allSilences">Every silence Analyze retained, which is where the bands come from.</param>
    /// <param name="ct">Cancellation token.</param>
    private Task SweepSubFloorSilencesAsync(
        ProbeEnvironment env, ProbeContext ctx, GapRegion gap, List<int> missing, List<DetectedChapter> found,
        List<DetectedMark> namedFound, LanguageProfile profile, List<Silence> allSilences,
        CancellationToken ct)
        => SweepGapBandsAsync(
            env, ctx, gap, missing, found, namedFound, new LanguageState(profile, null, 0),
            allSilences, "Re-probe", ct);

    /// <summary>
    /// One gap's sub-floor sweep, shared by Probe's and Re-probe's: the silences just under
    /// --min-silence-length, in <see cref="DetectionTuning.SubFloorSweepBandSeconds"/> bands
    /// longest-first, each priced against what this gap may spend in total, stopping as soon as the
    /// gap closes or the next band would take the sweep past that budget.
    /// <para>
    /// <strong>Bands longest-first, not one wide sweep, is what makes it affordable at all.</strong>
    /// Band populations grow roughly geometrically downwards (see
    /// <see cref="DetectionTuning.SubFloorSweepBandCount"/>), so a single band covering the whole
    /// range costs several times the top one while the chance of a real chapter break falls with
    /// every step down. Measured on the build-300 corpus run, where Probe's own sweep was still one
    /// wide band: it announced itself on three books and was then refused on every single gap - 50
    /// candidates against a budget of 21.8 on "Paula Monti"'s chapter 11-13 gap, 43 against 19.5, 45
    /// against 23.3, 127 against 25.5, and 323 against 158.3 on "I Shall Wear Midnight" - so it did
    /// no work whatsoever. Every one of that book's five missed chapters sat in the <em>top</em>
    /// band, which holds about five silences per gap and is affordable several times over.
    /// </para>
    /// <para>
    /// The budget was not the thing at fault there: fifty 18-second probes really do cost more than
    /// transcribing a fourteen-minute gap outright, and refusing that is right. Offering it
    /// all-or-nothing was.
    /// </para>
    /// </summary>
    /// <param name="env">The probe environment, and with it the recognizer this sweep runs on.</param>
    /// <param name="ctx">The probe context, whose silence list each band replaces.</param>
    /// <param name="gap">The gap being swept.</param>
    /// <param name="missing">The chapter numbers that gap was expected to yield.</param>
    /// <param name="found">Accumulator of chapters, added to in place.</param>
    /// <param name="namedFound">The file's prologue/epilogue accumulator.</param>
    /// <param name="language">The file's settled language resolution.</param>
    /// <param name="allSilences">Every silence Analyze retained, which is where the bands come from.</param>
    /// <param name="phase">How the log lines name the pass this sweep belongs to.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task SweepGapBandsAsync(
        ProbeEnvironment env, ProbeContext ctx, GapRegion gap, List<int> missing,
        List<DetectedChapter> found, List<DetectedMark> namedFound, LanguageState language,
        List<Silence> allSilences, string phase, CancellationToken ct)
    {
        var stillMissing = StillMissing(missing, found, gap.Sequence);
        if (stillMissing.Count == 0)
            return;

        // Budget and spending are both counted in Whisper's own internal decode windows, which is
        // the only unit in which a handful of short probes and one long transcription compare
        // honestly: recognition cost is per window, and an 18 s probe costs a whole one just as a
        // 30 s stretch of a Scan chunk does.
        var windowsPerProbe = ChunkWindows(RegionProber.RecoveryProbeSeconds);
        var budget = GapProbeBudget(gap.ToSeconds - gap.FromSeconds);
        var spent = 0;

        // Bounded below by the shortest break this run would ever believe in rather than by what
        // Analyze stored, so an explicit --min-silence-length - where the two are the same number -
        // yields no bands at all and nothing is ever probed under the length the user asked for.
        foreach (var (min, max) in SubFloorSweepBands(
                     _options.MinSilenceSeconds, _options.AdaptiveFloorSeconds))
        {
            var band = SilencesInBand(allSilences, gap, min, max);
            if (band.Count == 0)
                continue;
            var wouldSpend = spent + band.Count * windowsPerProbe;
            if (wouldSpend > budget)
            {
                _log?.Invoke(
                    $"{phase}: sweep stopped before the {min:0.0#}-{max:0.0#} s band - " +
                    $"{band.Count} more probe(s) -> {wouldSpend} decode window(s), over this gap's " +
                    $"{budget:0.#}");
                return;
            }
            spent += band.Count * windowsPerProbe;

            _log?.Invoke(
                $"{phase}: sweeping {band.Count} silence(s) of {min:0.0#}-{max:0.0#} s for " +
                $"chapter{(stillMissing.Count > 1 ? "s" : "")} {string.Join(", ", stillMissing)}");
            var region = new DetectionRegion(
                gap.FromSeconds, gap.ToSeconds, stillMissing[0] - 1, stillMissing[^1] + 1, gap.Sequence);
            var prober = new RegionProber(
                env, ctx with { Silences = band }, region, found, namedFound, language,
                sweepingSubFloorSilences: true, hunting: stillMissing);
            // Named for the length of the sweep itself and not a moment longer: the enclosing phase
            // is Probe or Re-probe, its bar is the one this walks over, and the name it was under is
            // restored whatever the sweep does - which is also why the name is taken from the
            // tracker rather than from the log's own phase word, a walk having possibly begun under
            // SC-probe rather than Probe.
            var enclosingPhase = ctx.Work.PhaseName;
            ctx.Work.Relabel(PhaseNames.SubFloorProbe);
            try
            {
                await prober.RunAsync(ct);
            }
            finally
            {
                ctx.Work.Relabel(enclosingPhase);
            }
            _customLimitHit |= prober.CustomLimitHit;
            _sequenceRestartSkips += prober.SequenceRestartSkips;

            stillMissing = StillMissing(stillMissing, found, gap.Sequence);
            if (stillMissing.Count == 0)
            {
                _log?.Invoke($"{phase}: sub-floor sweep closed the gap at {min:0.0#}-{max:0.0#} s");
                return;
            }
        }
    }

    /// <summary>
    /// Probe's own sub-floor sweep: re-visits the gaps left in the numbering with the pauses
    /// between the floor below and --min-silence-length - the ones the run was never willing to
    /// probe at all.
    /// <para>
    /// <strong>The gap is the trigger, not the measurement.</strong> This used to run only where a
    /// mark had measured a chapter break shorter than the demand, which sounds like evidence and is
    /// really an availability accident: a mark found at a jingle measures nothing (see
    /// <see cref="RegionProber.ThresholdSilenceFor"/>, which withholds a jingle's hush on purpose),
    /// so a book whose chapters all open with music never triggered the sweep however many chapters
    /// went missing - and a mixed book, jingles on most chapters and a bare pause on the one that
    /// vanished, is exactly the shape the sweep exists for. A gap says a chapter is missing here,
    /// which is the stronger claim; the measurement now only lowers the floor where one exists,
    /// down from <see cref="CliOptions.AdaptiveFloorSeconds"/> - the shortest break this run would
    /// ever believe in, and equal to the demand itself when --min-silence-length was given
    /// explicitly, which is what keeps an explicit demand honoured to the second.
    /// </para>
    /// <para>
    /// <strong>Why this is a separate sweep and not simply a wider candidate list.</strong> The
    /// obvious implementation - have Analyze keep everything down to
    /// <see cref="DetectionTuning.AdaptiveSilenceFloorSeconds"/> and let the threshold admit what it
    /// likes - was built first and measured, and it is wrong. Probe's candidate list is not only a
    /// list of places to look: it is the grid the windows are planned on, so a shared border is
    /// where one decode <em>stops</em> and the next resumes, and read-ahead, transcript caching and
    /// VAD jingle-candidate selection all read it too. Extra entries therefore re-cut the decodes of
    /// the whole book, and Whisper's reading of a stretch depends on the window it arrives in.
    /// A sweep cannot do this: it builds its own candidate list
    /// (<see cref="RegionProber"/>'s sweeping mode) and never touches the grid the ordinary walk
    /// used, so it is additive by construction.
    /// </para>
    /// <para>
    /// Gaps only, and budgeted: the sweep is worth its probes where a chapter is known to be
    /// missing, and <see cref="SweepGapBandsAsync"/>'s share of what transcribing the gap would cost
    /// keeps it the cheaper bet even when it finds nothing. Without both, a book that measures a
    /// very short break would sweep the whole sub-floor range across its entire length, which on a
    /// long file is thousands of probes for pauses no chapter is missing behind.
    /// </para>
    /// <para>
    /// Runs on the Probe recognizer and before Re-probe, so a chapter this recovers costs a handful
    /// of probe windows instead of the heavier model's gap re-probe and, failing that, Scan
    /// transcribing the gap end to end. Re-probe's own sweep still follows for whatever this leaves,
    /// and reads the same audio through the upgrade model, which is a genuinely different answer.
    /// </para>
    /// </summary>
    /// <remarks>Notes: what a wider candidate list did to the decode grid when that was tried instead, and the break length that would make an ungated sweep unbounded.
    /// <include file='../../notes/Detection/ChapterDetector.xml' path='doc/member[@name="SweepAdaptiveSubFloorAsync"]/*' /></remarks>
    /// <param name="ctx">Probe's probe context, whose silence list the band replaces.</param>
    /// <param name="found">The chapter accumulator, added to in place.</param>
    /// <param name="namedFound">The file's prologue/epilogue accumulator.</param>
    /// <param name="language">The file's settled language resolution.</param>
    /// <param name="measuredBreakSeconds">The shortest chapter break Probe's marks measured, or
    /// null where none of them measured one at all. Reported and nothing more: the sweep looks
    /// below where probing started, so what the book's own breaks came to says nothing about
    /// whether a shorter pause is worth a look inside a gap that is still missing a chapter.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task SweepAdaptiveSubFloorAsync(
        ProbeContext ctx, List<DetectedChapter> found, List<DetectedMark> namedFound,
        LanguageState language, double? measuredBreakSeconds, CancellationToken ct)
    {
        if (SubFloorSweepBands(_options.MinSilenceSeconds, _options.AdaptiveFloorSeconds).Count == 0)
            return;

        var known = Normalize(found);
        var work = FindGaps(known, ctx.Info.DurationSeconds, ExpectedStartChapter)
            .Select(gap => (Gap: gap, Missing: MissingNumbersInGap(known, gap, ExpectedStartChapter)))
            .Where(g => g.Missing.Count > 0)
            .ToList();
        if (work.Count == 0)
            return;

        var env = BuildProbeEnvironment();
        // "below" only where it is below: the shortest break a book measures is regularly longer
        // than the threshold probing started at, and the line used to call that "below" too.
        _log?.Invoke("Probe: " + (measuredBreakSeconds is { } measured
                ? $"chapter breaks measure down to {measured:0.0#} s, " +
                  (measured < _options.MinSilenceSeconds ? "below the " : "against the ") +
                  $"{_options.MinSilenceSeconds:0.0#} s probing started at"
                : $"no chapter break measured, nothing confirms the {_options.MinSilenceSeconds:0.0#} s " +
                  "probing started at") +
            " - sweeping the gaps for shorter pauses");

        foreach (var (gap, missing) in work)
            await SweepGapBandsAsync(
                env, ctx, gap, missing, found, namedFound, language, ctx.AllSilences, "Probe", ct);
    }

    /// <summary>Which of <paramref name="expected"/> the accumulator still has no chapter for.</summary>
    /// <param name="expected">The chapter numbers a gap was expected to yield.</param>
    /// <param name="found">Chapters known so far.</param>
    /// <param name="sequence">The part the gap belongs to. Identity is (part, number) here as it is
    /// in <see cref="RepairSequenceOutliersAsync"/> and <see cref="ResolveTrailingRegion"/>: on a
    /// book whose numbering restarts, part 1's chapter 3 says nothing about part 2's, and reading it
    /// as an answer would declare the gap closed and skip the sweep about to go looking.</param>
    private static List<int> StillMissing(List<int> expected, List<DetectedChapter> found, int sequence)
        => expected.Where(n => !found.Any(c => c.Sequence == sequence && c.Number == n)).ToList();

    /// <summary>
    /// One sweep band's candidate silences: those inside <paramref name="gap"/> whose length falls
    /// in [<paramref name="minSeconds"/>, <paramref name="maxSeconds"/>). The one-second margin at
    /// the gap end mirrors <see cref="RegionProber"/>'s own candidate filter, so a silence ending
    /// against the bounding chapter's mark is not counted against the sweep's budget only to be
    /// dropped when the prober builds its list.
    /// </summary>
    /// <param name="allSilences">Every silence Analyze retained.</param>
    /// <param name="gap">The gap being swept.</param>
    /// <param name="minSeconds">Inclusive lower bound on silence length.</param>
    /// <param name="maxSeconds">Exclusive upper bound on silence length.</param>
    private static List<Silence> SilencesInBand(
        List<Silence> allSilences, GapRegion gap, double minSeconds, double maxSeconds)
        => allSilences
            .Where(s => s.EndSeconds >= gap.FromSeconds && s.EndSeconds < gap.ToSeconds - 1)
            .Where(s => s.EndSeconds - s.StartSeconds >= minSeconds &&
                        s.EndSeconds - s.StartSeconds < maxSeconds)
            .ToList();

    /// <summary>
    /// Assembles the final <see cref="DetectionResult"/> once Probe and Scan are done: the
    /// remaining-gap consistency check, the low-confidence list, the lead-in speech check for
    /// <see cref="FileProcessor"/>'s intro-chapter insertion, and the per-file statistics.
    /// </summary>
    /// <param name="chapters">The final chapter list, after Scan.</param>
    /// <param name="namedMarks">The file's prologue/epilogue marks, at most one of each.</param>
    /// <param name="speechSegments">The VAD speech segments from <see cref="RunAnalysisAsync"/>
    /// (empty when the VAD pre-pass did not run).</param>
    /// <param name="profile">The language profile resolved for this file.</param>
    /// <param name="detectedLanguage">Whisper's raw language guess with --lang auto, or null.</param>
    /// <param name="detectedProbability">Whisper's probability for <paramref name="detectedLanguage"/>.</param>
    /// <param name="earlyAborted">True when --early-abort cut detection short.</param>
    /// <param name="belowExpectedStartNumber">The chapter number Probe found first, when
    /// --expected-start-chapter aborted detection because it was numbered below that expectation.</param>
    private DetectionResult BuildDetectionResult(
        List<DetectedChapter> chapters, List<DetectedMark> namedMarks,
        List<SpeechSegment> speechSegments, LanguageProfile profile,
        string? detectedLanguage, double detectedProbability, bool earlyAborted, int? belowExpectedStartNumber)
    {
        // Final consistency check: internal gaps that remain are fatal for this file, and so is a
        // leading gap Scan above could not fully close - but only when --expected-start-chapter
        // actually named a number to hold it to; without that, there is nothing to be missing.
        // Both loops skip a number nothing could corroborate, for the reason
        // GapPlanning.FindGaps does: this is the answer the ".missing-marks" tag is built from, and
        // a spoken year must not be able to declare two thousand chapters lost (see
        // DetectedChapter.NumberUnverified).
        // Part by part: a restart is not a hole, and the number a new part starts at is 1 whatever
        // the part before it counted up to (see GapPlanning.StartOfSequence).
        var missing = new List<int>();
        foreach (var sequence in BySequence(chapters))
        {
            if (StartOfSequence(sequence[0].Sequence, ExpectedStartChapter) is { } expectedStart &&
                !sequence[0].NumberUnverified)
                for (var n = expectedStart; n < sequence[0].Number; n++)
                    missing.Add(n);
            for (var i = 1; i < sequence.Count; i++)
                if (!sequence[i].NumberUnverified)
                    for (var n = sequence[i - 1].Number + 1; n < sequence[i].Number; n++)
                        missing.Add(n);
        }
        // The trailing end, which only --chapter-count can speak for: a chapter after the last one
        // found is invisible to every other check here, nothing above it being available to compare
        // against. A run that found nothing at all is left out - such a file is reported as having
        // no chapters rather than as missing all of them.
        missing.AddRange(DeclaredTrailingNumbers(chapters));

        // Kept whole rather than reduced to numbers: a listing that names these has to say which
        // part each one belongs to on a file whose numbering restarts.
        var lowConfidence = chapters.Where(c => c.Confidence < LowConfidenceThreshold).ToList();

        var unverified = chapters.Where(c => c.NumberUnverified).Select(c => c.Number).ToList();

        // A file that yielded no chapter at all is left unchanged by FileProcessor, and a lone
        // prologue or epilogue must not be what makes it worth rewriting: a book whose chapter
        // announcements were never heard is a failed detection, not a two-mark book. With
        // --ignore-chapter-numbers that reasoning inverts - the chapters are themselves named marks
        // then, and there is no numbered list whose emptiness could condemn them.
        var named = chapters.Count > 0 || _options.IgnoreChapterNumbers
            ? DropOutOfScopeNamedMarks(
                ResolveEpiloguePlacement(
                    namedMarks.OrderBy(m => m.TimeSeconds).ToList(), chapters, profile, _log),
                chapters, profile, _log)
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

        // Counted off the surviving chapters rather than off the restarts Probe announced: a part
        // whose every chapter was later dropped is not a part of the written file, and the titles
        // are built from this same list.
        var sequenceCount = chapters.Count == 0 ? 1 : chapters.Select(c => c.Sequence).Distinct().Count();

        return new DetectionResult(
            chapters, named, missing.Count > 0, missing, lowConfidence,
            profile, detectedLanguage, detectedProbability, stats, earlyAborted, belowExpectedStartNumber,
            leadInHasSpeech, _customLimitHit, _sequenceRestartSkips,
            unverified.Count > 0 ? unverified : null, sequenceCount);
    }

    /// <summary>
    /// Holds the built-in epilogue to the one place a book has for it: after its last chapter. A
    /// mark anywhere else is dropped - or, where one of the user's own <c>--custom</c> mappings
    /// would have claimed the same announcement, handed over to it.
    /// <para>
    /// The scope alone cannot express this. <see cref="NamedPhraseScope.AfterFirstChapter"/> is
    /// everything detection can check while it runs, since which chapter is the last one is unknown
    /// until every pass has finished - so the check has to happen here, at the end, where the mark
    /// is either written or not. What it catches is the word turning up in ordinary prose: "epilogue"
    /// is a perfectly common noun, Italian "riepilogo" contains "epilogo", and the built-in phrase is
    /// deliberately short enough to survive a recognizer's spelling.
    /// </para>
    /// <para>
    /// Pure bookkeeping over marks already placed: no audio is consulted, so this needs neither a
    /// decoder nor a recognizer to test. Internal for that reason, as
    /// <see cref="DropNamedMarkEchoes"/> is.
    /// </para>
    /// <para>
    /// Mid-book epilogue marks are not a hypothetical - a book in the test corpus has one - and the
    /// answer for a book that really does divide itself that way is a <c>--custom</c> mapping, which
    /// names whatever recurring element the user says it does, at whatever position. That is also
    /// why a dropped mark is offered to those mappings first: the built-in phrase and a mapping can
    /// match the same words, and a mapping that did would normally have produced a mark of its own
    /// alongside this one - but not when it was passed over as a duplicate, or when the phrase that
    /// matched belongs to a language whose mapping was compiled out. Where the mapping's own mark is
    /// already there, this one is simply dropped: the announcement keeps its mark either way.
    /// </para>
    /// </summary>
    /// <param name="named">The file's named marks, ascending in time.</param>
    /// <param name="chapters">The file's chapters, ascending in time; empty under
    /// <c>--ignore-chapter-numbers</c>, where the chapters are themselves named marks.</param>
    /// <param name="profile">The file's language profile, supplying the <c>--custom</c> mappings a
    /// dropped mark may still belong to.</param>
    /// <param name="log">Sink for --verbose log messages, or null when not verbose.</param>
    /// <returns><paramref name="named"/> with the epilogue kept, converted or gone.</returns>
    internal static List<DetectedMark> ResolveEpiloguePlacement(
        List<DetectedMark> named, List<DetectedChapter> chapters, LanguageProfile profile,
        Action<string>? log)
    {
        var index = named.FindIndex(m => m.Kind == NamedPhrase.EpilogueKind);
        if (index < 0)
            return named;

        // Under --ignore-chapter-numbers the chapters live in the named list; either way, a file
        // with no chapter at all has nothing for the epilogue to be after, so it is left alone.
        var lastChapter = chapters.Count > 0
            ? chapters[^1].TimeSeconds
            : named.Where(m => m.Kind == profile.ChapterAnnouncement.Kind)
                .Select(m => (double?)m.TimeSeconds).LastOrDefault();
        var epilogue = named[index];
        if (lastChapter is not { } last || epilogue.TimeSeconds > last)
            return named;

        var claimed = ClaimedByCustomMapping(epilogue, named, profile);
        log?.Invoke(
            $"epilogue mark at {FormatTimestamp(epilogue.TimeSeconds)} does not follow the last " +
            $"chapter (at {FormatTimestamp(last)}), so it is no epilogue" +
            (claimed is { } custom
                ? $" - keeping it as {custom.Kind} (\"{custom.Title}\")"
                : $" - dropping it{(epilogue.Text.Length > 0 ? $" (heard as \"{epilogue.Text}\")" : "")}"));

        var resolved = new List<DetectedMark>(named);
        if (claimed is { } replacement)
            resolved[index] = replacement;
        else
            resolved.RemoveAt(index);
        return resolved;
    }

    /// <summary>
    /// Drops the <c>--custom</c> marks whose mapping restricted them to
    /// <see cref="NamedPhraseScope.AfterLastChapter"/> and which turned out to sit before the file's
    /// last chapter. The other two scopes are pre-placement filters inside
    /// <see cref="RegionProber"/> and cost nothing; this one can only be applied here, for the same
    /// reason the built-in epilogue's placement can (see <see cref="ResolveEpiloguePlacement"/>) -
    /// which chapter is last is unknown until every pass has finished. So it buys precision only,
    /// never saved transcription: the announcement has been heard, placed and refined by the time it
    /// can be judged.
    /// <para>
    /// Unlike the epilogue's own check there is no handing the mark over to another mapping: it
    /// <em>is</em> a mapping's mark, and the user said where it belongs.
    /// </para>
    /// <para>
    /// Pure bookkeeping, like the two rules around it; internal so it can be tested without a
    /// decoder or a recognizer.
    /// </para>
    /// </summary>
    /// <param name="named">The file's named marks, ascending in time.</param>
    /// <param name="chapters">The file's chapters, ascending in time; empty under
    /// <c>--ignore-chapter-numbers</c>, where the chapters are themselves named marks.</param>
    /// <param name="profile">The file's language profile, supplying each mark's own phrase.</param>
    /// <param name="log">Sink for --verbose log messages, or null when not verbose.</param>
    /// <returns><paramref name="named"/> without the marks that fell outside their scope.</returns>
    internal static List<DetectedMark> DropOutOfScopeNamedMarks(
        List<DetectedMark> named, List<DetectedChapter> chapters, LanguageProfile profile,
        Action<string>? log)
    {
        var trailing = profile.NamedPhrases
            .Where(p => p.Scope == NamedPhraseScope.AfterLastChapter)
            .Select(p => p.Kind)
            .ToHashSet();
        if (trailing.Count == 0)
            return named;

        // Under --ignore-chapter-numbers the chapters live in the named list; either way, a file
        // with no chapter at all has no "last chapter" for anything to be after, and every such
        // mark is left alone rather than all of them dropped.
        var lastChapter = chapters.Count > 0
            ? chapters[^1].TimeSeconds
            : named.Where(m => m.Kind == profile.ChapterAnnouncement.Kind)
                .Select(m => (double?)m.TimeSeconds).LastOrDefault();
        if (lastChapter is not { } last)
            return named;

        var kept = new List<DetectedMark>(named.Count);
        foreach (var mark in named)
        {
            if (trailing.Contains(mark.Kind) && mark.TimeSeconds <= last)
            {
                log?.Invoke(
                    $"{mark.Kind} mark (\"{mark.Title}\") at {FormatTimestamp(mark.TimeSeconds)} " +
                    $"does not follow the last chapter (at {FormatTimestamp(last)}) - dropping it, " +
                    "as its \"after-last-chapter\" hint asks");
                continue;
            }
            kept.Add(mark);
        }
        return kept;
    }

    /// <summary>
    /// The <c>--custom</c> mark a rejected built-in announcement turns into, or null when no mapping
    /// claims it. Matched against the transcript segment the announcement was heard in
    /// (<see cref="DetectedMark.Text"/>), which is what the mappings were run against in the first
    /// place - so a mark carried over from the file's existing marks, which was never heard at
    /// all, can only ever be dropped.
    /// </summary>
    /// <param name="mark">The mark being rejected.</param>
    /// <param name="named">Every named mark of the file, for the "a mapping already marked this
    /// announcement" case.</param>
    /// <param name="profile">The file's language profile, supplying the mappings.</param>
    private static DetectedMark? ClaimedByCustomMapping(
        DetectedMark mark, List<DetectedMark> named, LanguageProfile profile)
    {
        if (mark.Text.Length == 0 ||
            named.Any(m => m.Kind.StartsWith(NamedPhrase.CustomKindPrefix, StringComparison.Ordinal) &&
                           Math.Abs(m.PhraseTimeSeconds - mark.PhraseTimeSeconds) < NamedMarkDedupeSeconds))
            return null;

        foreach (var phrase in profile.NamedPhrases.Where(p => p.IsCustom))
            if (phrase.Pattern.Matches(mark.Text).FirstOrDefault() is { Match: not null } hit)
                return mark with
                {
                    Kind = phrase.Kind,
                    Title = phrase.ResolveTitle(hit.Match, profile.Language),
                    Repeatable = true,
                };
        return null;
    }

    /// <summary>Result of <see cref="RunAnalysisAsync"/>: every silence/VAD signal Probe and Scan
    /// need, gathered in one full-file pass.</summary>
    /// <param name="AllSilences">Every silence down to <see cref="MinStoredSilenceSeconds"/>,
    /// regardless of --min-silence-length - used for seam snapping and mark anchoring.</param>
    /// <param name="Silences">The subset of <paramref name="AllSilences"/> at or above
    /// --min-silence-length - Probe's own candidate/logging silences.</param>
    /// <param name="NonSpeechRegions">Merged VAD non-speech regions (empty when the VAD pre-pass
    /// did not run) - see <see cref="ComputeNonSpeechRegions"/>.</param>
    /// <param name="SpeechSegments">The raw VAD speech segments behind <paramref
    /// name="NonSpeechRegions"/>, kept for the anchor-time jingle edge adjustment; empty when the
    /// VAD pre-pass did not run.</param>
    /// <param name="Jingles">The file's music stretches (see <see cref="JingleCensus"/>), which
    /// Probe's primary scan builds its jingle candidates from; empty without the VAD pre-pass.</param>
    private readonly record struct AnalysisResult(
        List<Silence> AllSilences, List<Silence> Silences,
        List<NonSpeechRegion> NonSpeechRegions, List<SpeechSegment> SpeechSegments,
        List<Jingle> Jingles);

    /// <summary>
    /// Analyze: scans the whole file for silences and, in the same decode, for the VAD non-speech
    /// regions the pre-pass finds - silencedetect alone never produces a Probe candidate at a chapter transition
    /// where the jingle abuts speech on both sides with no amplitude gap; VAD sees that transition
    /// as a non-speech region (music, like silence, reads as non-speech to a speech detector)
    /// regardless of amplitude, so it can catch what silencedetect misses. See <see
    /// cref="JingleGeometry.ComputeMarkBeforeJingle"/> for how the two detectors' findings
    /// combine to place the mark with --mark-before-jingle.
    /// </summary>
    /// <param name="file">Path of the audio file.</param>
    /// <param name="info">Probe result of the file.</param>
    /// <param name="work">Progress tracker; begins the "Analyze" phase itself.</param>
    /// <param name="bytesPerSecond">The file's average byte rate, for progress reporting.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<AnalysisResult> RunAnalysisAsync(
        string file, MediaInfo info, WorkTracker work, double bytesPerSecond, CancellationToken ct)
    {
        work.BeginPhase(PhaseNames.Analyze, info.SizeBytes);
        // The scan always goes down to MinStoredSilenceSeconds (or --min-silence-length, if lower
        // still) so short silences are available for overlap-border snapping (see
        // FindOverlapSplitPoint). allSilences holds all of those; `silences` keeps only the ones at
        // or above --min-silence-length.
        var storedSilenceFloor = _options.StoredSilenceFloorSeconds;
        // Before the scan, because the scan is what the answer is needed for. The measurement
        // decodes a few short excerpts and costs about a second even on a 15-hour book, against the
        // full decode that follows it.
        var reading = _options.AutoNoiseFloor
            ? await SilenceThresholdProbe.MeasureAsync(
                _audio, file, info.DurationSeconds, info.InputDecoder, ct)
            : (SilenceThresholdProbe.Reading?)null;
        var noiseDb = reading?.ThresholdDb ?? _options.NoiseFloorDb;
        _log?.Invoke("Analyze: " + SilenceThresholdProbe.Describe(reading, noiseDb));
        List<Silence> allSilences;
        var nonSpeechRegions = new List<NonSpeechRegion>();
        // The raw VAD speech segments behind nonSpeechRegions, kept for the anchor-time jingle
        // edge adjustment (see AdjustJingleRegion): the merged regions alone no longer say where
        // the speech blips inside them lie. Empty when VAD is off.
        var speechSegments = new List<SpeechSegment>();
        if (_vad is { } vad)
        {
            allSilences = await _audio.DetectSilencesAndStreamPcmAsync(
                file, info.DurationSeconds, storedSilenceFloor, noiseDb,
                async (pcm, innerCt) => speechSegments = await vad.DetectSpeechAsync(pcm, innerCt),
                seconds => work.SetPhaseProgress(WorkTracker.Position(seconds, bytesPerSecond)), info.InputDecoder, ct);
            nonSpeechRegions = ComputeNonSpeechRegions(speechSegments);
        }
        else
        {
            allSilences = await _audio.DetectSilencesAsync(
                file, info.DurationSeconds, storedSilenceFloor, noiseDb,
                seconds => work.SetPhaseProgress(WorkTracker.Position(seconds, bytesPerSecond)), info.InputDecoder, ct);
        }
        // Empty rather than "everything at or above 0" when silence probing is off: this list is
        // Probe's candidate set and nothing else, so emptying it is exactly what the option asks
        // for - and it is also what makes every jingle a candidate, since a VAD region is otherwise
        // dropped as a duplicate of the silence leading it (see RegionProber's BuildCandidates).
        var silences = _options.ProbeSilences
            ? allSilences.Where(s => s.EndSeconds - s.StartSeconds >= _options.MinSilenceSeconds).ToList()
            : [];

        _log?.Invoke(_options.ProbeSilences
            ? $"Analyze: {silences.Count} silence(s) of >= {_options.MinSilenceSeconds:0.#} s found" +
              (_options.AutoMinSilence ? " (adaptive threshold)" : "")
            : $"Analyze: {allSilences.Count} silence(s) found, none probed " +
              "(--min-silence-length 0 - jingles only)");
        // Derived from the scan's own two signals and nothing else, so it costs no audio work - and
        // from the raw speech segments rather than the merged regions, for the reason JingleCensus
        // gives. Empty without the VAD pre-pass, which is why it is only logged with it.
        var jingles = JingleCensus.Measure(speechSegments, allSilences);
        // The one figure the census is not merely diagnostic for: how far back this book's music can
        // reach. Set here rather than passed down, because the two places that ask sit in different
        // passes and neither has the census in scope.
        _jingleReachSeconds = JingleCensus.ReachSeconds(jingles);
        // SetLog runs before every pass, so the placer is never actually null here; the guard is the
        // compiler's, not a real case.
        if (_marks is { } marks)
            marks.JingleReachSeconds = _jingleReachSeconds;
        if (_vad != null)
        {
            // The speech-segment count carries no extra information (a non-speech region is just
            // the gap between two consecutive speech segments), so only the regions are logged.
            _log?.Invoke($"Analyze: {nonSpeechRegions.Count} non-speech region(s) found");
            _log?.Invoke($"Analyze: {JingleCensus.Describe(jingles)}");
        }
        DumpAnalysisSignals(allSilences, nonSpeechRegions, speechSegments, jingles);

        return new AnalysisResult(allSilences, silences, nonSpeechRegions, speechSegments, jingles);
    }

    /// <summary>
    /// Writes Analyze's raw findings to the --debug file: every silence, every VAD speech segment,
    /// every merged non-speech region. This is the material every later "why there?" question comes
    /// back to - which silence a mark anchored to, whether VAD saw the jingle at all, where its
    /// edges really were - and it cannot be recovered afterwards without re-decoding the whole file,
    /// which for a 13-hour audiobook is the better part of an hour.
    /// <para>
    /// Debug-only, and this is the reason the sink is separate: the counts here run to several
    /// thousand silences and tens of thousands of speech segments on a full book, so in the ordinary
    /// log they would bury every other line. Each entry is one line, so the file stays greppable by
    /// timestamp.
    /// </para>
    /// </summary>
    /// <param name="allSilences">Every silence found, including those below --min-silence-length,
    /// which are flagged so the subset Probe actually works from stays visible.</param>
    /// <param name="nonSpeechRegions">The merged non-speech regions, empty without the VAD pre-pass.</param>
    /// <param name="speechSegments">The raw VAD speech segments, empty without the VAD pre-pass.</param>
    /// <param name="jingles">The audible part of those regions (see <see cref="JingleCensus"/>),
    /// listed as well as counted: the --verbose line says how long this book's jingles run, and the
    /// listing says which chapter openings the outliers in it belong to.</param>
    private void DumpAnalysisSignals(
        List<Silence> allSilences, List<NonSpeechRegion> nonSpeechRegions,
        List<SpeechSegment> speechSegments, List<Jingle> jingles)
    {
        if (_debug is not { } debug)
            return;

        debug($"Analyze detail: {allSilences.Count} silence(s), {speechSegments.Count} VAD speech " +
              $"segment(s), {nonSpeechRegions.Count} non-speech region(s), {jingles.Count} jingle(s)");
        foreach (var s in allSilences)
            debug($"  silence {FormatTimestamp(s.StartSeconds)}-{FormatTimestamp(s.EndSeconds)} " +
                  $"({s.EndSeconds - s.StartSeconds:0.00} s)" +
                  (_options.ProbeSilences && s.EndSeconds - s.StartSeconds >= _options.MinSilenceSeconds
                      ? " *" : ""));
        foreach (var s in speechSegments)
            debug($"  speech {FormatTimestamp(s.StartSeconds)}-{FormatTimestamp(s.EndSeconds)} " +
                  $"({s.EndSeconds - s.StartSeconds:0.00} s)");
        foreach (var r in nonSpeechRegions)
            debug($"  non-speech {FormatTimestamp(r.StartSeconds)}-{FormatTimestamp(r.EndSeconds)} " +
                  $"({r.EndSeconds - r.StartSeconds:0.00} s)");
        foreach (var j in jingles)
            debug($"  jingle {FormatTimestamp(j.StartSeconds)}-{FormatTimestamp(j.EndSeconds)} " +
                  $"({j.LengthSeconds:0.00} s, speech at {FormatTimestamp(j.AnnouncementSeconds)}" +
                  (j.BridgedBlips > 0 ? $", {j.BridgedBlips} bridged blip(s)" : "") + ")");
    }

    /// <summary>
    /// Checks pre-existing chapter marks against the audio (--verify) - far quicker than the
    /// full silence-scan/probe pipeline, since only the marks' own timestamps are visited. For
    /// every mark whose title yields a parseable expected chapter number, a short window around
    /// its timestamp is probed with Whisper and checked for a phrase match with that number. A
    /// mark whose title has no parseable number (e.g. a prelude/intro entry) cannot be checked
    /// and counts neither for nor against the result; when none of a file's marks have one,
    /// verification trivially passes - there is nothing to disprove, so the file is left alone
    /// rather than needlessly re-detected.
    /// </summary>
    /// <param name="file">Path of the audio file.</param>
    /// <param name="info">Probe result of the file, including its pre-existing chapter marks.</param>
    /// <param name="work">Progress tracker, advanced once per mark (checked or skipped).</param>
    /// <param name="log">This file's log sinks; default when nothing is listening.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<VerifyResult> VerifyExistingChaptersAsync(
        string file, MediaInfo info, WorkTracker work, DetectionLog log, CancellationToken ct)
    {
        SetLog(log);
        var language = await ResolveProfileFromExistingMarksAsync(file, info, ct);
        var profile = language.Profile;
        _transcriber.ChangeLanguage(profile.Language);

        var checkedCount = 0;
        var failed = 0;
        // Mirrors Probe/Scan's found-chapters list, but of confirmed marks rather than fresh
        // detections, so the same ChapterProgress/bar display applies: the highest confirmed
        // number, with any lower unconfirmed one shown as a "(-N)" gap beneath it.
        var confirmedChapters = new List<DetectedChapter>();
        // Every mark's outcome in file order - the input BuildGapRegions groups into
        // DetectGapsAsync's recovery regions. A skipped mark (empty window or no parseable
        // number) is still recorded, as null/false, so it cannot split a run of unconfirmed
        // marks around it into two.
        var outcomes = new List<ExistingMarkOutcome>();
        // Reported, never counted - see NamedMarkOutcome for why the two lists stay apart.
        var namedOutcomes = new List<NamedMarkOutcome>();
        // Confirmations only, and for the progress line only - see RefreshNamedProgress.
        var confirmedNamed = 0;

        work.BeginPhase(PhaseNames.Verify, info.ExistingChapters.Count);
        foreach (var mark in info.ExistingChapters)
        {
            ct.ThrowIfCancellationRequested();

            var windowStart = Math.Max(0, mark.StartSeconds - VerifyMarginBeforeSeconds);
            var windowLen = Math.Min(VerifyWindowSeconds, info.DurationSeconds - windowStart);
            if (windowLen <= 0)
            {
                outcomes.Add(new ExistingMarkOutcome(mark.StartSeconds, null, false));
                work.Advance(1);
                continue;
            }

            var part = PartOf(mark.Title, profile);
            if (!TryParseExpectedNumber(mark.Title, profile, out var expected))
            {
                // A mark with no number is still worth asking about, just not worth counting: it
                // may be this file's prologue, epilogue or --custom mark, and whether the phrase
                // behind it is really spoken there is exactly as answerable as a chapter's. What it
                // is not is a gap boundary, which is why the ExistingMarkOutcome recorded beside it
                // is unchanged - null number, unconfirmed - and BuildGapRegions goes on skipping it.
                // The intro entry is passed over rather than reported unverifiable, exactly as
                // CarryOverNamedMarks passes it over: it is this tool's own lead-in entry, covering
                // whatever precedes the first announcement, so there is no phrase behind it and
                // never was. Reported, it would add a line saying nothing to every summary of every
                // book this tool has ever marked.
                if (!string.Equals(mark.Title.Trim(), profile.IntroTitle, StringComparison.OrdinalIgnoreCase))
                {
                    var named = await CheckNamedMarkAsync(
                        file, info, mark, windowStart, windowLen, profile, ct);
                    namedOutcomes.Add(named);
                    if (named.Confirmed)
                        RefreshNamedProgress(work, ++confirmedNamed);
                }
                else
                    _log?.Invoke($"mark at {FormatTimestamp(mark.StartSeconds)} " +
                                 $"(\"{mark.Title}\") - the lead-in entry, nothing to check");
                outcomes.Add(new ExistingMarkOutcome(mark.StartSeconds, null, false));
                work.Advance(1);
                continue;
            }

            var samples = await _audio.DecodePcmAsync(file, windowStart, windowLen, info.InputDecoder, ct);
            var segments = await _transcriber.TranscribeAsync(samples, ct);
            LogTranscript($"verify @{FormatTimestamp(mark.StartSeconds)}", segments);

            checkedCount++;
            // The match itself rather than a yes/no, because --fix corrects the mark to where the
            // announcement was actually heard.
            PhraseMatch? match = FindCappedPhraseMatches(segments, profile)
                .Cast<PhraseMatch?>().FirstOrDefault(m => m!.Value.Number == expected);
            match ??= await TryConfirmViaGapRetranscribeAsync(
                file, info, windowStart, windowLen, segments, profile, expected, ct);
            match ??= await TryConfirmViaReframedRereadAsync(
                file, info, mark, windowStart, windowLen, profile, profile.Language, expected, ct);
            var confirmed = match != null;
            _log?.Invoke(confirmed
                ? $"chapter {expected} mark at {FormatTimestamp(mark.StartSeconds)} confirmed"
                : $"chapter {expected} mark at {FormatTimestamp(mark.StartSeconds)} NOT confirmed - phrase not found nearby");
            var corrected = confirmed && _options.Fix
                ? await ComputeMarkFixAsync(
                    file, info, mark, windowStart, windowLen, match!.Value, profile, language.Profile.Language, ct)
                : null;
            outcomes.Add(
                new ExistingMarkOutcome(mark.StartSeconds, expected, confirmed, corrected, part));
            if (!confirmed)
                failed++;
            else
                confirmedChapters.Add(new DetectedChapter(
                    expected, corrected ?? mark.StartSeconds, Sequence: part));
            RefreshChapterProgress(work, confirmedChapters);
            work.Advance(1);
        }

        return new VerifyResult(failed == 0, checkedCount, failed, confirmedChapters, outcomes,
            profile, language.DetectedLanguage, language.DetectedProbability,
            CarryOverNamedMarks(info, profile),
            namedOutcomes.Count > 0 ? namedOutcomes : null);
    }

    /// <summary>
    /// Works out where a confirmed mark really belongs (<c>--verify --fix</c>): the announcement
    /// this window found, put through the same re-transcription refinement a detection run gives
    /// every mark, minus <c>--mark-lead</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The refinement is the right machinery to borrow because it needs nothing but the audio at the
    /// mark - it locates the announcement's onset by re-transcribing shrinking windows around it,
    /// which is exactly what makes it usable here, where no Analyze has run and there is no silence
    /// list, no VAD and no jingle geometry to place a mark against. What that costs is the waveform
    /// anchor a full run applies on top (see
    /// <see cref="PreciseMarkRefiner.AnchorOnsetToSoundAsync"/>, which needs those silences and is
    /// simply skipped here): a fixed mark can therefore sit a fraction of a second later than the
    /// same chapter would land in a from-scratch run. That is the honest bargain of this mode -
    /// against a mark that was seconds out, a tenth of a second is not the problem, and a user who
    /// wants the last tenth wants a real run.
    /// </para>
    /// <para>
    /// A correction is only offered inside <see cref="DetectionTuning.VerifyFixMinShiftSeconds"/>
    /// and <see cref="DetectionTuning.VerifyFixMaxShiftSeconds"/> - see those for why each bound
    /// exists.
    /// </para>
    /// </remarks>
    /// <param name="file">Path of the audio file.</param>
    /// <param name="info">Probe result of the file.</param>
    /// <param name="mark">The pre-existing mark being corrected.</param>
    /// <param name="windowStart">Absolute start of the window the announcement was confirmed in.</param>
    /// <param name="windowLen">Length of that window in seconds.</param>
    /// <param name="match">The confirming match, in that window's own time base.</param>
    /// <param name="profile">The file's resolved language profile.</param>
    /// <param name="language">Its language code, for the refinement's upgrade-model retry.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The corrected position, or null to leave the mark where it is.</returns>
    private async Task<double?> ComputeMarkFixAsync(
        string file, MediaInfo info, Chapter mark, double windowStart, double windowLen,
        PhraseMatch match, LanguageProfile profile, string language, CancellationToken ct)
    {
        var phraseAbs = windowStart + match.PhraseStartSeconds;
        var phraseEndAbs = windowStart + match.PhraseEndSeconds;
        var refined = await _marks!.Refiner.RefinePreciseMarkAsync(
            Math.Max(0, phraseAbs - _options.MarkLeadSeconds), file, info.InputDecoder,
            // The mark being corrected already asserts which chapter this is, so a bare-number
            // refinement may take that number and no other - the tightest bound available anywhere,
            // and the one that keeps the onset walk off a year or quantity in the chapter's own
            // first sentence.
            profile.AnnouncementFor(
                match.Wording, BareNumberReading.SpokenAloneAtSegmentStart, n => n == match.Number),
            language, phraseAbs, phraseEndAbs, windowStart + windowLen,
            // No Analyze, so neither anchor has anything to work from: no silence list, and no VAD
            // region whose end would say where the music gives way to speech. Both are skipped, which
            // is the fraction of a second this mode trades away - see the remarks above.
            [], null, ct);
        var shift = Math.Abs(refined.Mark - mark.StartSeconds);
        if (shift < VerifyFixMinShiftSeconds)
            return null;
        if (shift > VerifyFixMaxShiftSeconds)
        {
            _log?.Invoke(
                $"mark at {FormatTimestamp(mark.StartSeconds)} left alone - announcement " +
                $"{shift:0.#} s away at {FormatTimestamp(refined.Mark)}, too far to be a mark " +
                "that merely drifted");
            return null;
        }
        _log?.Invoke($"mark at {FormatTimestamp(mark.StartSeconds)} corrected to " +
                     $"{FormatTimestamp(refined.Mark)} ({refined.Mark - mark.StartSeconds:+0.##;-0.##} s)");
        return refined.Mark;
    }

    /// <summary>
    /// Resolves the language profile for a file from its pre-existing chapter marks, shared by
    /// <see cref="VerifyExistingChaptersAsync"/> and <see cref="ResumeMissingMarksAsync"/>. Neither
    /// path has run a VAD pre-pass, so the marks stand in for it as
    /// <see cref="LanguageResolver"/>'s idea of where the narration is - see
    /// <see cref="LanguageResolver.ExistingMarkPositions"/>. Does not itself call
    /// <see cref="ITranscriber.ChangeLanguage"/> - every caller needs that applied at a slightly
    /// different point, so it is left to them.
    /// </summary>
    /// <param name="file">Path of the audio file.</param>
    /// <param name="info">Probe result of the file, including its pre-existing chapter marks.</param>
    /// <param name="ct">Cancellation token.</param>
    private Task<LanguageState> ResolveProfileFromExistingMarksAsync(
        string file, MediaInfo info, CancellationToken ct)
        => NewLanguageResolver().ResolveAsync(
            file, info,
            LanguageResolver.ExistingMarkPositions(info.ExistingChapters, info.DurationSeconds), ct);

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
    /// <param name="expected">The chapter number this mark is expected to confirm.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The confirming match in the outer window's own time base, or null when the phrase
    /// was not found - the position being what <c>--verify --fix</c> corrects a mark to.</returns>
    private async Task<PhraseMatch?> TryConfirmViaGapRetranscribeAsync(
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
            // An overlap at or above the chunk length leaves the walk standing still, re-reading
            // one chunk for ever. Only reachable through --set:, which validates a value for type
            // and finiteness but not for whether it leaves a loop able to advance.
            if (chunkStep <= 0)
                continue;
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
                foreach (var m in FindCappedPhraseMatches(gapSegments, profile))
                {
                    if (m.Number != expected)
                        continue;
                    // Sub-chunk time back to the outer window's, which is what the caller's own
                    // matches are expressed in and what a correction is measured from.
                    return m with
                    {
                        PhraseStartSeconds = chunkStart + m.PhraseStartSeconds,
                        PhraseEndSeconds = chunkStart + m.PhraseEndSeconds,
                    };
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Last-chance confirmation for a <c>--verify</c> mark that neither the first pass nor the gap
    /// retry could find the expected number in: a ladder of up to
    /// <see cref="DetectionTuning.VerifyRereadAttempts"/> shorter windows, each starting further
    /// before the mark than the last, read with the <c>--upgrade-model</c> recognizer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two levers at once, because they are independent and both measured. A window start moved by
    /// tens of milliseconds decides whether the chapter word is heard at all on roughly half of all
    /// framings, and at the very offset where the probing model loses the word the heavier one
    /// reads it correctly - so a ladder that changed only one of the two would leave most of its
    /// recall on the table. The evidence is on
    /// <see cref="DetectionTuning.VerifyRereadAttempts"/>.
    /// </para>
    /// <para>
    /// The framings come from <see cref="RereadFramings"/>, which clamps every one of them inside
    /// the caller's window - which is what lets the match come back in the caller's coordinates and
    /// keeps <see cref="ComputeMarkFixAsync"/>'s refinement bounded by the window that was actually
    /// verified.
    /// </para>
    /// </remarks>
    /// <param name="file">Path of the audio file.</param>
    /// <param name="info">Probe result of the file.</param>
    /// <param name="mark">The pre-existing mark being verified, which every framing is anchored on.</param>
    /// <param name="windowStart">Start of the caller's verify window, which the result is
    /// relative to.</param>
    /// <param name="windowLen">Length of the caller's verify window.</param>
    /// <param name="profile">The file's resolved language profile.</param>
    /// <param name="language">The file's resolved language, for the heavier recognizer.</param>
    /// <param name="expected">The chapter number the mark's title claims.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The match confirming the mark, in the caller's window coordinates, or null when no
    /// framing found it.</returns>
    private async Task<PhraseMatch?> TryConfirmViaReframedRereadAsync(
        string file, MediaInfo info, Chapter mark, double windowStart, double windowLen,
        LanguageProfile profile, string language, int expected, CancellationToken ct)
    {
        foreach (var (start, length) in RereadFramings(mark.StartSeconds, windowStart, windowLen))
        {
            var segments = await RereadAsync(file, info, start, length, language, ct);
            foreach (var m in FindCappedPhraseMatches(segments, profile))
            {
                if (m.Number != expected)
                    continue;
                // Back into the caller's window, which is what its own matches are expressed in and
                // what a --fix correction is measured from.
                var offset = start - windowStart;
                return m with
                {
                    PhraseStartSeconds = offset + m.PhraseStartSeconds,
                    PhraseEndSeconds = offset + m.PhraseEndSeconds,
                };
            }
            ct.ThrowIfCancellationRequested();
        }
        return null;
    }

    /// <summary>
    /// Checks one existing mark that carries no chapter number - a prologue, an epilogue or a
    /// <c>--custom</c> mapping's mark - by asking whether the phrase its title belongs to is
    /// actually spoken there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Which phrase that is comes from the mark's <em>title</em>, matched exactly as
    /// <see cref="CarryOverNamedMarks"/> matches it: the written text is all a chapter entry
    /// preserves, and this run's own titles are what a previous run of the same command wrote
    /// there. So a mark this run has no phrase for - an intro entry, another tool's mark, a
    /// <c>--custom</c> mapping left off the command line - is reported as unverifiable rather than
    /// as wrong. It is not a mark that failed; it is a question this run was not equipped to ask.
    /// </para>
    /// <para>
    /// Confirmation is the same shape as a numbered mark's, down to the reframing ladder, and for
    /// the same reason: a named mark reported wrong sends somebody to listen to a book by hand, so
    /// a framing artifact must not be allowed to look like a bad mark. What it does not share is
    /// any consequence - see <see cref="NamedMarkOutcome"/>.
    /// </para>
    /// </remarks>
    /// <param name="file">Path of the audio file.</param>
    /// <param name="info">Probe result of the file.</param>
    /// <param name="mark">The numberless mark to check.</param>
    /// <param name="windowStart">Start of this mark's verify window.</param>
    /// <param name="windowLen">Length of this mark's verify window.</param>
    /// <param name="profile">The file's resolved language profile.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<NamedMarkOutcome> CheckNamedMarkAsync(
        string file, MediaInfo info, Chapter mark, double windowStart, double windowLen,
        LanguageProfile profile, CancellationToken ct)
    {
        var title = mark.Title.Trim();
        if (profile.NamedPhrases.FirstOrDefault(p => p.TitleMatcher.IsMatch(title)) is not { } phrase)
        {
            _log?.Invoke($"mark at {FormatTimestamp(mark.StartSeconds)} (\"{mark.Title}\") " +
                         "- no chapter number and no phrase this run knows, not checked");
            return new NamedMarkOutcome(mark.StartSeconds, mark.Title, null, false);
        }

        var samples = await _audio.DecodePcmAsync(file, windowStart, windowLen, info.InputDecoder, ct);
        var segments = await TranscribeCountingAsync(samples, ct);
        LogTranscript($"verify {phrase.Kind} @{FormatTimestamp(mark.StartSeconds)}", segments);

        var confirmed = HeardHere(segments, profile, phrase);
        if (!confirmed)
            foreach (var (start, length) in RereadFramings(mark.StartSeconds, windowStart, windowLen))
            {
                confirmed = HeardHere(
                    await RereadAsync(file, info, start, length, profile.Language, ct),
                    profile, phrase);
                if (confirmed)
                    break;
                ct.ThrowIfCancellationRequested();
            }

        _log?.Invoke(confirmed
            ? $"{phrase.Kind} mark at {FormatTimestamp(mark.StartSeconds)} confirmed"
            : $"{phrase.Kind} mark at {FormatTimestamp(mark.StartSeconds)} NOT confirmed - " +
              "phrase not found nearby");
        return new NamedMarkOutcome(mark.StartSeconds, mark.Title, phrase.Kind, confirmed);
    }

    /// <summary>Whether one particular named phrase was heard in a window's transcript.</summary>
    /// <remarks>
    /// Compared by <see cref="NamedPhrase.Kind"/> rather than by the resolved title: a
    /// <c>--custom</c> mapping's title may carry group references that expand differently per
    /// match, so two marks of the same phrase can be written under two different titles and still
    /// be the same announcement.
    /// </remarks>
    /// <param name="segments">The window's transcript.</param>
    /// <param name="profile">The file's resolved language profile.</param>
    /// <param name="phrase">The phrase the mark's title identified it as.</param>
    private static bool HeardHere(
        List<TranscriptSegment> segments, LanguageProfile profile, NamedPhrase phrase)
        => FindNamedMatches(segments, profile).Any(m => m.Phrase.Kind == phrase.Kind);

    /// <summary>
    /// The windows a failed <c>--verify</c> mark is read again from: up to
    /// <see cref="DetectionTuning.VerifyRereadAttempts"/> of them, each starting further before the
    /// mark than the last and each clamped inside the caller's own window, so a match found in one
    /// can be expressed in the caller's coordinates and <c>--fix</c>'s refinement stays bounded by
    /// the window that was actually verified.
    /// </summary>
    /// <remarks>
    /// The geometry lives here rather than in either caller because both the numbered and the
    /// named check reframe the same way, and a second copy of "which windows, and in what order"
    /// is a copy that would be retuned once and not twice. A framing that clamps onto one already
    /// yielded is dropped: near the start of a file the whole ladder can collapse onto a single
    /// window, and re-reading it buys nothing but the time it takes.
    /// </remarks>
    /// <param name="markSeconds">The mark every framing is anchored on.</param>
    /// <param name="windowStart">Start of the caller's verify window.</param>
    /// <param name="windowLen">Length of the caller's verify window.</param>
    private static IEnumerable<(double Start, double Length)> RereadFramings(
        double markSeconds, double windowStart, double windowLen)
    {
        var windowEnd = windowStart + windowLen;
        var tried = new HashSet<(double Start, double Length)>();
        for (var attempt = 0; attempt < VerifyRereadAttempts; attempt++)
        {
            var lead = VerifyRereadLeadSeconds + attempt * VerifyRereadLeadStepSeconds;
            var start = Math.Max(windowStart, markSeconds - lead);
            var length = Math.Min(VerifyRereadWindowSeconds, windowEnd - start);
            if (length <= 0)
                continue;
            // Rounded to the millisecond: two framings that differ by less than that are the same
            // decode.
            if (tried.Add((Math.Round(start, 3), Math.Round(length, 3))))
                yield return (start, length);
        }
    }

    /// <summary>
    /// Reads one reframed <c>--verify</c> window, on the heavier recognizer where there is one.
    /// </summary>
    /// <remarks>
    /// The heavier one is used only where it is actually heavier. <c>--upgrade-model</c> naming
    /// something lighter than <c>--model</c> is a deliberate downgrade (see
    /// <see cref="CliOptions.UpgradeModelIsWorse"/>), and asking that one for a second opinion would
    /// be asking the wrong recognizer - but the reframing on its own is worth the pass, so the
    /// ladder still runs on the probing model.
    /// </remarks>
    /// <param name="file">Path of the audio file.</param>
    /// <param name="info">Probe result of the file.</param>
    /// <param name="start">Where the re-read starts.</param>
    /// <param name="length">How long it is.</param>
    /// <param name="language">The file's resolved language, for the heavier recognizer.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<List<TranscriptSegment>> RereadAsync(
        string file, MediaInfo info, double start, double length, string language,
        CancellationToken ct)
    {
        var samples = await _audio.DecodePcmAsync(file, start, length, info.InputDecoder, ct);
        var segments = _options.UpgradeModelIsBetter
                       && !ReferenceEquals(_upgradeTranscriber, _transcriber)
            ? await SecondOpinionAsync(samples, language, ct)
            : await TranscribeCountingAsync(samples, ct);
        LogTranscript($"verify re-read {length:0.0}s@{FormatTimestamp(start)}", segments);
        return segments;
    }

    /// <summary>
    /// Extracts an expected chapter number from a pre-existing mark's title - see
    /// <see cref="ExistingMarkTitle"/>, which owns the rules. Returns false when the title has no
    /// readable number at all, which is a mark <c>--verify</c> can neither confirm nor fault and
    /// a resume path has no chapter identity for.
    /// </summary>
    /// <param name="title">The mark's title.</param>
    /// <param name="profile">The file's resolved language profile.</param>
    /// <param name="number">Receives the chapter number on success.</param>
    private static bool TryParseExpectedNumber(string title, LanguageProfile profile, out int number)
        => ExistingMarkTitle.TryParse(title, profile, out number);

    /// <summary>
    /// The 0-based chapter sequence a pre-existing mark's title puts it in - 0 unless a previous
    /// run of this tool wrote a part prefix onto it (see
    /// <see cref="ExistingMarkTitle.TryParsePart"/>). Both resume paths need it: without it a book
    /// in parts reads as a numbering that jumps backwards, and <see cref="Normalize"/> keeps one
    /// part and throws the rest of the file's committed marks away.
    /// </summary>
    /// <param name="title">The mark's title.</param>
    /// <param name="profile">The file's resolved language profile.</param>
    private static int PartOf(string title, LanguageProfile profile)
        => ExistingMarkTitle.TryParsePart(title, profile, out var part) ? part - 1 : 0;

    /// <summary>This file's language resolver, bound to the current <see cref="_log"/> so its probe
    /// lines land in the same sinks as the rest of the file's detection log.</summary>
    private LanguageResolver NewLanguageResolver()
        => new(_options, _audio, _transcriber, _log);

    /// <summary>
    /// Transcribes decoded PCM and tallies its length toward the per-file Whisper-audio statistic
    /// (<see cref="_whisperAudioSeconds"/>). All detection-path recognition routes through here so
    /// the tally stays complete and counts re-probed audio each time it is decoded; the --verify
    /// path calls the transcriber directly, as its audio is not part of a detection run's stat.
    /// </summary>
    /// <param name="samples">16 kHz mono PCM for one probe window or gap chunk.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="transcriber">Recognizer to use; defaults to the probe transcriber. Scan
    /// passes <see cref="_upgradeTranscriber"/> so a distinct <c>--upgrade-model</c> can do the gap
    /// work while the audio and time still count toward the same statistics.</param>
    /// <param name="onProgressSeconds">Forwarded to
    /// <see cref="ITranscriber.TranscribeAsync"/>; only the long Scan chunks pass one.</param>
    private async Task<List<TranscriptSegment>> TranscribeCountingAsync(
        float[] samples, CancellationToken ct, ITranscriber? transcriber = null,
        Action<double>? onProgressSeconds = null)
    {
        _whisperAudioSeconds += samples.Length / (double)FfmpegClient.SampleRate;
        var watch = Stopwatch.StartNew();
        var segments = await (transcriber ?? _transcriber)
            .TranscribeAsync(samples, ct, onProgressSeconds);
        _whisperTranscribeSeconds += watch.Elapsed.TotalSeconds;
        return segments;
    }

    /// <summary>
    /// Logs a Whisper transcript's header line and, only with --verbose-transcripts, the segments
    /// themselves (each with its start/end time relative to the decoded window). Under plain
    /// --verbose just the header - the "&lt;length&gt;@&lt;timestamp&gt;" context - is printed, so
    /// the log stays readable without the full recognizer output.
    /// <para>
    /// The --debug file always gets the full transcript, and only that: this is the one place where
    /// the two streams carry the same event at different lengths, so the header goes to the ordinary
    /// sink alone rather than preceding every transcript in the file people go on to grep with a
    /// redundant copy of its own first field.
    /// </para>
    /// </summary>
    /// <param name="context">Description of the decoded window, e.g. "probe 50s@0:12:34.00".</param>
    /// <param name="segments">The transcribed segments.</param>
    private void LogTranscript(string context, List<TranscriptSegment> segments)
    {
        if (_options.VerboseTranscripts)
        {
            _log?.Invoke(FormatTranscript(context, segments));
            return;
        }
        _plainLog?.Invoke(context);
        _debug?.Invoke(FormatTranscript(context, segments));
    }

    /// <summary>
    /// <see cref="PhraseMatching.FindPhraseMatches"/> with
    /// <see cref="CliOptions.EffectiveMaxChapterNumber"/> applied: a match whose parsed number sits
    /// above the cap is dropped (and logged under --verbose) rather than handed on. Every pass
    /// funnels its matching through here - Probe, Scan, the gap chunk scan and --verify alike -
    /// so an implausible number can enter the chapter sequence by no route, neither as a mark of its
    /// own nor as the upper bound that turns everything below it into a gap to hunt for. Without a
    /// cap this is exactly <see cref="PhraseMatching.FindPhraseMatches"/>.
    /// </summary>
    /// <param name="segments">The transcript segments to search, in whatever time base the caller
    /// works in (this method neither reads nor rewrites the timings).</param>
    /// <param name="profile">Language profile supplying the chapter phrase and number parsing.</param>
    /// <param name="mergeBoundarySegIndex">Passed straight through to
    /// <see cref="PhraseMatching.FindPhraseMatches"/>.</param>
    /// <param name="reading">The same, and defaulted the same: a caller that says nothing gets the
    /// narrow <c>--chapter-phrase none</c> reading, which is the safe answer for every pass that
    /// has no bounded hole to justify the wider one.</param>
    private IEnumerable<PhraseMatch> FindCappedPhraseMatches(
        List<TranscriptSegment> segments, LanguageProfile profile, int? mergeBoundarySegIndex = null,
        BareNumberReading reading = BareNumberReading.SpokenAloneAtSegmentStart)
        => FindCappedPhraseReadings(segments, profile, mergeBoundarySegIndex, reading).Select(g => g[0]);

    /// <summary>
    /// The same, over <see cref="PhraseMatching.FindPhraseReadings"/>: every reading of each
    /// announcement rather than only the one that claimed it, with the cap applied to each of them
    /// separately. A group whose every reading is above the cap disappears entirely, which is the
    /// same answer the capped winner alone would have given.
    /// </summary>
    /// <param name="segments">The transcript segments to search, in the caller's time base.</param>
    /// <param name="profile">Language profile supplying the chapter phrase and number parsing.</param>
    /// <param name="mergeBoundarySegIndex">Passed straight through.</param>
    /// <param name="reading">The same, and defaulted the same.</param>
    private IEnumerable<IReadOnlyList<PhraseMatch>> FindCappedPhraseReadings(
        List<TranscriptSegment> segments, LanguageProfile profile, int? mergeBoundarySegIndex = null,
        BareNumberReading reading = BareNumberReading.SpokenAloneAtSegmentStart)
    {
        foreach (var group in FindPhraseReadings(segments, profile, mergeBoundarySegIndex, reading))
        {
            var kept = new List<PhraseMatch>(group.Count);
            foreach (var match in group)
            {
                if (_options.EffectiveMaxChapterNumber is { } cap && match.Number > cap)
                {
                    // Walks the same fallback chain EffectiveMaxChapterNumber does, so the line
                    // names the option the reader actually typed - a cap of 60 attributed to a
                    // --max-chapter-number nobody passed is a number with no visible source. The
                    // options being mutually exclusive is what lets these be plain tests rather
                    // than an ordering. The last case is the default nobody typed, credited to
                    // --max-chapter-number as the option that documents it and the one to reach
                    // for after reading this line.
                    var option =
                        _options.ChapterCount != null ? "--chapter-count"
                        : _options.MaxChapterNumber == null && _options.MaxChapters is > 0 ? "--max-chapters"
                        : "--max-chapter-number";
                    _log?.Invoke($"discarded chapter {match.Number} - above the {option} cap of {cap}");
                    continue;
                }
                kept.Add(match);
            }
            if (kept.Count > 0)
                yield return kept;
        }
    }
}
