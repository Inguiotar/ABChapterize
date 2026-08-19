// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Xunit;
using ABChapterize.Audio;
using ABChapterize.Cli;
using ABChapterize.Detection;
using ABChapterize.Language;
using ABChapterize.Transcription;
using ABChapterize.Ui;
using ABChapterize.Vad;
using static ABChapterize.Detection.DetectionTuning;

namespace ABChapterize.Tests;

/// <summary>
/// Tests for the <see cref="ChapterDetector"/> pipeline using a scripted audio source and
/// transcriber: probe placement after silences, phrase/number matching, gap resolution via
/// full transcription (Scan), jingle anchoring, and the pure helper functions.
/// The fake file has a duration of 3600 s and a size of 3600 bytes, so all byte-based
/// progress arithmetic works with 1 byte per second.
/// </summary>
public sealed class ChapterDetectorTests : IDisposable
{
    private const double Duration = 3600;
    private static readonly MediaInfo Info = new(Duration, (long)Duration, 0);

    private readonly string _dir;
    private readonly string _file;

    /// <summary>Creates a temp .m4b file so <see cref="CliOptions.Parse"/> accepts the target.</summary>
    public ChapterDetectorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"abchapterize-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "book.m4b");
        File.WriteAllText(_file, "x");
    }

    /// <summary>Removes the temp directory.</summary>
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>
    /// Audio source returning a fixed silence list; decoding just records the requested
    /// window start so the scripted transcriber can look up what is "heard" there.
    /// </summary>
    private sealed class FakeAudioSource : IAudioSource
    {
        /// <summary>Silences reported by the fake silence scan.</summary>
        public List<Silence> Silences { get; init; } = [];

        /// <summary>Start positions of all decode requests, in call order.</summary>
        public List<double> DecodeStarts { get; } = [];

        /// <summary>All decode requests as (start, duration), in call order - durations included
        /// so tests can assert that a planned window end (e.g. a snapped shared border) actually
        /// shortened or extended the decode itself, not just where the next decode started.</summary>
        public List<(double Start, double? Duration)> DecodeWindows { get; } = [];

        private readonly List<(double Start, float[] Samples)> _pcmScript = [];

        /// <summary>Scripts real PCM sample amplitudes for the decode window starting near
        /// <paramref name="start"/> - for tests that need actual waveform content (e.g.
        /// precise marking's quiet-snap step), unlike <see cref="ScriptedTranscriber"/>'s
        /// text-only script, which ignores samples entirely. Decodes with no script still return
        /// the default all-zero buffer below.</summary>
        public void AddPcm(double start, float[] samples) => _pcmScript.Add((start, samples));

        /// <inheritdoc/>
        public Task<List<Silence>> DetectSilencesAsync(
            string file, double durationSeconds, double minSilenceSeconds, double noiseDb,
            Action<double>? progress, string? inputDecoder, CancellationToken ct)
            => Task.FromResult(Silences);

        /// <inheritdoc/>
        public Task<float[]> DecodePcmAsync(
            string file, double startSeconds, double? durationSeconds, string? inputDecoder, CancellationToken ct)
        {
            DecodeStarts.Add(startSeconds);
            DecodeWindows.Add((startSeconds, durationSeconds));
            var hit = _pcmScript.FirstOrDefault(e => Math.Abs(e.Start - startSeconds) < 0.25);
            return Task.FromResult(hit.Samples ?? new float[16000]);
        }

        /// <inheritdoc/>
        /// <remarks>The PCM stream itself is never scripted - VAD is scripted directly via
        /// <see cref="FakeVad"/>, which ignores the (empty) stream passed to it - but the
        /// consumer callback must still be invoked, exactly as the real ffmpeg-backed
        /// implementation does, so FakeVad.CallCount reflects reality.</remarks>
        public async Task<List<Silence>> DetectSilencesAndStreamPcmAsync(
            string file, double durationSeconds, double minSilenceSeconds, double noiseDb,
            Func<IAsyncEnumerable<float[]>, CancellationToken, Task> consumePcm,
            Action<double>? progress, string? inputDecoder, CancellationToken ct)
        {
            await consumePcm(EmptyPcm(), ct);
            return Silences;
        }

        /// <summary>An empty PCM stream, for <see cref="DetectSilencesAndStreamPcmAsync"/>'s
        /// fake decode - FakeVad ignores its contents entirely.</summary>
        private static async IAsyncEnumerable<float[]> EmptyPcm()
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    /// <summary>Voice activity detector returning a fixed, scripted list of speech segments.</summary>
    private sealed class FakeVad : IVoiceActivityDetector
    {
        /// <summary>Speech segments to return; empty means the whole file is non-speech.</summary>
        public List<SpeechSegment> Speech { get; init; } = [];

        /// <summary>Number of times <see cref="DetectSpeechAsync"/> was called.</summary>
        public int CallCount { get; private set; }

        /// <inheritdoc/>
        public Task<List<SpeechSegment>> DetectSpeechAsync(IAsyncEnumerable<float[]> pcm, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(Speech);
        }
    }

    /// <summary>
    /// Transcriber that returns scripted segments depending on the start position of the
    /// most recent decode request; unscripted windows yield no speech.
    /// </summary>
    private sealed class ScriptedTranscriber : ITranscriber
    {
        private readonly FakeAudioSource _audio;
        private readonly List<(double Start, double MinWindowSeconds, double MaxWindowSeconds,
                               List<TranscriptSegment> Segments)> _script = [];

        /// <summary>Creates a transcriber that follows the decode requests of <paramref name="audio"/>.</summary>
        public ScriptedTranscriber(FakeAudioSource audio) => _audio = audio;

        /// <summary>The audio source backing this transcriber, exposed so a test's script
        /// callback can also script raw PCM (<see cref="FakeAudioSource.AddPcm"/>) without a
        /// separate hook into <c>DetectFullAsync</c>.</summary>
        public FakeAudioSource Audio => _audio;

        /// <summary>
        /// Scripts speech at a place in the file: <paramref name="segments"/> carry times relative
        /// to <paramref name="start"/>, so a call reads as "the window at <paramref name="start"/>
        /// transcribes to this" while actually pinning each segment to an absolute position
        /// (<paramref name="start"/> plus its own offset). Which decode later picks a segment up is
        /// decided by that absolute position alone - see <see cref="TranscribeAsync"/>.
        /// </summary>
        /// <param name="start">Absolute time the segment offsets are measured from.</param>
        /// <param name="segments">The scripted speech, in window-relative time.</param>
        public void Add(double start, params TranscriptSegment[] segments)
            => _script.Add((start, 0, double.PositiveInfinity, [.. segments]));

        /// <summary>
        /// Scripts speech the recognizer only hears when the decode is at most
        /// <paramref name="maxWindowSeconds"/> long - real Whisper's one framing artifact that
        /// nothing else in this class can express. A window past its 30 s decode chunk re-frames the
        /// audio and can drop a lone word inside a jingle entirely, which is what loses an
        /// announcement no shorter window has any trouble with (Gruelfin.m4b's prologue, 2026-07-30:
        /// heard from the same position over 17.5 s and 23.5 s, gone over 30.0 s and 50.0 s).
        /// Scripted per entry rather than as a property of the whole transcriber, so a test can put
        /// one chunk-sensitive announcement in a file whose other chapters are heard normally.
        /// </summary>
        /// <param name="maxWindowSeconds">Longest decode that still hears this speech.</param>
        /// <param name="start">Absolute time the segment offsets are measured from.</param>
        /// <param name="segments">The scripted speech, in window-relative time.</param>
        public void AddWithin(double maxWindowSeconds, double start, params TranscriptSegment[] segments)
            => _script.Add((start, 0, maxWindowSeconds, [.. segments]));

        /// <summary>
        /// <see cref="AddWithin"/>'s mirror: speech the recognizer only hears when the decode is
        /// <em>longer</em> than <paramref name="minWindowSeconds"/>. Pairing the two at one position
        /// is how a test spells the failure that motivated
        /// <see cref="RefinedNumberVote"/> - one stretch of audio read as one
        /// chapter number by a wide window and as another by a narrow one, which is not a fault in
        /// either reading but the ordinary consequence of where the announcement lands inside
        /// Whisper's fixed 30 s mel frame ("Die Cyber-Brutzellen" 7:01:30, 2026-08-01: "Kapitel 40"
        /// from 44.2 s, "Kapitel 14" from every window under 7 s).
        /// </summary>
        /// <param name="minWindowSeconds">Shortest decode that still hears this speech.</param>
        /// <param name="start">Absolute time the segment offsets are measured from.</param>
        /// <param name="segments">The scripted speech, in window-relative time.</param>
        public void AddBeyond(double minWindowSeconds, double start, params TranscriptSegment[] segments)
            => _script.Add((start, minWindowSeconds, double.PositiveInfinity, [.. segments]));

        /// <summary>Called at the start of every <see cref="TranscribeAsync"/>, so a test can
        /// sample detector-external state (progress, say) at the exact moments this transcriber
        /// is being used.</summary>
        public Action? OnTranscribe { get; set; }

        /// <summary>Called after every progress report this transcriber makes, once the detector's
        /// own handler has seen it. <see cref="OnTranscribe"/>'s counterpart for the *inside* of a
        /// long transcription: the only moment at which a test can sample what a chunk still being
        /// recognized has told the progress tracker.</summary>
        public Action<double>? OnProgressReported { get; set; }

        /// <summary>
        /// Returns whatever scripted speech actually begins inside the window just decoded,
        /// re-based to that window's own start - the same contract Whisper has, and the reason a
        /// decode's exact boundaries matter here rather than only its rough position. Matching the
        /// script by proximity to the window start instead (as this once did, within 0.25 s) makes
        /// every decode near a scripted entry produce that entry's phrase, which cannot express the
        /// one thing precise marking is built to measure: that a probe starting <em>past</em> an
        /// announcement's onset no longer hears it first. Under proximity matching
        /// <see cref="PreciseMarkRefiner.FindOnsetEdgeAsync"/> converges on the tolerance constant
        /// rather than on the scripted onset, so its results were an artifact of this class.
        /// <para>
        /// A segment starting before the window is dropped rather than clipped: real Whisper would
        /// emit some leading fragment of it, which no script here has a way to spell, and treating
        /// the window as starting mid-word would let a phrase be "heard" from a position it has
        /// already passed - exactly the false positive the edge walk must not see.
        /// </para>
        /// </summary>
        /// <param name="samples">Ignored; the decode's own boundaries carry the information.</param>
        /// <param name="ct">Ignored; nothing here blocks.</param>
        /// <param name="onProgressSeconds">Reported per segment, as the real recognizer does, so a
        /// test can observe what a long transcription tells the progress bar on its way through.</param>
        public Task<List<TranscriptSegment>> TranscribeAsync(
            float[] samples, CancellationToken ct, Action<double>? onProgressSeconds = null)
        {
            OnTranscribe?.Invoke();
            var (start, duration) = _audio.DecodeWindows[^1];
            var end = duration is { } seconds ? start + seconds : double.PositiveInfinity;
            // Decode starts are arithmetic (mark minus a lead, plus a step), so a segment scripted
            // to sit exactly on one lands a rounding error either side of it.
            const double epsilon = 1e-9;
            var segments = _script
                .Where(entry => end - start <= entry.MaxWindowSeconds + epsilon &&
                                end - start > entry.MinWindowSeconds - epsilon)
                .SelectMany(entry => entry.Segments.Select(seg => (Absolute: entry.Start + seg.StartSeconds,
                                                                   End: entry.Start + seg.EndSeconds, seg)))
                .Where(x => x.Absolute >= start - epsilon && x.Absolute < end)
                .OrderBy(x => x.Absolute)
                .Select(x => new TranscriptSegment(
                    x.Absolute - start, x.End - start, x.seg.Text, x.seg.Probability))
                .ToList();
            foreach (var segment in segments)
            {
                onProgressSeconds?.Invoke(segment.EndSeconds);
                OnProgressReported?.Invoke(segment.EndSeconds);
            }
            return Task.FromResult(segments);
        }

        /// <summary>Language auto-detection result to return; defaults to a confident "en". Every
        /// call answers the same, which is what a test about anything other than language
        /// resolution wants - see <see cref="LanguageAnswers"/> for the other case.</summary>
        public (string Language, float Probability) DetectedLanguage { get; set; } = ("en", 0.99f);

        /// <summary>
        /// Per-call language answers, consumed in order and taking precedence over
        /// <see cref="DetectedLanguage"/> while any remain. This is what makes
        /// <see cref="LanguageResolver"/>'s re-probing testable at all: its whole subject is a
        /// detector that answers differently at different points in the same book, which a single
        /// fixed answer cannot express. Running out falls back to
        /// <see cref="DetectedLanguage"/> rather than throwing, so a script may cover just the
        /// first few probes.
        /// </summary>
        public Queue<(string Language, float Probability)> LanguageAnswers { get; } = new();

        /// <summary>Languages this transcriber was told to switch to, in call order.</summary>
        public List<string> LanguageChanges { get; } = [];

        /// <summary>Number of times <see cref="DetectLanguageWithProbabilityAsync"/> was called.</summary>
        public int DetectLanguageCalls { get; private set; }

        /// <inheritdoc/>
        public Task<(string Language, float Probability)> DetectLanguageWithProbabilityAsync(float[] samples, CancellationToken ct)
        {
            DetectLanguageCalls++;
            return Task.FromResult(LanguageAnswers.Count > 0 ? LanguageAnswers.Dequeue() : DetectedLanguage);
        }

        /// <inheritdoc/>
        public void ChangeLanguage(string language) => LanguageChanges.Add(language);
    }

    /// <summary>One speech segment starting at the given offset within the decode window.</summary>
    private static TranscriptSegment Seg(double startSeconds, string text, double confidence = 1.0)
        => new(startSeconds, startSeconds + 2, text, confidence);

    /// <summary>
    /// The --mark-lead every test below is written against, pinned rather than inherited from
    /// <see cref="DetectionTuning.DefaultMarkLeadSeconds"/>: the lead is a matter of taste and will
    /// be retuned again, while what these tests are about is <em>which onset</em> placement picked,
    /// not how far in front of it the mark sits. Inheriting the default would make eighty-odd
    /// expectations - most of them about gap tracking or language handling - churn on a change that
    /// says nothing about detection. <see cref="MarkLead_ShiftsEveryDefaultModeMark"/> covers the
    /// offset itself, and CliOptionsTests covers the default's value.
    /// </summary>
    private const double PinnedMarkLeadSeconds = 0.25;

    /// <summary>
    /// Builds validated options with the temp file as target, at
    /// <see cref="PinnedMarkLeadSeconds"/> unless the test asks for a lead of its own, and with
    /// <c>--noise-floor</c> pinned to the default level.
    /// <para>
    /// The noise floor is pinned for a different reason than the lead: not because its value would
    /// churn expectations, but because its <em>automatic</em> mode decodes excerpts from across the
    /// file before Analyze runs, and a good many tests below count decodes or assert that a given
    /// stretch was never read. Those excerpts would show up in every one of them while saying
    /// nothing about what the test is checking. What the measurement itself decides is
    /// <see cref="SilenceThresholdProbeTests"/>'s subject instead.
    /// </para>
    /// <para>
    /// The trailing scan is pinned off for the noise-floor reason rather than the mark-lead one: it
    /// runs by default from 0.11.0 on, it transcribes everything after the last chapter found, and
    /// almost every fixture below puts something at the end of the file that Probe was never meant
    /// to reach. Left on, it would add a chapter to test after test that is about candidate
    /// geometry, threshold adaptation or gap planning and says nothing about the tail. Tests whose
    /// subject <em>is</em> the tail use <see cref="OptionsWithTrailingScan"/>, which is also what
    /// keeps the default itself covered - see
    /// <see cref="TrailingScan_FindsAChapterAfterTheLastOneDetected"/> and CliOptionsTests.
    /// </para>
    /// </summary>
    private CliOptions Options(params string[] args)
        => BuildOptions([.. args, "--no-trailing-scan"]);

    /// <summary>Like <see cref="Options"/>, but leaving the trailing scan at its default (on) for the
    /// tests that are about it.</summary>
    private CliOptions OptionsWithTrailingScan(params string[] args) => BuildOptions(args);

    /// <summary>
    /// Like <see cref="Options"/>, but keeping Probe in its ordinary one-sweep shape rather than
    /// either of the two-part ones. A file with at least one jingle per hour of play time reads its
    /// music first and its pauses afterwards (<see cref="JingleFirstScan"/>) - and these fixtures
    /// run an hour, so a single scripted non-speech region trips it - while a file with none reads
    /// its longest pauses first instead (<see cref="DescendingSilenceScan"/>), which is every other
    /// fixture. Both defer candidates, and a test about how a pause window and a jingle window share
    /// a seam, about which of two announcements is heard first, or about the in-flight recovery a
    /// sequence gap triggers, is a test about the walk that defers nothing.
    /// <para>
    /// Pinned with a <c>--custom</c> mapping rather than an option of its own, because that is the
    /// pin the tool has, and one pin covers both: a mapping that may be announced between two
    /// chapters is exactly what either gate declines to run without, the pauses between two
    /// consecutive chapters being the one place such a mapping could be heard. The phrase is written
    /// so that nothing a fixture scripts can ever match it.
    /// </para>
    /// </summary>
    private CliOptions OptionsInOneSweep(params string[] args)
        => Options([.. args, "--custom", "/nothing-ever-says-this/:Marker"]);

    /// <summary>The shared body of the two option builders above.</summary>
    /// <param name="args">The option list, already carrying whatever trailing-scan choice was made.</param>
    private CliOptions BuildOptions(string[] args)
    {
        string[] withFloor = args.Contains("--noise-floor")
            ? args
            : [.. args, "--noise-floor", $"{DetectionTuning.DefaultSilenceNoiseDb}"];
        return CliOptions.Parse(args.Contains("--mark-lead") || args.Contains("-k")
            ? [.. withFloor, _file]
            : [.. withFloor, "--mark-lead", $"{PinnedMarkLeadSeconds}", _file])!;
    }

    /// <summary>The named marks reduced to what these tests are actually about - which phrase
    /// produced what title, where - so that a bookkeeping field like
    /// <see cref="DetectedMark.PhraseTimeSeconds"/> can change without rewriting every
    /// expectation.</summary>
    private static List<(string Kind, string Title, double TimeSeconds)> Named(DetectionResult result)
        => result.NamedMarks.Select(m => (m.Kind, m.Title, m.TimeSeconds)).ToList();

    /// <summary>Named-mark counterpart of <see cref="AssertChapters"/>, with the same one-sided
    /// tolerance - named marks go through the very same precise-marking refinement.</summary>
    private static void AssertNamed(
        IReadOnlyList<(string Kind, string Title, double TimeSeconds)> expected, DetectionResult result)
    {
        var actual = Named(result);
        Assert.Equal(expected.Select(m => (m.Kind, m.Title)), actual.Select(m => (m.Kind, m.Title)));
        foreach (var (want, got) in expected.Zip(actual))
            AssertMarkTime(want.Title, want.TimeSeconds, got.TimeSeconds);
    }

    /// <summary>
    /// Asserts the detected chapters, allowing precise marking its measurement resolution: a mark
    /// may sit up to one <see cref="DetectionTuning.PreciseMarkFixedStepSeconds"/> plus one
    /// <see cref="DetectionTuning.PreciseMarkSilenceAnchorSeconds"/> <em>before</em> the expected
    /// position, but never after it.
    /// <para>
    /// The expected values are where the heuristic alone would put the mark - the phrase onset the
    /// script states, less <see cref="PinnedMarkLeadSeconds"/>. Precise mark
    /// does not trust that position; it brackets the true onset by bisection and reports the last
    /// probe that still confirmed the phrase, which lands within one step below the real edge
    /// rather than exactly on it, and then anchors that onset back onto the end of the silence in
    /// front of it. These fixtures universally script the phrase a fraction of a second after their
    /// silence ends - a gap real audio does not show, see
    /// <see cref="DetectionTuning.PreciseMarkSilenceAnchorSeconds"/> - so the anchor moves nearly
    /// every mark in this file by that fraction, which is what the second half of the tolerance
    /// covers. Pinning these tests to the bisection's own arithmetic would make
    /// eighty-odd assertions - most of them about gap tracking or language handling, not placement -
    /// churn on every tuning change, and would assert an artifact instead of the contract precise
    /// mark actually owes its callers. What the placement steps owe exactly is asserted exactly,
    /// by the tests named for them.
    /// </para>
    /// </summary>
    private static void AssertChapters(
        IReadOnlyList<DetectedChapter> expected, IReadOnlyList<DetectedChapter> actual)
    {
        Assert.Equal(expected.Select(c => c.Number), actual.Select(c => c.Number));
        Assert.Equal(expected.Select(c => c.Confidence), actual.Select(c => c.Confidence));
        foreach (var (want, got) in expected.Zip(actual))
            AssertMarkTime($"chapter {want.Number}", want.TimeSeconds, got.TimeSeconds);
    }

    /// <summary>Single-chapter counterpart of <see cref="AssertChapters"/>, for the tests that only
    /// pin one mark out of a longer sequence.</summary>
    private static void AssertContainsChapter(
        DetectedChapter expected, IReadOnlyList<DetectedChapter> actual)
    {
        var got = Assert.Single(actual, c => c.Number == expected.Number);
        Assert.Equal(expected.Confidence, got.Confidence);
        AssertMarkTime($"chapter {expected.Number}", expected.TimeSeconds, got.TimeSeconds);
    }

    /// <summary>Asserts that a decode began at the given position, tolerating the binary-float dust
    /// a computed position carries - a gap's start is a refined mark, not a round number.</summary>
    private static void AssertDecodedFrom(ScriptedTranscriber transcriber, double start)
        => Assert.Contains(transcriber.Audio.DecodeStarts, d => Math.Abs(d - start) < 1e-6);

    /// <summary>The one-sided tolerance every mark assertion shares - see
    /// <see cref="AssertChapters"/> for what each half of it pays for. One-sided on purpose: a mark
    /// landing <em>later</em> than the announcement is the failure every placement step exists to
    /// prevent, so no amount of it is tolerated here.</summary>
    private static void AssertMarkTime(string what, double expected, double actual)
    {
        // A mark at 0 is clamped rather than measured, so it has no tolerance to spend.
        var floor = expected == 0
            ? 0
            : expected - DetectionTuning.PreciseMarkFixedStepSeconds
                       - DetectionTuning.PreciseMarkSilenceAnchorSeconds;
        Assert.True(actual <= expected + 1e-9 && actual >= floor - 1e-9,
            $"{what}: expected a mark in [{floor}, {expected}], got {actual}");
    }

    /// <summary>Runs the detector against the given silences and script.</summary>
    private async Task<DetectionResult> DetectAsync(
        CliOptions options, List<Silence> silences, Action<ScriptedTranscriber> script, FakeVad? vad = null)
        => (await DetectWithTranscriberAsync(options, silences, script, vad)).Result;

    /// <summary>Runs the detector, also returning the transcriber for language-detection assertions.</summary>
    private async Task<(DetectionResult Result, ScriptedTranscriber Transcriber)> DetectWithTranscriberAsync(
        CliOptions options, List<Silence> silences, Action<ScriptedTranscriber> script, FakeVad? vad = null)
    {
        var (result, transcriber, _) = await DetectFullAsync(options, silences, script, vad);
        return (result, transcriber);
    }

    /// <summary>Runs the detector, also returning the audio source for decode-window assertions
    /// (e.g. which probes the adaptive --min-silence-length threshold actually decoded).</summary>
    private async Task<(DetectionResult Result, ScriptedTranscriber Transcriber, FakeAudioSource Audio)> DetectFullAsync(
        CliOptions options, List<Silence> silences, Action<ScriptedTranscriber> script, FakeVad? vad = null)
    {
        var audio = new FakeAudioSource { Silences = silences };
        var transcriber = new ScriptedTranscriber(audio);
        script(transcriber);
        var detector = new ChapterDetector(options, audio, transcriber, vad);
        var result = await detector.DetectAsync(_file, Info, new WorkTracker(), default, CancellationToken.None);
        return (result, transcriber, audio);
    }

    /// <summary>
    /// Runs the detector with a separate transcriber for Scan (as <c>--upgrade-model</c> sets up):
    /// Probe uses <paramref name="probeScript"/>, Scan uses <paramref name="upgradeScript"/>, both
    /// keyed off the same fake audio source. Returns the result plus both transcribers, so a test
    /// can prove that a gap was filled by the upgrade transcriber rather than the Probe one.
    /// </summary>
    /// <param name="options">The run's options.</param>
    /// <param name="silences">The fake audio source's silences.</param>
    /// <param name="probeScript">Scripts the probe transcriber.</param>
    /// <param name="upgradeScript">Scripts the upgrade transcriber.</param>
    /// <param name="log">Collects the --verbose lines, for tests that assert on which recovery
    /// route a chapter came back through; null to run without logging.</param>
    /// <param name="vad">Scripted voice activity, for the Scan-model paths that only exist with a
    /// VAD pre-pass (the jingle re-read); null runs the silence-only geometry.</param>
    private async Task<(DetectionResult Result, ScriptedTranscriber Probe, ScriptedTranscriber Upgrade)> DetectWithUpgradeTranscriberAsync(
        CliOptions options, List<Silence> silences,
        Action<ScriptedTranscriber> probeScript, Action<ScriptedTranscriber> upgradeScript,
        List<string>? log = null, FakeVad? vad = null)
    {
        var audio = new FakeAudioSource { Silences = silences };
        var probe = new ScriptedTranscriber(audio);
        var upgrade = new ScriptedTranscriber(audio);
        probeScript(probe);
        upgradeScript(upgrade);
        var detector = new ChapterDetector(options, audio, probe, vad, upgradeTranscriber: upgrade);
        var result = await detector.DetectAsync(
            _file, Info, new WorkTracker(),
            log is null ? default : new DetectionLog(log.Add, null), CancellationToken.None);
        return (result, probe, upgrade);
    }

    /// <summary>Runs the detector with --verbose logging captured, for assertions on what
    /// DetectAsync actually printed (e.g. that a probe's log only shows freshly transcribed
    /// segments, not the reused ones restated at window-relative time).</summary>
    private async Task<(DetectionResult Result, List<string> Log, FakeAudioSource Audio)> DetectWithLogAsync(
        CliOptions options, List<Silence> silences, Action<ScriptedTranscriber> script, FakeVad? vad = null)
    {
        var audio = new FakeAudioSource { Silences = silences };
        var transcriber = new ScriptedTranscriber(audio);
        script(transcriber);
        var log = new List<string>();
        var detector = new ChapterDetector(options, audio, transcriber, vad);
        var result = await detector.DetectAsync(_file, Info, new WorkTracker(), new DetectionLog(log.Add, null), CancellationToken.None);
        return (result, log, audio);
    }

    /// <summary>Runs the detector with the --debug sink captured separately from the ordinary one,
    /// for assertions on what only the debug file receives.</summary>
    /// <param name="options">The run's options.</param>
    /// <param name="silences">The fake audio source's silences.</param>
    /// <param name="script">Scripts the transcriber.</param>
    /// <param name="vad">Scripted VAD, or null for no pre-pass.</param>
    private async Task<List<string>> DetectWithDebugAsync(
        CliOptions options, List<Silence> silences, Action<ScriptedTranscriber> script, FakeVad? vad = null)
    {
        var audio = new FakeAudioSource { Silences = silences };
        var transcriber = new ScriptedTranscriber(audio);
        script(transcriber);
        var debug = new List<string>();
        var detector = new ChapterDetector(options, audio, transcriber, vad);
        await detector.DetectAsync(
            _file, Info, new WorkTracker(), new DetectionLog(_ => { }, debug.Add), CancellationToken.None);
        return debug;
    }

    [Fact]
    public async Task SequentialChapters_AreDetectedAtSilenceEnds()
    {
        var result = await DetectAsync(
            Options(),
            [new(595, 600), new(1195, 1200)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(600, Seg(0.3, " Chapter two."));
                s.Add(1200, Seg(0.2, " Chapter three."));
            });

        Assert.False(result.GapRemains);
        AssertChapters(
            [new(1, 0.25), new(2, 600.05), new(3, 1199.95)],
            result.Chapters);
    }

    [Fact]
    public async Task Prologue_AndEpilogue_AreDetectedAsNamedMarks()
    {
        var result = await DetectAsync(
            Options(),
            [new(595, 600), new(1195, 1200), new(1795, 1800)],
            s =>
            {
                s.Add(0, Seg(0.5, " Prologue."));
                s.Add(600, Seg(0.3, " Chapter one."));
                s.Add(1200, Seg(0.2, " Chapter two."));
                s.Add(1800, Seg(0.4, " Epilogue."));
            });

        AssertChapters([new(1, 600.05), new(2, 1199.95)], result.Chapters);
        AssertNamed(
            [("prologue", "Prologue", 0.25), ("epilogue", "Epilogue", 1800.15)],
            result);
    }

    [Fact]
    public async Task Prologue_IsIgnored_OnceAChapterHasBeenFound()
    {
        // "the prologue" said inside chapter one is prose, not an announcement - the scope is
        // what keeps it from becoming a second, spurious mark.
        var result = await DetectAsync(
            Options(),
            [new(595, 600), new(1195, 1200)],
            s =>
            {
                s.Add(600, Seg(0.3, " Chapter one."));
                s.Add(1200, Seg(0.2, " As the prologue explained, chapter two."));
            });

        AssertChapters([new(1, 600.05), new(2, 1199.95)], result.Chapters);
        Assert.Empty(result.NamedMarks);
    }

    [Fact]
    public async Task Epilogue_IsIgnored_BeforeAnyChapterHasBeenFound()
    {
        var result = await DetectAsync(
            Options(),
            [new(595, 600), new(1195, 1200)],
            s =>
            {
                s.Add(0, Seg(0.5, " With a prologue and an epilogue."));
                s.Add(600, Seg(0.3, " Chapter one."));
            });

        AssertChapters([new(1, 600.05)], result.Chapters);
        AssertNamed([("prologue", "Prologue", 0.25)], result);
    }

    [Fact]
    public async Task Prologue_LastMatchInScopeWins()
    {
        // Front matter routinely names what is coming before the narrator announces it; the real
        // announcement is by construction the later of the two, and only one mark survives.
        var result = await DetectAsync(
            Options(),
            [new(295, 300), new(595, 600)],
            s =>
            {
                s.Add(0, Seg(0.5, " Read by someone. Contains a prologue."));
                s.Add(300, Seg(0.3, " Prologue."));
                s.Add(600, Seg(0.2, " Chapter one."));
            });

        AssertNamed([("prologue", "Prologue", 300.05)], result);
    }

    [Fact]
    public async Task NamedMarks_AreDropped_WhenNoChapterWasFoundAtAll()
    {
        // A book whose chapter announcements were never heard is a failed detection; a lone
        // prologue must not be what makes the file worth rewriting.
        var result = await DetectAsync(
            Options("--early-abort", "0"),
            [new(595, 600)],
            s => s.Add(0, Seg(0.5, " Prologue.")));

        Assert.Empty(result.Chapters);
        Assert.Empty(result.NamedMarks);
    }

    [Fact]
    public async Task Epilogue_IsDropped_WhenItDoesNotFollowTheLastChapter()
    {
        // "epilogue" is an ordinary word, and a match between two chapters is prose rather than the
        // book's epilogue - which by definition has no chapter after it.
        var result = await DetectAsync(
            Options(),
            [new(595, 600), new(1195, 1200), new(1795, 1800)],
            s =>
            {
                s.Add(600, Seg(0.3, " Chapter one."));
                s.Add(1200, Seg(0.2, " Epilogue."));
                s.Add(1800, Seg(0.4, " Chapter two."));
            });

        AssertChapters([new(1, 600.05), new(2, 1800.15)], result.Chapters);
        Assert.Empty(result.NamedMarks);
    }

    [Fact]
    public async Task Epilogue_KeepsTheAnnouncementLaterInTheFile_WhateverPassHeardItFirst()
    {
        // Probe marks the real epilogue after the last chapter; Re-probe then re-probes the 1-3 gap
        // with the upgrade model and hears "epilogue" inside chapter 2's prose. Later in the run,
        // earlier in the book - and the book is what decides.
        var (result, _, _) = await DetectWithUpgradeTranscriberAsync(
            Options("--model", "small", "--upgrade-model", "turbo"),
            [new(595, 600), new(1195, 1200), new(1795, 1800), new(2395, 2400)],
            probe =>
            {
                probe.Add(600, Seg(0.3, " Chapter one."));
                probe.Add(1800, Seg(0.4, " Chapter three."));
                probe.Add(2400, Seg(0.2, " Epilogue."));
            },
            upgrade =>
            {
                upgrade.Add(1200, Seg(0.2, " Chapter two, in which the epilogue is foretold."));
                upgrade.Add(1800, Seg(0.4, " Chapter three."));
            });

        // Chapter 2 proves Re-probe really read that window, so the mark below survived the match
        // rather than the match never happening.
        AssertChapters([new(1, 600.05), new(2, 1199.95), new(3, 1800.15)], result.Chapters);
        AssertNamed([("epilogue", "Epilogue", 2399.95)], result);
    }

    [Fact]
    public void ResolveEpiloguePlacement_HandsAMidBookEpilogueToACustomMapping()
    {
        // The mapping matches the same announcement but produced no mark of its own - its own
        // placement can fail where the epilogue's succeeded, both being refined against their own
        // phrase. The user asked for a mark on this word, so the mark becomes theirs.
        var profile = Options("--custom", "/epilog/:Zwischenspiel").DefaultProfile;
        var resolved = ChapterDetector.ResolveEpiloguePlacement(
            [new("epilogue", "Epilogue", 1200, PhraseTimeSeconds: 1200.5, Text: "Epilogue.")],
            [new DetectedChapter(1, 600), new DetectedChapter(2, 1800)], profile, null);

        var mark = Assert.Single(resolved);
        Assert.Equal("custom 1", mark.Kind);
        Assert.Equal("Zwischenspiel", mark.Title);
        Assert.Equal(1200, mark.TimeSeconds);
        Assert.True(mark.Repeatable);
    }

    [Fact]
    public void ResolveEpiloguePlacement_DropsAMidBookEpilogue_WhoseMappingAlreadyMarkedIt()
    {
        var profile = Options("--custom", "/epilog/:Zwischenspiel").DefaultProfile;
        var resolved = ChapterDetector.ResolveEpiloguePlacement(
            [
                new("epilogue", "Epilogue", 1200, PhraseTimeSeconds: 1200.5, Text: "Epilogue."),
                new("custom 1", "Zwischenspiel", 1200, PhraseTimeSeconds: 1200.5, Repeatable: true,
                    Text: "Epilogue."),
            ],
            [new DetectedChapter(1, 600), new DetectedChapter(2, 1800)], profile, null);

        Assert.Equal([("custom 1", "Zwischenspiel")], resolved.Select(m => (m.Kind, m.Title)));
    }

    [Fact]
    public void ResolveEpiloguePlacement_KeepsAnEpilogueAfterTheLastChapter()
    {
        var resolved = ChapterDetector.ResolveEpiloguePlacement(
            [new("epilogue", "Epilogue", 1900, Text: "Epilogue.")],
            [new DetectedChapter(1, 600), new DetectedChapter(2, 1800)],
            Options().DefaultProfile, null);

        Assert.Equal("epilogue", Assert.Single(resolved).Kind);
    }

    [Fact]
    public void ResolveEpiloguePlacement_LeavesAnEpilogueAlone_WhenNoChapterWasFound()
    {
        // Nothing for it to be after: with --ignore-chapter-numbers a chapterless named list is all
        // there is, and dropping the one mark the file yielded would be its own kind of wrong.
        var resolved = ChapterDetector.ResolveEpiloguePlacement(
            [new("epilogue", "Epilogue", 1900, Text: "Epilogue.")], [],
            Options().DefaultProfile, null);

        Assert.Equal("epilogue", Assert.Single(resolved).Kind);
    }

    [Fact]
    public async Task NamedMarks_AreNotDetected_WhenTheirPhraseIsSwitchedOff()
    {
        var result = await DetectAsync(
            Options("--prologue-phrase", "", "--epilogue-phrase", ""),
            [new(595, 600)],
            s =>
            {
                s.Add(0, Seg(0.5, " Prologue."));
                s.Add(600, Seg(0.3, " Chapter one."));
            });

        AssertChapters([new(1, 600.05)], result.Chapters);
        Assert.Empty(result.NamedMarks);
    }

    [Fact]
    public async Task NamedMarks_AreLocalized_ByLang()
    {
        var result = await DetectAsync(
            Options("--lang", "de"),
            [new(595, 600)],
            s =>
            {
                s.Add(0, Seg(0.5, " Prolog."));
                s.Add(600, Seg(0.3, " Kapitel eins."));
            });

        AssertNamed([("prologue", "Prolog", 0.25)], result);
    }

    [Fact]
    public async Task NamedMarks_DoNotCountAsChaptersForGapDetection()
    {
        // The whole reason they travel in their own list: a numberless mark between chapter 1 and
        // chapter 2 must leave the sequence - and therefore GapRemains - completely untouched.
        var result = await DetectAsync(
            Options(),
            [new(595, 600), new(1195, 1200)],
            s =>
            {
                s.Add(600, Seg(0.3, " Chapter one."));
                s.Add(1200, Seg(0.2, " Chapter two. And so the epilogue neared."));
            });

        Assert.False(result.GapRemains);
        Assert.Empty(result.MissingNumbers);
        AssertChapters([new(1, 600.05), new(2, 1199.95)], result.Chapters);
    }

    [Fact]
    public async Task CustomPhrase_MarksEveryOccurrence()
    {
        // Unlike the prologue, a --custom phrase is repeatable: each announcement gets its own mark
        // instead of the last one replacing all the others.
        var result = await DetectAsync(
            Options("--custom", "zwischenspiel:Zwischenspiel"),
            [new(595, 600), new(1195, 1200), new(1795, 1800)],
            s =>
            {
                s.Add(600, Seg(0.3, " Chapter one."));
                s.Add(1200, Seg(0.2, " Zwischenspiel."));
                s.Add(1800, Seg(0.4, " Zwischenspiel."));
            });

        AssertChapters([new(1, 600.05)], result.Chapters);
        AssertNamed(
            [("custom 1", "Zwischenspiel", 1199.95), ("custom 1", "Zwischenspiel", 1800.15)],
            result);
    }

    [Fact]
    public async Task CustomPhrase_TaggedOnce_KeepsOnlyTheLastMatch()
    {
        // "once" is NamedPhrase.Repeatable inverted, which is the prologue's and epilogue's own
        // rule: the last announcement inside the scope wins, since front matter routinely mentions
        // what is coming before the narrator actually announces it.
        var result = await DetectAsync(
            Options("--custom", "[once]zwischenspiel:Zwischenspiel"),
            [new(595, 600), new(1195, 1200), new(1795, 1800)],
            s =>
            {
                s.Add(600, Seg(0.3, " Chapter one."));
                s.Add(1200, Seg(0.2, " Zwischenspiel."));
                s.Add(1800, Seg(0.4, " Zwischenspiel."));
            });

        AssertNamed([("custom 1", "Zwischenspiel", 1800.15)], result);
    }

    [Fact]
    public async Task CustomPhrase_TaggedWithAMarkCap_StopsAtIt()
    {
        // The per-mapping counterpart of the file-wide --custom cap, and counted the same way
        // round: the first ones are kept and the rest dropped.
        var (result, log, _) = await DetectWithLogAsync(
            Options("--custom", "[max=2]zwischenspiel:Zwischenspiel"),
            [new(595, 600), new(1195, 1200), new(1795, 1800), new(2395, 2400)],
            s =>
            {
                s.Add(600, Seg(0.3, " Chapter one."));
                s.Add(1200, Seg(0.2, " Zwischenspiel."));
                s.Add(1800, Seg(0.4, " Zwischenspiel."));
                s.Add(2400, Seg(0.4, " Zwischenspiel."));
            });

        AssertNamed(
            [("custom 1", "Zwischenspiel", 1199.95), ("custom 1", "Zwischenspiel", 1800.15)],
            result);
        Assert.Contains(log, l => l.Contains("this mapping's own limit of 2 mark(s) is reached"));
        // The file-wide cap is untouched by a mapping reaching its own.
        Assert.False(result.CustomMarkLimitHit);
    }

    [Fact]
    public async Task CustomPhrase_TaggedBeforeFirstChapter_IsDroppedAfterOne_AndSaysSoOnce()
    {
        // The prologue's scope, available to a mapping by name. The note is capped at one line per
        // phrase: an out-of-scope drop is silent by design, and it took a hint the user wrote by
        // hand to make the silence a support question.
        var (result, log, _) = await DetectWithLogAsync(
            Options("--custom", "[before-first-chapter]/vorwort/:Vorwort"),
            [new(595, 600), new(1195, 1200), new(1795, 1800)],
            s =>
            {
                s.Add(0, Seg(0.5, " Vorwort."));
                s.Add(600, Seg(0.3, " Chapter one."));
                s.Add(1200, Seg(0.2, " Vorwort."));
                s.Add(1800, Seg(0.4, " Vorwort."));
            });

        AssertNamed([("custom 1", "Vorwort", 0.25)], result);
        Assert.Single(log, l => l.Contains("outside the \"before-first-chapter\" position"));
    }

    [Fact]
    public async Task CustomPhrase_TaggedAfterLastChapter_LosesItsMidBookMatches()
    {
        // The one hint that cannot be a pre-placement filter: which chapter is last is unknown
        // until every pass has finished, so the mid-book marks are placed and then dropped at the
        // end. Precision only - it saves no transcription, and the doc comment says so.
        // Pinned to one sweep because the mid-book "Nachwort" sits between two consecutive
        // chapters, which the descending shape passes over - so it is never placed to be dropped.
        // That is the shape working: an after-last-chapter mark there would have been dropped
        // anyway, and no scoped mapping that could survive can sit in such a stretch.
        var (result, log, _) = await DetectWithLogAsync(
            OptionsInOneSweep("--custom", "[after-last-chapter]nachwort:Nachwort"),
            [new(595, 600), new(1195, 1200), new(1795, 1800), new(2395, 2400)],
            s =>
            {
                s.Add(600, Seg(0.3, " Chapter one."));
                s.Add(1200, Seg(0.2, " Nachwort."));
                s.Add(1800, Seg(0.4, " Chapter two."));
                s.Add(2400, Seg(0.4, " Nachwort."));
            });

        AssertChapters([new(1, 600.05), new(2, 1800.15)], result.Chapters);
        AssertNamed([("custom 1", "Nachwort", 2399.95)], result);
        Assert.Contains(log, l => l.Contains("as its \"after-last-chapter\" hint asks"));
    }

    [Fact]
    public void DropOutOfScopeNamedMarks_LeavesEveryOtherMappingAlone()
    {
        // Pure bookkeeping, so it is tested without a decoder: only a mapping that asked to sit
        // after the last chapter is judged, and only against that chapter.
        var profile = Options(
            "--custom", "[after-last-chapter]nachwort:Nachwort;zwischenspiel:Zwischenspiel")
            .DefaultProfile;
        var resolved = ChapterDetector.DropOutOfScopeNamedMarks(
            [
                new("custom 1", "Nachwort", 1200, Repeatable: true),
                new("custom 2", "Zwischenspiel", 1200, Repeatable: true),
                new("custom 1", "Nachwort", 1900, Repeatable: true),
            ],
            [new DetectedChapter(1, 600), new DetectedChapter(2, 1800)], profile, null);

        Assert.Equal(
            [("custom 2", 1200d), ("custom 1", 1900d)],
            resolved.Select(m => (m.Kind, m.TimeSeconds)));
    }

    [Fact]
    public void DropOutOfScopeNamedMarks_KeepsEverything_WhenNoChapterWasFound()
    {
        // With no chapter there is no "last chapter" for anything to be after, and dropping every
        // such mark would turn a book whose chapters were never heard into a book with nothing at
        // all - the strictly worse of the two answers.
        var profile = Options("--custom", "[after-last-chapter]nachwort:Nachwort").DefaultProfile;
        var resolved = ChapterDetector.DropOutOfScopeNamedMarks(
            [new("custom 1", "Nachwort", 1200, Repeatable: true)], [], profile, null);

        Assert.Single(resolved);
    }

    [Fact]
    public async Task CustomPhrase_IsNotBoundByTheChapterSequence()
    {
        // Both before the first chapter and after the last: neither scope rule applies to it.
        var result = await DetectAsync(
            Options("--custom", "/zeit[- ]?tafel/:Zeittafel"),
            [new(595, 600), new(1195, 1200)],
            s =>
            {
                s.Add(0, Seg(0.5, " Zeittafel."));
                s.Add(600, Seg(0.3, " Chapter one."));
                s.Add(1200, Seg(0.2, " Zeit-Tafel."));
            });

        AssertNamed(
            [("custom 1", "Zeittafel", 0.25), ("custom 1", "Zeittafel", 1199.95)],
            result);
    }

    [Fact]
    public async Task CustomTitle_ExpandsACapturingGroup()
    {
        var result = await DetectAsync(
            Options("--custom", "/(?<kind>interlude|intermezzo)/:The ${kind}"),
            [new(595, 600), new(1195, 1200)],
            s =>
            {
                s.Add(600, Seg(0.3, " Chapter one."));
                s.Add(1200, Seg(0.2, " Intermezzo."));
            });

        AssertNamed([("custom 1", "The Intermezzo", 1199.95)], result);
    }

    [Fact]
    public async Task CustomMarks_AreCapped_PerFile()
    {
        // The "--custom the:the" accident: a phrase matching ordinary prose must not place a mark
        // every few seconds for the length of a book.
        // 20 more candidates than the cap allows, spaced wider than both the probe window and the
        // duplicate-match tolerance so each one is a decode of its own and a mark of its own.
        var silences = Enumerable.Range(1, MaxCustomMarksPerFile + 20)
            .Select(i => new Silence(i * 25 - 3, i * 25))
            .ToList();
        var result = await DetectAsync(
            Options("--min-silence-length", "1",
                    "--custom", "interlude:Interlude"),
            silences,
            s =>
            {
                foreach (var silence in silences)
                    s.Add(silence.EndSeconds, Seg(0.2, " Interlude."));
                s.Add(0, Seg(0.5, " Chapter one."));
            });

        Assert.True(result.CustomMarkLimitHit);
        Assert.Equal(MaxCustomMarksPerFile, result.NamedMarks.Count);
    }

    [Fact]
    public async Task IgnoreChapterNumbers_MarksEveryAnnouncement_KeepingItsSpokenNumber()
    {
        var result = await DetectAsync(
            Options("--ignore-chapter-numbers",
                    "--custom", "zwischenspiel:Zwischenspiel"),
            [new(595, 600), new(1195, 1200), new(1795, 1800)],
            s =>
            {
                s.Add(600, Seg(0.3, " Chapter one."));
                s.Add(1200, Seg(0.2, " Zwischenspiel."));
                s.Add(1800, Seg(0.4, " Chapter one."));
            });

        Assert.Empty(result.Chapters);
        Assert.False(result.GapRemains);
        // Two chapter 1s, and neither is a gap, a duplicate or a reason to re-probe anything.
        AssertNamed(
            [("chapter", "Chapter 1", 600.05), ("custom 1", "Zwischenspiel", 1199.95),
             ("chapter", "Chapter 1", 1800.15)],
            result);
    }

    [Fact]
    public async Task IgnoreChapterNumbers_MarksAnAnnouncementThatCarriesNoNumber()
    {
        var result = await DetectAsync(
            Options("--ignore-chapter-numbers"),
            [new(595, 600)],
            s => s.Add(600, Seg(0.3, " Chapter. The rain had not let up.")));

        AssertNamed([("chapter", "Chapter", 600.05)], result);
    }

    [Fact]
    public async Task IgnoreChapterNumbers_KeepsTheNamedMarks_WithoutASingleChapter()
    {
        // With numbers on, a lone prologue is a failed detection and gets dropped; with them off it
        // is exactly what the run was asked for.
        var result = await DetectAsync(
            Options("--ignore-chapter-numbers", "--early-abort", "0"),
            [new(595, 600)],
            s => s.Add(0, Seg(0.5, " Prologue.")));

        AssertNamed([("prologue", "Prologue", 0.25)], result);
    }

    [Fact]
    public async Task IgnoreChapterNumbers_StillScopesThePrologueAtTheFirstChapter()
    {
        // The chapters live in the named list here, so the prologue's scope has to close on those -
        // otherwise "as the prologue explained" would walk the mark into the middle of the book.
        var result = await DetectAsync(
            Options("--ignore-chapter-numbers"),
            [new(295, 300), new(595, 600)],
            s =>
            {
                s.Add(0, Seg(0.5, " Prologue."));
                s.Add(300, Seg(0.3, " Chapter one."));
                s.Add(600, Seg(0.2, " As the prologue explained."));
            });

        AssertNamed([("prologue", "Prologue", 0.25), ("chapter", "Chapter 1", 300.05)], result);
    }

    [Fact]
    public async Task IgnoreChapterNumbers_DoesNotEarlyAbort_WhileNamedMarksAreFound()
    {
        // --early-abort counts "no chapter found"; with the chapters in the named list, those are
        // what proves the file is yielding something.
        var result = await DetectAsync(
            Options("--ignore-chapter-numbers", "--early-abort", "1",
                    "--custom", "interlude:Interlude"),
            [new(27, 30), new(597, 600)],
            s =>
            {
                // The first one lands inside the one-minute window, so by the time the second
                // candidate is judged the file has already proven itself.
                s.Add(30, Seg(0.3, " Interlude."));
                s.Add(600, Seg(0.2, " Interlude."));
            });

        Assert.False(result.EarlyAborted);
        Assert.Equal(2, result.NamedMarks.Count);
    }

    [Fact]
    public async Task NumberWords_BeforeThePhrase_AreUnderstood()
    {
        var result = await DetectAsync(
            Options("--lang", "de"),
            [new(595, 600)],
            s =>
            {
                s.Add(0, Seg(0.5, " Erstes Kapitel."));
                s.Add(600, Seg(0.3, " Zweites Kapitel."));
            });

        AssertChapters([new(1, 0.25), new(2, 600.05)], result.Chapters);
    }

    [Fact]
    public async Task RegexPhrase_WithCaptureGroup_ParsesTheNumber()
    {
        var result = await DetectAsync(
            Options("-c", @"/chapter (\d+)/"),
            [new(595, 600)],
            s => s.Add(600, Seg(0.3, " Chapter 12 begins.")));

        AssertChapters([new(12, 600.05)], result.Chapters);
    }

    [Fact]
    public async Task MarkLog_ReportsTheLoudnessAtTheFinalPosition_AlongsideTheConfidence()
    {
        // A real (loud) quarter second of audio scripted at chapter 2's finished mark: half-scale
        // samples are -6 dBFS, which is what the log must report next to the confidence figure.
        // Chapter 1's mark has no PCM scripted, so its all-zero window is digital silence.
        var audio = new FakeAudioSource { Silences = [new(595, 600)] };
        audio.AddPcm(599.75, Enumerable.Repeat(0.5f, 4000).ToArray()); // chapter 2's finished mark
        var transcriber = new ScriptedTranscriber(audio);
        transcriber.Add(0, Seg(0.5, " Chapter one."));
        transcriber.Add(600, Seg(0.25, " Chapter two."));
        var log = new List<string>();
        var detector = new ChapterDetector(Options(), audio, transcriber);

        await detector.DetectAsync(_file, Info, new WorkTracker(), new DetectionLog(log.Add, null), CancellationToken.None);

        // "." regardless of the machine's locale - see NumberCulture.
        Assert.Contains(log, l => l.Contains("chapter 2 detected") && l.Contains("-6.0 dBFS"));
        Assert.Contains(log, l => l.Contains("chapter 1 detected") && l.Contains("-inf dBFS"));
    }

    [Fact]
    public async Task MarkLoudness_IsNotEvenMeasured_WhenVerboseIsOff()
    {
        // The measurement costs a decode per mark, so a non-verbose run must not perform it at
        // all. Proven by the decode count: with logging off, no decode ever starts at the finished
        // mark position (0.25), only at the probe window start (0). Runs with --quick-marks so that
        // precise marking's own probes - which sweep right across 0.25 - cannot be mistaken for the
        // loudness measurement.
        var (result, _, audio) = await DetectFullAsync(
            Options("--quick-marks"), [new(595, 600)],
            s => s.Add(0, Seg(0.5, " Chapter one.")));

        Assert.Equal(0.25, Assert.Single(result.Chapters).TimeSeconds);
        Assert.DoesNotContain(0.25, audio.DecodeStarts);
    }

    [Fact]
    public async Task ChapterNumberAboveTheCap_IsDiscarded()
    {
        // "Chapter five hundred and ten" in a three-chapter book is a mishearing, not a chapter:
        // with --max-chapter-number it never enters the sequence, so nothing is left to hunt for.
        // Pinned to one sweep: the window holding it sits between chapters 1 and 2, which the
        // descending shape passes over, so the line this asserts is never reached. The cap is what
        // is under test here, not which windows a shape reads.
        var (result, log, _) = await DetectWithLogAsync(
            OptionsInOneSweep("--max-chapter-number", "12"),
            [new(595, 600), new(1195, 1200)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(600, Seg(0.3, " Chapter five hundred ten."));
                s.Add(1200, Seg(0.2, " Chapter two."));
            });

        AssertChapters([new(1, 0.25), new(2, 1199.95)], result.Chapters);
        Assert.False(result.GapRemains);
        Assert.Contains(log, l => l.Contains("discarded chapter 510"));
    }

    [Fact]
    public async Task ChapterNumberBelowTheLastAccepted_IsLoggedAsSkipped()
    {
        // Probe drops a number that does not top the last accepted one and keeps scanning, which
        // is right - but the number *was* heard, so a --verbose run has to say why it did not
        // become a mark. Without the line, this is indistinguishable from the phrase matcher
        // having missed it entirely.
        var (result, log, _) = await DetectWithLogAsync(
            Options(),
            [new(595, 600), new(1195, 1200)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(600, Seg(0.4, " Chapter two."));
                s.Add(1200, Seg(0.3, " Chapter one."));
            });

        AssertChapters([new(1, 0.25), new(2, 600.15)], result.Chapters);
        Assert.Contains(log, l =>
            l.Contains("skipped chapter 1 at 0:20:00.30") &&
            l.Contains("not above last accepted 2") &&
            l.Contains("(in-text mention?)"));
    }

    /// <summary>
    /// A wording claiming words the narrator never said loses the announcement to the wording behind
    /// it. Whisper fills a short window's opening with plausible text, and "Two chapter three" is a
    /// shape three of the reference corpus's 12,916 probe transcripts really have (2026-08-14): the
    /// number-first wording claims "Two chapter" for its earlier start and reads chapter 2, which
    /// the sequence has already had. The reading it superseded says 3, and that is the mark.
    /// </summary>
    [Fact]
    public async Task ANumberHallucinatedInFrontOfTheChapterWord_LosesToTheReadingBehindIt()
    {
        var (result, log, _) = await DetectWithLogAsync(
            Options(),
            [new(595, 600), new(1195, 1200)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(600, Seg(0.4, " Chapter two."));
                s.Add(1200, Seg(0.3, " Two chapter three."));
            });

        AssertChapters([new(1, 0.25), new(2, 600.15), new(3, 1199.95)], result.Chapters);
        Assert.Contains(log, l => l.Contains("skipped chapter 2 at 0:20:00.30"));
    }

    [Fact]
    public async Task ChapterNumberEqualToTheLastAccepted_IsLoggedWithoutTheInTextHint()
    {
        // A re-detection of the chapter just marked is a different story from a regression: it is
        // the same announcement seen again, not a mention buried in the narration, so the hint
        // that would send someone looking for one is left off.
        var (_, log, _) = await DetectWithLogAsync(
            Options(),
            [new(595, 600), new(1195, 1200)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(600, Seg(0.4, " Chapter two."));
                s.Add(1200, Seg(0.3, " Chapter two."));
            });

        Assert.Contains(log, l =>
            l.Contains("skipped chapter 2 at 0:20:00.30") &&
            l.Contains("not above last accepted 2"));
        Assert.DoesNotContain(log, l => l.Contains("in-text mention?"));
    }

    [Fact]
    public async Task ChapterNumbersRestartingMidFile_OpenASecondSequenceOnceThreeOfThemAgree()
    {
        // Real-world case ("The Forever War", 2026-08-03): the book is divided into parts and each
        // one starts its chapters over at one, so everything after part one's chapter 15 was heard,
        // numbered correctly, and then dropped for not topping the sequence. 27 announcements went
        // that way, and the file simply stopped yielding chapters a quarter of the way in.
        //
        // Four chapters, then a restart at one: the ascending run of announcements below the
        // sequence is what tells a book in parts from an in-text mention, and three is where
        // SequenceRestartRunLength draws the line. Note that the run has to be built from numbers
        // strictly *below* the sequence - a repeat that merely equals the last accepted chapter is
        // the ordinary re-detection of an announcement two windows overlapped, and says nothing
        // about parts.
        var (result, log, _) = await DetectWithLogAsync(
            Options(),
            [new(395, 400), new(795, 800), new(1195, 1200),
             new(1595, 1600), new(1995, 2000), new(2395, 2400)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(400, Seg(0.4, " Chapter two."));
                s.Add(800, Seg(0.3, " Chapter three."));
                s.Add(1200, Seg(0.3, " Chapter four."));
                s.Add(1600, Seg(0.3, " Chapter one."));
                s.Add(2000, Seg(0.3, " Chapter two."));
                s.Add(2400, Seg(0.3, " Chapter three."));
            });

        // Every announcement becomes a mark, the second part counting from one again.
        Assert.Equal([1, 2, 3, 4, 1, 2, 3], result.Chapters.Select(c => c.Number));
        Assert.Equal([0, 0, 0, 0, 1, 1, 1], result.Chapters.Select(c => c.Sequence));
        Assert.Equal(2, result.SequenceCount);
        // Nothing was lost, so nothing is reported as lost - and the boundary is not a hole, so no
        // chapter is declared missing between part one's chapter 4 and part two's chapter 1.
        Assert.Equal(0, result.SequenceRestartSkips);
        Assert.False(result.GapRemains);
        Assert.Empty(result.MissingNumbers);
        Assert.Contains(log, l => l.Contains("the chapter numbering restarts at 0:26:40"));
        Assert.Single(log, l => l.Contains("the chapter numbering restarts"));
    }

    [Fact]
    public async Task ARestartedSequence_GivesTheProgressBarOneNumberPerPart()
    {
        // The same geometry as the restart fixture above, watched from the progress bar's side:
        // once the second part opens, the bar has two numbers to show rather than one, and the
        // last part's alone would read as the book having gone backwards from 4 to 3.
        var tracker = new WorkTracker();
        var audio = new FakeAudioSource
        {
            Silences = [new(395, 400), new(795, 800), new(1195, 1200),
                        new(1595, 1600), new(1995, 2000), new(2395, 2400)],
        };
        var transcriber = new ScriptedTranscriber(audio);
        transcriber.Add(0, Seg(0.5, " Chapter one."));
        transcriber.Add(400, Seg(0.4, " Chapter two."));
        transcriber.Add(800, Seg(0.3, " Chapter three."));
        transcriber.Add(1200, Seg(0.3, " Chapter four."));
        transcriber.Add(1600, Seg(0.3, " Chapter one."));
        transcriber.Add(2000, Seg(0.3, " Chapter two."));
        transcriber.Add(2400, Seg(0.3, " Chapter three."));
        var detector = new ChapterDetector(Options(), audio, transcriber, null);

        var result = await detector.DetectAsync(
            _file, Info, tracker, new DetectionLog(_ => { }, null), CancellationToken.None);

        Assert.Equal(2, result.SequenceCount);
        Assert.Equal([4, 3], tracker.HighestChapters);
        Assert.Equal(0, tracker.MissingChapters);
    }

    [Fact]
    public async Task ARestartedSequence_KeepsCountingWhereTheOldOneWouldHaveSwallowedIt()
    {
        // The mixing-up hazard, and the reason a run being tracked outranks the ordinary sequence
        // test: part one ends at chapter 3, so part two's chapters 1 and 2 sit below the sequence
        // but its chapter 3 equals it and its chapter 4 *tops* it. Judged on the sequence alone,
        // that 4 would be accepted as part one's chapter 4 and the two numberings would be spliced
        // into one - which is exactly the failure the pending run exists to prevent.
        var (result, _, _) = await DetectWithLogAsync(
            Options(),
            [new(395, 400), new(795, 800), new(1195, 1200), new(1595, 1600), new(1995, 2000),
             new(2395, 2400)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(400, Seg(0.4, " Chapter two."));
                s.Add(800, Seg(0.3, " Chapter three."));
                s.Add(1200, Seg(0.3, " Chapter one."));
                s.Add(1600, Seg(0.3, " Chapter two."));
                s.Add(2000, Seg(0.3, " Chapter three."));
                s.Add(2400, Seg(0.3, " Chapter four."));
            });

        Assert.Equal([1, 2, 3, 1, 2, 3, 4], result.Chapters.Select(c => c.Number));
        Assert.Equal([0, 0, 0, 1, 1, 1, 1], result.Chapters.Select(c => c.Sequence));
        Assert.Equal(2, result.SequenceCount);
    }

    [Fact]
    public async Task ARestartHeardTwiceByOverlappingWindows_StillConfirmsOnThreeDistinctChapters()
    {
        // Overlapping probe windows routinely hear one announcement twice, and the second hearing
        // must neither restart the run nor count towards it: "The Forever War"'s own log has part
        // two's chapter 2 rejected four times over. A run that reset on a repeat would never reach
        // three on a real book.
        var (result, _, _) = await DetectWithLogAsync(
            Options(),
            [new(395, 400), new(795, 800), new(1195, 1200), new(1595, 1600), new(1995, 2000),
             new(2395, 2400)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(400, Seg(0.4, " Chapter two."));
                s.Add(800, Seg(0.3, " Chapter three."));
                s.Add(1200, Seg(0.3, " Chapter one."));
                s.Add(1600, Seg(0.3, " Chapter one."));
                s.Add(2000, Seg(0.3, " Chapter two."));
                s.Add(2400, Seg(0.3, " Chapter three."));
            });

        Assert.Equal(2, result.SequenceCount);
        Assert.Equal([1, 2, 3, 1, 2, 3], result.Chapters.Select(c => c.Number));
    }

    [Fact]
    public async Task TwoAnnouncementsBelowTheSequence_AreNotEnoughToOpenANewPart()
    {
        // One short of the run length: the announcements are held back, never confirmed, and end up
        // booked as lost exactly as they were before parts existed. Deliberately the conservative
        // side of the line - a part is a claim about the whole rest of the file.
        var (result, _, _) = await DetectWithLogAsync(
            Options(),
            [new(395, 400), new(795, 800), new(1195, 1200), new(1595, 1600)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(400, Seg(0.4, " Chapter two."));
                s.Add(800, Seg(0.3, " Chapter three."));
                s.Add(1200, Seg(0.3, " Chapter one."));
                s.Add(1600, Seg(0.3, " Chapter two."));
            });

        Assert.Equal([1, 2, 3], result.Chapters.Select(c => c.Number));
        Assert.Equal(1, result.SequenceCount);
    }

    [Fact]
    public async Task ASingleInTextMention_IsNotReportedAsARestartingSequence()
    {
        // The other side of that line: one announcement below the sequence is prose mentioning an
        // earlier chapter, which every book does, and calling that a restart would put a misleading
        // note on the summary line of ordinary files. Thirteen of the fourteen books measured on
        // 2026-08-03 produced no run of these at all.
        var (result, log, _) = await DetectWithLogAsync(
            Options(),
            [new(595, 600), new(1195, 1200), new(1795, 1800)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(600, Seg(0.4, " Chapter two."));
                s.Add(1200, Seg(0.3, " Chapter three."));
                s.Add(1800, Seg(0.3, " Chapter one."));
            });

        Assert.Equal(0, result.SequenceRestartSkips);
        Assert.DoesNotContain(log, l => l.Contains("the chapter numbering appears to restart"));
    }

    /// <summary>
    /// The same script with no option at all: <see cref="CliOptions.DefaultChapterCount"/> caps the
    /// run anyway, so the mishearing never becomes a chapter and the real chapter 2 behind it
    /// survives. Before the default existed this is where a 510 displaced it.
    /// <para>
    /// Pinned to one sweep for the same reason as the test above it: the misheard window sits
    /// between two consecutive chapters, which the descending shape passes over.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ChapterNumberAboveTheDefaultCap_IsDiscarded()
    {
        var (result, log, _) = await DetectWithLogAsync(
            OptionsInOneSweep(),
            [new(595, 600), new(1195, 1200)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(600, Seg(0.3, " Chapter five hundred ten."));
                s.Add(1200, Seg(0.2, " Chapter two."));
            });

        Assert.Equal([1, 2], result.Chapters.Select(c => c.Number));
        // Nothing to report as unverified either: the number never entered the sequence at all.
        Assert.True(result.UnverifiedNumbers is null or []);
        Assert.Contains(log, l => l.Contains("discarded chapter 510"));
    }

    [Fact]
    public async Task ChapterNumberAboveTheSequence_IsAccepted_WhenTheCapAllowsIt()
    {
        // A cap raised past the mishearing puts the old behaviour back: 510 becomes a chapter of its
        // own and displaces the real chapter 2 behind it, which is then "not above the last accepted
        // chapter 510".
        //
        // What it does not do is declare 2..509 missing. Nothing corroborated the 510, so the
        // sequence refuses to measure the book by it (see DetectedChapter.NumberUnverified) and no
        // pass goes hunting behind it; the summary line says so instead.
        var result = await DetectAsync(
            Options("--max-chapter-number", "5000"),
            [new(595, 600), new(1195, 1200)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(600, Seg(0.3, " Chapter five hundred ten."));
                s.Add(1200, Seg(0.2, " Chapter two."));
            });

        Assert.Contains(result.Chapters, c => c.Number == 510);
        Assert.DoesNotContain(result.Chapters, c => c.Number == 2);
        Assert.False(result.GapRemains);
        Assert.Equal([510], result.UnverifiedNumbers);
    }

    [Fact]
    public async Task ChapterNumberAtTheCap_IsStillAccepted()
    {
        var result = await DetectAsync(
            Options("--max-chapter-number", "2"),
            [new(595, 600)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(600, Seg(0.3, " Chapter two."));
            });

        AssertChapters([new(1, 0.25), new(2, 600.05)], result.Chapters);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.25)]
    [InlineData(0.35)]
    [InlineData(1.5)]
    public async Task MarkLead_ShiftsEveryDefaultModeMark(double lead)
    {
        // The one test that does not use the pinned lead: every other expectation here is written
        // against a fixed offset, so this is where the offset itself has to be pinned to the
        // announcement instead. Both onsets are far enough from 0 for a 1.5 s lead not to clamp.
        var result = await DetectAsync(
            Options("--mark-lead", $"{lead}"),
            [new(295, 300), new(595, 600)],
            s =>
            {
                s.Add(300, Seg(2.0, " Chapter one."));
                s.Add(600, Seg(2.0, " Chapter two."));
            });

        AssertChapters([new(1, 302 - lead), new(2, 602 - lead)], result.Chapters);
    }

    [Fact]
    public async Task PhraseTooLongAfterSilence_IsIgnored_WithoutJingle()
    {
        var result = await DetectAsync(
            Options(),
            [new(595, 600)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(600, Seg(6.0, " Chapter two.")); // starts later than 5 s after the silence
            });

        AssertChapters([new DetectedChapter(1, 0.25)], result.Chapters);
        Assert.False(result.GapRemains);
    }

    /// <summary>Candidates every 300 s, none of which ever yield a chapter phrase - the
    /// scenario --early-abort exists to cut short.</summary>
    private static readonly List<Silence> NoChapterSilences =
        [.. Enumerable.Range(1, 11).Select(i => new Silence(i * 300 - 5, i * 300))];

    [Fact]
    public async Task EarlyAbort_StopsProbing_OnceThresholdReached_WithNoChapterFound()
    {
        var (result, _, audio) = await DetectFullAsync(
            Options("--early-abort", "10"),
            NoChapterSilences,
            _ => { });

        Assert.True(result.EarlyAborted);
        Assert.Empty(result.Chapters);
        // 720 s is the language sample LanguageResolver takes before Probe begins - a fifth of the
        // way into the file, not a candidate. After it come the candidates: the region start, and
        // one per silence, each opening a SilenceLeadInSeconds run-up inside its own silence (300 s
        // silence -> 297 s window). Those three are all under the 600 s/10 min threshold; the one at
        // 897 s triggers the abort before it is ever probed.
        Assert.Equal([720.0, 0.0, 297.0, 597.0], audio.DecodeStarts);
    }

    [Fact]
    public async Task EarlyAbort_Zero_DisablesTheFeature_AndProbesTheWholeFile()
    {
        var (result, _, audio) = await DetectFullAsync(
            Options("--early-abort", "0"),
            NoChapterSilences,
            _ => { });

        Assert.False(result.EarlyAborted);
        Assert.Empty(result.Chapters);
        Assert.Contains(3297.0, audio.DecodeStarts);
    }

    [Fact]
    public async Task EarlyAbort_DoesNotFire_OnceAChapterHasBeenFound()
    {
        var (result, _, audio) = await DetectFullAsync(
            Options("--early-abort", "10"),
            NoChapterSilences,
            s => s.Add(300, Seg(0.3, " Chapter one.")));

        Assert.False(result.EarlyAborted);
        AssertChapters([new DetectedChapter(1, 300.05)], result.Chapters);
        Assert.Contains(3297.0, audio.DecodeStarts);
    }

    [Fact]
    public async Task ExpectedStartChapter_Aborts_WhenFirstChapterFoundIsBelowExpectation()
    {
        var result = await DetectAsync(
            Options("--expected-start-chapter", "15"),
            [new(595, 600)],
            s => s.Add(0, Seg(0.5, " Chapter three.")));

        Assert.Equal(3, result.BelowExpectedStartNumber);
        Assert.Empty(result.Chapters);
        Assert.False(result.GapRemains);
    }

    [Fact]
    public async Task ExpectedStartChapter_DoesNotFire_WhenFirstChapterMeetsOrExceedsExpectation()
    {
        var result = await DetectAsync(
            Options("--expected-start-chapter", "3"),
            [new(595, 600)],
            s => s.Add(0, Seg(0.5, " Chapter three.")));

        Assert.Null(result.BelowExpectedStartNumber);
        AssertChapters([new DetectedChapter(3, 0.25)], result.Chapters);
    }

    [Fact]
    public async Task ExpectedStartChapter_HuntsAndFillsLeadingGap_ForANonOneStart()
    {
        // Probe only finds chapter 13; with --expected-start-chapter 12, Scan must hunt the
        // leading gap for chapter 12 and finds it in the very first chunk.
        var (result, _, audio) = await DetectFullAsync(
            Options("--expected-start-chapter", "12"),
            [new(1195, 1200)],
            s =>
            {
                s.Add(1200, Seg(0.2, " Chapter thirteen."));
                s.Add(0, Seg(10, " Chapter twelve.")); // Scan chunk 1 [0, 600], phrase at 10
            });

        Assert.False(result.GapRemains);
        AssertChapters([new(12, 9.75), new(13, 1199.95)], result.Chapters);
        Assert.DoesNotContain(590.0, audio.DecodeStarts);
    }

    [Fact]
    public async Task ExpectedStartChapter_ReportsGapRemains_WhenTheScanCannotFillTheLeadingGap()
    {
        // Probe finds only chapter 4; with --expected-start-chapter 1, Scan hunts for 1-3, but
        // the audio never actually says them, so the leading gap stays unresolved.
        var result = await DetectAsync(
            Options("--expected-start-chapter", "1"),
            [new(1195, 1200)],
            s => s.Add(1200, Seg(0.2, " Chapter four.")));

        Assert.True(result.GapRemains);
        Assert.Equal([1, 2, 3], result.MissingNumbers);
        AssertChapters([new(4, 1199.95)], result.Chapters);
    }

    [Fact]
    public async Task Prologue_ImpliesAStartAtChapterOne_AndTheLeadingGapIsHunted()
    {
        // No --expected-start-chapter, but the prologue says this file holds the book's beginning,
        // so chapters 1-3 under the first one found are really missing rather than another part's.
        var result = await DetectAsync(
            Options(),
            [new(595, 600), new(1195, 1200)],
            s =>
            {
                s.Add(0, Seg(0.5, " Prologue."));
                s.Add(1200, Seg(0.2, " Chapter four."));
            });

        Assert.True(result.GapRemains);
        Assert.Equal([1, 2, 3], result.MissingNumbers);
        AssertChapters([new(4, 1199.95)], result.Chapters);
    }

    [Fact]
    public async Task NoPrologue_LeavesTheLeadingGapUnraised()
    {
        // The same book without the prologue is indistinguishable from a split-book part starting
        // at chapter 4 - which is exactly why nothing is hunted below it.
        var result = await DetectAsync(
            Options(),
            [new(595, 600), new(1195, 1200)],
            s => s.Add(1200, Seg(0.2, " Chapter four.")));

        Assert.False(result.GapRemains);
        Assert.Empty(result.MissingNumbers);
        AssertChapters([new(4, 1199.95)], result.Chapters);
    }

    [Fact]
    public async Task ExpectedStartChapter_OverrulesThePrologueImplication()
    {
        // A split part carrying its own prologue is described by -e: the option wins, so nothing
        // under chapter 4 is hunted even though a prologue was found.
        var result = await DetectAsync(
            Options("--expected-start-chapter", "4"),
            [new(595, 600), new(1195, 1200)],
            s =>
            {
                s.Add(0, Seg(0.5, " Prologue."));
                s.Add(1200, Seg(0.2, " Chapter four."));
            });

        Assert.False(result.GapRemains);
        Assert.Empty(result.MissingNumbers);
        AssertNamed([("prologue", "Prologue", 0.25)], result);
    }

    [Fact]
    public void ExpectedStartFor_PrefersTheOption_OverThePrologueImplication()
    {
        var options = Options("--expected-start-chapter", "12");
        Assert.Equal(12, GapPlanning.ExpectedStartFor(options, [Prologue()]));
        Assert.Equal(12, GapPlanning.ExpectedStartFor(options, []));
    }

    [Fact]
    public void ExpectedStartFor_ImpliesOne_OnlyWhenAPrologueWasFound()
    {
        var options = Options();
        Assert.Equal(1, GapPlanning.ExpectedStartFor(options, [Prologue()]));
        Assert.Null(GapPlanning.ExpectedStartFor(options, []));
        Assert.Null(GapPlanning.ExpectedStartFor(
            options, [new DetectedMark("custom 1", "Interlude", 100)]));
    }

    [Fact]
    public void ExpectedStartFor_ImpliesNothing_WithoutAChapterSequence()
    {
        // --ignore-chapter-numbers has no numbered sequence for a start to be the start of.
        Assert.Null(GapPlanning.ExpectedStartFor(
            Options("--ignore-chapter-numbers"), [Prologue()]));
    }

    /// <summary>A detected prologue mark, for the expected-start-chapter rule's own tests.</summary>
    private static DetectedMark Prologue() => new(NamedPhrase.PrologueKind, "Prologue", 0.25);

    [Fact]
    public async Task SequenceGap_IsResolved_ByFullTranscription()
    {
        // The probe after the first silence hears nothing, so Probe yields chapters 1 and 3;
        // Scan must transcribe the region in between and find chapter 2 at 600 s. The first
        // Scan chunk's border (natural end 600.5) snaps to the [595, 600] silence's mid-point
        // (597.5), so the second chunk starts exactly there - that is where the phrase is heard.
        var result = await DetectAsync(
            Options("--quick-marks"),
            [new(595, 600), new(1195, 1200)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(1200, Seg(0.2, " Chapter three."));
                s.Add(597.5, Seg(2.5, " Chapter two.")); // gap chunk starting at the snapped seam
            });

        Assert.False(result.GapRemains);
        AssertChapters(
            [new(1, 0.25), new(2, 599.75), new(3, 1199.95)],
            result.Chapters);
    }

    [Fact]
    public async Task UnresolvedSequenceGap_IsReported()
    {
        var result = await DetectAsync(
            Options(),
            [new(595, 600), new(1195, 1200)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(1200, Seg(0.2, " Chapter four."));
            });

        Assert.True(result.GapRemains);
        Assert.Equal([2, 3], result.MissingNumbers);
    }

    [Fact]
    public void MissingNumbersInGap_ReturnsTheChapterNumbersBoundingEachGap()
    {
        var chapters = new List<DetectedChapter> { new(2, 500), new(3, 900), new(6, 2000) };
        var gaps = GapPlanning.FindGaps(chapters, Duration, expectedStartChapter: 1); // (0, 500) and (900, 2000)
        Assert.Equal([1], GapPlanning.MissingNumbersInGap(chapters, gaps[0], expectedStartChapter: 1)); // leading gap: 1
        Assert.Equal([4, 5], GapPlanning.MissingNumbersInGap(chapters, gaps[1])); // 3 -> 6: 4, 5
    }

    [Fact]
    public void ChapterProgress_ReportsNoMissingLeadingChapters_WithoutAnExpectedStartChapter()
    {
        // A split-book part starting at chapter 2 must not report chapter 1 as missing - mirrors
        // FindGaps_RaisesNoLeadingGap_WithoutAnExpectedStartChapter below for the live progress
        // display/log line (GitHub-reported regression: "still missing: 1" with no -e given).
        var chapters = new List<DetectedChapter> { new(2, 500) };
        var (highest, missing) = GapPlanning.ChapterProgress(chapters);
        Assert.Equal([2], highest);
        Assert.Empty(missing);
    }

    [Fact]
    public void ChapterProgress_StillReportsInteriorGaps_WithoutAnExpectedStartChapter()
    {
        var chapters = new List<DetectedChapter> { new(2, 500), new(3, 900), new(6, 2000) };
        var (highest, missing) = GapPlanning.ChapterProgress(chapters);
        Assert.Equal([6], highest);
        Assert.Equal([4, 5], missing);
    }

    [Fact]
    public void ChapterProgress_ReportsLeadingGap_WhenExpectedStartChapterIsGiven()
    {
        var chapters = new List<DetectedChapter> { new(2, 500) };
        var (highest, missing) = GapPlanning.ChapterProgress(chapters, expectedStartChapter: 1);
        Assert.Equal([2], highest);
        Assert.Equal([1], missing);
    }

    [Fact]
    public void ChapterProgress_ReturnsNoMissingChapters_WhenNoneFoundYet()
    {
        var (highest, missing) = GapPlanning.ChapterProgress([]);
        Assert.Empty(highest);
        Assert.Empty(missing);
    }

    [Fact]
    public void ChapterProgress_DoesNotThrow_WhenExpectedStartChapterExceedsHighestFound()
    {
        // The very first chapter Probe finds can transiently be numbered below
        // expectedStartChapter for one ChapterProgress call, right before ChapterDetector's own
        // "below expectation" check aborts the run - must not crash on a negative-length range.
        var chapters = new List<DetectedChapter> { new(2, 500) };
        var (highest, missing) = GapPlanning.ChapterProgress(chapters, expectedStartChapter: 5);
        Assert.Equal([2], highest);
        Assert.Empty(missing);
    }

    [Fact]
    public void FindGaps_RaisesNoLeadingGap_WithoutAnExpectedStartChapter()
    {
        // Without --expected-start-chapter, a first-found chapter numbered above 1 is trusted
        // outright - there is no way to tell a legitimate split-book start from a Probe miss, so
        // guessing "1" is never attempted. Only the interior gap (3 -> 6) is raised.
        var chapters = new List<DetectedChapter> { new(2, 500), new(3, 900), new(6, 2000) };
        var gaps = GapPlanning.FindGaps(chapters, Duration);
        Assert.Equal([new(900, 2000)], gaps);
    }

    [Fact]
    public void FindGaps_RaisesLeadingGap_OnlyWhenFirstChapterIsAboveTheExpectedStart()
    {
        var chapters = new List<DetectedChapter> { new(15, 500) };
        Assert.Empty(GapPlanning.FindGaps(chapters, Duration, expectedStartChapter: 15));
        var gaps = GapPlanning.FindGaps(chapters, Duration, expectedStartChapter: 12);
        Assert.Equal([new(0, 500)], gaps);
        Assert.Equal([12, 13, 14], GapPlanning.MissingNumbersInGap(chapters, gaps[0], expectedStartChapter: 12));
    }

    [Fact]
    public void FindGaps_RaisesNoGapAcrossARestart()
    {
        // The boundary itself is not a hole: part two's chapter 1 follows part one's chapter 4 with
        // nothing missing between them. A gap here would send Scan across the whole of part one
        // hunting numbers 5..15 that were never spoken.
        var chapters = new List<DetectedChapter>
        {
            new(1, 100), new(2, 500), new(1, 900, Sequence: 1), new(2, 1300, Sequence: 1),
        };
        Assert.Empty(GapPlanning.FindGaps(chapters, Duration));
    }

    [Fact]
    public void FindGaps_RaisesTheHeadOfARestartedPart_WhenItDoesNotStartAtOne()
    {
        // What the boundary *can* hide: the new part's own opening chapters. Unlike the file-level
        // leading gap this needs no --expected-start-chapter - a part counting from 1 again is
        // exactly the evidence a restart was confirmed on, so a part whose lowest number is 3 is a
        // part missing its first two chapters.
        var chapters = new List<DetectedChapter>
        {
            new(1, 100), new(2, 500), new(3, 900, Sequence: 1), new(4, 1300, Sequence: 1),
        };
        var gaps = GapPlanning.FindGaps(chapters, Duration);
        Assert.Equal([new(500, 900, 1)], gaps);
        Assert.Equal([1, 2], GapPlanning.MissingNumbersInGap(chapters, gaps[0]));
    }

    [Fact]
    public void Normalize_KeepsBothPartsOfARestartingBook()
    {
        // Run across a restart, the longest-increasing-subsequence filter keeps whichever part is
        // longer and throws the other away wholesale - the failure the sequence field exists to end.
        var chapters = new List<DetectedChapter>
        {
            new(1, 100), new(2, 500), new(3, 900),
            new(1, 1300, Sequence: 1), new(2, 1700, Sequence: 1),
        };
        Assert.Equal([1, 2, 3, 1, 2], GapPlanning.Normalize(chapters).Select(c => c.Number));
    }

    [Fact]
    public void Normalize_StillDropsARegressionInsideOnePart()
    {
        // And it still does its own job within a part: an in-text mention numbered below the
        // sequence is dropped exactly as before, the sequence field having narrowed the question
        // rather than weakened it.
        var chapters = new List<DetectedChapter>
        {
            new(1, 100), new(2, 500), new(3, 900),
            new(1, 1300, Sequence: 1), new(1, 1500, Sequence: 1), new(2, 1700, Sequence: 1),
        };
        Assert.Equal([1, 2, 3, 1, 2], GapPlanning.Normalize(chapters).Select(c => c.Number));
        Assert.Equal([100d, 500, 900, 1300, 1700],
            GapPlanning.Normalize(chapters).Select(c => c.TimeSeconds));
    }

    [Fact]
    public void ChapterProgress_CountsEachPartsMissingChaptersSeparately()
    {
        // The bar reports how far into the book the run has got, which on a book in parts is
        // every part's own position: one number each, in part order. The missing count stays a
        // single total across them.
        var chapters = new List<DetectedChapter>
        {
            new(1, 100), new(3, 500), new(1, 900, Sequence: 1), new(3, 1300, Sequence: 1),
        };
        var (highest, missing) = GapPlanning.ChapterProgress(chapters);
        Assert.Equal([3, 3], highest);
        Assert.Equal([2, 2], missing);
    }

    [Fact]
    public async Task RegionBeforeFirstChapter_IsSearched_WhenItStartsAboveOne()
    {
        // Only chapter 2 is found by the probes; with --expected-start-chapter 1 given, Scan
        // transcribes the file start looking for chapter 1 (without it, FindGaps would never
        // raise this leading gap at all - see FindGaps_* below). It is not in the first chunk
        // [0, 600] but past its end, so the search must
        // continue into the second chunk: that chunk's border (natural end 600) has no seam target
        // within reach, so the unsnapped fallback keeps the 10-second overlap and the second chunk
        // starts at 590, not 600 - and that is where chapter 1 (phrase at 610) is found. (Were
        // chapter 1 already in the first chunk, the gap's sole missing number would be complete and
        // Scan would stop before decoding the second chunk at all - see GapCompletes_* below.)
        var (result, _, audio) = await DetectFullAsync(
            Options("--expected-start-chapter", "1"),
            [new(1195, 1200)],
            s =>
            {
                s.Add(1200, Seg(0.2, " Chapter two."));
                s.Add(590, Seg(20, " Chapter one.")); // Scan chunk 2 (window start 590), phrase at 610
            });

        Assert.False(result.GapRemains);
        AssertChapters([new(1, 609.75), new(2, 1199.95)], result.Chapters);
        Assert.Contains(590.0, audio.DecodeStarts);
    }

    [Fact]
    public async Task GapCompletes_WhenAllExpectedChaptersAreFound_StopsBeforeTheNextChunk()
    {
        // Same leading-gap setup as above, but chapter 1's phrase sits in the *first* Scan chunk
        // [0, 600] (at 10). The gap's sole missing number is then complete after that chunk, so
        // transcription stops immediately - the second chunk at 590 is never decoded.
        var (result, _, audio) = await DetectFullAsync(
            Options("--expected-start-chapter", "1"),
            [new(1195, 1200)],
            s =>
            {
                s.Add(1200, Seg(0.2, " Chapter two."));
                s.Add(0, Seg(10, " Chapter one.")); // Scan chunk 1 [0, 600], phrase at 10
            });

        Assert.False(result.GapRemains);
        AssertChapters([new(1, 9.75), new(2, 1199.95)], result.Chapters);
        Assert.DoesNotContain(590.0, audio.DecodeStarts);
    }

    [Fact]
    public async Task TrailingScan_FindsAChapterAfterTheLastOneDetected()
    {
        // Chapter 3 is announced at 1799.95, past the last chapter Probe found and with nothing
        // above it - the one hole FindGaps structurally cannot see, since a sequence gap needs a
        // known chapter on either side. The trailing scan transcribes from chapter 2's mark to the
        // end of the file and picks it up. (The scan's second chunk carries it: its first, starting
        // at chapter 2's own mark, is too close to that chapter's probe window for the scripted
        // transcriber to tell the two decodes apart.)
        var (result, _, audio) = await DetectFullAsync(
            OptionsWithTrailingScan(),
            [new(595, 600), new(1195, 1200)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(1200, Seg(0.2, " Chapter two."));
                s.Add(1789.95, Seg(10, " Chapter three.")); // trailing chunk 2, phrase at 1799.95
            });

        AssertChapters([new(1, 0.25), new(2, 1199.95), new(3, 1799.7)], result.Chapters);
        // Scanned from the last chapter's own mark, and - having no expected numbers to satisfy -
        // carried on to the end of the file rather than stopping at the find.
        Assert.Contains(1199.95, audio.DecodeStarts);
        Assert.Contains(audio.DecodeWindows, w => w.Start > 2900);
    }

    [Fact]
    public async Task TrailingRegion_IsLeftAlone_WithoutTheOption()
    {
        // Same audio as above, minus the flag: chapters 1 and 2 form an unbroken sequence, so
        // nothing raises a gap and chapter 3 is never looked for. This is the default, and the
        // reason the trailing scan exists, and --no-trailing-scan is how it is declined.
        var (result, _, audio) = await DetectFullAsync(
            Options(),
            [new(595, 600), new(1195, 1200)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(1200, Seg(0.2, " Chapter two."));
                s.Add(1789.95, Seg(10, " Chapter three."));
            });

        AssertChapters([new(1, 0.25), new(2, 1199.95)], result.Chapters);
        Assert.DoesNotContain(1789.95, audio.DecodeStarts);
    }

    [Fact]
    public async Task TrailingScan_DoesNothing_WhenNoChapterWasFoundAtAll()
    {
        // With nothing found there is no "last chapter" to scan from - the trailing region would be
        // the entire book, which is Probe's job. This also covers the --early-abort and
        // --expected-start-chapter aborts, both of which leave the chapter list empty.
        var (result, _, audio) = await DetectFullAsync(
            OptionsWithTrailingScan(),
            [new(595, 600)],
            s => { });

        Assert.Empty(result.Chapters);
        // Nothing but probe-sized decodes: no 600 s Scan chunk was ever transcribed.
        Assert.All(audio.DecodeWindows, w => Assert.True(w.Duration is null or <= 60));
    }

    [Fact]
    public async Task TrailingScan_IgnoresANumberNotAboveEveryChapterAlreadyFound()
    {
        // An open-ended scan has no expected-number list to test a match against, so the only thing
        // that makes one new is topping every chapter already known. Here Probe finds 1 and 3 and
        // Scan fails to fill the gap, so chapter 2 is genuinely still missing - but hearing it
        // announced *after* chapter 3 is an in-text mention, not a chapter start. Accepting it would
        // report a find that Normalize then quietly drops again.
        var (result, log, _) = await DetectWithLogAsync(
            OptionsWithTrailingScan(),
            [new(595, 600), new(1195, 1200)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(1200, Seg(0.2, " Chapter three."));
                s.Add(1789.95, Seg(10, " Chapter two."));
            });

        AssertChapters([new(1, 0.25), new(3, 1199.95)], result.Chapters);
        Assert.True(result.GapRemains);
        Assert.Contains(log, l => l.Contains("skipped chapter 2") &&
                                  l.Contains("not above every chapter found"));
        Assert.DoesNotContain(log, l => l.Contains("chapter 2 found in gap"));
    }

    [Fact]
    public async Task BareNumbers_AreAnnouncementsWithChapterPhraseNone()
    {
        // A book that names its chapters by number alone. Each number is its own transcript
        // segment, which is what says it was spoken between two pauses rather than inside a
        // sentence.
        var result = await DetectAsync(
            Options("--chapter-phrase", "none"),
            [new(595, 600), new(1195, 1200)],
            s =>
            {
                s.Add(0, Seg(0.5, " One."));
                s.Add(1200, Seg(0.2, " Two."));
            });

        AssertChapters([new(1, 0.25), new(2, 1199.95)], result.Chapters);
    }

    [Fact]
    public async Task BareNumbers_IgnoreANumberInsideASentence()
    {
        // The one thing standing between this mode and a mark on every year, price and street
        // number in the book: an announcement is a segment that is a number and nothing else.
        var result = await DetectAsync(
            Options("--chapter-phrase", "none"),
            [new(595, 600), new(1195, 1200)],
            s =>
            {
                s.Add(0, Seg(0.5, " One."));
                s.Add(1200, Seg(0.2, " Two hundred men stood at the gate."));
            });

        AssertChapters([new(1, 0.25)], result.Chapters);
    }

    [Fact]
    public async Task BareNumbers_AcceptASpelledOutNumberOfSeveralWords()
    {
        var result = await DetectAsync(
            Options("--chapter-phrase", "none"),
            [new(595, 600), new(1195, 1200)],
            s =>
            {
                s.Add(0, Seg(0.5, " Twenty-one."));
                s.Add(1200, Seg(0.2, " Twenty two."));
            });

        AssertChapters([new(21, 0.25), new(22, 1199.95)], result.Chapters);
    }

    /// <summary>The chapter phrase is gone, but everything else about a run is not: a prologue or
    /// a --custom mapping still matches its own phrase as usual.</summary>
    [Fact]
    public async Task BareNumbers_LeaveTheNamedPhrasesAlone()
    {
        var result = await DetectAsync(
            Options("--chapter-phrase", "none"),
            [new(595, 600), new(1195, 1200), new(1795, 1800)],
            s =>
            {
                s.Add(0, Seg(0.5, " Prologue."));
                s.Add(600, Seg(0.3, " One."));
                s.Add(1200, Seg(0.2, " Two."));
                s.Add(1800, Seg(0.4, " Epilogue."));
            });

        AssertChapters([new(1, 600.05), new(2, 1199.95)], result.Chapters);
        AssertNamed(
            [("prologue", "Prologue", 0.25), ("epilogue", "Epilogue", 1800.15)],
            result);
    }

    /// <summary>
    /// The regression that cost "Corsa nello spazio" ten chapters (build 244, 2026-08-05): Whisper
    /// glues the announcement onto the first sentence of the chapter it announces, and the old rule
    /// - the whole transcript segment must be a number - threw every one of them away despite
    /// having read the number correctly. Probe's own forward scan has to take these.
    /// </summary>
    [Fact]
    public async Task BareNumbers_AcceptANumberGluedToTheSentenceAfterIt()
    {
        var result = await DetectAsync(
            Options("--chapter-phrase", "none"),
            [new(595, 600), new(1195, 1200)],
            s =>
            {
                s.Add(0, Seg(0.5, " One."));
                s.Add(1200, Seg(0.2, " Two. He was late. Terribly late, though he did not much care."));
            });

        AssertChapters([new(1, 0.25), new(2, 1199.95)], result.Chapters);
    }

    /// <summary>
    /// What Probe's forward scan must still refuse. Both lines are real transcript from the same
    /// book, and the first is the reason the rule cannot simply be "the segment starts with a
    /// number": chapter 1's announcement reads "1. 9 febbraio 2066…", so a dropped "1." must not
    /// hand chapter 9 to the date behind it.
    /// </summary>
    [Fact]
    public async Task BareNumbers_StillIgnoreANumberOpeningASentence()
    {
        var result = await DetectAsync(
            Options("--chapter-phrase", "none"),
            [new(595, 600), new(1195, 1200)],
            s =>
            {
                s.Add(0, Seg(0.5, " One."));
                s.Add(1200, Seg(0.2, " Two hundred men stood at the gate."));
            });

        AssertChapters([new(1, 0.25)], result.Chapters);
    }

    /// <summary>
    /// The false epilogue that destroyed a real one (2026-08-05): Italian "riepilogo" contains
    /// "epilogo", so the default <c>/epilogo/</c> matched mid-sentence four and a half hours before
    /// the book's actual epilogue - and the epilogue being non-repeatable, the later <em>detection</em>
    /// replaced the earlier one, whatever their positions. The lead-in guard settles it from Analyze
    /// geometry alone: the match sits inside continuous speech, so it never becomes a mark.
    /// </summary>
    [Fact]
    public async Task NamedMarks_RejectAPhraseMatchedInsideContinuousSpeech()
    {
        var vad = new FakeVad
        {
            // Narration up to 1199.5, a 3 s pause, the epilogue announcement, then the text: the
            // real geometry of a section boundary. The decoy at 600 sits mid-sentence instead.
            Speech =
            [
                new(0, 599.9), new(600.2, 610), new(1100, 1199.5), new(1202.5, 1203.2), new(1205, 1300),
            ],
        };
        var (result, log, _) = await DetectWithLogAsync(
            OptionsInOneSweep(),
            [new(595, 600), new(1195, 1200)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(600, Seg(0.2, " The details are deleted entirely from the recap epilogue."));
                s.Add(1200, Seg(2.5, " Epilogue."));
            },
            vad);

        AssertNamed([("epilogue", "Epilogue", 1202.5)], result);
        Assert.Contains(log, l => l.Contains("discarded the named mark") &&
                                  l.Contains("not set off by a pause"));
    }

    /// <summary>
    /// Why the prologue and epilogue are asked for a leading pause only, and <c>--custom</c> for
    /// nothing at all. A heading word is routinely run straight into the text behind it - Gruelfin's
    /// "Zeittafel" leaves 0.16 s there, genuine - so a trailing requirement would cost real marks,
    /// and a --custom mapping names whatever the user says it does, wherever they say it is.
    /// </summary>
    [Fact]
    public async Task NamedMarks_KeepAHeadingRunStraightIntoItsText()
    {
        var vad = new FakeVad
        {
            Speech = [new(0, 599.9), new(603, 603.6), new(603.76, 700)],
        };
        var result = await DetectAsync(
            Options("--custom", "timeline:Timeline"),
            [new(595, 600), new(1195, 1200)],
            s =>
            {
                s.Add(600, Seg(3, " Timeline."));
                s.Add(1200, Seg(0.2, " Chapter one."));
            },
            vad);

        AssertNamed([("custom 1", "Timeline", 603)], result);
    }

    [Fact]
    public async Task ChapterCount_HuntsTheChaptersAfterTheLastOneDetected()
    {
        // The same hole the trailing scan brute-forces, told exactly what is missing instead: chapter
        // 3 is announced past everything Probe found, with no chapter above it to make its absence
        // visible as a sequence gap.
        var (result, _, audio) = await DetectFullAsync(
            Options("--chapter-count", "3"),
            [new(595, 600), new(1195, 1200)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(1200, Seg(0.2, " Chapter two."));
                s.Add(1789.95, Seg(10, " Chapter three.")); // trailing chunk 2, phrase at 1799.95
            });

        AssertChapters([new(1, 0.25), new(2, 1199.95), new(3, 1799.7)], result.Chapters);
        Assert.False(result.GapRemains);
        // Knowing what it was after, the hunt stopped at chapter 3 rather than carrying on to the
        // end of the file the way an open-ended scan has to.
        Assert.Contains(1199.95, audio.DecodeStarts);
        Assert.DoesNotContain(audio.DecodeWindows, w => w.Start > 2900);
    }

    [Fact]
    public async Task ChapterCount_TranscribesNoTail_WhenTheCountIsAlreadyReached()
    {
        // Two chapters declared, two found: nothing is owed, so the tail is never touched - which is
        // the whole difference from the blind scan, whose sweep is paid for on every file.
        var (result, _, audio) = await DetectFullAsync(
            Options("--chapter-count", "2"),
            [new(595, 600), new(1195, 1200)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(1200, Seg(0.2, " Chapter two."));
                s.Add(1789.95, Seg(10, " Chapter three."));
            });

        AssertChapters([new(1, 0.25), new(2, 1199.95)], result.Chapters);
        Assert.False(result.GapRemains);
        Assert.All(audio.DecodeWindows, w => Assert.True(w.Duration is null or <= 60));
    }

    [Fact]
    public async Task ChapterCount_BeatsTheOpenEndedTrailingSweep()
    {
        // Both apply here - the count is satisfied, and the trailing scan is on by default - and the
        // count has to win. An open-ended sweep starts at the same place but runs to the end of the
        // file with nothing able to stop it, so letting it take precedence would quietly hand every
        // --chapter-count run the bill the option exists to avoid.
        var (result, _, audio) = await DetectFullAsync(
            OptionsWithTrailingScan("--chapter-count", "2"),
            [new(595, 600), new(1195, 1200)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(1200, Seg(0.2, " Chapter two."));
                s.Add(1789.95, Seg(10, " Chapter three."));
            });

        AssertChapters([new(1, 0.25), new(2, 1199.95)], result.Chapters);
        Assert.False(result.GapRemains);
        Assert.All(audio.DecodeWindows, w => Assert.True(w.Duration is null or <= 60));
    }

    [Fact]
    public async Task ChapterCount_ReportsTheTrailingChaptersItCouldNotFind()
    {
        // Nothing beyond chapter 2 is in the audio at all. Without a declared count the file would
        // be written out looking complete; with one it is tagged, which is the point.
        var result = await DetectAsync(
            Options("--chapter-count", "4"),
            [new(595, 600), new(1195, 1200)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(1200, Seg(0.2, " Chapter two."));
            });

        AssertChapters([new(1, 0.25), new(2, 1199.95)], result.Chapters);
        Assert.True(result.GapRemains);
        Assert.Equal([3, 4], result.MissingNumbers);
    }

    [Fact]
    public async Task ChapterCount_DiscardsANumberAboveTheDeclaredLast()
    {
        // A count is also a cap, the same one --max-chapter-number provides: without it a misheard
        // "chapter thirty" in a three-chapter book leaves twenty-seven chapters "missing".
        var (result, log, _) = await DetectWithLogAsync(
            Options("--chapter-count", "3"),
            [new(595, 600), new(1195, 1200)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(1200, Seg(0.2, " Chapter thirty."));
            });

        AssertChapters([new(1, 0.25)], result.Chapters);
        Assert.Contains(log, l => l.Contains("discarded chapter 30") && l.Contains("--chapter-count"));
    }

    [Fact]
    public async Task ChapterCount_CountsFromExpectedStartChapter()
    {
        // A split-book part starting at chapter 5 with three chapters in it runs 5 to 7, not 1 to 3.
        var result = await DetectAsync(
            Options("--chapter-count", "3",
                    "--expected-start-chapter", "5"),
            [new(595, 600), new(1195, 1200)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter five."));
                s.Add(1200, Seg(0.2, " Chapter six."));
            });

        AssertChapters([new(5, 0.25), new(6, 1199.95)], result.Chapters);
        Assert.Equal([7], result.MissingNumbers);
    }

    [Fact]
    public async Task ChapterCount_DoesNotEndTheSearchAtTheLastChapter()
    {
        // The declared count says when the *numbered* chapters run out, not when the book does: an
        // epilogue (or any --custom phrase) may still follow, and stopping at the count would cost
        // that mark silently.
        var result = await DetectAsync(
            Options("--chapter-count", "2"),
            [new(595, 600), new(1195, 1200), new(1795, 1800)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(1200, Seg(0.2, " Chapter two."));
                s.Add(1800, Seg(0.4, " Epilogue."));
            });

        AssertChapters([new(1, 0.25), new(2, 1199.95)], result.Chapters);
        AssertNamed([("epilogue", "Epilogue", 1800.15)], result);
    }

    [Fact]
    public async Task ANumberLeavingALargeGap_IsReReadWithTheUpgradeModel()
    {
        // BARDIOC.m4b, 2026-07-30: "neunzehn" (19) came back as 90 right after chapter 18, declaring
        // seventy chapters missing. Here the probe transcriber hears 90 where chapter 2 is, and only
        // the upgrade model reads it correctly - so the corrected number can only have come from the
        // re-read, and the mark keeps the position the original reading gave it.
        var (result, _, upgrade) = await DetectWithUpgradeTranscriberAsync(
            Options("--model", "base", "--upgrade-model", "large", "--quick-marks"),
            [new(595, 600)],
            probe =>
            {
                probe.Add(0, Seg(0.5, " Chapter one."));
                probe.Add(600, Seg(0.5, " Chapter ninety."));
            },
            upgrade => upgrade.Add(600, Seg(0.5, " Chapter two.")));

        Assert.False(result.GapRemains);
        AssertChapters([new(1, 0.25), new(2, 600.25)], result.Chapters);
        // The re-read went through the upgrade model with the file's own language applied.
        Assert.Contains("en", upgrade.LanguageChanges);
    }

    [Fact]
    public async Task ANumberLeavingALargeGap_IsReReadFromAWiderWindow_WithNoUpgradeModel()
    {
        // Without --upgrade-model there is no better recognizer to consult, so the same audio is asked
        // again through differently sized windows - which is a real second reading, since what Whisper
        // writes depends on the window a stretch arrives in. Chapter 2's announcement is scripted
        // ahead of the probe window, so only the 45 s re-framing (which leads the misheard phrase by
        // 12 s, i.e. from 590.5) ever sees it: the 15 s re-framing starts at 600.5 and the probe
        // window itself at 598.4, both past it. The silence is deliberately short, since the probe
        // window opens a lead-in inside it and a longer one would reach back over the announcement.
        var (result, log, _) = await DetectWithLogAsync(
            Options("--quick-marks"),
            [new(598.4, 600)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(600, Seg(2.5, " Chapter ninety."));  // the probe's own reading, phrase at 602.5
                s.Add(598, Seg(0, " Chapter two."));       // ahead of it, outside the probe window
            });

        Assert.False(result.GapRemains);
        AssertChapters([new(1, 0.25), new(2, 602.25)], result.Chapters);
        Assert.Contains(log, l => l.Contains("chapter 90 at 0:10:02.50 outside") &&
                                  l.Contains("would leave 88 missing"));
        Assert.Contains(log, l => l.Contains("45 s window: 2 instead of 90"));
    }

    [Fact]
    public async Task ANumberBelowTheSequence_IsReReadInsteadOfDiscarded()
    {
        // The mirror mishearing, and the more damaging one: a number heard *below* the sequence is
        // indistinguishable from an in-text mention of an earlier chapter, so it used to be dropped
        // without appeal and the chapter went missing. Re-reading first recovers it.
        var (result, log, _) = await DetectWithLogAsync(
            Options("--quick-marks"),
            [new(595, 600), new(1198.4, 1200)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter five."));
                s.Add(1200, Seg(2.5, " Chapter two."));   // heard below chapter 5, phrase at 1202.5
                // What the wider re-framing hears, placed ahead of the probe window's own opening
                // (1198.4, this silence being short enough that the lead-in is clamped to its start)
                // so only that re-framing can reach it.
                s.Add(1198, Seg(0, " Chapter six."));
            },
            null);

        AssertChapters([new(5, 0.25), new(6, 1202.25)], result.Chapters);
        Assert.Contains(log, l => l.Contains("chapter 2 at 0:20:02.50 outside") &&
                                  l.Contains("not above it"));
        Assert.Contains(log, l => l.Contains("45 s window: 6 instead of 2"));
    }

    [Fact]
    public async Task TheMarkRefinement_CorrectsANumberTheDetectingWindowMisread()
    {
        // "Die Cyber-Brutzellen", 2026-08-01, in miniature. One announcement, two readings: the 12 s
        // probe window that finds it reads one number, and every window short enough to be framed on
        // the announcement itself reads another. That is not a coin toss - the short windows are the
        // ones the mark refinement decodes anyway, and on that book all ten of them read "Kapitel 14"
        // against the wide window's "Kapitel 40". Here the wide window says three and the refinement
        // says two, and two is what the mark is recorded under.
        var (result, log, _) = await DetectWithLogAsync(
            Options(),
            [new(595, 600)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.AddBeyond(11, 600, Seg(2.5, " Chapter three."));  // the 12 s probe window
                s.AddWithin(11, 600, Seg(2.5, " Chapter two."));    // every refinement probe
            });

        Assert.False(result.GapRemains);
        AssertChapters([new(1, 0.25), new(2, 602.25)], result.Chapters);
        Assert.Contains(log, l => l.Contains("refinement read chapter 3 at 0:10:02.50 as 2") &&
                                  l.Contains("number corrected, mark unchanged"));
    }

    [Fact]
    public async Task TheMarkRefinement_DoesNotCorrectANumberIntoOneTheSequenceCannotHold()
    {
        // The guard that keeps a free second opinion from becoming a free second chance to be wrong.
        // The refinement is unanimous about chapter 20, and it is refused anyway: after chapter 1
        // that would leave eighteen chapters missing at a stroke, which is no better founded than
        // the 3 it would replace. The mark keeps the number the window gave it and the ordinary gap
        // machinery takes over.
        var (result, log, _) = await DetectWithLogAsync(
            Options(),
            [new(595, 600)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.AddBeyond(11, 600, Seg(2.5, " Chapter three."));
                s.AddWithin(11, 600, Seg(2.5, " Chapter twenty."));
            });

        Assert.Contains(result.Chapters, c => c.Number == 3);
        Assert.DoesNotContain(result.Chapters, c => c.Number == 20);
        Assert.Contains(log, l => l.Contains("refinement read chapter 3 at 0:10:02.50 as 20") &&
                                  l.Contains("outside the sequence after chapter 1 - keeping 3"));
    }

    [Fact]
    public async Task AGapReprobe_ReReadsANumberItsOwnHoleCannotHold()
    {
        // The failure that cost "Die Cyber-Brutzellen" (2026-08-01) half its marks. A sequence-gap
        // re-probe is searching a hole it knows both ends of - here the one chapter between 2 and 4
        // - and it used to accept whatever number came back, on the reasoning that its own window
        // was already the remedy. A window is never that: whichever one reads the announcement can
        // be the one that misreads it, and here the re-probe's own 18 s framing comes back with
        // chapter 40. Questioned against the hole it is filling, the only readings worth having are
        // the ones that fit in it, and the mender's re-framing supplies one.
        var (result, log, _) = await DetectWithLogAsync(
            Options("--verbose", "--quick-marks"),
            // 2.0 s is below the 3.75 s threshold chapter 2's own 5 s silence sets, so 700 is
            // skipped on the first pass and only the gap re-probe ever decodes it.
            [new(595, 600), new(698, 700), new(1195, 1200)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(600, Seg(0.3, " Chapter two."));
                s.AddWithin(16, 700, Seg(0.3, " Chapter three."));  // the mender's 15 s re-framing
                s.AddBeyond(16, 700, Seg(0.3, " Chapter forty."));  // the 18 s re-probe window
                s.Add(1200, Seg(0.3, " Chapter four."));
            });

        Assert.False(result.GapRemains);
        Assert.Equal([1, 2, 3, 4], result.Chapters.Select(c => c.Number));
        Assert.Contains(log, l => l.Contains("chapter 40 at 0:11:40.30 outside " +
                                             "the gap between chapters 2 and 4") &&
                                  l.Contains("4 already follows"));
        Assert.Contains(log, l => l.Contains("15 s window: 3 instead of 40"));
    }

    [Fact]
    public async Task AGapReprobe_MarksTheProgressBarAsRevisiting_ForExactlyItsOwnStretch()
    {
        // A gap re-probe runs inside Probe and walks back through candidates the phase has
        // already counted, so the bar's percentage falls; the label is what says why. Sampling it
        // as each log line is written is the only way to see a marker that is up transiently -
        // and the geometry is the re-read fixture's above, which is where this file's re-probe
        // comes from.
        var tracker = new WorkTracker();
        var seen = new List<(string Message, string Label)>();
        var audio = new FakeAudioSource { Silences = [new(595, 600), new(698, 700), new(1195, 1200)] };
        var transcriber = new ScriptedTranscriber(audio);
        transcriber.Add(0, Seg(0.5, " Chapter one."));
        transcriber.Add(600, Seg(0.3, " Chapter two."));
        transcriber.AddWithin(16, 700, Seg(0.3, " Chapter three."));
        transcriber.AddBeyond(16, 700, Seg(0.3, " Chapter forty."));
        transcriber.Add(1200, Seg(0.3, " Chapter four."));
        var detector = new ChapterDetector(
            OptionsInOneSweep("--verbose", "--quick-marks"), audio, transcriber, null);

        await detector.DetectAsync(
            _file, Info, tracker,
            new DetectionLog(m => seen.Add((m, tracker.PhaseLabel)), null),
            CancellationToken.None);

        Assert.Contains(seen, s => s.Message.Contains("re-probing") && s.Label == "Probe<<");
        // The primary walk's own lines are not marked, or the suffix would say nothing.
        Assert.Contains(seen, s => s.Label == "Probe");
        // And nothing after the re-probe inherits it.
        Assert.False(tracker.PhaseRevisiting);
    }

    [Fact]
    public async Task AnOrdinaryGap_AndARepeatedAnnouncement_AreNotReRead()
    {
        // The two cases that must stay cheap. A gap of two chapters is the ordinary kind the re-probe
        // and Re-probe/3 exist for, and a number equal to the last accepted one is an overlapping
        // window re-hearing a mark already placed - questioning either would spend transcriptions with
        // nothing to gain (and, for the repeat, could only "improve" by inventing the next number).
        var (result, log, _) = await DetectWithLogAsync(
            Options("--quick-marks"),
            [new(595, 600), new(1195, 1200)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(600, Seg(0.5, " Chapter four."));   // leaves 2 and 3 missing: ordinary
                s.Add(1200, Seg(0.5, " Chapter four."));  // the same number again
            });

        Assert.True(result.GapRemains);
        AssertChapters([new(1, 0.25), new(4, 600.25)], result.Chapters);
        Assert.DoesNotContain(log, l => l.Contains("does not fit the sequence"));
        Assert.Contains(log, l => l.Contains("skipped chapter 4") &&
                                  l.Contains("not above last accepted 4"));
    }

    [Fact]
    public async Task Scan_UsesTheSeparateUpgradeTranscriber_WhenOneIsGiven()
    {
        // Probe finds only chapters 1 and 3 (its transcriber never hears chapter 2), leaving a
        // sequence gap. Chapter 2 lives *solely* in the upgrade transcriber's script, so the gap can
        // only be filled if Scan actually routed through it - exactly what --upgrade-model sets up.
        var (result, _, upgrade) = await DetectWithUpgradeTranscriberAsync(
            Options(),
            [new(595, 600), new(1195, 1200)],
            probe =>
            {
                probe.Add(0, Seg(0.5, " Chapter one."));
                probe.Add(1200, Seg(0.2, " Chapter three."));
            },
            upgrade => upgrade.Add(597.5, Seg(2.5, " Chapter two."))); // snapped gap-chunk seam

        Assert.False(result.GapRemains);
        AssertChapters([new(1, 0.25), new(2, 599.75), new(3, 1199.95)], result.Chapters);
        // The upgrade transcriber had its language set before it was used (auto-detected "en").
        Assert.Contains("en", upgrade.LanguageChanges);
    }

    [Fact]
    public async Task Reprobe_ClosesTheGapCheaply_BeforeTheScanEverTranscribesTheRegion()
    {
        // The whole point of Re-probe: Probe's own candidate probe at the gap's silence was right
        // on top of chapter 2's announcement, it just misheard it. Scripting chapter 2 into the
        // upgrade transcriber at the *probe* position (600, the silence end) rather than at a
        // gap-chunk seam means only a probe-style re-probe can find it - a full Scan
        // transcription decodes from the gap's start (0) instead and would come up empty.
        var (result, _, upgrade) = await DetectWithUpgradeTranscriberAsync(
            Options("--model", "base", "--upgrade-model", "large"),
            [new(595, 600), new(1195, 1200)],
            probe =>
            {
                probe.Add(0, Seg(0.5, " Chapter one."));
                probe.Add(1200, Seg(0.2, " Chapter three."));
            },
            upgrade => upgrade.Add(600, Seg(0.5, " Chapter two.")));

        Assert.False(result.GapRemains);
        AssertChapters([new(1, 0.25), new(2, 600.25), new(3, 1199.95)], result.Chapters);
        Assert.Contains(600, upgrade.Audio.DecodeStarts);
        // Scan proper never ran: every decode stayed probe-sized (12 s plain probe window), so
        // nothing was ever transcribed in Scan's 600 s gap chunks. This is the saving Re-probe
        // exists for - asserting on decode *lengths* rather than positions, since a gap region
        // starts at the bounding chapter's own mark and Re-probe legitimately probes there too.
        Assert.All(upgrade.Audio.DecodeWindows, w => Assert.True(w.Duration is null or <= 60));
    }

    [Fact]
    public async Task Reprobe_StopsProbingAGapTheMomentItCloses()
    {
        // Probe's own re-probe has always stopped at the chapter that closes its gap; Re-probe
        // drove a whole region and could not, for want of knowing which numbers it was sent to find
        // - it knew only the region's bounds. Everything past the last missing chapter is then a
        // chapter's worth of audio with no announcement left in it, each candidate paying for a full
        // mark placement to produce a duplicate that is thrown away.
        var log = new List<string>();
        var (result, _, _) = await DetectWithUpgradeTranscriberAsync(
            Options("--model", "base", "--upgrade-model", "large", "--quick-marks"),
            [new(295, 300), new(595, 600), new(895, 900), new(1195, 1200)],
            probe =>
            {
                probe.Add(0, Seg(0.5, " Chapter one."));
                probe.Add(1200, Seg(0.2, " Chapter three."));
            },
            upgrade => upgrade.Add(300, Seg(0.5, " Chapter two.")),
            log);

        Assert.False(result.GapRemains);
        Assert.Equal([1, 2, 3], result.Chapters.Select(c => c.Number));
        // Two of four: the region-start candidate, then the silence that yields chapter 2. The two
        // behind it are a chapter's worth of audio with nothing left to find in it.
        Assert.Contains(log, l => l.Contains("stretch complete, nothing left missing") &&
                                  l.Contains("stopped after 2 of 4 candidate(s)"));
        // The gap closed inside Re-probe, so Scan never transcribed the region either.
        Assert.DoesNotContain(log, l => l.Contains("transcribing suspicious region"));
    }

    [Fact]
    public async Task Reprobe_IsSkipped_WhenTheUpgradeModelIsNotBetter()
    {
        // A lighter (or equal) --upgrade-model means a re-probe would only reach the same conclusion
        // more slowly, so Re-probe must not run at all - the gap goes straight to Scan, which
        // here decodes the region from its start and finds chapter 2 there instead.
        var (result, _, upgrade) = await DetectWithUpgradeTranscriberAsync(
            Options("--model", "large", "--upgrade-model", "base"),
            [new(595, 600), new(1195, 1200)],
            probe =>
            {
                probe.Add(0, Seg(0.5, " Chapter one."));
                probe.Add(1200, Seg(0.2, " Chapter three."));
            },
            upgrade => upgrade.Add(597.5, Seg(2.5, " Chapter two."))); // snapped gap-chunk seam

        Assert.False(result.GapRemains);
        AssertChapters([new(1, 0.25), new(2, 599.75), new(3, 1199.95)], result.Chapters);
        // The gap-chunk decode from the gap's own start, i.e. Scan - not a Re-probe probe.
        AssertDecodedFrom(upgrade, result.Chapters[0].TimeSeconds);
    }

    [Fact]
    public async Task Reprobe_FallsThroughToTheScan_WhenItFindsNothing()
    {
        // Re-probe runs (large beats base) but its probe hears nothing, so Scan must still get
        // its turn on the very same gap and close it from the full transcription.
        var (result, _, upgrade) = await DetectWithUpgradeTranscriberAsync(
            Options("--model", "base", "--upgrade-model", "large"),
            [new(595, 600), new(1195, 1200)],
            probe =>
            {
                probe.Add(0, Seg(0.5, " Chapter one."));
                probe.Add(1200, Seg(0.2, " Chapter three."));
            },
            upgrade => upgrade.Add(597.5, Seg(2.5, " Chapter two."))); // only findable by Scan's chunking

        Assert.False(result.GapRemains);
        AssertChapters([new(1, 0.25), new(2, 599.75), new(3, 1199.95)], result.Chapters);
        Assert.Contains(0.25, upgrade.Audio.DecodeStarts);
    }

    [Fact]
    public async Task Reprobe_NeverAcceptsAChapterNumberOutsideTheGapItIsRecovering()
    {
        // The gap between chapters 1 and 3 expects only chapter 2. A re-probe that mishears its
        // way to "chapter 7" must not be able to plant it here - the region's own bounds reject
        // anything at or above the chapter that closes the gap, so the gap simply stays open.
        var (result, _, _) = await DetectWithUpgradeTranscriberAsync(
            Options("--model", "base", "--upgrade-model", "large"),
            [new(595, 600), new(1195, 1200)],
            probe =>
            {
                probe.Add(0, Seg(0.5, " Chapter one."));
                probe.Add(1200, Seg(0.2, " Chapter three."));
            },
            upgrade => upgrade.Add(600, Seg(0.5, " Chapter seven.")));

        Assert.True(result.GapRemains);
        AssertChapters([new(1, 0.25), new(3, 1199.95)], result.Chapters);
    }

    [Fact]
    public async Task Reprobe_ReportsProgressRelativeToTheGapsItBudgetedFor_NotAbsoluteFilePosition()
    {
        // Re-probe's phase total is the summed length of the gaps it will re-probe, so its progress
        // has to be measured in the same currency. Reporting the probe's absolute file position
        // instead pegged the bar at 100 % for the whole pass whenever the gap sat late in the file -
        // here a 999.7 s gap starting at 2400.25 s, where an absolute 3100 s would read as 310 %.
        var audio = new FakeAudioSource { Silences = [new(2395, 2400), new(3095, 3100), new(3395, 3400)] };
        var probe = new ScriptedTranscriber(audio);
        var upgrade = new ScriptedTranscriber(audio);
        probe.Add(0, Seg(0.5, " Chapter one."));
        probe.Add(2400, Seg(0.25, " Chapter two."));
        probe.Add(3400, Seg(0.05, " Chapter four."));
        upgrade.Add(3100, Seg(0.5, " Chapter three."));

        var tracker = new WorkTracker();
        var during = new List<double>();
        upgrade.OnTranscribe = () =>
        {
            if (tracker.PhaseLabel == "Re-probe")
                during.Add(tracker.Fraction);
        };

        var detector = new ChapterDetector(
            Options("--model", "base", "--upgrade-model", "large", "--quick-marks"),
            audio, probe, vad: null, upgradeTranscriber: upgrade);
        var result = await detector.DetectAsync(_file, Info, tracker, default, CancellationToken.None);

        // Re-probe really ran and closed the gap (so the samples below are not an empty list).
        Assert.False(result.GapRemains);
        Assert.Contains(result.Chapters, c => c.Number == 3);
        Assert.NotEmpty(during);
        // The probe at 3100 s sits 699.75 s into a 999.7 s budget - nowhere near the clamp.
        Assert.All(during, f => Assert.InRange(f, 0.0, 0.8));
        // And the pass still lands exactly on 100 % when its last gap is done; nothing else began
        // a phase afterwards, since Scan found no gap left to fill.
        Assert.Equal("Re-probe", tracker.PhaseLabel);
        Assert.Equal(1.0, tracker.Fraction, 6);
    }

    [Fact]
    public async Task Reprobe_SweepsTheSilencesJustBelowTheFloor_WhenItsOwnReProbeLeavesTheGapOpen()
    {
        // The Paula Monti shape (2026-07-31): the chapter's announcement is preceded by a 1.4 s
        // pause against the 1.5 s floor, so Analyze never offered it as a candidate and no probe of
        // Probe or of Re-probe's ordinary re-probe ever pointed at it. Only the sub-floor sweep
        // can reach it - and it has to, because Scan's own long-form decode of the gap is the
        // very thing that lost the announcement on the real file.
        var log = new List<string>();
        var (result, _, upgrade) = await DetectWithUpgradeTranscriberAsync(
            Options("--model", "base", "--upgrade-model", "large"),
            [new(595, 600), new(898.55, 900), new(1195, 1200)],
            probe =>
            {
                probe.Add(0, Seg(0.5, " Chapter one."));
                probe.Add(1200, Seg(0.2, " Chapter three."));
            },
            upgrade => upgrade.Add(900, Seg(0.5, " Chapter two.")),
            log);

        Assert.False(result.GapRemains);
        AssertChapters([new(1, 0.25), new(2, 900.25), new(3, 1199.95)], result.Chapters);
        Assert.Contains(log, l => l.Contains("Re-probe: sweeping 1 silence(s) of 1.4-1.5 s for chapter 2"));
        // Every decode stayed probe-sized, so Scan proper never transcribed the gap - the same
        // saving Re-probe's ordinary re-probe exists for, on a candidate it cannot see.
        Assert.All(upgrade.Audio.DecodeWindows, w => Assert.True(w.Duration is null or <= 60));
    }

    [Fact]
    public async Task Reprobe_StopsSweeping_AsSoonAsTheGapsLastMissingChapterIsFound()
    {
        // Bands run longest-first and the sweep ends the moment nothing is missing, so the 1.05 s
        // silence at 1000 - which would be swept by the bottom band - is never probed at all.
        // Scripting a second, duplicate announcement there makes the omission provable rather than
        // merely plausible: if the bottom band ran, that decode would show up.
        var log = new List<string>();
        var (result, _, upgrade) = await DetectWithUpgradeTranscriberAsync(
            Options("--model", "base", "--upgrade-model", "large"),
            [new(595, 600), new(898.55, 900), new(998.95, 1000), new(1195, 1200)],
            probe =>
            {
                probe.Add(0, Seg(0.5, " Chapter one."));
                probe.Add(1200, Seg(0.2, " Chapter three."));
            },
            upgrade =>
            {
                upgrade.Add(900, Seg(0.5, " Chapter two."));
                upgrade.Add(1000, Seg(0.5, " Chapter two."));
            },
            log);

        AssertChapters([new(1, 0.25), new(2, 900.25), new(3, 1199.95)], result.Chapters);
        Assert.Contains(log, l => l.Contains("Re-probe: sub-floor sweep closed the gap at 1.4-1.5 s"));
        // Probe walks the same bands first on its own recognizer, which hears nothing here, so
        // the bottom band is only proof of anything for the pass under test.
        Assert.DoesNotContain(log, l => l.Contains("Re-probe") && l.Contains("1.0-1.1 s"));
        Assert.DoesNotContain(upgrade.Audio.DecodeStarts, d => Math.Abs(d - 1000) < 1e-6);
    }

    [Fact]
    public async Task Reprobe_KeepsSweepingDownTheBands_WhenTheLongerOnesHoldNothing()
    {
        // The gap's only sub-floor silence is 1.05 s, four empty bands below where the sweep starts.
        // Empty bands cost nothing, so walking down to it is the whole point of sweeping in steps
        // rather than stopping at the first band that comes back empty.
        var log = new List<string>();
        var (result, _, _) = await DetectWithUpgradeTranscriberAsync(
            Options("--model", "base", "--upgrade-model", "large"),
            [new(595, 600), new(1098.95, 1100), new(1195, 1200)],
            probe =>
            {
                probe.Add(0, Seg(0.5, " Chapter one."));
                probe.Add(1200, Seg(0.2, " Chapter three."));
            },
            upgrade => upgrade.Add(1100, Seg(0.5, " Chapter two.")),
            log);

        Assert.False(result.GapRemains);
        AssertChapters([new(1, 0.25), new(2, 1100.25), new(3, 1199.95)], result.Chapters);
        Assert.Contains(log, l => l.Contains("Re-probe: sweeping 1 silence(s) of 1.0-1.1 s for chapter 2"));
    }

    [Fact]
    public async Task Reprobe_DoesNotSweep_WhenItsOwnReProbeAlreadyClosedTheGap()
    {
        // The sweep is the fallback, not a second helping: with the gap closed by the ordinary
        // re-probe at 600, the 1.4 s silence at 900 stays untouched even though it is in range.
        var log = new List<string>();
        var (result, _, upgrade) = await DetectWithUpgradeTranscriberAsync(
            Options("--model", "base", "--upgrade-model", "large"),
            [new(595, 600), new(898.55, 900), new(1195, 1200)],
            probe =>
            {
                probe.Add(0, Seg(0.5, " Chapter one."));
                probe.Add(1200, Seg(0.2, " Chapter three."));
            },
            upgrade => upgrade.Add(600, Seg(0.5, " Chapter two.")),
            log);

        AssertChapters([new(1, 0.25), new(2, 600.25), new(3, 1199.95)], result.Chapters);
        Assert.DoesNotContain(log, l => l.Contains("Re-probe: sweeping"));
        Assert.DoesNotContain(upgrade.Audio.DecodeStarts, d => Math.Abs(d - 900) < 1e-6);
    }

    [Fact]
    public async Task Reprobe_AbandonsTheSweep_WhenABandWouldTakeItPastItsShareOfTheScanCost()
    {
        // Budget and spending are both counted in Whisper's 30 s decode windows. A 59.7 s gap costs
        // Scan two of them, so the sweep may spend 1.5 - which affords exactly one 12 s probe. The
        // 1.45 s band gets it and finds nothing; the 1.35 s band below would take the running total
        // to two windows, past the budget, so the sweep ends there rather than paying for it. Chapter
        // 2 stays missing, which is exactly the trade being made.
        var log = new List<string>();
        var (result, _, _) = await DetectWithUpgradeTranscriberAsync(
            Options("--model", "base", "--upgrade-model", "large"),
            [new(595, 600), new(608.55, 610), new(618.65, 620), new(655, 660)],
            probe =>
            {
                probe.Add(600, Seg(0.5, " Chapter one."));
                probe.Add(660, Seg(0.2, " Chapter three."));
            },
            upgrade => { },
            log);

        Assert.True(result.GapRemains);
        Assert.Contains(log, l => l.Contains("Re-probe: sweeping 1 silence(s) of 1.4-1.5 s"));
        Assert.Contains(log, l => l.Contains("sweep stopped before the 1.3-1.4 s band") &&
                                  l.Contains("2 decode window(s)") && l.Contains("1.5"));
    }

    [Fact]
    public async Task Scan_ReReadsAStillOpenGap_WithItsDecodesShiftedHalfAWhisperWindow()
    {
        // What a gap surviving a *complete* transcription means: not audio nobody read, but audio
        // the recognizer read wrongly - and the likeliest reason is where the announcement fell
        // inside Whisper's 30 s decode window. Chapter 14 of "Paula Monti" (2026-07-31) vanished
        // from a 601 s chunk that read every second around it, and reappeared at p=0.94 from the
        // same chunk started 15 s later. Scripting chapter 2 as audible only below a 340 s decode
        // reproduces that here: Scan reads the whole gap [0.25, 349.75] in one 349.5 s chunk and
        // misses it, the shifted re-read's own single chunk runs 334.5 s from 15.25 and does not.
        var log = new List<string>();
        var (result, _, _) = await DetectWithUpgradeTranscriberAsync(
            Options("--model", "base", "--upgrade-model", "large"),
            [new(295, 300), new(345, 350)],
            probe =>
            {
                probe.Add(0, Seg(0.5, " Chapter one."));
                probe.Add(350, Seg(0.2, " Chapter three."));
            },
            upgrade => upgrade.AddWithin(340, 150, Seg(0, " Chapter two.")),
            log);

        Assert.False(result.GapRemains);
        AssertChapters([new(1, 0.25), new(2, 149.75), new(3, 349.95)], result.Chapters);
        Assert.Contains(log, l => l.Contains("Re-scan: 0:00:00.25 - 0:05:49.75 from 0:00:15.25"));
        Assert.Contains(log, l => l.Contains("Re-scan recovered 1 chapter(s)"));
    }

    [Fact]
    public async Task Scan_DoesNotReReadAShiftedGap_WhenTheUpgradeModelIsADeliberateDowngrade()
    {
        // A lighter --upgrade-model is the one unambiguous "get the stragglers over with quickly", so
        // doubling the cost of the gap it just failed on is the last thing wanted. Same fixture as
        // above with the models swapped: the chapter stays missing rather than being paid for.
        var log = new List<string>();
        var (result, _, _) = await DetectWithUpgradeTranscriberAsync(
            Options("--model", "large", "--upgrade-model", "base"),
            [new(595, 600), new(645, 650)],
            probe =>
            {
                probe.Add(0, Seg(0.5, " Chapter one."));
                probe.Add(650, Seg(0.2, " Chapter three."));
            },
            upgrade => upgrade.AddWithin(590, 300, Seg(0, " Chapter two.")),
            log);

        Assert.True(result.GapRemains);
        AssertChapters([new(1, 0.25), new(3, 649.95)], result.Chapters);
        Assert.DoesNotContain(log, l => l.Contains("re-reading"));
    }

    [Fact]
    public async Task Scan_ReadsTheOpenEndedTrailingRegionOnce_NeverTwice()
    {
        // The open-ended sweep runs on every file now that the trailing scan is the default, so its
        // cost is a standing cost for a whole library and a second reading of audio nothing suspects
        // would double it. It is not left unguarded against the chunk-boundary problem the shifted
        // re-read exists for: with no re-read to follow, this transcription snaps its own seams to
        // silences instead, which is what keeps an announcement off a border to begin with.
        var (_, log, _) = await DetectWithLogAsync(
            OptionsWithTrailingScan(),
            [new(595, 600)],
            s => s.Add(600, Seg(0.5, " Chapter one.")));

        Assert.Contains(log, l => l.Contains("transcribing trailing region 0:09:59.75 - 1:00:00.00"));
        Assert.DoesNotContain(log, l => l.Contains("re-reading trailing region"));
    }

    [Fact]
    public async Task Scan_ReReadsATargetedTrailingRegionShifted()
    {
        // A targeted sweep is the other half of that rule: --chapter-count names a chapter it has
        // good reason to believe is in the tail, so a second look is worth the same as it is for any
        // sequence gap - and goes by the same model gate.
        var (_, log, _) = await DetectWithLogAsync(
            OptionsWithTrailingScan("--chapter-count", "2"),
            [new(595, 600)],
            s => s.Add(600, Seg(0.5, " Chapter one.")));

        Assert.Contains(log, l => l.Contains(
            "Re-scan: suspicious trailing region 0:09:59.75 - 1:00:00.00 from 0:10:14.75"));
    }

    [Fact]
    public async Task Scan_SkipsTheTrailingReRead_OnAWorseUpgradeModel()
    {
        // A lighter upgrade model is the one unambiguous statement that this file's stragglers are
        // not worth more time, and it governs the targeted trailing sweep exactly as it governs the
        // gaps.
        var log = new List<string>();
        await DetectWithUpgradeTranscriberAsync(
            OptionsWithTrailingScan("--model", "large", "--upgrade-model", "base",
                "--chapter-count", "2"),
            [new(595, 600)],
            probe => probe.Add(600, Seg(0.5, " Chapter one.")),
            upgrade => { },
            log);

        Assert.Contains(log, l => l.Contains("transcribing suspicious trailing region"));
        Assert.DoesNotContain(log, l => l.Contains("re-reading"));
    }

    [Theory]
    // The default floor: exactly the five bands from just under it down to half a second below.
    [InlineData(1.5, 0.5, 5, 1.4, 1.5, 1.0, 1.1)]
    // A floor set low enough that Analyze stored nothing below the third band's minimum.
    [InlineData(0.8, 0.5, 3, 0.7, 0.8, 0.5, 0.6)]
    public void SubFloorSweepBands_RunFromJustUnderTheFloorDownwards_AndStopAtWhatTheAnalysisStored(
        double floor, double storedFloor, int count,
        double firstMin, double firstMax, double lastMin, double lastMax)
    {
        var bands = GapPlanning.SubFloorSweepBands(floor, storedFloor);

        Assert.Equal(count, bands.Count);
        Assert.Equal(firstMin, bands[0].MinSeconds, 9);
        Assert.Equal(firstMax, bands[0].MaxSeconds, 9);
        Assert.Equal(lastMin, bands[^1].MinSeconds, 9);
        Assert.Equal(lastMax, bands[^1].MaxSeconds, 9);
    }

    [Fact]
    public void SubFloorSweepBands_AreEmpty_WhenTheFloorIsAlreadyAtWhatTheAnalysisStored()
        => Assert.Empty(GapPlanning.SubFloorSweepBands(0.5, 0.5));

    [Fact]
    public async Task AnUnreadableChapterNumber_IsReReadWithTheUpgradeModel_AndMarkedWhereItWasHeard()
    {
        // "Paula Monti"'s last chapter, 2026-07-31: heard as "1ère partie, chapitre ban 5" and
        // discarded for want of a number. It sat past the last detected chapter, so no gap ever
        // formed around it and neither the gap re-probe nor Scan was ever pointed at it - the
        // re-read in Probe is the only thing that can reach an announcement in that position.
        var log = new List<string>();
        var (result, _, _) = await DetectWithUpgradeTranscriberAsync(
            Options("--model", "base", "--upgrade-model", "large"),
            [new(595, 600)],
            probe =>
            {
                probe.Add(0, Seg(0.5, " Chapter one."));
                probe.Add(600, Seg(2.5, " CHAPTER XIIII. THE SHAKING OF THE SHEETS"));
            },
            upgrade => upgrade.Add(600, Seg(2.5, " Chapter two.")),
            log);

        Assert.False(result.GapRemains);
        AssertChapters([new(1, 0.25), new(2, 602.25)], result.Chapters);
        Assert.Contains(log, l => l.Contains("chapter phrase at 0:10:02.50, no readable number"));
        Assert.Contains(log, l => l.Contains("upgrade model: chapter 2"));
    }

    [Fact]
    public async Task AnUnreadableChapterNumber_KeepsThePositionAndConfidenceOfTheReadingThatHeardIt()
    {
        // The re-read contributes the number and nothing else. Here it reads the number two and a
        // half seconds away from where the original announcement sits and with a far higher
        // confidence; the mark must still land on the original phrase (602.5 minus the 0.25 s lead)
        // and carry the original 0.42, or a recovered chapter would report a confidence measured on
        // audio it was not found in.
        var (result, _, _) = await DetectWithUpgradeTranscriberAsync(
            Options("--model", "base", "--upgrade-model", "large", "--quick-marks"),
            [new(595, 600)],
            probe =>
            {
                probe.Add(0, Seg(0.5, " Chapter one."));
                probe.Add(600, Seg(2.5, " CHAPTER XIIII. THE SHAKING OF THE SHEETS", 0.42));
            },
            upgrade => upgrade.Add(600, Seg(5.0, " Chapter two.", 0.99)));

        AssertChapters([new(1, 0.25, 1.0), new(2, 602.25, 0.42)], result.Chapters);
    }

    [Fact]
    public async Task AnUnreadableChapterNumber_IsLeftUnmarked_WhenTheReReadDoesNotContinueTheSequence()
    {
        // The guard that keeps this from planting marks on prose. "I Shall Wear Midnight",
        // 2026-07-31: a window re-heard the already-marked chapter 10 as "CHAPTER X", and a re-read
        // of it can only ever produce that same 10 - which does not continue the sequence, so no
        // mark comes of it. An in-text mention behaves identically.
        var log = new List<string>();
        var (result, _, _) = await DetectWithUpgradeTranscriberAsync(
            Options("--model", "base", "--upgrade-model", "large", "--quick-marks"),
            [new(595, 600)],
            probe =>
            {
                probe.Add(0, Seg(0.5, " Chapter one."));
                probe.Add(600, Seg(2.5, " CHAPTER XIIII. THE SHAKING OF THE SHEETS"));
            },
            upgrade => upgrade.Add(600, Seg(2.5, " Chapter one.")),
            log);

        AssertChapters([new(1, 0.25)], result.Chapters);
        Assert.Contains(log, l => l.Contains("upgrade model: 1 - " +
                                             "also outside the sequence after chapter 1"));
        Assert.Contains(log, l => l.Contains("no sequence-continuing number read - " +
                                             "announcement left unmarked"));
    }

    [Fact]
    public async Task Statistics_ReportShortestPrecedingSilence_WithAndWithoutChapterOne()
    {
        // Chapter 1's own (intro) silence is the shortest at 2 s, chapter 2's is 3 s, chapter 3's
        // 4 s. The overall shortest-preceding-silence statistic is therefore 2 s, but the
        // inter-chapter figure - which excludes chapter 1's atypical intro transition - is 3 s.
        // The jingle statistics stay null (plain mode), and audio was fed to Whisper.
        var (result, _, _) = await DetectFullAsync(
            Options("--min-silence-length", "1.5"),
            [new(8, 10), new(597, 600), new(903, 907)],
            s =>
            {
                s.Add(0, Seg(10.2, " Chapter one.")); // in the file-start window; anchored to the 2 s silence 8-10
                s.Add(600, Seg(0.3, " Chapter two.")); // preceded by 3 s
                s.Add(907, Seg(0.2, " Chapter three.")); // preceded by 4 s
            });

        Assert.Equal([1, 2, 3], result.Chapters.Select(c => c.Number));
        Assert.Equal(2.0, result.Stats.MinPrecedingSilenceSeconds!.Value, 3);
        Assert.Equal(3.0, result.Stats.MinInterChapterSilenceSeconds!.Value, 3);
        Assert.Null(result.Stats.MaxJingleLengthSeconds);
        Assert.Null(result.Stats.MaxInterChapterJingleSeconds);
        Assert.True(result.Stats.WhisperAudioSeconds > 0);
    }

    [Fact]
    public async Task Statistics_InJingleMode_MeasureTheJingle_AndCountOnlyTheLeadingSilence()
    {
        // Chapter 2's transition is framed [silence 638-642][jingle][silence 648-650][phrase],
        // the whole 640-651 stretch being one VAD non-speech region. The jingle is measured from
        // the leading silence's end (642) up to the phrase (snapped to the region end 651): 9 s.
        // Only the *leading* 4 s silence counts toward the silence statistic; the 2 s silence
        // between jingle and phrase is ignored, so the shortest preceding silence is 4 s, not 2 s.
        var (result, _, _) = await DetectFullAsync(
            Options("--mark-before-jingle", "--min-silence-length", "1.5"),
            [new(638, 642), new(648, 650)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                // The region has a leading silence, so its transition is probed from that silence's
                // end (642), not the region start; the phrase (650) then snaps to the region end 651.
                s.Add(642, Seg(8, " Chapter two."));
            },
            new FakeVad { Speech = [new(0, 640), new(651, 3600)] }); // non-speech region 640-651

        Assert.Equal([1, 2], result.Chapters.Select(c => c.Number));
        Assert.Equal(4.0, result.Stats.MinPrecedingSilenceSeconds!.Value, 3);
        Assert.Equal(9.0, result.Stats.MaxJingleLengthSeconds!.Value, 3);
        // Chapter 1 (at the file start) contributes no silence or jingle, so the inter-chapter
        // figures equal the overall ones here - only chapter 2 was measurable either way.
        Assert.Equal(4.0, result.Stats.MinInterChapterSilenceSeconds!.Value, 3);
        Assert.Equal(9.0, result.Stats.MaxInterChapterJingleSeconds!.Value, 3);
    }

    /// <summary>With silence probing off, a chapter whose transition is a jingle is still found -
    /// and found through the jingle, even though a silence leads it. Without the option that VAD
    /// region is dropped as a duplicate of its leading silence's candidate; with it there is no
    /// such candidate to be a duplicate of.</summary>
    [Fact]
    public async Task MinSilenceZero_StillProbesJingles()
    {
        var (result, _, _) = await DetectFullAsync(
            Options("--min-silence-length", "0"),
            [new(638, 642), new(648, 650)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(640, Seg(10, " Chapter two."));
            },
            new FakeVad { Speech = [new(0, 640), new(651, 3600)] }); // non-speech region 640-651

        Assert.Equal([1, 2], result.Chapters.Select(c => c.Number));
    }

    /// <summary>The other half of the bargain: an ordinary pause is no longer a reason to look, so
    /// a chapter announced after one and nothing else is not found at all.</summary>
    [Fact]
    public async Task MinSilenceZero_DoesNotProbeAPlainSilence()
    {
        var (result, _, audio) = await DetectFullAsync(
            Options("--min-silence-length", "0"),
            [new(595, 600), new(1195, 1200)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(1200, Seg(0.2, " Chapter two."));
            },
            new FakeVad { Speech = [new(0, 3600)] }); // speech throughout: no jingle anywhere

        // Only the region-start candidate at 0 was probed, which is where chapter 1 is.
        AssertChapters([new(1, 0.25)], result.Chapters);
        Assert.DoesNotContain(1200.0, audio.DecodeStarts);
    }

    /// <summary>The silence scan itself keeps running: marks are placed and refined against it,
    /// so switching it off would degrade every mark rather than merely finding fewer of them.</summary>
    [Fact]
    public async Task MinSilenceZero_StillScansForSilences()
    {
        var (_, log, _) = await DetectWithLogAsync(
            Options("--min-silence-length", "0"),
            [new(595, 600), new(1195, 1200)],
            s => s.Add(0, Seg(0.5, " Chapter one.")),
            vad: new FakeVad { Speech = [new(0, 3600)] });

        Assert.Contains(log, l => l.Contains("Analyze: 2 silence(s) found, none probed"));
    }

    /// <summary>Nothing about a run without the option may change - the whole existing suite is
    /// the real evidence for that, and this pins the one figure the option reads.</summary>
    [Fact]
    public void MinSilenceZero_LeavesTheStoredSilenceFloorAlone()
    {
        Assert.Equal(DetectionTuning.MinStoredSilenceSeconds,
            Options("--min-silence-length", "0").StoredSilenceFloorSeconds);
        Assert.Equal(DetectionTuning.MinStoredSilenceSeconds,
            Options("--min-silence-length", "1.5").StoredSilenceFloorSeconds);
        // Only a floor below the stored one lowers it, exactly as before.
        Assert.Equal(0.2, Options("--min-silence-length", "0.2").StoredSilenceFloorSeconds, 3);
    }

    [Fact]
    public async Task AutoMinSilence_TightensThreshold_AndSkipsShorterSilences()
    {
        // Default --min-silence-length auto. Chapter 2's triggering silence is 5 s, tightening
        // the threshold to 3.75 s (0.75x); the 3 s silence at 700-703 falls below it and must
        // not be probed at all, but the 5 s silence at 900-905 still is, finding chapter 3.
        var (result, _, audio) = await DetectFullAsync(
            Options(),
            [new(595, 600), new(700, 703), new(900, 905)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(600, Seg(0.3, " Chapter two."));
                s.Add(905, Seg(0.2, " Chapter three."));
            });

        Assert.False(result.GapRemains);
        AssertChapters(
            [new(1, 0.25), new(2, 600.05), new(3, 904.95)],
            result.Chapters);
        Assert.DoesNotContain(703, audio.DecodeStarts);
    }

    [Fact]
    public async Task AutoMinSilence_DoesNotTightenOnTheFirstMark()
    {
        // The intro-to-chapter-1 silence (30 s) is longer than the real inter-chapter breaks
        // (5 s each) - a common shape (title/credits pause vs. normal chapter breaks).
        // Tightening on chapter 1's own triggering silence would push the threshold above
        // those breaks and silently skip chapters 2 and 3 entirely (no gap would even be
        // detected, since Probe would never find a second chapter to compare against).
        // Tightening must only start once the second mark is found, using its own (genuine
        // inter-chapter) triggering silence instead.
        var (result, _, audio) = await DetectFullAsync(
            Options(),
            [new(10, 40), new(595, 600), new(900, 905)],
            s =>
            {
                s.Add(40, Seg(0.3, " Chapter one."));
                s.Add(600, Seg(0.3, " Chapter two."));
                s.Add(905, Seg(0.2, " Chapter three."));
            });

        Assert.False(result.GapRemains);
        AssertChapters(
            [new(1, 40.05), new(2, 600.05), new(3, 904.95)],
            result.Chapters);
        Assert.Contains(600.0, audio.DecodeStarts);
        Assert.Contains(905.0, audio.DecodeStarts);
    }

    [Fact]
    public async Task AutoMinSilence_ResetsThreshold_AndRetriesSkippedSilences_OnSequenceGap()
    {
        // Same setup, but chapter 3's phrase only lives in the skipped 700-703 silence and the
        // next probed silence yields chapter 4 instead - a sequence gap. The detector must
        // re-probe what it skipped since chapter 2 unconditionally, finding chapter 3 there and
        // closing the gap without needing Scan at all.
        var (result, _, audio) = await DetectFullAsync(
            Options(),
            [new(595, 600), new(700, 703), new(900, 905)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(600, Seg(0.3, " Chapter two."));
                s.Add(703, Seg(0.3, " Chapter three."));
                s.Add(905, Seg(0.2, " Chapter four."));
            });

        Assert.False(result.GapRemains);
        AssertChapters(
            [new(1, 0.25), new(2, 600.05), new(3, 703.05), new(4, 904.95)],
            result.Chapters);
        Assert.Contains(703, audio.DecodeStarts);
    }

    [Fact]
    public async Task AutoMinSilence_NeverRaisesTheThreshold_AboveAnEarlierAnchorSilence()
    {
        // Chapter 2's 4 s anchor silence sets the threshold to 3 s (0.75x). Chapter 3's anchor
        // is much longer (8 s) - it must NOT raise the threshold to 6 s: a threshold above an
        // already observed inter-chapter silence would skip exactly the kind of break that has
        // proven to precede this book's chapters. Chapter 4's 3.5 s silence (above 3 s, below
        // the wrongly-raised 6 s) must therefore still be probed and found - and since chapter 4
        // is the last one, no later mark could ever trigger a gap recovery for it, so a raised
        // threshold would lose it silently and for good.
        var (result, _, audio) = await DetectFullAsync(
            Options(),
            [new(596, 600), new(892, 900), new(1196.5, 1200)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(600, Seg(0.3, " Chapter two."));
                s.Add(900, Seg(0.3, " Chapter three."));
                s.Add(1200, Seg(0.3, " Chapter four."));
            });

        Assert.False(result.GapRemains);
        Assert.Equal([1, 2, 3, 4], result.Chapters.Select(c => c.Number));
        Assert.Contains(1200.0, audio.DecodeStarts);
    }

    [Fact]
    public async Task AutoMinSilence_LowersTheThreshold_WhenAnAnchorSilenceComesInShorter()
    {
        // Chapter 2's 5 s anchor silence sets the threshold to 3.75 s (0.75x); chapter 3's
        // shorter 4 s anchor (still above 3.75 s, so it is probed) must lower it further to
        // 3 s - the threshold follows the *shortest* observed inter-chapter break down.
        // Chapter 4's 3.2 s silence sits between the two (3 < 3.2 < 3.75), so it is only
        // probed - and chapter 4, being last, only ever found - if the lowering happened.
        var (result, _, audio) = await DetectFullAsync(
            Options(),
            [new(595, 600), new(896, 900), new(1196.8, 1200)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(600, Seg(0.3, " Chapter two."));
                s.Add(900, Seg(0.3, " Chapter three."));
                s.Add(1200, Seg(0.3, " Chapter four."));
            });

        Assert.False(result.GapRemains);
        Assert.Equal([1, 2, 3, 4], result.Chapters.Select(c => c.Number));
        Assert.Contains(1200.0, audio.DecodeStarts);
    }

    /// <summary>The two figures --min-silence-length auto is made of: where probing starts, and how
    /// far the book's own breaks may argue it down. They used to be one number, which made the
    /// lowering half of the adaptation unreachable.</summary>
    [Fact]
    public void AutoMinSilence_StartsAtTheDemand_ButItsFloorSitsWellBelowIt()
    {
        Assert.Equal(1.5, Options().MinSilenceSeconds, 3);
        Assert.Equal(DetectionTuning.AdaptiveSilenceFloorSeconds, Options().AdaptiveFloorSeconds, 3);
        // An explicit length is the whole story - no adaptation, so nothing below it is ever looked at.
        Assert.Equal(2.0, Options("--min-silence-length", "2.0").AdaptiveFloorSeconds, 3);
        // A demand already under the floor stays the binding one.
        Assert.Equal(0.6, Options("--min-silence-length", "0.6").AdaptiveFloorSeconds, 3);
    }

    [Fact]
    public async Task AdaptiveSubFloorSweep_RecoversAChapterThePassItselfCouldNotReach()
    {
        // Why the sweep still exists now that a gap re-probe reaches under the threshold too: the
        // re-probe reaches only as far as the threshold has adapted *by the time it runs*, and the
        // threshold keeps falling as the book goes on. The whole descent is here:
        //   * chapter 2's 1.6 s anchor measures the book at 1.2 s;
        //   * chapter 4 reveals the gap over chapter 3, but its 1.0 s pause is under that 1.2 s
        //     reach, so the re-probe finds nothing - and chapter 4's own 1.5 s anchor then brings
        //     the measurement to 1.125 s;
        //   * chapter 6 reveals the gap over chapter 5, whose 1.2 s pause *is* within reach now, so
        //     the re-probe recovers it - and that pause, being a proven chapter break, takes the
        //     measurement down to 0.9 s;
        //   * the sweep runs after the region loop with that final figure, and only there does
        //     chapter 3 come within reach.
        // The real shape is "Paula Monti" (2026-07-31), whose threshold was 1.23 s at the chapter
        // 2-5 gap and 0.8 s by the end of the book.
        var (result, log, audio) = await DetectWithLogAsync(
            OptionsInOneSweep(),
            [new(598.4, 600), new(899, 900), new(1198.5, 1200), new(1498.8, 1500),
             new(1798.5, 1800)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(600, Seg(0.3, " Chapter two."));
                s.Add(900, Seg(0.3, " Chapter three."));
                s.Add(1200, Seg(0.3, " Chapter four."));
                s.Add(1500, Seg(0.3, " Chapter five."));
                s.Add(1800, Seg(0.3, " Chapter six."));
            });

        Assert.False(result.GapRemains);
        Assert.Equal([1, 2, 3, 4, 5, 6], result.Chapters.Select(c => c.Number));
        Assert.Contains(log, l => l.Contains("sequence gap 2-4") &&
                                  l.Contains("no candidates between the two marks"));
        // Chapter 5 by the re-probe reaching under the demand, chapter 3 only by the sweep.
        Assert.Contains(1499.0, audio.DecodeStarts);
        Assert.Contains(log, l => l.Contains("measure down to 0.9 s, below the 1.5 s"));
        Assert.Contains(log, l => l.Contains("Probe: sweeping 1 silence(s) of 1.0-1.1 s for chapter 3"));
    }

    [Fact]
    public async Task AdaptiveSubFloorSweep_FiresForAGap_EvenWhereNoMarkMeasuredABreak()
    {
        // The book the old trigger could not see: every chapter opens with music, so every mark is
        // found at a jingle, and a jingle's hush is deliberately not allowed to teach the threshold
        // (it measures the transition's lead-in, not the break between two chapters). Nothing
        // measures anything, and the sweep used to wait for a measurement - on the one shape it
        // exists for, a book of jingles with a bare 1.0 s pause in front of the chapter that went
        // missing. The gap is what says a chapter is missing, so the gap is what fires it.
        var (result, log, _) = await DetectWithLogAsync(
            Options("--quick-marks"),
            [new(899, 900)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(610, Seg(0.3, " Chapter two."));
                s.Add(900, Seg(0.3, " Chapter three."));
                s.Add(1210, Seg(0.3, " Chapter four."));
            },
            new FakeVad { Speech = [new(0, 600), new(610, 1200), new(1210, 3600)] });

        Assert.False(result.GapRemains);
        Assert.Equal([1, 2, 3, 4], result.Chapters.Select(c => c.Number));
        Assert.Contains(log, l => l.Contains("no chapter break measured") &&
                                  l.Contains("sweeping the gaps for shorter pauses"));
        Assert.Contains(log, l => l.Contains("Probe: sweeping 1 silence(s) of 1.0-1.1 s for chapter 3"));
    }

    [Fact]
    public async Task AdaptiveSubFloorSweep_SpendsItsBudgetOnTheLikeliestBand_RatherThanRefusingTheLot()
    {
        // Why Probe sweeps in bands rather than one wide pass over everything under the demand.
        // The gap holds eleven sub-floor pauses: chapter 3's at 1.45 s and ten fillers at 1.05 s.
        // One band covering the whole range costs eleven probes against a budget of 7.5, so the
        // sweep would be refused outright and the chapter left to the later passes - which is
        // exactly what the build-300 corpus run did on every gap of "Paula Monti" and
        // "I Shall Wear Midnight". Longest-first, the top band costs one probe, finds the chapter
        // and closes the gap, and the ten fillers are never looked at.
        var (result, log, audio) = await DetectWithLogAsync(
            Options("--quick-marks"),
            [
                new(595, 600), new(698.55, 700),
                new(748.95, 750), new(758.95, 760), new(768.95, 770), new(778.95, 780),
                new(788.95, 790), new(798.95, 800), new(808.95, 810), new(818.95, 820),
                new(828.95, 830), new(838.95, 840),
                new(895, 900)
            ],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(600, Seg(0.3, " Chapter two."));
                s.Add(700, Seg(0.3, " Chapter three."));
                s.Add(900, Seg(0.3, " Chapter four."));
            });

        Assert.False(result.GapRemains);
        Assert.Equal([1, 2, 3, 4], result.Chapters.Select(c => c.Number));
        Assert.Contains(log, l => l.Contains("Probe: sweeping 1 silence(s) of 1.4-1.5 s for chapter 3"));
        Assert.Contains(log, l => l.Contains("Probe: sub-floor sweep closed the gap at 1.4-1.5 s"));
        Assert.DoesNotContain(audio.DecodeStarts, d => d >= 745 && d <= 845);
    }

    [Fact]
    public async Task AdaptiveSubFloorSweep_LeavesTheProbeCandidateGridAlone()
    {
        // The same book without chapter 4, so nothing bounds a gap around chapter 3 and the sweep
        // has nowhere to run. The 1.4 s pause must then never be decoded at all: it is not a Probe
        // candidate and must not become one just because the threshold measured 1.2 s.
        //
        // This is the whole point of sweeping separately rather than widening Analyze's candidate
        // list. That list is the grid the probe windows are planned on - a shared border is where
        // one decode stops and the next resumes - so an extra entry re-cuts the decodes around it,
        // and Whisper's reading of a stretch depends on the window it arrives in. Measured on a
        // BARDIOC clip (2026-08-08): widening the list re-cut the last two minutes from 49.4 s and
        // 34.9 s decodes into 28.8, 19.8 and 13.5 s, and a chapter announcement that the long decode
        // heard comfortably fell 1.2 s short of a short one's end and was lost.
        var (result, _, audio) = await DetectFullAsync(
            Options(),
            [new(598.4, 600), new(898.6, 900)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(600, Seg(0.3, " Chapter two."));
                s.Add(900, Seg(0.3, " Chapter three."));
            });

        Assert.Equal([1, 2], result.Chapters.Select(c => c.Number));
        Assert.DoesNotContain(900.0, audio.DecodeStarts);
    }

    [Fact]
    public async Task AutoMinSilence_AfterAGapRecovery_TheThresholdAccountsForTheGapMarksShorterSilence()
    {
        // Chapter 2 (5 s anchor) tightens the threshold to 3.75 s; chapter 3's 3 s silence is
        // skipped, chapter 4 is found -> sequence gap -> re-probe recovers chapter 3. Its 3 s
        // anchor must fold into the threshold (0.75 x 3 = 2.25 s), so chapter 5's 2.5 s
        // silence - below chapter 2's 3.75 s but above 2.25 s - is still probed and found.
        // Chapter 5 is the last mark, so nothing could recover it if it were skipped.
        var (result, log, audio) = await DetectWithLogAsync(
            OptionsInOneSweep(),
            [new(595, 600), new(697, 700), new(895, 900), new(1197.5, 1200)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(600, Seg(0.3, " Chapter two."));
                s.Add(700, Seg(0.3, " Chapter three."));
                s.Add(900, Seg(0.3, " Chapter four."));
                s.Add(1200, Seg(0.3, " Chapter five."));
            });

        Assert.False(result.GapRemains);
        Assert.Equal([1, 2, 3, 4, 5], result.Chapters.Select(c => c.Number));
        Assert.Contains(700.0, audio.DecodeStarts);
        Assert.Contains(1200.0, audio.DecodeStarts);
        // The gate did follow this recovery down, so the withholding note must stay off - it would
        // otherwise sit in the log directly contradicting the "threshold lowered" line beneath it,
        // which is exactly what the first build-341 corpus run printed on "Paula Monti".
        Assert.Contains(log, l => l.Contains("threshold lowered to 2.25"));
        Assert.DoesNotContain(log, l => l.Contains("not applied to the forward scan"));
    }

    [Fact]
    public async Task AutoMinSilence_AGapRecoveryAtFloorLevel_TeachesTheGapPassesWithoutOpeningTheForwardScan()
    {
        // The other side of the test above, and the reason the rule keys on the adaptive floor
        // rather than on "was this a gap recovery". Same shape - chapter 2's 5 s anchor sets a
        // 3.75 s threshold, chapter 3 is skipped and recovered from the gap - but chapter 3's break
        // is 1 s, which scales to 0.75 s and is therefore clamped to the 0.8 s floor. A break the
        // forward scan would never probe for even at its most generous says nothing about what the
        // forward scan should spend on the rest of the book, so the gate stays at 3.75 s and the
        // 1.2 s decoy pause at 1100 - which the old running minimum would have decoded, the gate
        // having dropped to 0.8 s - is passed over. The evidence is still recorded for the two
        // gap-scoped mechanisms that can act on it.
        //
        // Measured on "Die Cyber-Brutzellen" (builds 331 vs 339, 2026-08-17): exactly this, at
        // 1.01 s, floored the threshold with 9.5 hours of book left to scan and cost 921 -> 2177
        // probes and 26.5 -> 55.7 minutes for byte-identical output - all 29 chapters at the same
        // positions, the seven build 339 accepted "at a silence" having been accepted "at a jingle"
        // by build 331 at the same millisecond.
        // Getting a floor-level anchor takes the route a real book takes, because an ordinary
        // candidate cannot produce one: Analyze only stores silences of 1.5 s and up, and 0.75 x 1.5
        // is still 1.125 s. Chapter 3 is announced behind the 1 s pause at 700-701, which reaches the
        // candidate list only by being promoted (the sandwiched-announcement rule admits a stored
        // pause once a probed one follows within 3.5 s - here the 4 s pause at 702-706). Promoted or
        // not it is still under the 3.75 s gate, so the forward scan skips it and only the gap
        // re-probe reads it; the mark it recovers anchors to that 1 s pause, scaling to 0.75 s and
        // clamping to the floor.
        //
        // The decoy is 2 s rather than something shorter for the same reason: below 1.5 s it would
        // never be a candidate and the assertion would pass without proving anything.
        var (result, log, audio) = await DetectWithLogAsync(
            OptionsInOneSweep(),
            [new(595, 600), new(700, 701), new(702, 706), new(895, 900),
             new(1098, 1100), new(1495, 1500)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(600, Seg(0.3, " Chapter two."));
                s.Add(701, Seg(0.3, " Chapter three."));
                s.Add(900, Seg(0.3, " Chapter four."));
                s.Add(1500, Seg(0.3, " Chapter five."));
            });

        Assert.False(result.GapRemains);
        Assert.Equal([1, 2, 3, 4, 5], result.Chapters.Select(c => c.Number));
        Assert.Contains(log, l => l.Contains("measured a 0.8 s chapter break") &&
                                  l.Contains("not applied to the forward scan"));
        Assert.DoesNotContain(log, l => l.Contains("threshold lowered to 0.8"));
        Assert.DoesNotContain(audio.DecodeStarts, d => d is >= 1096 and <= 1101);
    }

    [Fact]
    public async Task AGapReprobe_StopsAtTheChapterThatClosesTheGap_WithoutRefindingIt()
    {
        // Three consecutive skipped candidates (2.0 s silences, below the 3.75 s threshold chapter
        // 2's 5 s anchor sets), all within one probe window of each other, and chapter 3 announced
        // right after the first of them. Chapter 4 opens the gap, the re-probe recovers chapter 3
        // from candidate 700 - and must then stop: the windows at 705 and 710 overlap the same
        // announcement and would each re-detect it, since the re-probe deliberately keeps accepting
        // numbers above chapter 2. Observed on real audio as four identical marks for one chapter,
        // each paying for its own mark refinement (BARDIOC.m4b, 2026-07-30).
        var (result, log, audio) = await DetectWithLogAsync(
            OptionsInOneSweep("--verbose"),
            [new(595, 600), new(698, 700), new(703, 705), new(708, 710), new(1195, 1200)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(600, Seg(0.3, " Chapter two."));
                s.Add(700, Seg(0.3, " Chapter three."));
                s.Add(1200, Seg(0.3, " Chapter four."));
            });

        Assert.False(result.GapRemains);
        Assert.Equal([1, 2, 3, 4], result.Chapters.Select(c => c.Number));
        // The first re-probe candidate ran - its window opens one trimmed lead-in before the
        // silence ends, not at the silence's own end - and the two behind it never did.
        Assert.Contains(699.0, audio.DecodeStarts);
        Assert.DoesNotContain(704.0, audio.DecodeStarts);
        Assert.DoesNotContain(709.0, audio.DecodeStarts);
        Assert.Single(log, l => l.Contains("chapter 3 detected"));
        Assert.Contains(log, l => l.Contains("gap before chapter 4 closed") &&
                                  l.Contains("stopped after 1 of 3 candidate(s)"));
    }

    [Fact]
    public async Task AfterAGap_WithNothingLookedAtSinceTheLastMark_SaysSoRatherThanReprobing()
    {
        // Two marks with nothing between them: no candidate at all sits after chapter 2's own
        // announcement, so a gap leaves Probe with nothing to retry. The log has to say that:
        // without the note, this case and "a candidate was declined" look identical from the
        // outside, which is the first thing worth knowing when a chapter goes missing.
        var (result, log, _) = await DetectWithLogAsync(
            Options("--verbose", "--min-silence-length", "1.5"),
            [new(595, 600), new(1195, 1200)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(600, Seg(0.3, " Chapter two."));
                s.Add(1200, Seg(0.3, " Chapter four."));
            });

        Assert.True(result.GapRemains);
        Assert.Contains(log, l => l.Contains("sequence gap 2-4") &&
                                  l.Contains("no candidates between the two marks"));
        Assert.DoesNotContain(log, l => l.Contains("re-probing"));
    }

    [Fact]
    public async Task AGapReprobe_ReframesTheStretch_RatherThanWideningIt()
    {
        // What a second look is worth is its own framing. The candidate at 897 was probed with the
        // primary scan's window - a 3 s lead-in inside the silence, 22 s past its end - and heard
        // nothing; the re-probe rebuilds the same stretch as recovery candidates, which open one
        // second inside the silence and reach 17 s past it. Different audio in a different frame,
        // and narrower rather than wider: a window sized to span this book's longest jingle is
        // exactly the width that loses an announcement (see WhisperChunkSeconds).
        var (result, log, audio) = await DetectWithLogAsync(
            OptionsInOneSweep("--verbose"),
            [new(595, 600), new(895, 900), new(1195, 1200)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(600, Seg(3.0, " Chapter two."));
                // Heard by the re-probe's 18 s framing and by nothing wider, so the primary scan's
                // 25 s window comes back empty and only the re-framing finds the chapter.
                s.AddWithin(20, 900, Seg(5.0, " Chapter three."));
                s.Add(1200, Seg(3.0, " Chapter four."));
            },
            new FakeVad { Speech = [new(0, 3600)] });

        Assert.False(result.GapRemains);
        Assert.Equal([1, 2, 3, 4], result.Chapters.Select(c => c.Number));
        // The two framings of one candidate, in order: the primary scan's 25 s from 897 and the
        // re-probe's 18 s from 899.
        Assert.Contains((897.0, (double?)25.0), audio.DecodeWindows);
        Assert.Contains((899.0, (double?)18.0), audio.DecodeWindows);
        Assert.Contains(log, l => l.Contains("sequence gap 2-4") &&
                                  l.Contains("re-probing 1 candidate(s)"));
    }

    [Fact]
    public async Task AGapReprobe_ReachesUnderTheThresholdItStartedAt()
    {
        // "Paula Monti"'s shape: a narrator whose chapter breaks run shorter than the 1.5 s the run
        // opened at. Chapter 2's 1.8 s break brings the threshold down to 1.35 s, but Analyze's
        // candidate list was cut at 1.5 s before Probe ever saw it, so chapter 3's 1.4 s break is
        // not on it and no amount of adapting can put it there. The gap re-probe builds its own
        // list and reaches down to what the book has just taught.
        var (result, _, audio) = await DetectFullAsync(
            Options("--quick-marks"),
            [new(598.2, 600), new(898.6, 900), new(1195, 1200)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(600, Seg(0.3, " Chapter two."));
                s.Add(900, Seg(0.3, " Chapter three."));
                s.Add(1200, Seg(0.3, " Chapter four."));
            });

        Assert.False(result.GapRemains);
        Assert.Equal([1, 2, 3, 4], result.Chapters.Select(c => c.Number));
        // 899: a recovery pass opens one trimmed lead-in inside the silence it never saw before.
        Assert.Contains(899.0, audio.DecodeStarts);
    }

    [Fact]
    public async Task AGapReprobe_DoesNotReachUnderTheThreshold_WhenTheGapCannotAffordIt()
    {
        // Same shape in a gap a tenth the length and dense with short pauses: four candidates cost
        // more decode windows than a minute-long gap may spend on being probed at all, which is the
        // bound that stops a book of short pauses from probing its way past what transcribing the
        // gap outright would have cost. The re-probe falls back to the list it would have had.
        //
        // AddWithin(20) keeps the chapter reachable only from a recovery window, so nothing later
        // in the run quietly covers for the refusal: Re-probe's sweep is refused on the same
        // budget, and Scan's chunks are too wide to hear it.
        var (result, log, _) = await DetectWithLogAsync(
            OptionsInOneSweep("--verbose", "--quick-marks"),
            [new(598.2, 600), new(608.6, 610), new(618.6, 620), new(628.6, 630),
             new(638.6, 640), new(655, 660)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(600, Seg(0.3, " Chapter two."));
                s.AddWithin(20, 630, Seg(0.2, " Chapter three."));
                s.Add(660, Seg(0.3, " Chapter four."));
            });

        Assert.True(result.GapRemains);
        Assert.Equal([1, 2, 4], result.Chapters.Select(c => c.Number));
        Assert.Contains(log, l => l.Contains("sequence gap 2-4") &&
                                  l.Contains("not reaching below 1.35 s"));
    }

    [Fact]
    public async Task AGapReprobe_ProbesTheSilencesTheJingleClassificationSuppressed()
    {
        // The 3 s hush at 877-880 leads a jingle, so the primary scan drops it as a candidate: the
        // jingle below covers that transition and knows where its own music ends (900). Here the
        // census is wrong - the announcement is spoken at 880.2, right behind the hush - and the
        // jingle candidate duly comes back empty. A gap re-probe is the one situation where the
        // census's word about a transition is worth nothing, so the hush becomes a candidate again
        // and chapter 3 is found behind it.
        //
        // AddWithin(20) is what confines it to that candidate: at 25 s the jingle's music tiles
        // cover the same audio and would find it first.
        var (result, log, audio) = await DetectWithLogAsync(
            Options("--verbose", "--quick-marks"),
            [new(595, 600), new(877, 880), new(1195, 1200)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(600, Seg(0.3, " Chapter two."));
                s.AddWithin(20, 880.2, Seg(0, " Chapter three."));
                s.Add(1200, Seg(0.3, " Chapter four."));
            },
            new FakeVad { Speech = [new(0, 877), new(900, 3600)] });

        Assert.False(result.GapRemains);
        Assert.Equal([1, 2, 3, 4], result.Chapters.Select(c => c.Number));
        // 879: one trimmed lead-in inside the suppressed hush, a candidate the primary scan never
        // built at all.
        Assert.Contains(879.0, audio.DecodeStarts);
        Assert.Contains(log, l => l.StartsWith("chapter 3 detected") && l.Contains(", at a silence)"));
    }

    [Fact]
    public async Task ExplicitMinSilenceLength_NeverSkipsAnyDetectedSilence()
    {
        // With an explicit numeric --min-silence-length, adaptive tightening is off: every
        // silence from Analyze is probed regardless of length or what was found before it.
        var (result, _, audio) = await DetectFullAsync(
            Options("--min-silence-length", "1.5"),
            [new(595, 600), new(700, 703), new(900, 905)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(600, Seg(0.3, " Chapter two."));
                s.Add(905, Seg(0.2, " Chapter three."));
            });

        AssertChapters([new(1, 0.25), new(2, 600.05), new(3, 904.95)], result.Chapters);
        // 700, not 703: a silence candidate's window opens a lead-in inside its own silence.
        Assert.Contains(700, audio.DecodeStarts);
    }

    [Fact]
    public async Task InTextMentions_OfEarlierChapters_AreDropped()
    {
        // "chapter two" spoken inside chapter 3's probe window is a regression and must
        // not override the already detected chapter sequence.
        var result = await DetectAsync(
            Options(),
            [new(595, 600), new(1195, 1200), new(1795, 1800)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(600, Seg(0.3, " Chapter two."));
                s.Add(1200, Seg(0.2, " Chapter three."));
                s.Add(1800, Seg(0.4, " as I said in chapter two."));
            });

        AssertChapters(
            [new(1, 0.25), new(2, 600.05), new(3, 1199.95)],
            result.Chapters);
    }

    [Fact]
    public async Task JingleMark_WithContinuousSpeechAroundTheSilence_KeepsTheOriginalMark()
    {
        // Probe window at 600: continuous speech until 615, short silence 615-618, phrase at
        // 618.2. The default-mode original mark (617.95, 0.25 s before the phrase) sits inside
        // that silence, so step 1 backs out to the silence's own start (615) - but real VAD
        // speech (continuous [0, 3600]) covers that point too, so step 2 recognises this as an
        // ordinary in-narration pause with no jingle in it at all and returns the original mark
        // unchanged, rather than the old fixed "0.5 s before the silence" placement (617.5).
        var result = await DetectAsync(
            Options("--quick-marks", "--mark-before-jingle"),
            [new(595, 600), new(615, 618)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(600, Seg(18.2, " Chapter two."));
            },
            // VAD runs (as it always does with --mark-before-jingle in production) but finds no
            // non-speech region of its own here - continuous speech throughout - so the anchor
            // falls back to the plain silence-based rule exactly as it would without VAD at all.
            new FakeVad { Speech = [new(0, 3600)] });

        AssertContainsChapter(new DetectedChapter(2, 617.95), result.Chapters);
    }

    [Fact]
    public async Task JingleWithNoSilenceEitherSide_IsCaughtByVad_MarkAtJingleStart()
    {
        // No silencedetect silence anywhere near the transition - the jingle abuts speech on
        // both sides, the bare-jingle-as-sole-separator case. Only VAD (which sees the
        // jingle's music as non-speech, same as it would silence) can locate this transition
        // at all; silencedetect alone would never produce a Probe candidate here. The mark
        // must land at the jingle's own start, with no lead, since there is no absorbable
        // silence to place the usual 0.5 s lead in.
        var (result, _, audio) = await DetectFullAsync(
            Options("--quick-marks", "--mark-before-jingle"),
            [],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(700, Seg(0.3, " Chapter two."));
            },
            new FakeVad { Speech = [new(0, 700), new(705, 3600)] });

        Assert.False(result.GapRemains);
        AssertContainsChapter(new DetectedChapter(2, 700), result.Chapters);
        Assert.Contains(700.0, audio.DecodeStarts);
    }

    [Fact]
    public async Task JingleWithLeadingSilence_MarksInsideThatHush_AndVadDoesNotDoubleProbe()
    {
        // A silence (695-700) precedes the jingle's own music (700-703) - the existing
        // silence-based candidate already probes this transition, so the VAD non-speech region
        // covering the same silence+jingle span must not add a second, duplicate candidate
        // (dedup): the silence path stays primary. VAD itself draws no line between the silence
        // and the jingle music that follows it - both read as one continuous non-speech stretch
        // (695-703) - but silencedetect does, and ComputeMarkBeforeJingle's backward walk stops
        // at that stored silence's own end (700, the jingle's true start) rather than crossing it
        // into the previous chapter's narration beyond. An earlier version of the walk retreated
        // straight through both all the way to 695 instead; confirmed wrong on real audio (a
        // chapter transition with two genuinely separate jingles, merged by VAD into one region
        // with a real silence between them, landed at the first jingle's start rather than the
        // second's). The mark lead then backs the mark 0.25 s into that 5 s hush, so the final
        // position is 699.75 - the same "a moment of quiet first" rule default-mode marks follow.
        var (result, _, audio) = await DetectFullAsync(
            Options("--quick-marks", "--mark-before-jingle"),
            [new(695, 700)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(700, Seg(3.2, " Chapter two."));
            },
            new FakeVad { Speech = [new(0, 695), new(703, 3600)] });

        AssertContainsChapter(new DetectedChapter(2, 699.75), result.Chapters);
        // Exact 700.0 is the anchor probe itself; every other decode around the transition (the
        // quiet-point snap's, the walked mark's own) lands on a different value, so this isolates
        // "no duplicate probe decode" without being confused by them.
        Assert.Single(audio.DecodeStarts, d => d == 700.0);
    }

    [Fact]
    public async Task DefaultMode_PlacesMarkAtAFixedOffsetBeforeThePhrase_AndSizesTheWindowToTheSilence()
    {
        // No options at all. A silence with no jingle behind it expects its announcement right
        // after it, so its window opens a lead-in inside the silence (600 - 3) and runs the
        // expectation length from there (3 + 22) - not the jingle ceiling, which nothing here has
        // to cross. The mark still lands at a flat 0.25 s before the phrase, "no matter what
        // exists there".
        var (result, _, audio) = await DetectFullAsync(
            Options(),
            [new(595, 600)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(600, Seg(0.3, " Chapter two."));
            });

        AssertChapters([new(1, 0.25), new(2, 600.05)], result.Chapters);
        Assert.Contains(audio.DecodeWindows,
            w => w.Start == 597 && w.Duration is { } d && Math.Abs(d - 25) < 0.01);
    }

    [Fact]
    public async Task ABookWithNoJingles_KeepsThePlainSilenceWindow_AndTheFixedOffset()
    {
        // The same layout as DefaultMode_PlacesMarkAtAFixedOffsetBeforeThePhrase_..., on a book
        // where VAD finds no jingle at all. A silence candidate is sized by what it expects and
        // nothing else, and the mark placement formula never depended on the probe width - which is
        // what let --max-jingle-length go in 0.12.0 without either of them noticing.
        var (result, _, audio) = await DetectFullAsync(
            Options(),
            [new(595, 600)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(600, Seg(0.3, " Chapter two."));
            });

        AssertChapters([new(1, 0.25), new(2, 600.05)], result.Chapters);
        Assert.Contains(audio.DecodeWindows,
            w => w.Start == 597 && w.Duration is { } d && Math.Abs(d - 25) < 0.01);
    }

    [Fact]
    public async Task MarkBeforeJingle_AnchorsViaVad()
    {
        // --mark-before-jingle's own backward-walk mark placement (see
        // JingleWithLeadingSilence_MarksInsideThatHush_... for why this lands at 699.75, a mark
        // lead inside the hush ending at the jingle's true start).
        var (result, _, audio) = await DetectFullAsync(
            Options("--quick-marks", "--mark-before-jingle"),
            [new(695, 700)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(700, Seg(3.2, " Chapter two."));
            },
            new FakeVad { Speech = [new(0, 695), new(703, 3600)] });

        AssertContainsChapter(new DetectedChapter(2, 699.75), result.Chapters);
        // The 695-700 hush runs straight into the 700-703 jingle, so it is that jingle's lead-in
        // rather than a candidate of its own: the one window here belongs to the jingle, opening at
        // its first note and running the expectation length past the speech behind it.
        Assert.Contains(audio.DecodeWindows,
            w => w.Start == 700 && w.Duration is { } d && Math.Abs(d - 25) < 0.01);
    }

    [Fact]
    public async Task SilencelessJingle_TriggeredByAFalseInTextPause_MarksAtJingleNotThePause()
    {
        // A false in-text pause (silence 610-613, >= the 1.5 s floor) sits in the narration
        // before a silence-less jingle transition (jingle 640-645, phrase at 645). The pause's
        // candidate is probed first and its wide window reaches the phrase - but the mark must
        // still land at the jingle's own start (640, via the VAD region), NOT 0.5 s before the
        // pause (612.5). The pause does not lead the jingle's VAD region, so it must not be
        // mistaken for the anchor.
        var result = await DetectAsync(
            Options("--quick-marks", "--mark-before-jingle"),
            [new(610, 613)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(613, Seg(32, " Chapter two.")); // probe window [613, 663], phrase at 645
            },
            new FakeVad { Speech = [new(0, 610), new(613, 640), new(645, 3600)] });

        Assert.False(result.GapRemains);
        AssertChapters([new(1, 0.25), new(2, 640)], result.Chapters);
    }

    [Fact]
    public async Task SilencelessJingleAfterFalsePause_DoesNotCorruptTheAutoMechanisms()
    {
        // Same false-pause-before-silence-less-jingle shape, with --min-silence-length auto, laid
        // out so the threshold would visibly misbehave if the false pause were mistaken for chapter
        // two's anchor: the pause is 3 s, so the buggy path tightens the threshold to 2.25 s
        // (0.75x). Chapter three's genuine 2 s inter-chapter silence (1000-1002) would then be
        // skipped and lost. The correct path takes chapter two's anchor from the VAD region
        // (Silence = null), tightens nothing, and finds chapter three - so the result must be
        // [1, 2, 3], not [1, 2].
        var (result, _, _) = await DetectWithLogAsync(
            Options("--quick-marks", "--mark-before-jingle"),
            [new(610, 613), new(1000, 1002)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(613, Seg(32, " Chapter two."));  // window [613, 663], phrase at 645
                s.Add(1002, Seg(4, " Chapter three.")); // window [1002, ...], phrase at 1006
            },
            new FakeVad
            {
                Speech =
                [
                    new(0, 610), new(613, 640), new(645, 700),
                    new(720, 1000), new(1006, 3600),
                ],
            });

        Assert.False(result.GapRemains);
        Assert.Equal([1, 2, 3], result.Chapters.Select(c => c.Number));
        AssertContainsChapter(new DetectedChapter(2, 640), result.Chapters);       // jingle start, not 612.5
    }

    [Fact]
    public async Task SilencelessJingle_IsMatched_WhenVadResumesSlightlyAfterThePhrase()
    {
        // The jingle's VAD region ends at 645.3 - 0.3 s *after* Whisper's phrase timestamp (645),
        // as happens when the two detectors time the boundary slightly differently. The region
        // must still be recognised as the jingle (within the phrase-match tolerance) so the mark
        // lands at its start (640); without the tolerance it would be missed and the transition
        // would wrongly fall back to the false in-text pause at 610-613 (marking at 612.5).
        var result = await DetectAsync(
            Options("--quick-marks", "--mark-before-jingle"),
            [new(610, 613)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(613, Seg(32, " Chapter two.")); // phrase at 645, VAD resumes at 645.3
            },
            new FakeVad { Speech = [new(0, 610), new(613, 640), new(645.3, 3600)] });

        Assert.False(result.GapRemains);
        AssertChapters([new(1, 0.25), new(2, 640)], result.Chapters);
    }

    [Fact]
    public async Task JinglePhraseSpokenInsideTheJingle_MarksAtJingleStart_NotJustBeforeThePhrase()
    {
        // The real-world failure this fix addresses: the "Chapter N" announcement is spoken
        // *over* the jingle, so Whisper timestamps the phrase (645) *inside* the VAD non-speech
        // region (640-650), which therefore ends 5 s *after* the phrase - the jingle region
        // envelops the announcement. The probe is triggered by a false in-text pause (610-613)
        // whose wide window reaches the phrase, so the jingle must be resolved by region lookup
        // rather than from the triggering candidate. The mark must land at the jingle's own
        // start (640), found by containment; an end-alignment lookup would drop the region
        // (its end overshoots the phrase) and fall back to placing the mark 0.5 s before the
        // phrase (644.5) - seconds late, and on the wrong side of the jingle.
        var result = await DetectAsync(
            Options("--quick-marks", "--mark-before-jingle"),
            [new(610, 613)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(613, Seg(32, " Chapter two.")); // window [613, ...], phrase at 645
            },
            new FakeVad { Speech = [new(0, 640), new(650, 3600)] }); // jingle region 640-650 envelops 645

        Assert.False(result.GapRemains);
        AssertChapters([new(1, 0.25), new(2, 640)], result.Chapters);
    }

    [Fact]
    public async Task LateSilenceDeepInsideAJingleRegion_IsNotMistakenForTheLeadIn_MarkAtJingleStart()
    {
        // Real-world failure (Perry Rhodan "Die Dritte Macht", chapters 4/12/16, 2026-07-22): a
        // long, otherwise-unbroken jingle region (640-660) whose music never dips below the
        // silencedetect noise floor except for one ordinary breath-pause silence (654.5-655)
        // sitting deep inside it, right before the announcement - far from the region's own
        // start. LeadingSilence must not mistake that unrelated pause for the region's lead-in
        // hush: the mark belongs at the jingle's own start (640), not 0.5 s before the pause
        // (654.5) or the phrase. The 610-613 false in-text pause triggers the probe, exactly as
        // in the sibling tests above. VAD also picks up a short blip right at the announcement
        // itself (654.8-655.3) - confirmed on real audio (the same chapter 4): a real spoken
        // "Kapitel N" is reliably VAD-detectable even inside a jingle, so a scripted VAD that is
        // blind to it entirely would not be representative. That blip is what lets
        // --mark-before-jingle's own backward walk step out of the breath-pause silence via its
        // ordinary Step 1 containment check (originalMark - now correctly anchored close to the
        // announcement instead of overshooting past it - lands inside 654.5-655), rather than
        // needing to re-litigate the pause deep inside its own retreat loop.
        var result = await DetectAsync(
            Options("--quick-marks", "--mark-before-jingle"),
            [new(610, 613), new(654.5, 655)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(613, Seg(42, " Chapter two.")); // window [613, ...], phrase at 655
            },
            new FakeVad { Speech = [new(0, 610), new(613, 640), new(654.8, 655.3), new(660, 3600)] }); // jingle region 640-660, unbroken

        Assert.False(result.GapRemains);
        AssertChapters([new(1, 0.25), new(2, 640)], result.Chapters);
    }

    [Fact]
    public async Task AnnouncementLostInALongWindow_IsRecoveredByTheJingleReread()
    {
        // Real-world failure (Gruelfin.m4b, "Prolog" at 0:03:28, 2026-07-30): the announcement sits
        // alone inside a jingle and the candidate's window runs long enough to re-frame the audio -
        // at which point the lone word is dropped from the transcript entirely while the same audio
        // from the same position is transcribed correctly over 17.5 s or 23.5 s.
        // VAD does see it (blip 654.8-655.3, strictly inside the merged jingle region 640-660), and
        // speech inside a jingle that no transcript segment has words for is the tool's own evidence
        // that the recognizer, not the audiobook, lost the announcement. Geometry otherwise identical
        // to LateSilenceDeepInsideAJingleRegion_... above, so the mark must land in the same place.
        var (result, log, _) = await DetectWithLogAsync(
            Options("--quick-marks", "--mark-before-jingle"),
            [new(610, 613)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.AddWithin(25, 655, Seg(0, " Chapter two.")); // the jingle-spanning window misses it
            },
            // The 0.3 s musical transient at 645 is what makes this a jingle-spanning candidate: a
            // bridged blip says the announcement may be inside the music, so the window opens at the
            // jingle's own start (640) and runs 36.8 s - past the decode chunk that loses the word.
            new FakeVad
            {
                Speech = [new(0, 610), new(613, 640), new(645, 645.3), new(654.8, 655.3), new(660, 3600)],
            });

        Assert.False(result.GapRemains);
        AssertChapters([new(1, 0.25), new(2, 640)], result.Chapters);
        Assert.Contains(log, l => l.Contains("re-reading shorter"));
    }

    [Fact]
    public async Task JingleMusic_IsReadInOverlappingTiles_WhenTheAnnouncementIsDeepInsideIt()
    {
        // A 60 s jingle with the announcement spoken 35 s into it. The speech window opens 8 s
        // before the music ends and cannot reach that far back; the music is read afterwards in
        // 25 s tiles stepping 15 s, and the announcement lands whole inside the second of them.
        // What this must never do again is span the whole jingle in one window - 60 s of decode is
        // twice Whisper's chunk, and a lone word inside music is exactly what that loses.
        var (result, log, audio) = await DetectWithLogAsync(
            Options("--quick-marks"),
            [new(995, 1000)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(1035, Seg(0, " Chapter two."));
            },
            new FakeVad { Speech = [new(0, 1000), new(1035, 1035.3), new(1060, 3600)] });

        Assert.Equal([1, 2], result.Chapters.Select(c => c.Number));
        Assert.Contains(1000.0, audio.DecodeStarts);
        Assert.Contains(1015.0, audio.DecodeStarts);
        Assert.DoesNotContain(audio.DecodeStarts, d => d < 1000 && d > 900);
        Assert.Contains(log, l => l.StartsWith("chapter 2 detected") &&
                                  l.Contains(", embedded in a jingle)"));
    }

    [Fact]
    public async Task JingleMusic_IsNotRead_WhenTheSpeechBehindItAnswers()
    {
        // The order is the corpus's: eleven times out of twelve the announcement follows the music,
        // so the music is only ever read when the window behind it came back empty.
        var (result, log, _) = await DetectWithLogAsync(
            Options("--quick-marks"),
            [new(995, 1000)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(1060, Seg(0, " Chapter two."));
            },
            new FakeVad { Speech = [new(0, 1000), new(1035, 1035.3), new(1060, 3600)] });

        Assert.Equal([1, 2], result.Chapters.Select(c => c.Number));
        Assert.DoesNotContain(log, l => l.Contains("reading its music"));
    }

    [Fact]
    public async Task TheJingleReread_GoesThroughTheUpgradeModel_WhereTheRunHasABetterOne()
    {
        // The re-read fixes the framing; where a better recognizer is available it fixes both halves
        // of the failure at once, since an announcement quiet enough to be dropped from a long window
        // is exactly the kind a bigger model recovers. Proven by scripting the announcement onto the
        // upgrade transcriber only: the Probe one never hears "Chapter two" at any window length, so
        // a chapter 2 at all can only have come through the upgrade. AddWithin(30) keeps it out of
        // reach of Scan's own minutes-long gap chunks, and GapRemains being false means neither
        // Scan nor Re-probe ever ran.
        var log = new List<string>();
        var (result, _, upgrade) = await DetectWithUpgradeTranscriberAsync(
            Options("--quick-marks", "--mark-before-jingle", "--model", "base", "--upgrade-model", "large"),
            [new(610, 613)],
            probe => probe.Add(0, Seg(0.5, " Chapter one.")),
            p3 => p3.AddWithin(25, 655, Seg(0, " Chapter two.")),
            log,
            new FakeVad
            {
                Speech = [new(0, 610), new(613, 640), new(645, 645.3), new(654.8, 655.3), new(660, 3600)],
            });

        Assert.False(result.GapRemains);
        AssertChapters([new(1, 0.25), new(2, 640)], result.Chapters);
        Assert.Contains(log, l => l.Contains(
            "re-reading shorter, --upgrade-model"));
        Assert.Contains("en", upgrade.LanguageChanges);
    }

    [Fact]
    public async Task LongWindowWithNoUnheardJingleSpeech_IsNotReread()
    {
        // The gate on the re-read above: without a VAD speech blip inside the jingle there is nothing
        // to contradict the empty transcript, and an empty window is simply an empty window. Same
        // file shape, same 50 s window, VAD silent across the whole jingle - no second decode.
        var (_, log, _) = await DetectWithLogAsync(
            Options("--quick-marks", "--mark-before-jingle"),
            [new(610, 613)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.AddWithin(30, 655, Seg(0, " Chapter two."));
            },
            new FakeVad { Speech = [new(0, 610), new(613, 640), new(660, 3600)] });

        Assert.DoesNotContain(log, l => l.Contains("re-reading shorter"));
    }

    [Fact]
    public async Task AnnouncementLostInAThirtySecondJingleWindow_IsRecoveredByTheOnePassReread()
    {
        // The same book's prologue, lost a second time and to a different width (Gruelfin.m4b,
        // build 280, 2026-08-09). Nothing is inside the music here: the announcement is the first
        // speech behind it, exactly where a jingle candidate expects one, so VAD contradicts
        // nothing and the re-read above cannot fire. What loses it is the window's own planned
        // width - a JingleLeadInSeconds run-up plus ExpectedAnnouncementSeconds is 30.0 s to the
        // second, and 30.0 s is the width WhisperChunkSeconds exists to warn about. Measured on the
        // real audio at 0:03:20.19: read at 22.0, 23.5 and 25.0 s, gone at 27.0 and 30.0 s.
        var (result, log, _) = await DetectWithLogAsync(
            Options("--quick-marks", "--mark-before-jingle"),
            [new(610, 613)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.AddWithin(25, 660, Seg(0, " Chapter two.")); // the 30 s jingle window misses it
            },
            // No bridged blip, so this is the plain jingle shape: the window opens 8 s into the
            // music at 652 and expects the announcement at 660, where speech resumes.
            new FakeVad { Speech = [new(0, 610), new(613, 640), new(660, 3600)] });

        Assert.False(result.GapRemains);
        AssertChapters([new(1, 0.25), new(2, 640)], result.Chapters);
        Assert.Contains(log, l => l.Contains("wider than one recognizer pass"));
    }

    [Fact]
    public async Task ASilenceCandidatesWindow_IsNotRereadInOnePass()
    {
        // What bounds the cost of the re-read above. A pause's window is SilenceLeadInSeconds plus
        // ExpectedAnnouncementSeconds, one phrase margin inside a chunk already, so re-reading it
        // could only produce the same answer from the same framing - and a book has hundreds of
        // pauses that announce nothing, every one of which would otherwise buy a second decode.
        // The recovery passes are covered by the same gate, their candidates being unclassified.
        var (_, log, _) = await DetectWithLogAsync(
            Options("--quick-marks", "--mark-before-jingle"),
            [new(610, 613)],
            s => s.Add(0, Seg(0.5, " Chapter one.")),
            new FakeVad { Speech = [new(0, 3600)] }); // unbroken speech: no jingle, no jingle candidate

        Assert.DoesNotContain(log, l => l.Contains("wider than one recognizer pass"));
    }

    [Fact]
    public async Task TrailingNarrationSwallowedIntoTheRegionHead_IsTrimmedOff_MarkAtTrueJingleStart()
    {
        // Real-world failure (Perry Rhodan "Die Dritte Macht", chapters 18/21, 2026-07-22): the
        // previous chapter's short final sentence gets chopped by VAD into sub-second fragments
        // (blips 640.8-641.6 and 642.4-643.2) whose gaps the ComputeNonSpeechRegions merge
        // bridges, dragging the jingle region's start back to 640 - into what is still narration.
        // The transcript proves those blips are spoken text (a segment covering them ends before
        // the phrase), so the jingle's true leading edge is the last narration blip's end (643.2)
        // and the mark must land there, not at the naive region start 640 inside the sentence.
        var result = await DetectAsync(
            Options("--mark-before-jingle", "--min-silence-length", "1.5"),
            [],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(640, new TranscriptSegment(0.6, 3.4, " Then all was quiet.", 1.0),
                    Seg(15, " Chapter two.")); // region candidate window [640, ...], phrase at 655
            },
            new FakeVad { Speech = [new(0, 640), new(640.8, 641.6), new(642.4, 643.2), new(660, 3600)] });

        Assert.False(result.GapRemains);
        AssertChapters([new(1, 0.25), new(2, 643.2)], result.Chapters);
    }

    [Fact]
    public async Task LeadingSilenceFollowedBySwallowedNarration_IsDroppedWithTheNarration_MarkAtTrueJingleStart()
    {
        // The exact chapter-10 shape from the same book: a genuine silencedetect silence
        // (639.9-641.1) sits right at the region's start - but *narration* resumes after it
        // (blips 641.2-642.0 and 642.8-643.6, a short final sentence) before the jingle begins.
        // Anchoring to that silence would land the mark "a bit too early within speech". Once the
        // narration blips are trimmed off the region head, the silence no longer reaches the
        // adjusted start (643.6) and must be discarded with them; the mark belongs at 643.6.
        // (The 1.2 s silence is below the 1.5 s candidate threshold, so the region's own
        // candidate at 640 - not a silence candidate - probes this transition.)
        var result = await DetectAsync(
            Options("--mark-before-jingle", "--min-silence-length", "1.5"),
            [new(639.9, 641.1)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(640, new TranscriptSegment(1.2, 3.7, " It all ended.", 1.0),
                    Seg(15, " Chapter two.")); // phrase at 655
            },
            new FakeVad { Speech = [new(0, 640), new(641.2, 642.0), new(642.8, 643.6), new(660, 3600)] });

        Assert.False(result.GapRemains);
        AssertChapters([new(1, 0.25), new(2, 643.6)], result.Chapters);
    }

    [Fact]
    public async Task JingleSplitByAnUntranscribedVocal_RetreatsPastTheBlip_ToTheTrueJingleStart()
    {
        // The chapter-20 shape: a vocal-like passage in the jingle's own music (blip 645-646.2,
        // just over the 1 s merge limit) splits one continuous jingle into two VAD regions
        // ([640, 645] and [646.2, 660]). ResolveDefaultPhraseOnset still finds the blip as a
        // "swallowed" onset inside the region ResolveJingleAnchor's transcript-aware bridging
        // reassembles, landing the default-mode original mark right after it (644.75).
        // --mark-before-jingle's own backward walk (ComputeMarkBeforeJingle) now carries the same
        // transcript awareness ResolveJingleAnchor's own bridging already relies on (see
        // IsGenuineSpeech): the blip clears TransientSpeechFloorSeconds (0.4 s) easily on
        // duration alone (confirmed on real audio - a blip this long is well outside the
        // transient range the floor was calibrated against, real transients topping out around
        // 0.35 s), but nothing in the transcript corroborates it as spoken words, so it is
        // recognised as more of the jingle's own music and walked straight through - past it, to
        // the true jingle start (640) - rather than accepted as real preceding speech.
        var result = await DetectAsync(
            Options("--quick-marks", "--mark-before-jingle", "--min-silence-length", "1.5"),
            [],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(640, Seg(15, " Chapter two.")); // phrase at 655, inside the later fragment
            },
            new FakeVad { Speech = [new(0, 640), new(645, 646.2), new(660, 3600)] });

        Assert.False(result.GapRemains);
        AssertChapters([new(1, 0.25), new(2, 640)], result.Chapters);
    }

    [Fact]
    public async Task TranscribedSpeechBetweenTwoRegions_BlocksTheBridge_MarkAtTheLaterRegion()
    {
        // The bridge's negative case: an in-text pause region ([643, 645]) ends shortly before
        // the real jingle region ([646.5, 660]), but the speech between them (blip 645-646.5,
        // "He nodded.") IS transcribed narration - the previous chapter simply ends on a short
        // sentence framed by pauses. Bridging across it would cut that sentence into the next
        // chapter, so the transcript must block the bridge and the mark stays at the real
        // jingle's own start (646.5).
        var result = await DetectAsync(
            Options("--mark-before-jingle", "--min-silence-length", "1.5"),
            [],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(643, new TranscriptSegment(2.2, 3.6, " He nodded.", 1.0),
                    Seg(12, " Chapter two.")); // phrase at 655
            },
            new FakeVad { Speech = [new(0, 643), new(645, 646.5), new(660, 3600)] });

        Assert.False(result.GapRemains);
        AssertChapters([new(1, 0.25), new(2, 646.5)], result.Chapters);
    }

    [Fact]
    public async Task PhraseSmearedAcrossTheJingle_IsRescuedByItsSegmentSpan_MarkAtJingleStart()
    {
        // The chapter-14 shape: with a long near-silent jingle between the last narration and
        // the announcement, Whisper smears the phrase's segment across the whole jingle - its
        // start timestamp (618) pulled back before the VAD region (620-640) even begins, so
        // plain containment finds no region and the mark would fall back to the false in-text
        // pause that triggered the probe (610-613, marking at 612.5). The segment's span betrays
        // the smear: it overlaps the region by 18 s, so the region is rescued as the jingle and
        // the mark lands at its start (620).
        var result = await DetectAsync(
            Options("--quick-marks", "--mark-before-jingle", "--min-silence-length", "1.5"),
            [new(610, 613)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(610, new TranscriptSegment(8, 28, " Chapter two.", 1.0)); // abs 618-638, smeared
            },
            new FakeVad { Speech = [new(0, 620), new(640, 3600)] });

        Assert.False(result.GapRemains);
        AssertChapters([new(1, 0.25), new(2, 620)], result.Chapters);
    }

    [Fact]
    public async Task DefaultMode_PhraseSmearedAcrossTheJingle_FloorsAtTheRegionEnd_NotBeforeIt()
    {
        // The default-mode (no --mark-before-jingle) counterpart to the smeared-phrase test above:
        // --mark-before-jingle's ComputeJingleMark never trusts phraseAbs once a jingle anchor is
        // resolved, but the default path used to trust it blindly, landing the mark 0.25 s before
        // the smeared timestamp (617.75) - inside the *previous* chapter's narration, well before
        // the jingle (620-640) even starts. With no VAD speech blip inside the region to pinpoint
        // the true onset (see ResolveDefaultPhraseOnset's other test below for that case), it falls
        // back to flooring phraseAbs at the resolved region's own end (640), so the mark instead
        // lands at 639.75 - late into the jingle rather than early into the wrong chapter.
        var result = await DetectAsync(
            Options("--quick-marks", "--min-silence-length", "1.5"),
            [new(610, 613)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(610, new TranscriptSegment(8, 28, " Chapter two.", 1.0)); // abs 618-638, smeared
            },
            new FakeVad { Speech = [new(0, 620), new(640, 3600)] });

        Assert.False(result.GapRemains);
        AssertChapters([new(1, 0.25), new(2, 639.75)], result.Chapters);
    }

    [Fact]
    public async Task DefaultMode_PhraseAlreadyInsideTheJingleRegion_RefineAdvancesToRealSpeechResumption()
    {
        // ResolveDefaultPhraseOnset alone: when phraseAbs (645) already sits at or after the
        // resolved region's own start (640) - the containment case, not the smeared-away case -
        // and no VAD speech blip inside the region says otherwise, it leaves phraseAbs - 0.25s
        // (644.75) alone, "at least in the right neighbourhood". But RefineDefaultMark runs next
        // and scans forward regardless: since VAD shows zero speech anywhere from 644.75 through
        // the rest of the region, it advances all the way to where real speech actually resumes
        // (650) and re-derives the lead from there (649.75). An earlier version capped this scan at
        // the region's own end specifically to keep this test at 644.75, but that cap also silently
        // defeated the fix for phrases whose region resolution fails entirely - the cases still
        // broken live - so the scan was made unbounded per the user's explicit request instead; see
        // RefineDefaultMark's doc comment. A region with a genuine phrase match and no VAD speech
        // anywhere in it is not expected on real audio (jingle announcements are reliably
        // VAD-detectable), so this is a synthetic edge case, not a live-observed regression.
        var result = await DetectAsync(
            Options("--quick-marks"),
            [new(610, 613)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(613, Seg(32, " Chapter two.")); // window [613, ...], phrase at 645
            },
            new FakeVad { Speech = [new(0, 640), new(650, 3600)] }); // jingle region 640-650 envelops 645

        Assert.False(result.GapRemains);
        AssertChapters([new(1, 0.25), new(2, 649.75)], result.Chapters);
    }

    [Fact]
    public async Task DefaultMode_AnnouncementBlipSwallowedIntoTheJingle_MarksAtTheBlipStart()
    {
        // The real-world mid-phrase failure this addresses (Perry Rhodan "Die Dritte Macht",
        // chapter 35, 2026-07-22): VAD split the announcement into a short 0.6 s blip ("Kapitel")
        // and a longer 1.2 s blip ("35"), separated by a brief gap. ComputeNonSpeechRegions'
        // MergeShortSpeechGapSeconds merge - meant to bridge an announcement's own quiet syllables
        // inside a jingle - cannot tell that short first blip apart from a musical vocal transient
        // and merges it into the surrounding non-speech run, so the resolved region's end (637)
        // lands exactly *between* the two blips, mid-word. Whisper's own timestamp for the smeared
        // "Chapter two." segment (618-638) is no help - it is the reason the region had to be
        // rescued via the segment-span overlap in the first place. But the swallowed blip itself
        // (636-636.6) is real VAD data pinpointing the announcement's true onset: the mark must
        // land 0.25s before its start (635.75), not at the region's end (636.75) as a plain floor
        // would give.
        var result = await DetectAsync(
            Options("--quick-marks", "--min-silence-length", "1.5"),
            [new(610, 613)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(610, new TranscriptSegment(8, 28, " Chapter two.", 1.0)); // abs 618-638, smeared
            },
            new FakeVad { Speech = [new(0, 620), new(636, 636.6), new(637, 638.2), new(640, 3600)] });

        Assert.False(result.GapRemains);
        AssertChapters([new(1, 0.25), new(2, 635.75)], result.Chapters);
    }

    [Fact]
    public async Task DefaultMode_AnnouncementSplitAcrossTwoAdjacentBlips_MarksAtTheFirstBlipStart_NotTheLast()
    {
        // Real-world confirmed bug (Perry Rhodan "Die Dritte Macht", chapter 31, 2026-07-23):
        // unlike chapter 35 above (only "Kapitel" was swallowed - "35" itself was long enough to
        // end the region), here BOTH short words of "Kapitel 31" got swallowed into the same
        // merged region, 0.2s apart. The prior fix took only the *last* swallowed blip (636-636.6,
        // "31"'s stand-in here), landing the mark right after "Kapitel" (635.2-635.8) had already
        // been spoken - confirmed live by re-transcribing 5.25s starting at that mark and getting
        // unrelated narration instead of the phrase. Clustering the swallowed blips by the same
        // short-gap threshold that grouped them into one region, then anchoring to the *first*
        // blip of the *last* cluster, lands on "Kapitel"'s own onset (635.2) instead of "31"'s
        // (636).
        var result = await DetectAsync(
            Options("--quick-marks", "--min-silence-length", "1.5"),
            [new(610, 613)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(610, new TranscriptSegment(8, 28, " Chapter two.", 1.0)); // abs 618-638, smeared
            },
            new FakeVad
            {
                Speech = [new(0, 620), new(635.2, 635.8), new(636, 636.6), new(637, 638.2), new(640, 3600)],
            });

        Assert.False(result.GapRemains);
        AssertChapters([new(1, 0.25), new(2, 634.95)], result.Chapters);
    }

    [Fact]
    public async Task DefaultMode_IsolatedEarlierBlip_SeparatedByALongGap_IsNotTreatedAsPartOfTheAnnouncement()
    {
        // Guard for the clustering fix's other half: an early speech blip inside the jingle region,
        // separated from the true announcement blip by a gap well over MergeShortSpeechGapSeconds
        // (here 13.5s - an incidental musical vocal transient near the jingle's start, not part of
        // "Chapter two"), must form its own separate cluster and be ignored, not pull the mark back
        // to it. The mark must still land at the true (later) cluster's own start (636), matching
        // the single-blip case above.
        var result = await DetectAsync(
            Options("--quick-marks", "--min-silence-length", "1.5"),
            [new(610, 613)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(610, new TranscriptSegment(8, 28, " Chapter two.", 1.0)); // abs 618-638, smeared
            },
            new FakeVad
            {
                Speech = [new(0, 620), new(622, 622.5), new(636, 636.6), new(637, 638.2), new(640, 3600)],
            });

        Assert.False(result.GapRemains);
        AssertChapters([new(1, 0.25), new(2, 635.75)], result.Chapters);
    }

    [Fact]
    public async Task DefaultMode_BlipEndingTheHushBeforeTheJingle_IsNotTakenForTheAnnouncement()
    {
        // The clustering rule's other failure mode, and the one that needs the *leading* silence to
        // resolve (real audio, chapters 6 and 14 of one German book, 2026-08-02): the previous
        // chapter's closing words are split by the narrator's own pause into two VAD blips, and the
        // short-gap merge swallows the second one (640.9-641.7) into the jingle's region exactly as
        // it swallows a quiet announcement. Being the only swallowed blip it was the last cluster
        // too, so the mark landed on the last sentence of the chapter before - eighteen seconds
        // early, in the middle of a spoken word.
        //
        // AdjustJingleRegion exists to trim precisely that blip off the region's head, and cannot
        // here: it wants a transcript segment covering the blip with words, and this probe window
        // opens on the jingle (at 640) with the announcement its only content. The transcript's
        // silence about audio it never saw is not evidence that nothing was spoken there.
        //
        // What settles it without the transcript is which detector saw the gap in front of the blip.
        // Silencedetect does not read jingle music as silence, so a blip starting where the region's
        // leading silence ends (640.9) is speech resuming after a pause - in front of the music, not
        // inside it - and cannot be the announcement. Excluded, it leaves nothing pointing back into
        // the music, and the mark stays where the phrase actually is (659.75) instead of being
        // dragged nineteen seconds back to the blip (640.65). A blip genuinely inside the music is
        // unaffected: the tests above have no leading silence at all, so their region start stands
        // in for its end and their blips clear it easily.
        var result = await DetectAsync(
            Options("--quick-marks", "--min-silence-length", "1.5"),
            // Both below the 1.5 s candidate floor, so neither becomes a probe of its own: the
            // narrator's pause mid-sentence (640-640.9) and the hush before the music (641.7-642.7).
            // The jingle (642.7-660) is what carries the probe, opening 8 s before the speech
            // behind it.
            [new(640, 640.9), new(641.7, 642.7)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(652, new TranscriptSegment(0, 20, " Chapter two.", 1.0)); // abs 652-672
            },
            new FakeVad { Speech = [new(0, 640), new(640.9, 641.7), new(660, 3600)] });

        Assert.False(result.GapRemains);
        AssertChapters([new(1, 0.25), new(2, 659.75)], result.Chapters);
    }

    [Fact]
    public async Task PreciseMark_LeavesAnAlreadyCorrectMarkUnchanged()
    {
        // precise marking's cheap path: re-transcribing right at the mark already computed (here,
        // chapter one's plain 0.25 s lead) finds the phrase as the very first thing heard, so
        // the mark is confirmed and left exactly as is - no candidate search needed.
        var result = await DetectAsync(
            Options("--min-silence-length", "1.5"),
            [new(610, 613)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(613, Seg(2, " Chapter two.")); // window [613, ...], phrase at 615
                // precise marking checks, keyed by their own decode start (checked position - 0.1s
                // lead-in): chapter one's mark (0.25) decodes from max(0, 0.25-0.1) = 0.15, landing
                // (within the script's 0.25s match tolerance) on the very same script entry already
                // used for the real probe window at 0 - the phrase really is the first thing there,
                // so this is not a coincidence of the test.
                s.Add(614.65, Seg(0, " Chapter two.")); // check @ 614.75 (chapter two's own mark)
            });

        Assert.False(result.GapRemains);
        AssertChapters([new(1, 0.25), new(2, 614.75)], result.Chapters);
    }

    [Fact]
    public async Task PreciseMark_CorrectsAMarkStuckOnASpuriousVadBlip()
    {
        // Reproduces "ch19 disease" (Perry Rhodan "Die Dritte Macht", chapter 19, 2026-07-23):
        // a jingle's own musical/vocal transient can be long enough to clear
        // TransientSpeechFloorSeconds, fooling RefineDefaultMark's VAD-duration heuristic into
        // stopping on it (656) instead of the real announcement (657) - both blips look identical
        // to that heuristic, which only ever looks at duration. precise marking catches what the
        // heuristic cannot: it narrows in on the announcement from the heuristic's own mark
        // (655.75) and finds the phrase surviving up to 656.85 but no later, so the transient is
        // ruled out and the real onset at 656.9 is confirmed instead.
        //
        // The silence at 658.5 is what makes the shape expressible at all: the probe window's end
        // snaps into the jingle's own non-speech gap (648.5) without it, which would leave the
        // announcement outside the very window the phrase was detected in - something real
        // Whisper cannot produce, since a segment cannot extend past its own input.
        var result = await DetectAsync(
            Options("--min-silence-length", "1.5"),
            [new(610, 613), new(658.5, 662)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(613, new TranscriptSegment(25, 45, " Chapter two.", 1.0)); // abs 638-658, smeared
                s.Add(655.65, new TranscriptSegment(0, 1, " Music", 1.0));         // check @ 655.75 (RefineDefaultMark's mark) - the transient, not the phrase
                s.Add(655.9, new TranscriptSegment(0, 0.6, " Music", 1.0));        // the transient itself - still not the phrase
                s.Add(656.9, Seg(0, " Chapter two."));                            // the real announcement onset
                s.Add(659.9, new TranscriptSegment(0, 3, " Once upon a time.", 1.0)); // narration resumes, bounding the announcement
            },
            new FakeVad { Speech = [new(0, 640), new(656, 656.6), new(657, 658.2), new(660, 3600)] });

        Assert.False(result.GapRemains);
        AssertChapters([new(1, 0.25), new(2, 656.65)], result.Chapters);
    }

    [Fact]
    public async Task PreciseMark_FallsBackToTheOriginalMark_WhenNoCandidateEverConfirms()
    {
        // If the phrase can never be confirmed anywhere - every check hears something else first -
        // precise marking must not guess: it leaves the mark exactly as RefineDefaultMark computed
        // it, rather than looping forever or picking an arbitrary candidate. The search does narrow
        // in on the announcement at 619, but every foothold it then offers the ordinary check is
        // beaten to the microphone by the transient scripted just in front of it - so nothing is
        // ever confirmed and the mark stands where the heuristics left it.
        var result = await DetectAsync(
            Options("--min-silence-length", "1.5"),
            [new(610, 613)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(618.95, new TranscriptSegment(0, 0.04, " Music", 1.0)); // always the first thing heard
                s.Add(613, new TranscriptSegment(6, 45, " Chapter two.", 1.0));
                s.Add(655.65, new TranscriptSegment(0, 1, " Music", 1.0));
                s.Add(655.9, new TranscriptSegment(0, 0.6, " Music", 1.0));
                s.Add(656.9, new TranscriptSegment(0, 1.2, " Music", 1.0));
                s.Add(659.9, new TranscriptSegment(0, 3, " Once upon a time.", 1.0));
            },
            new FakeVad { Speech = [new(0, 640), new(656, 656.6), new(657, 658.2), new(660, 3600)] });

        Assert.False(result.GapRemains);
        AssertChapters([new(1, 0.25), new(2, 655.75)], result.Chapters);
    }

    [Fact]
    public async Task PreciseMark_FindsAnAnnouncementNoVadSegmentPointsAt()
    {
        // Why the search takes no help at all from VAD: the announcement at 625 sits where no VAD
        // speech segment starts, and the two segments VAD does offer (636, 637) carry unrelated
        // music. Bracketing the matched segment and narrowing in on the phrase finds it anyway,
        // starting from a mark the smeared abs-618 transcript put 7 s early.
        var result = await DetectAsync(
            Options("--min-silence-length", "1.5"),
            [new(610, 613)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(610, new TranscriptSegment(8, 28, " Chapter two.", 1.0)); // abs 618-638, smeared
                s.Add(625, Seg(0, " Chapter two."));                        // the real announcement onset
                s.Add(636, new TranscriptSegment(0, 0.5, " Music", 1.0));   // VAD candidate, not the phrase
                s.Add(637, new TranscriptSegment(0, 1.2, " Music", 1.0));   // VAD candidate, not the phrase
            },
            new FakeVad { Speech = [new(0, 620), new(636, 636.6), new(637, 638.2), new(640, 3600)] });

        Assert.False(result.GapRemains);
        AssertChapters([new(1, 0.25), new(2, 624.75)], result.Chapters);
    }

    [Fact]
    public async Task PreciseMark_WalksBackWhenTheMarkLandedPastTheAnnouncement()
    {
        // The search's other direction, and the failure shape that needs it (Perry Rhodan "Die
        // Dritte Macht", chapters 8 and 20, 2026-07-24): when the announcement's own swallowed blip
        // and an unrelated later one both fall inside a single over-merged non-speech region,
        // ResolveDefaultPhraseOnset's "first blip of the *last* cluster" rule lands the mark on the
        // wrong, later blip - seconds past the true announcement.
        //
        // Nothing is scripted at or after the mark, so the very first question - "does the phrase
        // survive being cut off here?" - answers no, and the search gallops backward instead of
        // forward: 0.1, 0.3, 0.7, 1.5, 3.1 and 6.3 s behind the mark, the last of which reaches
        // past the announcement at 655.55 and hears it again. Bisecting that bracket pins the
        // onset, and the mark follows 0.25 s in front of it.
        //
        // Driven through the refiner directly because the shape cannot be built through
        // DetectAsync: a mark that overshoots needs a detection whose timestamp is later than the
        // announcement, and ScriptedTranscriber models that mistiming as a second, separately
        // scripted copy of the phrase - which a large end-anchored window then finds, exactly as
        // real Whisper would not, there being only one utterance in the audio.
        var transcriber = new ScriptedTranscriber(new FakeAudioSource());
        transcriber.Add(655.55, Seg(0, " Chapter two."));
        var (refiner, profile) = MakeVerifier(transcriber);

        var result = await refiner.RefinePreciseMarkAsync(
            659.75, _file, null, profile.ChapterPattern, profile.Language, 650, 662, 700, [], null, CancellationToken.None);

        Assert.True(result.PhraseHeard);
        Assert.Equal(655.3, result.Mark, 3);
    }

    [Fact]
    public async Task PreciseMark_ReachesAnAnnouncementTheSegmentTimestampSitsSecondsBehind()
    {
        // Real-world failure (BARDIOC.m4b chapter 21, 2026-07-30): Whisper timestamped the segment
        // holding "Kapitel 21" 5.2 s *later* than the words were spoken (announced at 12:26:33.4,
        // segment start 12:26:38.6), so a bracket drawn one phrase margin below the segment start
        // began after the announcement and no probe inside it could ever hear it. The refinement
        // gave up and the mark stayed where the default heuristics had put it - 1.06 s *into* the
        // spoken announcement, which is precisely what the listener complains about.
        //
        // Modelled at 1/1 scale here: the announcement is at 640, the segment claims 645.2, and the
        // incoming mark sits 1.06 s past the onset. Reaching it needs the backward gallop to be
        // allowed below the segment bracket, out to PreciseMarkMaxBracketSeconds behind the end
        // anchor. Driven through the refiner directly for the reason MakeVerifier documents.
        var transcriber = new ScriptedTranscriber(new FakeAudioSource());
        transcriber.Add(640, Seg(0, " Chapter two."));
        var (refiner, profile) = MakeVerifier(transcriber);

        var result = await refiner.RefinePreciseMarkAsync(
            641.06, _file, null, profile.ChapterPattern, profile.Language, 645.2, 647.2, 651.7, [], null, CancellationToken.None);

        Assert.True(result.PhraseHeard);
        AssertMarkTime("chapter two", 639.75, result.Mark);
    }

    [Fact]
    public async Task PreciseMark_SearchesPastTheWindowEnd_WhenTheMatchedSegmentIsSmearedBeyondIt()
    {
        // Real-world failure (Raumschiff Erde chapter 25, 2026-08-02): a probe window is allowed to
        // keep a segment that merely *starts* inside it, and Whisper had stretched this one
        // seventeen seconds past the window's own end (window end 10:51:16.16, segment
        // 10:51:15.66-10:51:33.16). The window end therefore capped the search bracket in front of
        // the announcement instead of behind it, every probe read the jingle or the previous
        // chapter's narration, and the refinement reported "could not confirm the phrase" - the
        // same line it prints for an honest in-text mention, so nothing said the bracket had been
        // the problem.
        //
        // Modelled at 1/1 scale with that exact geometry: the window ends 0.5 s into a 17.5 s
        // segment, and the announcement sits at 710, ten seconds past the window end. Reaching it
        // needs the ceiling to stop obeying a window end that no longer bounds anything.
        var transcriber = new ScriptedTranscriber(new FakeAudioSource());
        transcriber.Add(710, Seg(0, " Chapter two."));
        var (refiner, profile) = MakeVerifier(transcriber);

        var result = await refiner.RefinePreciseMarkAsync(
            699, _file, null, profile.ChapterPattern, profile.Language, 699.5, 717, 700,
            [], null, CancellationToken.None);

        Assert.True(result.PhraseHeard);
        AssertMarkTime("chapter two", 709.75, result.Mark);
    }

    [Fact]
    public async Task PreciseMark_AimsFromTheFarEndOfTheBracket_WhenTheAnnouncementLiesPastIt()
    {
        // Real-world failure (Raumschiff Erde chapter 1, 2026-08-02): the announcement was found in
        // a 26.7 s overlap tail decoded from 0:04:55.38, and Whisper timestamped "Kapitel 1." as
        // that decode's very first segment, 0.0-7.0 - twelve seconds before the words were actually
        // spoken. The bracket drawn round that segment therefore ended at 0:05:07.38, right at the
        // announcement rather than behind it; all three survival probes still heard the phrase, the
        // gallop ran out of range with no edge to aim by, and the refinement gave up.
        //
        // Modelled at 1/1 scale with that exact geometry, the announcement scripted where the audio
        // really has it (307.5 s; established with tools\wprobe, whose plateau's last confirming
        // probe is 307.60 and first failing one 307.70). The ceiling comes out at 307.38, a tenth
        // of a second in front of the onset, so recovering the mark needs the far end of an
        // exhausted bracket to be offered to the foothold hunt instead of discarded.
        var transcriber = new ScriptedTranscriber(new FakeAudioSource());
        transcriber.Add(307.5, Seg(0, " Chapter two."));
        var (refiner, profile) = MakeVerifier(transcriber);

        var result = await refiner.RefinePreciseMarkAsync(
            307.13, _file, null, profile.ChapterPattern, profile.Language, 295.38, 302.38, 308.98,
            [], null, CancellationToken.None);

        Assert.True(result.PhraseHeard);
        AssertMarkTime("chapter two", 307.25, result.Mark);
    }

    [Fact]
    public async Task PreciseMark_GivesUp_WhenTheAnnouncementLiesFurtherPastTheBracketThanACheckWindow()
    {
        // The boundary of the recovery above, and the reason it needs no wider bracket: the far end
        // is only worth aiming from while the announcement starts inside the foothold probe's own
        // PreciseMarkCheckWindowSeconds. Same geometry, but with the announcement at 312 - still
        // close enough for the survival probes' PreciseMarkMinSurvivalSeconds floor to reach it, so
        // the gallop runs off the ceiling exactly as above, yet 4.6 s past that ceiling and so out
        // of reach of every foothold backoff. The mark is left alone rather than moved somewhere
        // unsupported. Over the ten-book run of 2026-08-02 the onset never landed more than 2.2 s
        // past the ceiling, so this is the case that stays theoretical.
        var transcriber = new ScriptedTranscriber(new FakeAudioSource());
        transcriber.Add(312, Seg(0, " Chapter two."));
        var (refiner, profile) = MakeVerifier(transcriber);

        var result = await refiner.RefinePreciseMarkAsync(
            307.13, _file, null, profile.ChapterPattern, profile.Language, 295.38, 302.38, 308.98,
            [], null, CancellationToken.None);

        Assert.False(result.PhraseHeard);
        Assert.Equal(307.13, result.Mark, 3);
    }

    [Fact]
    public async Task PreciseMark_KeepsProbingLongEnoughToBeHeard_WhenTheAnchorSitsRightAfterTheOnset()
    {
        // Real-world failure (Stalker.m4b's "Zeittafel", true onset 52.7 s, 2026-07-29/30): the end
        // anchor is the detecting window's own end, which here sits barely 1.5 s past the
        // announcement, so the probes nearest the onset asked Whisper about 2-3 s of audio. That is
        // below what the recognizer answers reliably - measured p=0.84 over 6.15 s, 0.54 over 4.55 s
        // and 0.49 over 2.95 s - and one wrong answer sends the bisection into the wrong half. The
        // mark was then left at the default-mode position, 30 s before the announcement.
        //
        // PhraseSurvivesFromAsync's floor is what fixes it: no probe decodes less than
        // PreciseMarkMinSurvivalSeconds, whatever the anchor says. A scripted recognizer cannot model
        // a coin flip, so what is asserted is the invariant rather than the symptom - every decode is
        // a phrase check, a quiet snap, or a survival probe of at least the floor's length. Without
        // the floor the probes nearest the onset here run 1.3-1.6 s.
        var transcriber = new ScriptedTranscriber(new FakeAudioSource());
        transcriber.Add(52.7, new TranscriptSegment(0, 1.5, " Chapter two.", 1.0));
        var (refiner, profile) = MakeVerifier(transcriber);

        // Anchor 54.2: one phrase margin past the segment end would be 59.2, but the transcript
        // ends within a hundredth of the segment - the tight anchor that produced the too-short
        // probes.
        var result = await refiner.RefinePreciseMarkAsync(
            52.9, _file, null, profile.ChapterPattern, profile.Language, 52.7, 54.2, 54.19, [], null, CancellationToken.None);

        Assert.True(result.PhraseHeard);
        AssertMarkTime("Zeittafel", 52.45, result.Mark);

        const double checkWindow =
            DetectionTuning.PreciseMarkCheckWindowSeconds + DetectionTuning.PreciseMarkLeadInSeconds;
        Assert.All(transcriber.Audio.DecodeWindows, w => Assert.True(
            w.Duration is not { } d ||
            d < 1.0 ||                              // the quiet snap, its radius rounded up to whole samples
            Math.Abs(d - checkWindow) < 1e-9 ||     // a phrase check
            d >= DetectionTuning.PreciseMarkMinSurvivalSeconds - 1e-9,
            $"decode at {w.Start} ran {w.Duration} s - too short to be a reliable survival probe"));
    }

    [Fact]
    public async Task PreciseMark_UnconfirmedByTheProbingModel_RetriesTheWholeSearchOnTheUpgradeModel()
    {
        // Real-world failure (chapters 6 and 14 of one German audiobook, 2026-08-02, -m small
        // -M turbo): a quietly-spoken announcement inside a jingle drops out of the smaller model's
        // reading of a long window as plain music, which reads exactly like "the announcement is not
        // in front of me" and sends the survival edge back to well before the onset - after which no
        // foothold can be confirmed and the mark is left where the heuristic put it. Replaying the
        // one probe that broke each search through both models settled it: "* Musik *" on the
        // probing model, the announcement in full on the upgrade one.
        //
        // Modelled here by scripting the announcement into the upgrade transcriber alone, so the
        // first attempt has nothing anywhere to find and the second has it at 660. The whole
        // procedure is re-run rather than resumed, which is why the second attempt needs no help
        // from the first: it re-derives the survival edge and the onset from scratch.
        var audio = new FakeAudioSource();
        var probing = new ScriptedTranscriber(audio);
        var upgrade = new ScriptedTranscriber(audio);
        upgrade.Add(660, Seg(0, " Chapter two."));
        var profile = Options().ResolveProfile("en");
        var refiner = new PreciseMarkRefiner(
            audio, Options(), default, (samples, ct) => probing.TranscribeAsync(samples, ct),
            (samples, language, ct) =>
            {
                Assert.Equal("en", language);
                return upgrade.TranscribeAsync(samples, ct);
            });

        var result = await refiner.RefinePreciseMarkAsync(
            655, _file, null, profile.ChapterPattern, profile.Language, 658, 662, 700, [], null, CancellationToken.None);

        Assert.True(result.PhraseHeard);
        AssertMarkTime("chapter 2", 659.75, result.Mark);
        // The transcripts the upgrade attempt produced are kept for the number vote exactly as the
        // probing model's would have been - they are the better reading of the two.
        Assert.NotEmpty(result.PhraseReadings);
    }

    [Fact]
    public async Task PreciseMark_UnconfirmedWithNoUpgradeModel_LeavesTheMarkAndDoesNotRetry()
    {
        // The other half of the gate: without a --upgrade-model worth asking (ChapterDetector passes
        // null then), an unconfirmed mark is still left exactly where the heuristic put it, and no
        // second search is paid for. Asserted on the decode count rather than only the mark, since
        // both outcomes agree on the mark.
        var audio = new FakeAudioSource();
        var probing = new ScriptedTranscriber(audio);
        var refiner = new PreciseMarkRefiner(
            audio, Options(), default, (samples, ct) => probing.TranscribeAsync(samples, ct));
        var profile = Options().ResolveProfile("en");

        var result = await refiner.RefinePreciseMarkAsync(
            655, _file, null, profile.ChapterPattern, profile.Language, 658, 662, 700, [], null, CancellationToken.None);
        var firstAttemptDecodes = audio.DecodeWindows.Count;

        Assert.False(result.PhraseHeard);
        AssertMarkTime("chapter 2", 655, result.Mark);

        // The same search with an upgrade model that also hears nothing costs strictly more, which
        // is what makes the count above evidence that no retry ran rather than a coincidence.
        var alsoDeaf = new ScriptedTranscriber(audio);
        var retrying = new PreciseMarkRefiner(
            audio, Options(), default, (samples, ct) => probing.TranscribeAsync(samples, ct),
            (samples, _, ct) => alsoDeaf.TranscribeAsync(samples, ct));
        audio.DecodeWindows.Clear();
        await retrying.RefinePreciseMarkAsync(
            655, _file, null, profile.ChapterPattern, profile.Language, 658, 662, 700, [], null, CancellationToken.None);

        Assert.True(audio.DecodeWindows.Count > firstAttemptDecodes,
            $"retry ran {audio.DecodeWindows.Count} decodes, single attempt {firstAttemptDecodes}");
    }

    /// <summary>
    /// Builds a stretch of PCM for the onset anchor's scan to measure: <paramref name="quietSeconds"/>
    /// of pause at <paramref name="quiet"/>, then speech-level audio, then enough more of it to be
    /// the window's peak. Amplitudes rather than a waveform, since every test using this asks only
    /// which frames clear <see cref="DetectionTuning.PreciseMarkOnsetFloorDb"/> below the peak.
    /// </summary>
    /// <param name="quietSeconds">How long the pause runs before speech starts.</param>
    /// <param name="totalSeconds">Length of the whole buffer.</param>
    /// <param name="quiet">Sample amplitude during the pause.</param>
    /// <param name="clickSeconds">Length of a louder transient at the very start - the mouth noise
    /// or page turn that closes a silence before the narrator speaks; 0 for none.</param>
    /// <param name="click">Sample amplitude of that transient.</param>
    private static float[] PcmPause(
        double quietSeconds, double totalSeconds, float quiet = 0.0005f,
        double clickSeconds = 0, float click = 0.004f)
    {
        var samples = new float[(int)(totalSeconds * FfmpegClient.SampleRate)];
        var speechFrom = (int)(quietSeconds * FfmpegClient.SampleRate);
        var clickTo = (int)(clickSeconds * FfmpegClient.SampleRate);
        for (var i = 0; i < samples.Length; i++)
            samples[i] = i >= speechFrom ? 0.25f : i < clickTo ? click : quiet;
        return samples;
    }

    [Fact]
    public async Task PreciseMark_AnchorsTheOnsetOntoTheSoundInFrontOfTheAnnouncement()
    {
        // Real-world failure ("The Philosopher's Stone" chapters 5, 8, 15 and 17, 2026-08-03): the
        // plateau the onset walk converges on ends where Whisper stops recognizing the phrase from a
        // window cut into it, and a clipped "Chapter" is something it reconstructs well - the probe
        // starting a third of the way into the word still came back "Chapter 5 Diagon Alley". The
        // reported onset therefore sat 0.29-0.40 s late on those four marks, and a 0.35 s mark lead
        // subtracted from it left 0.06 s of clearance. Every one of them landed on the announcement's
        // own first word, which is what the listener hears.
        //
        // Chapter 5's geometry at 1/1 scale, straight out of that run's debug log and its audio: an
        // 8.37 s silence ending at 6238.42, the announcement's own first sound 0.04 s later (that
        // 0.04 is silencedetect's threshold, not a gap - measured at -40 dBFS rising to -12.8 within
        // 60 ms), and a plateau that keeps confirming to 6238.71. The announcement is scripted at
        // the plateau's position rather than the true onset, since what the walk converges on is
        // exactly what a scripted recognizer can express.
        var audio = new FakeAudioSource();
        audio.AddPcm(6238.42, PcmPause(quietSeconds: 0, totalSeconds: 4.6));
        var transcriber = new ScriptedTranscriber(audio);
        transcriber.Add(6238.71, Seg(0, " Chapter two."));
        var (refiner, profile) = MakeVerifier(transcriber);

        var result = await refiner.RefinePreciseMarkAsync(
            6240.51, _file, null, profile.ChapterPattern, profile.Language, 6238.42, 6244.22, 6255,
            [new(6230.05, 6238.42), new(6239.28, 6240.86)], null, CancellationToken.None);

        Assert.True(result.PhraseHeard);
        // Not merely "earlier than before": the full lead, measured from where the sound starts.
        Assert.Equal(6238.42 - PinnedMarkLeadSeconds, result.Mark, 3);
    }

    [Fact]
    public async Task PreciseMark_AnchorsPastAClickThatClosedTheSilenceEarly()
    {
        // Why the anchor scans for sound instead of stopping at the silence's end, and the case that
        // settled it (Paula Monti chapter 19, 2026-08-03, verified correct by the user *before* any
        // of this): silencedetect judges individual samples against a fixed -35 dBFS threshold, so a
        // 20 ms mouth noise at -47 dBFS closed the pause at 3:18:50.72 while "Première" did not begin
        // until 3:18:51.13. Anchoring to the silence end alone would have moved a good mark 0.39 s
        // early. Over the fourteen-book run this is the one mark of 114 where the two rules disagree
        // by more than 0.1 s - which is exactly why it is written down here.
        //
        // That geometry at 1/1 scale: silence ends at 11930.74, the click occupies its first 20 ms,
        // room tone until 11931.13, speech from there, plateau confirming to 11931.31.
        var audio = new FakeAudioSource();
        audio.AddPcm(11930.74, PcmPause(quietSeconds: 0.39, totalSeconds: 4.6, clickSeconds: 0.02));
        var transcriber = new ScriptedTranscriber(audio);
        transcriber.Add(11931.31, Seg(0, " Chapter two."));
        var (refiner, profile) = MakeVerifier(transcriber);

        var result = await refiner.RefinePreciseMarkAsync(
            11932.5, _file, null, profile.ChapterPattern, profile.Language, 11931.31, 11935.9, 11945,
            [new(11929.34, 11930.74), new(11931.99, 11932.59)], null, CancellationToken.None);

        Assert.True(result.PhraseHeard);
        Assert.Equal(11931.13 - PinnedMarkLeadSeconds, result.Mark, 3);
    }

    [Fact]
    public async Task PreciseMark_LeavesTheOnsetAlone_WhenTheNearestSilenceEndsTooFarInFrontOfIt()
    {
        // The other side of the anchor, and what keeps it from touching a jingle book: music is not
        // silence, so on a book that plays a jingle between the last chapter's hush and the
        // announcement the nearest silence ends many seconds early and there is no boundary to
        // anchor to. The mark then stays exactly where the onset walk put it, which is the behaviour
        // 214 of the 328 marks in the 2026-08-03 corpus rely on.
        //
        // Same announcement as above with the silence pulled back to 6236.5, 2.2 s in front of it -
        // twice PreciseMarkSilenceAnchorSeconds, and still an order of magnitude short of a real
        // jingle.
        var transcriber = new ScriptedTranscriber(new FakeAudioSource());
        transcriber.Add(6238.71, Seg(0, " Chapter two."));
        var (refiner, profile) = MakeVerifier(transcriber);

        var result = await refiner.RefinePreciseMarkAsync(
            6240.51, _file, null, profile.ChapterPattern, profile.Language, 6238.42, 6244.22, 6255,
            [new(6230.05, 6236.5), new(6239.28, 6240.86)], null, CancellationToken.None);

        Assert.True(result.PhraseHeard);
        AssertMarkTime("chapter two", 6238.71 - PinnedMarkLeadSeconds, result.Mark);
        Assert.True(result.Mark > 6236.5,
            $"mark {result.Mark} was dragged back to the out-of-range silence at 6236.5");
    }

    [Fact]
    public async Task PreciseMark_LeavesTheOnsetAlone_WhenItAlreadyFallsInsideASilence()
    {
        // An onset the walk placed *inside* a silence needs no correction - it is already in front of
        // whatever the announcement is - and must not be dragged back to the silence before that one.
        // Real case ("The Philosopher's Stone" chapter 10, same run): onset 15920.15 against a
        // 9.05 s silence running to 15920.21, with the previous stored silence 15.5 s earlier.
        var transcriber = new ScriptedTranscriber(new FakeAudioSource());
        transcriber.Add(15920.15, Seg(0, " Chapter two."));
        var (refiner, profile) = MakeVerifier(transcriber);

        var result = await refiner.RefinePreciseMarkAsync(
            15921, _file, null, profile.ChapterPattern, profile.Language, 15920.15, 15926, 15935,
            [new(15903.82, 15904.61), new(15911.16, 15920.21)], null, CancellationToken.None);

        Assert.True(result.PhraseHeard);
        AssertMarkTime("chapter two", 15920.15 - PinnedMarkLeadSeconds, result.Mark);
        Assert.True(result.Mark > 15910, $"mark {result.Mark} fell back to the silence before last");
    }

    [Fact]
    public async Task PreciseMark_AnchorsTheOnsetBackOntoTheEdgeOfTheMusic()
    {
        // The jingle's half of the correction above. Music is not silence, so a book that plays one
        // into every chapter offers no floor within PreciseMarkSilenceAnchorSeconds and every one of
        // its marks used to keep the raw plateau edge - 264 of the fourteen-book corpus's 440 refined
        // marks, four books of them entirely. What stands in for the floor is the voice-activity
        // pre-pass's own reading of where the music gives way to speech, which the mark already
        // carries as its resolved jingle region.
        //
        // Real-world case (Stalker.m4b chapter 24, 2026-08-09): VAD put speech at 10:27:07.51 and the
        // plateau confirmed to 10:27:07.69, so the mark landed 0.18 s later than the same chapter got
        // in an earlier build. Modelled at 1/1 scale: onset 660, music edge 0.3 s in front of it -
        // further than the cap, so the pull-back stops at PreciseMarkMusicAnchorCapSeconds.
        var transcriber = new ScriptedTranscriber(new FakeAudioSource());
        transcriber.Add(660, Seg(0, " Chapter two."));
        var (refiner, profile) = MakeVerifier(transcriber);

        var result = await refiner.RefinePreciseMarkAsync(
            659.75, _file, null, profile.ChapterPattern, profile.Language, 660, 663, 700, [], 659.7,
            CancellationToken.None);

        Assert.True(result.PhraseHeard);
        Assert.Equal(659.9 - PinnedMarkLeadSeconds, result.Mark, 3);
    }

    [Fact]
    public async Task PreciseMark_AnchorsToTheMusicEdgeOnly_AsFarAsThatEdgeActuallyIs()
    {
        // The cap is a ceiling on the correction, not the correction itself: where the edge is nearer
        // than the cap the onset stops at the edge, because past it lies music rather than a late
        // plateau. Measured over the corpus, this is the common case - the edge sits a median of
        // 0.05 s behind the onset and within 0.10 s for 88 % of the marks that have one in reach.
        var transcriber = new ScriptedTranscriber(new FakeAudioSource());
        transcriber.Add(660, Seg(0, " Chapter two."));
        var (refiner, profile) = MakeVerifier(transcriber);

        var result = await refiner.RefinePreciseMarkAsync(
            659.75, _file, null, profile.ChapterPattern, profile.Language, 660, 663, 700, [], 659.96,
            CancellationToken.None);

        Assert.Equal(659.96 - PinnedMarkLeadSeconds, result.Mark, 3);
    }

    [Fact]
    public async Task PreciseMark_IgnoresAMusicEdgeTooFarInFrontOfTheOnset()
    {
        // Held to the same reach as a silence floor, and for the same reason: a region ending well
        // before the announcement describes a different transition, and the gap between them is
        // narration or a pause this correction knows nothing about. Without the limit the cap alone
        // would still pull every such mark back by its full width on no evidence at all.
        var transcriber = new ScriptedTranscriber(new FakeAudioSource());
        transcriber.Add(660, Seg(0, " Chapter two."));
        var (refiner, profile) = MakeVerifier(transcriber);

        var result = await refiner.RefinePreciseMarkAsync(
            659.75, _file, null, profile.ChapterPattern, profile.Language, 660, 663, 700, [], 658.5,
            CancellationToken.None);

        Assert.Equal(659.75, result.Mark, 3);
    }

    [Fact]
    public async Task PreciseMark_NeverMovesTheOnsetForwardToTheMusicEdge()
    {
        // A region ending *behind* the onset is the jingle-embedded shape: the announcement is spoken
        // over the music, so the region closes after it rather than in front of it. Anchoring there
        // would push the mark into the announcement, which is the one direction none of this may
        // ever move a mark.
        var transcriber = new ScriptedTranscriber(new FakeAudioSource());
        transcriber.Add(660, Seg(0, " Chapter two."));
        var (refiner, profile) = MakeVerifier(transcriber);

        var result = await refiner.RefinePreciseMarkAsync(
            659.75, _file, null, profile.ChapterPattern, profile.Language, 660, 663, 700, [], 660.4,
            CancellationToken.None);

        Assert.Equal(659.75, result.Mark, 3);
    }

    [Fact]
    public async Task PreciseMark_PrefersASilenceInReachOverTheMusicEdge()
    {
        // Precedence, where a mark has both. The silence path measures - it scans the real waveform
        // forward from the floor to where sound actually starts - while the music edge is a single
        // number from a detector with a known onset lag, corrected only within a cap. Measurement
        // wins. (The fixture's audio is digitally silent, so the scan takes its unmeasurable-window
        // exit and returns the floor itself, which is the value asserted here.)
        var transcriber = new ScriptedTranscriber(new FakeAudioSource());
        transcriber.Add(660, Seg(0, " Chapter two."));
        var (refiner, profile) = MakeVerifier(transcriber);

        var result = await refiner.RefinePreciseMarkAsync(
            659.75, _file, null, profile.ChapterPattern, profile.Language, 660, 663, 700,
            [new(658, 659.8)], 659.7, CancellationToken.None);

        Assert.Equal(659.8 - PinnedMarkLeadSeconds, result.Mark, 3);
    }

    [Fact]
    public async Task PreciseMark_SnapsToTheQuietestNearbyPoint()
    {
        // precise marking's final cleanup step (SnapToQuietestPointAsync): even a mark the phrase
        // check already confirmed can still coincide with a comparatively loud sample - a player
        // seeking there would start playback abruptly mid-waveform, an audible "plop". Chapter
        // two's mark (614.75) is confirmed unchanged exactly as in the simple case above, but here
        // the 0.15s lookback (decode window [614.60, 614.75], padded to 2560 samples so the
        // mark's own current-position window has enough trailing samples) is scripted as loud
        // (1.0, sum-of-squares 160 per window) throughout except for a genuine 10ms-wide, fully
        // silent dip 50ms *before* the mark (samples 1520-1679) - an infinite dB improvement, so
        // the mark should end up snapped to that dip's centre (614.70) rather than left at 614.75.
        var result = await DetectAsync(
            Options("--min-silence-length", "1.5"),
            [new(610, 613)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(613, Seg(2, " Chapter two.")); // window [613, ...], phrase at 615
                s.Add(614.65, Seg(0, " Chapter two.")); // check @ 614.75 (chapter two's own mark)

                var samples = new float[2560];
                Array.Fill(samples, 1.0f);
                Array.Clear(samples, 1520, 160); // quiet dip: 614.60 + 1520/16000 .. + 1680/16000
                s.Audio.AddPcm(614.60, samples); // quiet-snap decode window starts at 614.60
            });

        Assert.False(result.GapRemains);
        AssertChapters([new(1, 0.25), new(2, 614.70)], result.Chapters);
    }

    [Fact]
    public async Task PreciseMark_QuietSnap_LeavesTheMarkInPlace_WhenNothingNearbyIsQuieter()
    {
        // Guard for the quiet-snap step: audio that is uniformly loud throughout the 0.15s
        // backward search range (not just the trivial all-silent default every other test relies
        // on) offers no improvement over the mark's own position at all. The mark must resolve
        // back to exactly where it already was (614.75) rather than drifting anywhere.
        var result = await DetectAsync(
            Options("--min-silence-length", "1.5"),
            [new(610, 613)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(613, Seg(2, " Chapter two."));
                s.Add(614.65, Seg(0, " Chapter two."));

                var samples = new float[2560];
                Array.Fill(samples, 0.7f);
                s.Audio.AddPcm(614.60, samples);
            });

        Assert.False(result.GapRemains);
        AssertChapters([new(1, 0.25), new(2, 614.75)], result.Chapters);
    }

    [Fact]
    public async Task PreciseMark_QuietSnap_IgnoresAQuietSpotThatOnlyExistsAfterTheMark()
    {
        // The quiet-snap step now only ever looks backward (see SnapToQuietestPointAsync) - never
        // forward, no matter how quiet a forward spot is. Backward, and the mark's own
        // current-position window, are both scripted uniformly loud (1.0, no possible
        // improvement); only the very tail of the decoded window (samples 2480-2559 - past even
        // the current-position window, which ends at 2480) is silent. That spot sits after the
        // mark and is never reachable as a backward candidate by construction, so it must have
        // no effect at all: the mark must stay exactly at 614.75.
        var result = await DetectAsync(
            Options("--min-silence-length", "1.5"),
            [new(610, 613)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(613, Seg(2, " Chapter two."));
                s.Add(614.65, Seg(0, " Chapter two."));

                var samples = new float[2560];
                Array.Fill(samples, 1.0f);
                Array.Clear(samples, 2480, 80); // quiet spot strictly after the mark - must be ignored
                s.Audio.AddPcm(614.60, samples);
            });

        Assert.False(result.GapRemains);
        AssertChapters([new(1, 0.25), new(2, 614.75)], result.Chapters);
    }

    [Fact]
    public async Task PreciseMark_QuietSnap_DoesNotNudge_WhenBackwardImprovementIsUnderSixDb()
    {
        // The new minimum-improvement gate (PreciseMarkQuietSnapMinImprovementDb = 6 dB): a
        // backward dip that genuinely is quieter than the mark's own position, but not by enough,
        // must not trigger a nudge. Backward dip amplitude 0.6 (sum-of-squares 57.6) against the
        // loud (1.0, sum-of-squares 160) baseline is only a ~2.78x power ratio - 10*log10(2.78) =
        // ~4.44 dB, under the 6 dB bar - so the mark must stay exactly at 614.75.
        var result = await DetectAsync(
            Options("--min-silence-length", "1.5"),
            [new(610, 613)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(613, Seg(2, " Chapter two."));
                s.Add(614.65, Seg(0, " Chapter two."));

                var samples = new float[2560];
                Array.Fill(samples, 1.0f);
                for (var i = 1520; i < 1680; i++) samples[i] = 0.6f; // backward dip @ 614.70, ~4.44 dB quieter
                s.Audio.AddPcm(614.60, samples);
            });

        Assert.False(result.GapRemains);
        AssertChapters([new(1, 0.25), new(2, 614.75)], result.Chapters);
    }

    [Fact]
    public async Task PreciseMark_QuietSnap_NudgesBackward_WhenAtLeastSixDbQuieter()
    {
        // The mirror of the test above, just over the 6 dB bar: backward dip amplitude 0.5
        // (sum-of-squares 40) against the same loud (160) baseline is exactly a 4x power ratio -
        // 10*log10(4) = ~6.02 dB, clearing PreciseMarkQuietSnapMinImprovementDb - so this time the
        // mark must nudge to the dip's centre (614.70).
        var result = await DetectAsync(
            Options("--min-silence-length", "1.5"),
            [new(610, 613)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(613, Seg(2, " Chapter two."));
                s.Add(614.65, Seg(0, " Chapter two."));

                var samples = new float[2560];
                Array.Fill(samples, 1.0f);
                for (var i = 1520; i < 1680; i++) samples[i] = 0.5f; // backward dip @ 614.70, ~6.02 dB quieter
                s.Audio.AddPcm(614.60, samples);
            });

        Assert.False(result.GapRemains);
        AssertChapters([new(1, 0.25), new(2, 614.70)], result.Chapters);
    }

    // Retired 2026-08-08 with the Probe restructuring: AutoMaxJingle_MeasuresJingleUpToThePhrase_
    // NotTheInflatedRegionEnd, AutoMaxJingle_DoesNotResizeFromTheFirstMark and AutoMaxJingle_
    // NeverNarrowsTheWindow_AfterALongerJingleWasObserved lived here. All three asserted how the
    // --max-jingle-length auto window sizing governed the *primary* scan, which it stopped doing:
    // a primary candidate carries a window cut to what its own class expects.
    //
    // Retired 2026-08-11 with the recovery-geometry rework, for the same reason one step further on:
    // AutoMaxJingle_AfterAGap_ReprobesAProbedCandidateAtTheCeilingWindow,
    // AGapRecoveredChapter_WidensTheJingleWindow_ByAtMostTheGrowthCap and
    // AGapRecoveredChapter_WithinTheGrowthCap_GetsItsFullReach. The sequence-gap re-probe was the
    // last thing sizing a window off observed jingle lengths, and it now rebuilds its stretch as
    // recovery candidates instead - each cut to its own class and deliberately *narrower* than the
    // primary scan's rather than wider. Nothing is left to widen, so ObserveJingleLength,
    // ProposeJingleWindow and the ceiling reset went with them. AGapReprobe_ReframesTheStretch_
    // RatherThanWideningIt below is what covers the re-probe's geometry now.

    [Fact]
    public async Task AutoMinSilence_NeverSkipsVadCandidates_AndTheyDoNotMistightenTheThreshold()
    {
        // Chapter two's 5 s triggering silence tightens the threshold to 3.75 s (0.75x). Chapter
        // three is then found via a silence-less, VAD-only candidate (region length 3 s) -
        // since it carries no Silence, it must always be probed regardless of the threshold
        // (that's exactly what lets VAD catch silence-less chapters). It must also not disturb
        // the threshold itself: the following 3.7 s silence - just below 3.75 s - must still be
        // skipped, proving the threshold is unchanged by the silence-less mark.
        var (result, _, audio) = await DetectFullAsync(
            Options("--mark-before-jingle"),
            [new(595, 600), new(800, 803.7)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(600, Seg(0.3, " Chapter two."));
                s.Add(700, Seg(0.3, " Chapter three."));
            },
            new FakeVad { Speech = [new(0, 700), new(703, 3600)] });

        Assert.Equal([1, 2, 3], result.Chapters.Select(c => c.Number));
        Assert.Contains(700.0, audio.DecodeStarts);
        Assert.DoesNotContain(803.7, audio.DecodeStarts);
    }

    [Fact]
    public async Task VerboseMarkLine_NamesTheCandidateClassThatFoundIt()
    {
        // All four shapes in one file. The class decides where a window opened and how far it ran,
        // so a mark that landed oddly reads completely differently depending on which found it -
        // and the log is the only place that pairing is visible once a run is over.
        var (result, log, _) = await DetectWithLogAsync(
            Options("--quick-marks"),
            [new(595, 600)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));      // the region start: no class to report
                s.Add(600, Seg(0.3, " Chapter two."));    // a pause
                s.Add(1020, Seg(0, " Chapter three."));   // the speech behind a plain jingle
                s.Add(1410, Seg(0, " Chapter four."));    // spoken over the music, 20 s into it
            },
            // Jingle one (1000-1020) is unbroken. Jingle two (1390-1430) carries the announcement
            // itself as a 0.3 s VAD blip at 1410, short enough for the census to bridge rather than
            // split on - which is what licenses reading the music at all. The speech window opens at
            // 1422 (8 s before the music ends) and cannot reach 1410; only the music tiles do.
            new FakeVad
            {
                Speech = [new(0, 1000), new(1020, 1390), new(1410, 1410.3), new(1430, 3600)],
            });

        Assert.Equal([1, 2, 3, 4], result.Chapters.Select(c => c.Number));
        Assert.Contains(log, l => l.StartsWith("chapter 1 detected") &&
                                  !l.Contains(", at a") && !l.Contains("embedded"));
        Assert.Contains(log, l => l.StartsWith("chapter 2 detected") && l.Contains(", at a silence)"));
        Assert.Contains(log, l => l.StartsWith("chapter 3 detected") && l.Contains(", at a jingle)"));
        Assert.Contains(log, l => l.StartsWith("chapter 4 detected") &&
                                  l.Contains(", embedded in a jingle)"));
    }

    [Fact]
    public async Task AutoMinSilence_AJingleCandidatesMark_TeachesTheThresholdNothing()
    {
        // The sibling case the test above cannot reach: that jingle had no silence anywhere near
        // it, so its mark brought nothing to tighten from and the threshold was safe by accident.
        // This one has a 4 s hush in front of the music (996-1000), which the mark duly anchors to
        // (ResolveJingleAnchor), and off the back of which it would once have proposed 0.75 x 4 = 3 s.
        // It must not. That hush measures the lead-in to a music transition; the threshold is only
        // ever about how long this book's chapter-break *pauses* run, because that is what decides
        // which pauses become candidates at all - and a book whose jingles are led by a hush longer
        // than its pauses would have the threshold raised straight past the pauses it still needs.
        //
        // Chapter 3 is the control: an identical 4 s pause, found as an ordinary pause candidate,
        // and it does propose exactly that 3 s. The two differ in nothing but which class found them.
        var (result, log, _) = await DetectWithLogAsync(
            Options("--quick-marks"),
            [new(595, 600), new(996, 1000), new(1496, 1500)],
            s =>
            {
                s.Add(600, Seg(0.3, " Chapter one."));
                s.Add(1020, Seg(0, " Chapter two."));      // the speech behind the jingle
                s.Add(1500, Seg(0.3, " Chapter three."));
            },
            // VAD stops hearing speech where the hush starts, so the region opens at 996 and the
            // hush's end lies inside it - which is what LeadingSilence requires to call it the
            // jingle's lead-in rather than an unrelated pause. The music itself is 1000-1020.
            new FakeVad { Speech = [new(0, 996), new(1020, 3600)] });

        Assert.Equal([1, 2, 3], result.Chapters.Select(c => c.Number));
        Assert.Contains(log, l => l.StartsWith("chapter 2 detected") && l.Contains(", at a jingle)"));
        Assert.DoesNotContain(log, l => l.Contains("threshold") && l.Contains("after chapter 2"));
        // Chapter 3's proposal is therefore the first one to land, which is what makes the
        // absence above a real measurement: drop the rule and this line moves to chapter 2.
        Assert.Contains(log, l => l.Contains($"threshold tightened to {3.0:0.##} s after chapter 3"));
    }

    [Fact]
    public async Task LowConfidenceSegment_CarriesConfidence_AndIsFlagged()
    {
        var result = await DetectAsync(
            Options(),
            [new(595, 600)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one.", confidence: 0.95));
                s.Add(600, Seg(0.3, " Chapter two.", confidence: 0.2));
            });

        AssertChapters(
            [new(1, 0.25, 0.95), new(2, 600.05, 0.2)],
            result.Chapters);
        Assert.Equal([2], result.LowConfidenceChapters.Select(c => c.Number));
    }

    [Fact]
    public async Task HighConfidenceSegments_YieldNoLowConfidenceFlags()
    {
        var result = await DetectAsync(
            Options(),
            [new(595, 600)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(600, Seg(0.3, " Chapter two."));
            });

        Assert.Empty(result.LowConfidenceChapters);
    }

    [Fact]
    public async Task NoSpeechAnywhere_YieldsNoChapters()
    {
        var result = await DetectAsync(Options(), [new(595, 600)], _ => { });
        Assert.Empty(result.Chapters);
        Assert.False(result.GapRemains);
    }

    [Fact]
    public async Task OverlappingProbe_ReusesTheCachedTranscript_InsteadOfReDecodingTheOverlap()
    {
        // Two candidates close enough together to share one encoder pass. Each silence opens its
        // window a lead-in early ([597, ...] and [601, 625]), and window 1's own end is the border
        // with window 2, snapped to the mid-point of the silence they share (602). Chapter one is
        // found by the first probe - deliberately at low confidence, so the overlap-sequence skip
        // stays out of the way (a low-confidence mark must not skip the windows that could
        // re-detect the transition it may have gotten wrong) and the reuse path is exercised.
        //
        // Window 2 costs no Whisper call whatsoever: window 1's decode reads ahead to window 2's
        // planned end (625) because that still fits the one 30 s encoder pass it was already
        // paying for, which leaves window 2 wholly inside the cache. So the single decode runs
        // [597, 625] - not to window 1's own end (602) - and neither the candidate's own start
        // (601) nor the border (602) is ever decoded from. The detected chapter is unaffected.
        //
        // The narration at 624 is what makes the read-ahead cacheable at all: a transcript that
        // stops short of the decode end is not trusted to the end (see RegionProber.CacheableEnd),
        // and a scripted transcriber says nothing except where a test tells it to, so a window that
        // real audio would fill with words is otherwise silent from 602.5 on. Scripting the tail is
        // the faithful setup here - this test is about reuse, not about the untranscribed case,
        // which ReadAhead_DoesNotCacheATailTheRecognizerLeftUntranscribed covers on its own.
        var (result, _, audio) = await DetectFullAsync(
            Options("--min-silence-length", "1.5"),
            [new(595, 600), new(601, 603)],
            s =>
            {
                s.Add(600, Seg(0.5, " Chapter one.", confidence: 0.3));
                s.Add(600, Seg(24.0, " And the story went on.")); // abs 624-626, to the decode end
            });

        AssertChapters([new DetectedChapter(1, 600.25, 0.3)], result.Chapters);
        Assert.Contains((597.0, (double?)28.0), audio.DecodeWindows); // read ahead to 625, one pass
        Assert.DoesNotContain(602.0, audio.DecodeStarts);  // no fresh tail decode was needed
        Assert.DoesNotContain(601.0, audio.DecodeStarts);  // the overlap was reused, not re-decoded
    }

    [Fact]
    public async Task ReadAhead_StopsAtTheEncoderPass_RatherThanSwallowingTheNextWindow()
    {
        // The counterpart to the layout above: the last candidate's window ends at 630.3, and
        // window 1's decode starts at 597, so reaching that end would cost a second encoder pass.
        // Reading ahead exists to fill the pass already bought and never to buy another, so window 1
        // decodes exactly its own planned span ([597, 608.3], the snapped seam) and the window after
        // it pays for its own tail decode after all.
        var (_, _, audio) = await DetectFullAsync(
            Options("--min-silence-length", "1.5"),
            [new(595, 600), new(603, 606), new(608, 608.6)],
            s => s.Add(600, Seg(0.5, " Chapter one.", confidence: 0.3)));

        Assert.Contains(audio.DecodeWindows, w =>
            Math.Abs(w.Start - 597) < 1e-6 && Math.Abs((w.Duration ?? 0) - 11.3) < 1e-6);
        Assert.Contains(608.3, audio.DecodeStarts); // the fresh tail, not read ahead into
    }

    [Fact]
    public async Task ReadAhead_KeepsItsSurplusOutOfTheWindowThatDecodedIt()
    {
        // Window 1 (candidate 597, expecting its announcement at 600) ends at the seam 603 and reads
        // ahead to 627 - candidate 602's planned end, the furthest that still fits the one encoder
        // pass it was already paying for. Chapter two's announcement (abs 609) therefore sits in
        // audio window 1 has decoded, but not in the window window 1 was probed for.
        //
        // Its acceptance is what tells the two apart, and decisively: 609 is 9 s past window 1's own
        // expectation, which ResolveAnnouncementMark rejects outright (PhraseLatestStart is 5 s), and
        // 4 s past candidate 602's, which it accepts. A chapter two at all means the surplus was
        // scanned with the window that expects an announcement there, not with the one that read it.
        // Chapter one is scripted at low confidence so its own mark does not settle window 2 away.
        var (result, log, _) = await DetectWithLogAsync(
            Options("--min-silence-length", "1.5"),
            [new(595, 600), new(601, 605), new(634, 637)],
            s =>
            {
                s.Add(597, Seg(3.0, " Chapter one.", confidence: 0.3));
                s.Add(597, Seg(12.0, " Chapter two.")); // abs 609 - inside the read-ahead surplus
                s.Add(597, Seg(28.0, " And so it ended."));  // abs 625-627, to the decode end, so
            });                                              // the whole surplus stays cacheable

        AssertChapters([new DetectedChapter(1, 599.75, 0.3), new DetectedChapter(2, 608.75, 1.0)],
                       result.Chapters);
        // 30 s decoded from 597, of which the last 24 belong to the window still to come - which is
        // then served from the cache outright, with no probe line of its own.
        Assert.Contains(log, l => l.StartsWith($"probe {30.0:0.0}s@0:09:57.00 (+{24.0:0.0}s ahead)"));
        Assert.Single(log, l => l.StartsWith("probe") && l.Contains("@0:09:"));
    }

    [Fact]
    public async Task OverlapSkip_IsCapped_SoOneMarkCannotSettleAWholeBook()
    {
        // Fifteen candidates 5 s apart, each 12 s window overlapping the next, so the chain would
        // otherwise run to the end of the list off chapter one's single confident mark. That is the
        // BARDIOC.m4b failure of 2026-08-08 in miniature, where one mark settled 6260 windows and
        // probing resumed eleven hours later; the premise that an overlapping run covers one
        // transition is only true while the run is short. Capped, the rest are probed - and chapter
        // two, scripted well past the cap, has to be found, which is what a runaway chain loses.
        var silences = new List<Silence>();
        for (var i = 0; i < 15; i++)
            silences.Add(new(598.5 + 5 * i, 600 + 5 * i));

        var (result, log, _) = await DetectWithLogAsync(
            Options("--min-silence-length", "1.5"),
            silences,
            s =>
            {
                s.Add(600, Seg(0.5, " Chapter one."));
                s.Add(660, Seg(0.5, " Chapter two."));
            });

        Assert.Equal([1, 2], result.Chapters.Select(c => c.Number));
        Assert.Contains(log, l =>
            l.Contains($"{DetectionTuning.MaxSettledWindowSkip} overlapping window(s) skipped") &&
            l.Contains("chain capped"));
    }

    [Fact]
    public async Task ReadAhead_DoesNotCacheATailTheRecognizerLeftUntranscribed()
    {
        // "BARDIOC.m4b" (2026-08-02) in miniature, the failure that made CacheableEnd necessary: the
        // announcement sits in a window's read-ahead surplus, the long decode does not hear it, and
        // the surplus is then cached as though it had been read - so every later candidate is served
        // an empty transcript for audio no decode ever reported on, and the chapter is lost outright.
        // On the book itself that was the "Zeittafel" at 0:00:51, inside a 54.2 s decode from 0:00:00
        // that stopped emitting at 0:00:37 after ~20 s of jingle music.
        //
        // Here window 1 (candidate 597, planned end 602.75) reads ahead to 626.5 - candidate 601.5's
        // own end, the furthest that still fits the one encoder pass it was already paying for - and
        // the announcement at 605 is scripted chunk-sensitively: heard by any decode up to 25 s, lost
        // by the 29.5 s one - real Whisper's own framing artifact (see ScriptedTranscriber.AddWithin).
        // Capping the cache at the transcript's reach sends the next candidate back to the audio,
        // where a shorter decode reads it cleanly.
        var (result, log, audio) = await DetectWithLogAsync(
            Options("--min-silence-length", "1.5"),
            [new(595, 600), new(601, 604.5), new(700, 703)],
            s => s.AddWithin(25.0, 605, Seg(0, " Chapter one.")));   // abs 605, in the surplus

        AssertChapters([new DetectedChapter(1, 604.75, 1.0)], result.Chapters);
        // The decode read to 626.5 but is trusted only to 602.75 - its own planned end, the floor
        // this can never go below - so the 23.75 s of surplus it said nothing about is read again.
        Assert.Contains(log, l => l.StartsWith($"probe {29.5:0.0}s@0:09:57.00 " +
                                               $"(+{23.75:0.0}s ahead, {23.75:0.0}s uncached)"));
        Assert.Contains(audio.DecodeStarts, d => Math.Abs(d - 602.75) < 1e-6);
    }

    [Fact]
    public async Task OverlapReuse_IsRefused_WhenACachedSegmentHidesTheCandidatesExpectation()
    {
        var (result, log, audio) = await DetectWithLogAsync(
            Options("--min-silence-length", "1.5"),
            [new(595, 600), new(609, 613), new(618, 621)],
            s =>
            {
                // Window one's whole span came back as a single run-on segment - the shape that
                // lost "The Forever War" its chapter 1. It covers window two's expectation and
                // starts before window two, so the slice drops it and leaves a hole exactly there.
                s.Add(597, new TranscriptSegment(0, 23, " And now the title.", 0.62));
                // Heard only by window two's own short decode, not by window one's longer one -
                // which is why reuse loses the chapter outright: both windows end at the same
                // seam, so reuse leaves window two with no fresh audio to read at all.
                s.AddWithin(20, 611, Seg(0, " Chapter one."));
            });

        Assert.Contains(log, l => l.StartsWith("re-reading window at 0:10:10.00"));
        Assert.Contains(audio.DecodeWindows, w => Math.Abs(w.Start - 610) < 0.01);
        var chapter = Assert.Single(result.Chapters);
        Assert.Equal(1, chapter.Number);
    }

    [Fact]
    public async Task ConfidentMark_SkipsTheRemainingWindowsOfItsOverlapSequence()
    {
        // Same layout as above, but chapter one is found confidently: the mark settles the
        // whole overlapping window sequence, so the candidate at 606 is skipped outright - no
        // fresh tail decode at the border (612), no decode at the candidate start (606), and
        // this works with an explicit --min-silence-length (no adaptive skipping involved).
        var (result, _, audio) = await DetectFullAsync(
            Options("--min-silence-length", "1.5"),
            [new(595, 600), new(601, 606)],
            s => s.Add(600, Seg(0.5, " Chapter one.")));

        AssertChapters([new DetectedChapter(1, 600.25)], result.Chapters);
        Assert.DoesNotContain(612.0, audio.DecodeStarts);
        Assert.DoesNotContain(606.0, audio.DecodeStarts);
    }

    [Fact]
    public async Task SequenceSkippedWindow_IsReProbed_WhenASequenceGapTurnsUp()
    {
        // Chapter two's confident mark skips the overlapping window at 606 - and chapter three's
        // announcement hides inside it, at 615.4 (the "sequence spans two transitions" case the
        // skip bets against). It sits past 622, where chapter two's own window ends, so the
        // only decode that can reach it is the skipped window's. The later chapter-four mark
        // exposes the gap, and the skipped window must then be re-probed and chapter three
        // recovered - even with an explicit --min-silence-length, where the gap re-probe used to
        // be unreachable (nothing was ever skipped before the sequence skip existed).
        var (result, _, audio) = await DetectFullAsync(
            Options("--min-silence-length", "1.5"),
            [new(595, 600), new(606, 608), new(1195, 1200)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(600, Seg(0.3, " Chapter two."));
                s.Add(615, Seg(0.4, " Chapter three."));
                s.Add(1200, Seg(0.2, " Chapter four."));
            });

        Assert.False(result.GapRemains);
        Assert.Equal([1, 2, 3, 4], result.Chapters.Select(c => c.Number));
        // That window was skipped first and only decoded by the gap re-probe, i.e. after the probe
        // at 1197 that revealed the gap - and the re-probe re-frames it, opening one trimmed
        // lead-in before the silence ends (607) rather than where the primary scan would have (606).
        Assert.True(audio.DecodeStarts.IndexOf(607) > audio.DecodeStarts.IndexOf(1197));
    }

    [Fact]
    public async Task WideJingleWindow_MarksASecondChapterImmediately_AndSkipsItsOverlapSequence()
    {
        // Chapter one's wide VAD-widened window [600, 650] also contains chapter two's
        // announcement 40 s in. Both marks must come out of this single probe: segment
        // timestamps plus the stored silence list pinpoint chapter two at its own preceding
        // silence ([638, 640]) even though the window was triggered by chapter one's silence.
        // With continuous VAD speech throughout, --mark-before-jingle's backward walk finds real
        // speech covering the default-mode original mark (639.75, 0.25 s before the phrase) and
        // returns it unchanged - the same "ordinary in-narration pause, no jingle here" case as
        // JingleMark_WithContinuousSpeechAroundTheSilence_KeepsTheOriginalMark, not the old fixed
        // "0.5 s before the silence" placement (639.5). The confident marks then settle the
        // overlapping window sequence, so the candidate at 640 is never probed at all - neither
        // its start (640) nor the shared border (650) is ever decoded.
        var (result, _, audio) = await DetectFullAsync(
            Options("--quick-marks", "--mark-before-jingle"),
            [new(598, 600), new(638, 640)],
            s => s.Add(600, Seg(2, " Chapter one."), Seg(40, " Chapter two.")),
            new FakeVad { Speech = [new(0, 3600)] });

        Assert.Equal([1, 2], result.Chapters.Select(c => c.Number));
        AssertContainsChapter(new DetectedChapter(2, 639.75), result.Chapters);
        Assert.DoesNotContain(650.0, audio.DecodeStarts);
        Assert.DoesNotContain(640.0, audio.DecodeStarts);
    }

    [Fact]
    public async Task DeepPhrase_WithAQualifyingAnchorSilence_IsAcceptedAndPinpointedRightAway()
    {
        // Without a jingle the phrase must start within 5 s of the triggering silence - but a
        // phrase deeper in the window is no longer deferred to a later candidate's own probe:
        // chapter one's announcement sits 9 s into the window ([600, 612], phrase at 609),
        // directly after the qualifying [603, 606] silence, so it is accepted right away with
        // the mark pinpointed at that silence's end (606). The confident mark then skips the
        // overlapping candidate at 606 entirely: neither the border (612) nor the candidate
        // start (606) is ever decoded again.
        var (result, _, audio) = await DetectFullAsync(
            Options("--min-silence-length", "1.5"),
            [new(595, 600), new(603, 606)],
            s => s.Add(600, Seg(9, " Chapter one.")));

        AssertChapters([new DetectedChapter(1, 608.75)], result.Chapters);
        Assert.DoesNotContain(612.0, audio.DecodeStarts);
        Assert.DoesNotContain(606.0, audio.DecodeStarts);
    }

    [Fact]
    public async Task DeepPhrase_WithOnlyASubThresholdPauseBeforeIt_IsRejected()
    {
        // The phrase at 609 is directly preceded by a stored 0.6 s pause ([605, 605.6]) - far
        // below the 1.5 s candidate threshold. A breath pause in front of an in-text mention
        // ("Chapter two had been hard.") must not qualify as a deep-detection anchor, so no
        // chapter two mark may appear anywhere.
        var result = await DetectAsync(
            Options("--min-silence-length", "1.5"),
            [new(595, 600), new(605, 605.6)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(600, Seg(9, " Chapter two."));
            });

        AssertChapters([new DetectedChapter(1, 0.25)], result.Chapters);
        Assert.False(result.GapRemains);
    }

    [Fact]
    public async Task DeepPhrase_WithNoSilenceBeforeIt_LogsWhyItWasSkipped()
    {
        // Nothing at all separates the phrase at 9 from the file start - the region-start window
        // holds no silence whatsoever - so there is no anchor to pinpoint a mark at. The number was
        // heard, though, so the log has to say so: "never heard" and "heard but unanchorable" call
        // for completely different fixes.
        var (result, log, _) = await DetectWithLogAsync(
            Options("--min-silence-length", "1.5"),
            [new(595, 600)],
            s => s.Add(0, Seg(9, " Chapter one.")));

        Assert.Empty(result.Chapters);
        Assert.Contains(log, l =>
            l.Contains("skipped chapter 1 at 0:00:09.00") &&
            l.Contains("no silence precedes it inside the probe window"));
    }

    [Theory]
    [InlineData("--custom", "zeittafel:Zeittafel", "custom")]
    [InlineData("--epilogue-phrase", "epilog", "epilogue")]
    public async Task DeepNamedPhrase_WithNoSilenceBeforeIt_IsSkippedLikeAChapterWouldBe(
        string option, string value, string kind)
    {
        // Named phrases used to be exempt from the anchoring rules, so a narrator merely mentioning
        // one of these words deep in a window got a mark. They are announcements or they are
        // nothing, exactly as a chapter phrase is - and the log has to name which one it dropped.
        // The mention at 1209 sits 9 s past the only silence its window holds, well outside the 5 s
        // an announcement is granted, so there is no anchor for it.
        var word = value.Split(':')[0];
        var (result, log, _) = await DetectWithLogAsync(
            Options(option, value, "--min-silence-length", "1.5"),
            [new(595, 600), new(1195, 1200)],
            s =>
            {
                s.Add(600, Seg(0.3, " Chapter one."));
                s.Add(1200, Seg(9, $" Und dann kam der {word}."));
            });

        Assert.Empty(result.NamedMarks);
        Assert.Contains(log, l =>
            l.Contains($"skipped {kind}") &&
            l.Contains("at 0:20:09.00") &&
            l.Contains("the nearest silence ends 9.0 s before it"));
    }

    [Fact]
    public async Task NamedPhrase_DirectlyAfterItsSilence_IsStillAccepted()
    {
        // The other half of the rule: tightening acceptance must not cost a real announcement its
        // mark. This one starts 0.2 s into its window, well inside the 5 s the timing rule grants.
        var result = await DetectAsync(
            Options("--custom", "zeittafel:Zeittafel"),
            [new(595, 600), new(1195, 1200)],
            s =>
            {
                s.Add(600, Seg(0.3, " Chapter one."));
                s.Add(1200, Seg(0.2, " Zeittafel."));
            });

        AssertNamed([("custom 1", "Zeittafel", 1199.95)], result);
    }

    [Fact]
    public async Task DeepPhrase_WithTooDistantASilenceBeforeIt_LogsTheMeasuredDistance()
    {
        // The [601, 603] silence qualifies on length but ends 6 s before the phrase at 609, past
        // the 5 s the timing rule grants. The log names the measured distance, so the rule can be
        // checked against the audio rather than guessed at.
        var (_, log, _) = await DetectWithLogAsync(
            Options("--min-silence-length", "1.5"),
            [new(595, 600), new(601, 603)],
            s => s.Add(600, Seg(9, " Chapter one.")));

        Assert.Contains(log, l =>
            l.Contains("skipped chapter 1 at 0:10:09.00") &&
            l.Contains("the nearest silence ends 6.0 s before it") &&
            l.Contains("more than the 5 s allowed"));
    }

    [Fact]
    public async Task DeepPhrase_WithOnlyASubThresholdPauseBeforeIt_LogsTheSilenceAgainstTheOption()
    {
        // The 0.6 s breath pause before the phrase is a real silence, just too short to anchor on.
        // The log puts its length next to --min-silence-length, since that is the knob to reach
        // for when the book's own chapter breaks turn out to be shorter than the default.
        var (_, log, _) = await DetectWithLogAsync(
            Options("--min-silence-length", "1.5"),
            [new(595, 600), new(605, 605.6)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(600, Seg(9, " Chapter two."));
            });

        Assert.Contains(log, l =>
            l.Contains("skipped chapter 2 at 0:10:09.00") &&
            l.Contains("the silence before it is only 0.60 s long") &&
            l.Contains("below --min-silence-length 1.5 s"));
    }

    [Fact]
    public async Task AutoMinSilence_TightensFromTheSilenceTheMarkFallsInto_NotTheTrigger()
    {
        // Chapter two's probe is triggered by the 6 s silence at [594, 600], but its phrase
        // sits deep in the window, anchored to the 3 s silence at [605, 608] - the silence the
        // mark actually falls into. The threshold must tighten to 0.75 x 3 = 2.25 s, not
        // 0.75 x 6 = 4.5 s: chapter three's 2.5 s silence ([897.5, 900]) is only probed - and
        // chapter three, being last, only ever found - if the mark's own silence was used.
        var (result, _, audio) = await DetectFullAsync(
            Options(),
            [new(594, 600), new(605, 608), new(897.5, 900)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(600, Seg(8.4, " Chapter two."));
                s.Add(900, Seg(0.3, " Chapter three."));
            });

        Assert.False(result.GapRemains);
        AssertChapters(
            [new(1, 0.25), new(2, 608.15), new(3, 900.05)],
            result.Chapters);
        Assert.Contains(900.0, audio.DecodeStarts);
    }

    [Fact]
    public async Task OverlappingProbe_SnapsTheSplitToASilenceMidpointWithinWindowTwo()
    {
        // Window 1 (candidate 597, --min-silence-length 1.5) naturally spans [597, 622];
        // window 2 (candidate 603) spans [603, 628] and overlaps it. A short 0.6 s silence at
        // [620, 620.6] is well below the 1.5 s candidate threshold - it never becomes a Probe
        // candidate of its own - but is still retained down to the 0.5 s floor
        // (MinStoredSilenceSeconds) purely as a seam target, and it lies inside window 2.
        // The window-end plan (PlanWindowEnd) must move the shared border to its
        // mid-point (620.3) before anything is decoded: window 1's decode itself ends there
        // (23.3 s instead of the natural 25), and window 2's fresh tail starts exactly there -
        // never at the raw border (622) or the candidate start (603). Chapter one is scripted
        // at low confidence so the overlap-sequence skip stays out of the way and window 2 is
        // actually probed.
        var (_, _, audio) = await DetectFullAsync(
            Options("--min-silence-length", "1.5"),
            [new(595, 600), new(603, 606), new(620, 620.6)],
            s => s.Add(597, Seg(3.0, " Chapter one.", confidence: 0.3)));

        Assert.Contains(620.3, audio.DecodeStarts);
        Assert.DoesNotContain(622.0, audio.DecodeStarts);
        Assert.DoesNotContain(603.0, audio.DecodeStarts);
        Assert.Contains(audio.DecodeWindows,
            w => w.Start == 597 && w.Duration is { } d && Math.Abs(d - 23.3) < 0.01);
    }

    [Fact]
    public async Task OverlappingProbe_SnapsTheSplitToAVadRegion_WhenNoSilenceQualifies()
    {
        // With the VAD pre-pass running, three overlapping windows: candidate 600 (natural
        // span [600, 650]), candidate 640, and the VAD candidate the [648, 655] non-speech
        // region itself spawns at 648. No silence offers a seam anywhere, so the plan snaps
        // every shared border to the region's mid-point (651.5, only while the VAD pre-pass
        // ran): window 640's end lands there, and
        // window 600's border search - seeing window 640 end at 651.5 - accepts the very same
        // seam at its neighbor's end, extending window 600's decode to 651.5 and leaving
        // window 640 fully contained in its cache (no decode of its own). The VAD candidate's
        // fresh tail then starts exactly at the seam: 651.5 is decoded, the raw border (650)
        // and the swallowed candidate start (640) never are. Chapter one is scripted at low
        // confidence so the overlap-sequence skip stays out of the way.
        var (_, _, audio) = await DetectFullAsync(
            OptionsInOneSweep("--mark-before-jingle"),
            [new(598, 600), new(638, 640)],
            s => s.Add(600, Seg(2, " Chapter one.", confidence: 0.3)),
            new FakeVad { Speech = [new(0, 648), new(655, 3600)] });

        Assert.Contains(651.5, audio.DecodeStarts);
        Assert.DoesNotContain(650.0, audio.DecodeStarts);
        Assert.DoesNotContain(640.0, audio.DecodeStarts);
    }

    [Fact]
    public async Task OverlappingProbe_SnapsBeyondTheBorder_ByExtendingWindowOnesDecode()
    {
        // Window 1 naturally spans [597, 622], window 2 [603, 628] (border 622). The only seam
        // target lies entirely *beyond* the border ([623, 624], mid-point 623.5). Because
        // window 1's end is planned before window 1 is decoded (PlanWindowEnd), its decode is
        // simply *extended* to 623.5 and window 2's fresh tail starts exactly there: the plan moved the border
        // itself, so no [622, 623.5) hole can exist and nothing is cut mid-word at 622.
        // Chapter one is scripted at low confidence so the overlap-sequence skip stays out
        // of the way and window 2 is actually probed.
        var (_, _, audio) = await DetectFullAsync(
            Options("--min-silence-length", "1.5"),
            [new(595, 600), new(603, 606), new(623, 624)],
            s => s.Add(597, Seg(3.0, " Chapter one.", confidence: 0.3)));

        Assert.Contains(623.5, audio.DecodeStarts);
        Assert.DoesNotContain(622.0, audio.DecodeStarts);
        Assert.DoesNotContain(603.0, audio.DecodeStarts);
        Assert.Contains(audio.DecodeWindows,
            w => w.Start == 597 && w.Duration is { } d && Math.Abs(d - 26.5) < 0.01);
    }

    [Fact]
    public async Task OverlappingProbe_SnapsToASilenceStraddlingTheBorder()
    {
        // Same windows ([600, 612] and [606, 618]), but the 1 s silence at [611.6, 612.6]
        // straddles the border: its mid-point (612.1) lies a hair past 612, and the plan
        // simply extends window 1's decode to end there - the seam sits mid-silence, nothing
        // is cut mid-word and nothing is left undecoded. (At 1 s the silence is also below
        // the 1.5 s candidate threshold, so it is retained purely as a seam target. Chapter
        // one is scripted at low confidence so the overlap-sequence skip stays out of the way.)
        var (_, _, audio) = await DetectFullAsync(
            Options("--min-silence-length", "1.5"),
            [new(595, 600), new(603, 606), new(611.6, 612.6)],
            s => s.Add(600, Seg(0.5, " Chapter one.", confidence: 0.3)));

        Assert.Contains(612.1, audio.DecodeStarts);
        Assert.DoesNotContain(612.0, audio.DecodeStarts);
        Assert.DoesNotContain(606.0, audio.DecodeStarts);
    }

    [Fact]
    public async Task OverlappingProbe_LogsOnlyTheFreshTail_AtItsOwnTimestamps()
    {
        // Same split-snapping setup as OverlappingProbe_SnapsTheSplitToASilenceMidpointWithinWindowTwo
        // (split at 620.3), but this asserts on the --verbose-transcripts log itself: the tail
        // probe's log line must show only what was actually decoded from 620.3 onward, at Whisper's
        // own (0-based) timestamps - not the reused segment restated at window-relative time.
        // Chapter one is scripted at low confidence so the overlap-sequence skip stays out of the way.
        //
        // Window 2 ends at 628, past the 627 that window 1's one encoder pass reaches, so window 1
        // cannot read ahead over it and swallow the tail decode this asserts on.
        var (_, log, _) = await DetectWithLogAsync(
            Options("--verbose-transcripts", "--min-silence-length", "1.5"),
            [new(595, 600), new(603, 606), new(620, 620.6)],
            s =>
            {
                s.Add(597, Seg(3.0, " Chapter one.", confidence: 0.3));
                s.Add(620.3, Seg(1.0, " some fresh words"));
            });

        // The label carries the actually decoded length: split at 620.3, window end 628 -> 7.7 s.
        var tailLine = Assert.Single(log, l => l.StartsWith($"probe {7.7:0.#}s@0:10:20.30 (tail)"));
        Assert.Contains($"{1.0:0.0}-{3.0:0.0}", tailLine); // Whisper's own 0-based timestamp for the fresh segment
        Assert.DoesNotContain("Chapter one", tailLine); // that segment was reused, not re-decoded
    }

    [Fact]
    public async Task OverlappingProbe_FullyReusedWindow_LogsNothingAndDoesNotTranscribe()
    {
        // Near the end of the file the probe window is capped at the file's duration (3600 s),
        // so two close-together candidates can end up with the very same (capped) window end -
        // window 2 is then fully contained in window 1's cache and no Whisper call happens at
        // all. A fully-reused window is the common case for a fine-grained candidate scan, so
        // it logs nothing at all rather than a line (or a segment dump) per occurrence.
        // Chapter one is scripted at low confidence so the overlap-sequence skip stays out of
        // the way and the fully-contained window is actually visited.
        var (_, log, audio) = await DetectWithLogAsync(
            Options("--min-silence-length", "1.5"),
            [new(3585, 3590), new(3593, 3595)],
            s => s.Add(3590, Seg(0.5, " Chapter one.", confidence: 0.3)));

        Assert.DoesNotContain(log, l => l.Contains("0:59:55"));
        Assert.DoesNotContain(3595.0, audio.DecodeStarts);
    }

    [Fact]
    public async Task Verbose_LogsProbeHeadersButOmitsSegments_UnlessVerboseTranscriptsIsSet()
    {
        // The first probe (window at 0) transcribes "Chapter one." Plain --verbose must log the
        // probe's "<length>@<timestamp>" header but not the segment text; --verbose-transcripts
        // appends the segments after a colon. The chapter-detected line (not a transcript) shows
        // under both.
        List<Silence> silences = [new(595, 600)];
        Action<ScriptedTranscriber> script = s => s.Add(0, Seg(0.5, " Chapter one."));

        var (_, plain, _) = await DetectWithLogAsync(Options(), silences, script);
        var (_, full, _) = await DetectWithLogAsync(Options("-T"), silences, script);

        var plainHeader = Assert.Single(plain, l => l.StartsWith("probe ") && l.Contains("@0:00:00.00"));
        Assert.EndsWith("@0:00:00.00", plainHeader);      // header ends at the timestamp, no segment dump
        // No probe line dumps segments (the "(p=" marker) - the language-detection line has its
        // own "(p=" and is not a transcript, so scope the check to probe lines.
        Assert.DoesNotContain(plain, l => l.StartsWith("probe ") && l.Contains("(p="));

        var fullHeader = Assert.Single(full, l => l.StartsWith("probe ") && l.Contains("@0:00:00.00"));
        Assert.Contains("Chapter one.", fullHeader);

        Assert.Contains(plain, l => l.Contains("chapter 1 detected"));
    }

    [Fact]
    public async Task OverlappingProbe_FlagsADetectionThatSpansTheCacheFreshMerge()
    {
        // The "Chapter" segment (abs 606.5) is reused from window 1's cache; the number word
        // "one." only exists in window 2's freshly decoded tail (from the 617.3 split point).
        // Extracting the chapter number therefore has to reach across the cache/fresh boundary -
        // FindPhraseMatches must flag that detection, and DetectAsync must log it.
        //
        // The number is scripted as audible only in a short decode, which is what leaves the merge
        // as the sole route to it: window 1 hears a bare "Chapter", and the unnumbered re-read that
        // triggers re-frames it at 15 s and 45 s - both too wide here, so they come back with the
        // same unreadable announcement and the merge still has to do the work.
        //
        // The geometry is picked so that a tail decode both happens and stays short: the seam sits
        // at 617.3, and the silence candidate's 25 s window puts window
        // 2's end at 631 - past the 630 window 1's single encoder pass covers, so window 1 cannot
        // read ahead over it, and the tail is only 13.7 s.
        var (result, log, _) = await DetectWithLogAsync(
            Options("--min-silence-length", "1.5"),
            [new(595, 600), new(603, 606), new(617, 617.6)],
            s =>
            {
                s.Add(600, Seg(6.5, " Chapter"));        // abs 606.5 - reused by window 2
                s.AddWithin(14, 617.3, Seg(0, " one.")); // abs 617.3 - fresh tail of window 2
            });

        AssertChapters([new DetectedChapter(1, 606.25)], result.Chapters);
        Assert.Contains(log, l => l.Contains("chapter 1 spans the reused/fresh transcript merge"));
    }

    [Fact]
    public void PlanWindowEnd_KeepsTheNaturalEnd_WhenTheWindowsDoNotOverlap()
    {
        // The next window starts far past this one's natural end (612), and the only stored
        // silence ([608, 608.6], mid 608.3) lies before it - nothing in the (612, 617]
        // forward search either, so the window keeps its natural length.
        var end = GapPlanning.PlanWindowEnd(
            600, 1200, 12, 3600, [new(608, 608.6)], [], jingle: false);
        Assert.Equal(612, end);
    }

    [Fact]
    public void PlanWindowEnd_SnapsASharedBorder_ToTheSeamNearestTheBorder()
    {
        // Both [604, 605] (mid 604.5) and [610, 611] (mid 610.5) lie within window 2
        // ([603, 615]); the shared border (window 1's natural end, 612) snaps to the nearer
        // mid-point, shortening window 1's decode.
        var end = GapPlanning.PlanWindowEnd(
            600, 603, 12, 3600, [new(604, 605), new(610, 611)], [], jingle: false);
        Assert.Equal(610.5, end);
    }

    [Fact]
    public void PlanWindowEnd_ExtendsAWindow_WhenTheOnlySeamLiesBeyondItsNaturalEnd()
    {
        // The only target ([613, 614]) sits past window 1's natural end (612) - the plan may
        // move the border itself, so window 1 is extended to the mid-point (613.5) and the
        // next window's fresh decode will start exactly there. No hole, no mid-word cut.
        var end = GapPlanning.PlanWindowEnd(
            600, 606, 12, 3600, [new(613, 614)], [], jingle: false);
        Assert.Equal(613.5, end);
    }

    [Fact]
    public void PlanWindowEnd_FallsBackToTheNaturalEnd_WhenNoSeamTargetExists()
    {
        // The only silences lie at or before window 2's start - nothing inside (606, 618] to
        // snap to, so the shared border stays the natural end: the raw-border joint is the
        // only kind of overlap the plan leaves behind.
        var end = GapPlanning.PlanWindowEnd(
            600, 606, 12, 3600, [new(595, 600), new(601, 606)], [], jingle: false);
        Assert.Equal(612, end);
    }

    [Fact]
    public void PlanWindowEnd_SnapsEachBorder_AgainstTheNextWindowsNaturalSpan()
    {
        // On-the-fly planning searches the next window's *natural* span: window 1's border
        // (612) snaps to [610, 611]'s mid-point (nearest inside window 2's [606, 618]);
        // window 2's border (618) snaps to [616, 617]'s mid-point inside window 3's
        // [612, 624] - each end decided independently, right before its own probe.
        List<Silence> silences = [new(610, 611), new(616, 617)];
        var first = GapPlanning.PlanWindowEnd(600, 606, 12, 3600, silences, [], jingle: false);
        var second = GapPlanning.PlanWindowEnd(606, 612, 12, 3600, silences, [], jingle: false);
        Assert.Equal(610.5, first);
        Assert.Equal(616.5, second);
    }

    [Fact]
    public void PlanWindowEnd_LeavesABorderAlone_WhenTheNextWindowEndsWithinThisOne()
    {
        // Clamped to the file end, both windows end at 3600, so the later one is fully
        // contained in the earlier - there is no shared border to snap even though a target
        // would be available; the contained window is served from cache instead.
        var end = GapPlanning.PlanWindowEnd(
            3590, 3595, 12, 3600, [new(3596, 3597)], [], jingle: false);
        Assert.Equal(3600, end);
    }

    [Fact]
    public void PlanWindowEnd_UsesVadRegions_OnlyInJingleMode()
    {
        // A VAD non-speech region is a valid seam target when the VAD pre-pass ran, but
        // without it there's no VAD data worth trusting - the same layout must snap only
        // when the region is present.
        List<NonSpeechRegion> regions = [new(608, 609)];
        var plain = GapPlanning.PlanWindowEnd(600, 606, 12, 3600, [], regions, jingle: false);
        var jingle = GapPlanning.PlanWindowEnd(600, 606, 12, 3600, [], regions, jingle: true);
        Assert.Equal(612, plain);
        Assert.Equal(608.5, jingle);
    }

    [Fact]
    public void PlanWindowEnd_ExtendsAStandAloneEnd_ToASeamShortlyAfterIt()
    {
        // This window's end (12) does not lie inside the next window ([600, 612]) - no shared
        // border - but a silence sits just past it: the end is extended to its mid-point
        // (13.5) so the decode stops word-safely.
        var end = GapPlanning.PlanWindowEnd(
            0, 600, 12, 3600, [new(13, 14)], [], jingle: false);
        Assert.Equal(13.5, end);
    }

    [Fact]
    public void PlanWindowEnd_KeepsAStandAloneEnd_WhenNoSeamLiesWithinTheForwardSearch()
    {
        // Neither a target whose mid-point lies before the natural end (extension only - the
        // window must never shrink below its natural span) nor one past the 5 s search limit
        // ([18, 19], mid-point 18.5 > 17) may move the end: it stays at the natural 12.
        var end = GapPlanning.PlanWindowEnd(
            0, null, 12, 3600, [new(8, 9), new(18, 19)], [], jingle: false);
        Assert.Equal(12, end);
    }

    [Fact]
    public void PlanWindowEnd_StandAloneEndSnap_UsesVadRegionsOnlyInJingleMode()
    {
        List<NonSpeechRegion> regions = [new(13, 14)];
        var plain = GapPlanning.PlanWindowEnd(0, null, 12, 3600, [], regions, jingle: false);
        var jingle = GapPlanning.PlanWindowEnd(0, null, 12, 3600, [], regions, jingle: true);
        Assert.Equal(12, plain);
        Assert.Equal(13.5, jingle);
    }

    [Fact]
    public void PlanWindowEnd_StandAloneEndSnap_StopsAtTheFileEnd()
    {
        // The natural end is already clamped to the file end - there is no room to extend
        // into, so the forward search must come up empty regardless of nearby targets.
        var end = GapPlanning.PlanWindowEnd(
            3592, null, 12, 3600, [new(3596, 3598)], [], jingle: false);
        Assert.Equal(3600, end);
    }

    [Fact]
    public async Task ProbeWindow_DecodesToTheForwardSnappedEnd()
    {
        // Integration check for the stand-alone end snap: the probe window at 597 has no
        // overlapping neighbor, and the stored 0.8 s silence at [623.2, 624] (sub-threshold,
        // seam target only) sits within the 5 s forward search past the natural end (622) -
        // the decode itself must run 26.6 s, up to the mid-point (623.6).
        var (result, _, audio) = await DetectFullAsync(
            Options("--min-silence-length", "1.5"),
            [new(595, 600), new(623.2, 624)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(597, Seg(3.3, " Chapter two."));
            });

        AssertChapters([new(1, 0.25), new(2, 600.05)], result.Chapters);
        Assert.Contains(audio.DecodeWindows,
            w => w.Start == 597 && w.Duration is { } d && Math.Abs(d - 26.6) < 0.01);
    }

    [Fact]
    public void TrimLeadingNonSpeech_ChainsThroughALeadingSilenceAndItsJingle()
    {
        // Whisper timestamped the "Chapter two" segment from where the pause before the jingle
        // began (830.3), lumping the silence and the jingle music into the segment's head. The
        // real speech onset is the far end of that non-speech run: silence [830.3, 831.0] hands
        // off to the abutting jingle region [830.5, 836], so the corrected start is 836.
        var segments = new List<TranscriptSegment> { new(830.3, 850, " Chapter two.", 1.0) };
        var trimmed = JingleGeometry.TrimLeadingNonSpeech(
            segments, [new(830.3, 831.0)], [new(830.5, 836)], jingle: true);
        Assert.Equal(836, trimmed[0].StartSeconds);
        Assert.Equal(850, trimmed[0].EndSeconds); // the end is never touched
    }

    [Fact]
    public void TrimLeadingNonSpeech_LeavesASegmentThatOpensWithSpeech()
    {
        // The nearest silence ([45, 49]) ends before the segment starts (50) - it does not lead
        // the segment, so the start is untouched.
        var segments = new List<TranscriptSegment> { new(50, 60, " already talking.", 1.0) };
        var trimmed = JingleGeometry.TrimLeadingNonSpeech(
            segments, [new(45, 49)], [], jingle: false);
        Assert.Equal(50, trimmed[0].StartSeconds);
    }

    [Fact]
    public void TrimLeadingNonSpeech_UsesJingleRegions_OnlyInJingleMode()
    {
        // A VAD non-speech region leads the segment, but only when the VAD pre-pass ran is
        // that data trusted: without it the region is ignored and the start stays put.
        var segments = new List<TranscriptSegment> { new(830, 850, " Chapter two.", 1.0) };
        var plain = JingleGeometry.TrimLeadingNonSpeech(segments, [], [new(830, 835)], jingle: false);
        var jingle = JingleGeometry.TrimLeadingNonSpeech(segments, [], [new(830, 835)], jingle: true);
        Assert.Equal(830, plain[0].StartSeconds);
        Assert.Equal(835, jingle[0].StartSeconds);
    }

    [Fact]
    public void TrimLeadingNonSpeech_ToleratesASilenceThatStartsJustAfterTheSegment()
    {
        // Whisper's segment start (830) can sit a hair before silencedetect's frame-precise
        // onset (830.4); the small tolerance still recognises the silence as leading it.
        var segments = new List<TranscriptSegment> { new(830, 840, " Chapter two.", 1.0) };
        var trimmed = JingleGeometry.TrimLeadingNonSpeech(
            segments, [new(830.4, 835)], [], jingle: false);
        Assert.Equal(835, trimmed[0].StartSeconds);
    }

    [Fact]
    public async Task JingleMark_IsAnchoredFromTheRealOnset_WhenWhisperTimestampsFromTheLeadingSilence()
    {
        // The reported bug: Whisper emits the "Chapter two" announcement as one segment that
        // opens with the pause before the jingle and the jingle itself, so its timestamp (830.3)
        // sits back in that non-speech - well before the real spoken phrase (836). A false
        // in-text pause earlier in chapter one ([820, 823]) is the candidate whose wide jingle
        // window reaches the announcement. Taken at face value the segment start would resolve
        // to no jingle region and no preceding silence, dumping the mark 0.5 s before that false
        // pause (822.5) - back in chapter one's narration. Correcting the start to the real onset
        // (836) instead finds the jingle region [830.5, 836], whose leading silence [830.3, 831.0]
        // is the true anchor - the classic "silence then jingle" shape. --mark-before-jingle's own
        // backward walk lands right at that silence's own end (831.0, the jingle's true start
        // per silencedetect's amplitude-based measurement) rather than at VAD's slightly jittery
        // speech-segment boundary a moment earlier (830.5) - the same silence-anchoring preference
        // default-mode placement already has via LeadingSilence, and the same shape validated in
        // JingleWithLeadingSilence_MarksInsideThatHush_.... The mark lead then backs it 0.25 s into
        // the 0.7 s hush, to 830.75.
        var result = await DetectAsync(
            Options("--quick-marks", "--mark-before-jingle"),
            [new(595, 600), new(820, 823), new(830.3, 831.0)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(823, new TranscriptSegment(7.3, 27.3, " Chapter two.", 1.0)); // abs [830.3, 850.3]
            },
            new FakeVad { Speech = [new(0, 830.5), new(836, 3600)] });

        AssertContainsChapter(new DetectedChapter(2, 830.75), result.Chapters);
        Assert.DoesNotContain(result.Chapters, c => c.Number == 2 && c.TimeSeconds < 830);
    }

    [Fact]
    public async Task Scan_SnapsChunkBordersToSeams_AndBridgesAPhraseAcrossTheSeam()
    {
        // Probe finds chapters 1 and 3, so Scan transcribes [0.5, 1200]. The first chunk's
        // natural border (600.5) snaps to the stored 1 s silence at [598, 599] (mid-point
        // 598.5), so the chunks abut there with no overlap - and the announcement straddles
        // that very seam: "Chapter" ends just before it (in chunk 1), "two." starts just after
        // it (in chunk 2). Only the bridge - chunk 1's trailing segments carried into chunk
        // 2's matching - can assemble the phrase; chunk 1 alone has no number, chunk 2 alone
        // no phrase. The detection must be flagged as seam-spanning in the log, and the chunk
        // decodes must reflect the snapped borders: [0.5, 598.5] and [598.5, 1197.5] (the
        // second border snaps to [1195, 1200]'s mid-point).
        //
        // The lighter --upgrade-model is what puts seam snapping in play at all: it is the one
        // setting that switches the shifted re-read off, and where a re-read may follow, neither
        // attempt snaps (see TranscribeRegionAsync's snapSeams).
        var (result, log, audio) = await DetectWithLogAsync(
            Options("--model", "large", "--upgrade-model", "tiny",
                    "--min-silence-length", "1.5"),
            [new(598, 599), new(1195, 1200)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(1200, Seg(0.2, " Chapter three."));
                s.Add(0.25, Seg(596.75, " Chapter"));  // chunk 1: ends at abs 599, just before the seam
                s.Add(598.5, Seg(0.3, " two."));       // chunk 2: the number, just after the seam
            });

        Assert.False(result.GapRemains);
        Assert.Equal([1, 2, 3], result.Chapters.Select(c => c.Number));
        AssertContainsChapter(new DetectedChapter(2, 596.75), result.Chapters); // pinpointed at the phrase start
        Assert.Contains(log, l => l.Contains("chapter 2 detection spans a Scan chunk seam"));
        // The first chunk starts at chapter 1's own mark, so it is read back rather than written
        // out: precise marking measures that mark and need not land on a round number.
        var gapStart = result.Chapters[0].TimeSeconds;
        Assert.Contains(audio.DecodeWindows,
            w => Math.Abs(w.Start - gapStart) < 1e-6 &&
                 w.Duration is { } d && Math.Abs(d - (598.5 - gapStart)) < 0.01);
        Assert.Contains(audio.DecodeWindows,
            w => w.Start == 598.5 && w.Duration is { } d && Math.Abs(d - 599) < 0.01);
    }

    [Fact]
    public async Task Scan_LeavesItsChunkBordersUnsnapped_WhenAShiftedReReadMayFollow()
    {
        // The same fixture as the seam test above, minus the lighter --upgrade-model - so a shifted
        // re-read is on the table, and the borders stop snapping. Snapping searches 30 s either way
        // while the two attempts' natural borders lie only 15 s apart, so both could snap to the
        // silence at [598, 599] and the re-read would hand its second chunk exactly the framing
        // that had already failed. Unsnapped, chunk 1 of the re-read is chunk 1 of the first
        // attempt moved one shift later, and the same holds for every chunk after it.
        var (result, log, audio) = await DetectWithLogAsync(
            Options("--min-silence-length", "1.5"),
            [new(598, 599), new(1195, 1200)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(1200, Seg(0.2, " Chapter three."));
                // Chapter 2 is nowhere: Scan must fail so that Re-scan runs at all.
            });

        Assert.True(result.GapRemains);
        var gapStart = result.Chapters[0].TimeSeconds;
        Assert.Contains(audio.DecodeWindows,
            w => Math.Abs(w.Start - gapStart) < 1e-6 &&
                 w.Duration is { } d && Math.Abs(d - DetectionTuning.GapChunkSeconds) < 1e-6);
        Assert.Contains(audio.DecodeWindows,
            w => Math.Abs(w.Start - (gapStart + DetectionTuning.RescanShiftSeconds)) < 1e-6 &&
                 w.Duration is { } d && Math.Abs(d - DetectionTuning.GapChunkSeconds) < 1e-6);
        Assert.Contains(log, l => l.StartsWith("Re-scan: "));
    }

    [Fact]
    public async Task Scan_IgnoresAReDetectionOfTheGapsBoundingChapter_AtBothEnds()
    {
        // Probe finds chapters 1 (at 0.5, no preceding silence) and 3 (at 500, pinned to the
        // silence [495, 500] preceding it), leaving a gap for chapter 2. Scan then transcribes
        // [0.5, 500] as one chunk and - besides the genuine "Chapter two." - Whisper also
        // re-hears chapter 1's own announcement right at the chunk's start and chapter 3's right
        // near its end, at 495 instead of 3's real, silence-anchored position of 500. Neither
        // re-detection is new information: both must be ignored outright, with no log line and
        // without nudging chapter 3's mark from 500 down to 495.
        var (result, log, _) = await DetectWithLogAsync(
            Options(),
            [new(495, 500)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(500, Seg(0.3, " Chapter three."));
                // Scan's gap chunk, decoded from 0.25 (chapter 1's mark): re-hears chapter 1
                // at its own start, a genuine chapter 2 in the middle, and chapter 3 again near
                // the end (earlier than its real mark).
                s.Add(0.25, Seg(0.25, " Chapter one."), Seg(200.25, " Chapter two."), Seg(494.75, " Chapter three."));
            });

        Assert.False(result.GapRemains);
        AssertChapters([new(1, 0.25), new(2, 200.25), new(3, 500.05)], result.Chapters);
        Assert.DoesNotContain(log, l => l.Contains("chapter 1 found in gap"));
        Assert.DoesNotContain(log, l => l.Contains("chapter 3 found in gap"));
        Assert.Contains(log, l => l.Contains("chapter 2 found in gap"));
    }

    [Fact]
    public async Task Scan_MovesTheBarWhileAChunkIsStillBeingTranscribed()
    {
        // The same single-chunk gap as the test above ([0.25, 500], well under the 600 s chunk
        // size), and that is what makes the assertion mean anything: with one chunk in the phase
        // nothing has been *booked* while it runs, so any progress at all during it can only have
        // come from the recognizer's own position inside the call. Without it the bar stands still
        // for the whole transcription - minutes of audio per chunk, and on a long gap the better
        // part of an hour with nothing to show that the run is alive.
        var audio = new FakeAudioSource { Silences = [new(495, 500)] };
        var transcriber = new ScriptedTranscriber(audio);
        transcriber.Add(0, Seg(0.5, " Chapter one."));
        transcriber.Add(500, Seg(0.3, " Chapter three."));
        transcriber.Add(0.25, Seg(0.25, " Chapter one."), Seg(200.25, " Chapter two."),
                        Seg(494.75, " Chapter three."));

        var work = new WorkTracker();
        var duringScan = new List<double>();
        transcriber.OnProgressReported = _ =>
        {
            if (work.PhaseLabel == "Scan")
                duringScan.Add(work.Fraction);
        };

        var detector = new ChapterDetector(Options(), audio, transcriber);
        var result = await detector.DetectAsync(
            _file, Info, work, new DetectionLog(_ => { }, null), CancellationToken.None);

        AssertChapters([new(1, 0.25), new(2, 200.25), new(3, 500.05)], result.Chapters);
        // The first segment already moves it, and nothing ever moves it backwards - which is not
        // free, since a segment end may overshoot the audio it was given or arrive out of order
        // once a window re-segments. The samples after the chunk's own transcription (mark
        // refinement, still inside the phase) repeat its final value rather than adding to it.
        Assert.NotEmpty(duringScan);
        Assert.True(duringScan[0] > 0, $"the bar had not moved at the first report ({duringScan[0]})");
        Assert.Equal(duringScan.OrderBy(f => f), duringScan);
        Assert.All(duringScan, f => Assert.InRange(f, 0.0, 1.0));
    }

    [Fact]
    public async Task Scan_RetriesAStoredSilenceItsOwnChunkTranscriptSkippedEntirely()
    {
        // Same shape as the test above (chapter 1 at 0.5, chapter 3 pinned to the silence
        // [495, 500]), but this time the gap chunk's own transcript has nothing at all covering
        // the stored silence [200, 206] - Whisper silently dropped chapter 2's phrase there
        // rather than mis-hearing it. The gap retry re-scans padded around just that silence
        // ([198, 208]) rather than the whole [2.5, 494.5] stretch between the chunk's two
        // segments, and finds the phrase in the first 8 s sub-chunk (starting at 198).
        var (result, log, _) = await DetectWithLogAsync(
            Options("--quick-marks"),
            [new(495, 500), new(200, 206)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(500, Seg(0.3, " Chapter three."));
                // Scan's gap chunk [0.5, 500]: re-hears both endpoints, nothing in between.
                s.Add(0.5, Seg(0, " Chapter one."), Seg(494, " Chapter three."));
                // Gap retry around the qualifying silence, padded to [198, 208].
                s.Add(198, Seg(2, " Chapter 2."));
            });

        Assert.False(result.GapRemains);
        AssertChapters([new(1, 0.25), new(2, 199.75), new(3, 500.05)], result.Chapters);
        Assert.Contains(log, l => l.Contains("chapter 2 found in gap"));
    }

    [Fact]
    public async Task Scan_GapRetryStaysScopedToTheSilence_NotTheWholeStretchBetweenSegments()
    {
        // Same shape again, but the two segments bracketing the qualifying silence [200, 206]
        // are now far apart (the chunk's own transcript is sparse, as real narration sometimes
        // is over a full 600 s Scan chunk) - the raw stretch between them spans almost the
        // whole [0.5, 500] chunk. The retry must stay scoped to just the silence's own bounds
        // (padded to [198, 208]), not fan out across that whole stretch: only decode starts near
        // 198 are expected, never something like 50 or 300 that a naive "scan the whole gap"
        // approach would also have visited.
        var (result, _, audio) = await DetectFullAsync(
            Options("--quick-marks"),
            [new(495, 500), new(200, 206)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(500, Seg(0.3, " Chapter three."));
                s.Add(0.5, Seg(0, " Chapter one."), Seg(494, " Chapter three."));
                s.Add(198, Seg(2, " Chapter 2."));
            });

        Assert.False(result.GapRemains);
        AssertChapters([new(1, 0.25), new(2, 199.75), new(3, 500.05)], result.Chapters);
        Assert.DoesNotContain(audio.DecodeStarts, d => d is > 210 and < 490);
        // The one qualifying silence needs at most two 8 s sub-chunks (198 and 204) to cover its
        // padded [198, 208] span - nowhere near what scanning the ~492 s raw gap would take.
        Assert.True(audio.DecodeStarts.Count <= 6);
    }

    [Fact]
    public void ComputeNonSpeechRegions_MergesRegionsSeparatedByAShortSpeechBlip()
    {
        // A 0.5 s "speech" blip - short enough to be a vocal-like transient inside otherwise
        // instrumental jingle music, not a genuine return to narration - must not fragment the
        // jingle: the non-speech regions on either side merge into one spanning both.
        var speech = new List<SpeechSegment> { new(0, 100), new(110, 110.5), new(122.5, 200) };
        Assert.Equal(
            [new NonSpeechRegion(100, 122.5)],
            JingleGeometry.ComputeNonSpeechRegions(speech));
    }

    [Fact]
    public void ComputeNonSpeechRegions_ChainsMergesAcrossSeveralShortBlips()
    {
        // Three non-speech spans separated by two short blips (0.2 s, 0.9 s) must all merge into
        // a single region, not just the first pair.
        var speech = new List<SpeechSegment>
        {
            new(0, 100), new(108, 108.2), new(115, 115.9), new(120, 200),
        };
        Assert.Equal(
            [new NonSpeechRegion(100, 120)],
            JingleGeometry.ComputeNonSpeechRegions(speech));
    }

    [Fact]
    public void ComputeNonSpeechRegions_DoesNotMerge_WhenTheSpeechGapIsNotShort()
    {
        // A 1.5 s speech segment is a genuine return to narration, not VAD noise - the two
        // non-speech regions must stay separate (both are otherwise well above the 2 s floor).
        var speech = new List<SpeechSegment> { new(0, 100), new(110, 111.5), new(120, 200) };
        Assert.Equal(
            [new NonSpeechRegion(100, 110), new NonSpeechRegion(111.5, 120)],
            JingleGeometry.ComputeNonSpeechRegions(speech));
    }

    [Fact]
    public void ComputeNonSpeechRegions_DropsRegionsShorterThanTheFloor_AfterMerging()
    {
        // A 1.2 s non-speech region, not adjacent to anything it could merge with, never reaches
        // the 2 s floor and must be dropped entirely rather than surfacing as a candidate.
        var speech = new List<SpeechSegment> { new(0, 100), new(101.2, 200) };
        Assert.Empty(JingleGeometry.ComputeNonSpeechRegions(speech));
    }

    [Fact]
    public void ComputeNonSpeechRegions_KeepsRegionsAtExactlyTheThresholds()
    {
        // A speech gap of exactly 1 s is not "shorter than" the merge threshold, so the regions
        // must stay separate; a region of exactly 2 s is not "shorter than" the drop floor, so it
        // must be kept.
        var speech = new List<SpeechSegment> { new(0, 100), new(102, 103), new(105, 200) };
        Assert.Equal(
            [new NonSpeechRegion(100, 102), new NonSpeechRegion(103, 105)],
            JingleGeometry.ComputeNonSpeechRegions(speech));
    }

    [Fact]
    public void ComputeNonSpeechRegions_DropsMergedNarrationCadence_WithNoLongContiguousRun()
    {
        // Ordinary narration cadence, no jingle anywhere: short words (0.3 s) separated by short
        // pauses (0.4-0.7 s). Each speech blip is under the 1 s merge threshold, so the four
        // inter-word gaps chain-merge into one 3.4 s span - which clears the 2 s floor if the
        // floor is (wrongly) measured against the merged span. But no single contiguous non-speech
        // run reaches 2 s (the longest is 0.7 s), so this must be dropped: it is reading rhythm,
        // not a jingle. This is the false-positive class that flooded Probe with bogus VAD
        // candidates before the floor was moved onto the longest run.
        var speech = new List<SpeechSegment>
        {
            new(0, 1), new(1.4, 1.7), new(2.4, 2.7), new(3.4, 3.7), new(4.4, 5),
        };
        Assert.Empty(JingleGeometry.ComputeNonSpeechRegions(speech));
    }

    [Fact]
    public void ComputeNonSpeechRegions_KeepsFragmentedJingle_WithOneLongContiguousRun()
    {
        // A genuine jingle fragmented by a single 0.4 s misfire into a 5 s music block and a
        // trailing 0.6 s tail. The blip merges the two, and although the 0.6 s tail is itself far
        // below the floor, the 5 s run carries the merged region over it - a real jingle is kept
        // even when mildly fragmented, because it does contain a long continuous non-speech block.
        var speech = new List<SpeechSegment> { new(0, 100), new(105, 105.4), new(106, 200) };
        Assert.Equal(
            [new NonSpeechRegion(100, 106)],
            JingleGeometry.ComputeNonSpeechRegions(speech));
    }

    [Fact]
    public void AdvancePastNonSpeech_ReturnsTheOnsetOfTheNextQualifyingSpeechSegment()
    {
        // Scanning forward from a point still in non-speech (100) must land on the start of the
        // next speech segment that clears the noise floor (105), not its end or midpoint.
        var speech = new List<SpeechSegment> { new(0, 50), new(105, 108) };
        Assert.Equal(105, JingleGeometry.AdvancePastNonSpeech(100, speech, 0.1));
    }

    [Fact]
    public void AdvancePastNonSpeech_SkipsATransientShorterThanTheFloor()
    {
        // A 0.05 s blip between the scan start and the true onset is detector noise-floor jitter,
        // not real speech - it must be skipped over (resuming the scan from its own end), landing
        // on the next, genuine (3 s) segment's start instead.
        var speech = new List<SpeechSegment> { new(0, 50), new(102, 102.05), new(105, 108) };
        Assert.Equal(105, JingleGeometry.AdvancePastNonSpeech(100, speech, 0.1));
    }

    [Fact]
    public void AdvancePastNonSpeech_ChainsPastSeveralConsecutiveTransients()
    {
        // Three short blips (0.05, 0.08, 0.05 s) in a row must all be skipped as noise, not just
        // the first one, before reaching the genuine onset at 106.
        var speech = new List<SpeechSegment>
        {
            new(0, 50), new(102, 102.05), new(103, 103.08), new(104, 104.05), new(106, 109),
        };
        Assert.Equal(106, JingleGeometry.AdvancePastNonSpeech(100, speech, 0.1));
    }

    [Fact]
    public void AdvancePastNonSpeech_TreatsExactlyTheFloorDurationAsGenuine()
    {
        // A blip of exactly the floor duration is not "shorter than" it, so it counts as a real
        // onset rather than being skipped as a transient. Integer bounds avoid the binary64
        // rounding that a literal like 102.1 - 102 would introduce right at the comparison edge.
        var speech = new List<SpeechSegment> { new(0, 50), new(102, 103) };
        Assert.Equal(102, JingleGeometry.AdvancePastNonSpeech(100, speech, 1.0));
    }

    [Fact]
    public void AdvancePastNonSpeech_DoesNotMoveBackward_WhenAlreadyInsideAQualifyingSegment()
    {
        // The scan start (105) already sits inside a genuine speech segment (100-110) - it must be
        // returned unchanged, never snapped back to the segment's own start.
        var speech = new List<SpeechSegment> { new(0, 50), new(100, 110) };
        Assert.Equal(105, JingleGeometry.AdvancePastNonSpeech(105, speech, 0.1));
    }

    [Fact]
    public void AdvancePastNonSpeech_DoesNotMoveBackward_WhenAlreadyInsideATransient()
    {
        // The scan start (100.02) happens to fall inside a short (0.05 s) blip that would
        // otherwise be treated as a transient - "never move backward past a point already at or
        // beyond a segment" wins over transient-skipping, so it is still returned unchanged rather
        // than being pushed forward past this blip to the next segment.
        var speech = new List<SpeechSegment> { new(0, 50), new(100, 100.05), new(105, 108) };
        Assert.Equal(100.02, JingleGeometry.AdvancePastNonSpeech(100.02, speech, 0.1));
    }

    [Fact]
    public void AdvancePastNonSpeech_ReturnsNull_WhenTheSpeechDataDoesNotReachFarEnough()
    {
        // No segment in the given data ends after the scan start (100), so there is nothing left
        // to find a qualifying onset in - the caller must be told to look further (e.g. by
        // re-running VAD over a wider window) rather than being given a false answer.
        var speech = new List<SpeechSegment> { new(0, 50) };
        Assert.Null(JingleGeometry.AdvancePastNonSpeech(100, speech, 0.1));
    }

    [Fact]
    public void Normalize_SortsAndDropsDuplicatesAndRegressions()
    {
        var raw = new List<DetectedChapter>
        {
            new(3, 1200), new(1, 10), new(3, 1500), new(2, 600), new(2, 900), new(1, 1400),
        };
        Assert.Equal(
            [new(1, 10), new(2, 600), new(3, 1200)],
            GapPlanning.Normalize(raw));
    }

    [Fact]
    public void Normalize_KeepsTheManyChaptersAfterAnOutlier_RatherThanTheOutlier()
    {
        // "Die Cyber-Brutzellen", 2026-08-01, reduced: chapter 14's announcement was read as 40,
        // and a greedy left-to-right filter then measured chapters 15 onward against 40 and threw
        // every one of them away - 15 correctly placed marks lost to one misheard digit, leaving a
        // 17 h book marked as far as its chapter 13 and no further. The longest ascending
        // subsequence drops the outlier instead.
        var raw = new List<DetectedChapter> { new(13, 100), new(40, 200) };
        for (var n = 15; n <= 29; n++)
            raw.Add(new DetectedChapter(n, 200 + (n - 14) * 100));

        var kept = GapPlanning.Normalize(raw);
        Assert.Equal([13, .. Enumerable.Range(15, 15)], kept.Select(c => c.Number));
        Assert.DoesNotContain(kept, c => c.Number == 40);
    }

    [Fact]
    public void NormalizeWithOutliers_ReportsWhatItHadToDrop()
    {
        // The dropped half is what the repair works from, and it has to separate the two reasons an
        // entry falls out: a genuine outlier (40 here) versus a duplicate of a number that survived
        // (the second chapter 2), which is one announcement heard twice and nothing to repair.
        var raw = new List<DetectedChapter>
        {
            new(1, 10), new(2, 100), new(40, 200), new(2, 250), new(3, 400),
        };
        var (kept, dropped) = GapPlanning.NormalizeWithOutliers(raw);
        Assert.Equal([1, 2, 3], kept.Select(c => c.Number));
        Assert.Equal([(40, 200.0), (2, 250.0)], dropped.Select(c => (c.Number, c.TimeSeconds)));
    }

    [Fact]
    public void Normalize_BreaksTiesTowardTheEarliestEntries()
    {
        // Two equally long ascending subsequences exist here (chapter 2 at 600 or at 900). Keeping
        // the earlier one is what preserves the older "of two detections of one chapter, keep the
        // earlier" rule: overlapping probe windows hear one announcement twice, and the earlier
        // reading is the one that saw it rather than its tail.
        var raw = new List<DetectedChapter> { new(1, 10), new(2, 600), new(2, 900), new(3, 1200) };
        Assert.Equal(
            [new(1, 10), new(2, 600), new(3, 1200)],
            GapPlanning.Normalize(raw));
    }

    [Fact]
    public void NumberBounds_AdmitsOnlyWhatTheSequenceCanHold()
    {
        // Open above: anything up to SuspectGapMinMissing chapters of hole is ordinary.
        var open = new NumberBounds(13);
        Assert.True(open.Admits(14));
        Assert.True(open.Admits(13 + 1 + SuspectGapMinMissing));
        Assert.False(open.Admits(13 + 2 + SuspectGapMinMissing));
        Assert.False(open.Admits(13));

        // Bounded above: exactly the open interval, however small the hole.
        var closed = new NumberBounds(13, 15);
        Assert.True(closed.Admits(14));
        Assert.False(closed.Admits(15));
        Assert.False(closed.Admits(16));
    }

    [Fact]
    public void NumberBounds_LeavesTheBoundsThemselvesUnquestioned()
    {
        // A number equal to either bound is an overlapping window re-hearing an announcement that
        // is already marked, not a mishearing - and re-reading one could only "improve" by
        // inventing a neighbouring number for an announcement that is not it.
        var bounds = new NumberBounds(13, 15);
        Assert.False(bounds.WorthQuestioning(13));
        Assert.False(bounds.WorthQuestioning(15));
        Assert.False(bounds.WorthQuestioning(14));
        Assert.True(bounds.WorthQuestioning(40));
        Assert.True(bounds.WorthQuestioning(2));
    }

    [Fact]
    public void NumberBounds_NamesTheSoleCandidate_OnlyWhenThereIsExactlyOne()
    {
        Assert.Equal(14, new NumberBounds(13, 15).SoleCandidate(new HashSet<int>()));
        Assert.Null(new NumberBounds(13, 16).SoleCandidate(new HashSet<int>()));
        // A number already carried by another mark is no longer a possibility, which can narrow a
        // wider hole down to a single answer.
        Assert.Equal(15, new NumberBounds(13, 16).SoleCandidate(new HashSet<int> { 14 }));
        // Nothing left at all, and the open-ended case, both have no sole candidate.
        Assert.Null(new NumberBounds(13, 15).SoleCandidate(new HashSet<int> { 14 }));
        Assert.Null(new NumberBounds(13).SoleCandidate(new HashSet<int>()));
    }

    [Fact]
    public void FindGaps_ReportsMissingRegions()
    {
        var chapters = new List<DetectedChapter> { new(2, 500), new(3, 900), new(6, 2000) };
        Assert.Equal(
            [new(0, 500), new(900, 2000)],
            GapPlanning.FindGaps(chapters, Duration, expectedStartChapter: 1));
    }

    /// <summary>One refinement probe transcript reading the given text, as
    /// <see cref="PreciseMarkResult.PhraseReadings"/> carries them.</summary>
    private static List<TranscriptSegment> Reading(string text)
        => [new TranscriptSegment(0, 2, text, 0.9)];

    /// <summary>Runs <see cref="RefinedNumberVote.Recount"/> against the run's default (English)
    /// profile and uncapped phrase matching.</summary>
    private int? Recount(IReadOnlyList<List<TranscriptSegment>> readings, int heard, NumberBounds bounds)
        => RefinedNumberVote.Recount(
            readings, Options().DefaultProfile,
            (segments, profile, merge) => PhraseMatching.FindPhraseMatches(segments, profile, merge),
            heard, bounds, phraseAbs: 100, log: null);

    [Fact]
    public void RefinedNumberVote_OverrulesTheWindow_WhenItsOwnProbesAgreeOnAnotherNumber()
    {
        // The "Die Cyber-Brutzellen" shape: the window that found the announcement read it as 40,
        // every probe framed on the announcement itself read 14, and 14 fits the hole between
        // chapters 13 and 15 that the window was searching.
        var readings = Enumerable.Repeat(Reading("Chapter fourteen."), 4).ToList();
        Assert.Equal(14, Recount(readings, heard: 40, new NumberBounds(13, 15)));
    }

    /// <summary>
    /// And the same re-read is what settles a hallucinated prefix the sequence had no reason to turn
    /// down. A window reading "Two chapter three" while the sequence stands at 1 offers a 2 that
    /// fits, so no rival is ever reached - but the probes framed on the announcement itself say
    /// "Chapter three", and the vote is taken on what they say rather than on which wording read
    /// them. That is the after-refinement check the number-first wording is affordable under.
    /// </summary>
    [Fact]
    public void RefinedNumberVote_OverrulesAHallucinatedPrefix()
    {
        var readings = Enumerable.Repeat(Reading("Chapter three."), 4).ToList();
        Assert.Equal(3, Recount(readings, heard: 2, new NumberBounds(1, null)));
    }

    [Fact]
    public void RefinedNumberVote_KeepsQuiet_WhenTheProbesAgreeWithTheWindow()
    {
        // The overwhelmingly common case, and the one that must cost nothing: 267 of the 271 marks
        // in the ten-book run of 2026-08-01 looked like this.
        var readings = Enumerable.Repeat(Reading("Chapter fourteen."), 5).ToList();
        Assert.Null(Recount(readings, heard: 14, new NumberBounds(13, 15)));
    }

    [Fact]
    public void RefinedNumberVote_KeepsQuiet_BelowTheMinimumOrWithoutAMajority()
    {
        // Too thin a sample to overrule anything with.
        var thin = Enumerable.Repeat(Reading("Chapter fourteen."), RefinedNumberVoteMinimum - 1).ToList();
        Assert.Null(Recount(thin, heard: 40, new NumberBounds(13, 15)));

        // Enough readings, but they do not agree among themselves. A refinement that drifts across
        // several numbers has no verdict to offer, and half the votes is not a majority.
        List<List<TranscriptSegment>> split =
            [Reading("Chapter fourteen."), Reading("Chapter fourteen."),
             Reading("Chapter forty."), Reading("Chapter forty.")];
        Assert.Null(Recount(split, heard: 40, new NumberBounds(13, 15)));
    }

    [Fact]
    public void RefinedNumberVote_KeepsQuiet_WhenItsVerdictDoesNotFitTheSequence()
    {
        // Unanimous and still refused: the hole between chapters 13 and 15 cannot hold a 20, so a
        // reading of 20 is no better founded than the 40 it would replace. Same rule the mender
        // adopts by - the sequence is what makes a re-read evidence rather than a second guess.
        var readings = Enumerable.Repeat(Reading("Chapter twenty."), 5).ToList();
        Assert.Null(Recount(readings, heard: 40, new NumberBounds(13, 15)));
    }

    /// <summary>Runs the colliding-mark settling with a scripted re-read, recording what it was
    /// asked.</summary>
    /// <param name="chapters">The finished sequence, ascending in time.</param>
    /// <param name="reread">What the audio says when asked, or null for "nothing usable".</param>
    private static async Task<(List<DetectedChapter> Chapters, List<string> Log, List<(int Number, NumberBounds Bounds)> Asked)>
        SettleAsync(List<DetectedChapter> chapters, Func<NumberBounds, int?>? reread = null)
    {
        var log = new List<string>();
        var asked = new List<(int, NumberBounds)>();
        var settled = await ChapterDetector.SettleCollidingMarksAsync(
            chapters, expectedStartChapter: null, log.Add,
            (mark, bounds, _) =>
            {
                asked.Add((mark.Number, bounds));
                return Task.FromResult(reread?.Invoke(bounds));
            },
            CancellationToken.None);
        return (settled, log, asked);
    }

    [Fact]
    public async Task CollidingMarks_AreSettledByReadingTheAnnouncementAgain()
    {
        // "Paula Monti" (2026-07-31): chapter 13's announcement was read as "chapitre 12" by Probe
        // and correctly as 13 by Scan, leaving two marks a hundredth of a second apart. Neither
        // number is an outlier - both continue the sequence between 11 and 14 - so nothing else in
        // the detector has anything to say about it; only the geometry gives it away.
        var (chapters, log, asked) = await SettleAsync(
            [new(11, 100), new(12, 200), new(13, 200.01), new(14, 300)],
            _ => 13);

        Assert.Equal([11, 13, 14], chapters.Select(c => c.Number));
        Assert.Equal(200.01, chapters.Single(c => c.Number == 13).TimeSeconds);
        // Asked with the room the rest of the book leaves at that position, both candidates
        // excluded - which is what makes either answer acceptable and the question worth asking.
        Assert.Equal([(12, new NumberBounds(11, 14))], asked);
        Assert.Contains(log, l => l.Contains("chapters 12 and 13 0.01 s apart") &&
                                  l.Contains("one announcement read two ways"));
    }

    [Fact]
    public async Task CollidingMarks_KeepTheFirstReading_WhenTheReReadConfirmsIt()
    {
        // The mirror case, and the reason the re-read has to decide rather than a rule of thumb
        // about which pass to believe: here it is the later find that is wrong.
        var (chapters, _, _) = await SettleAsync(
            [new(11, 100), new(12, 200), new(13, 200.01), new(14, 300)],
            _ => 12);

        Assert.Equal([11, 12, 14], chapters.Select(c => c.Number));
        Assert.Equal(200, chapters.Single(c => c.Number == 12).TimeSeconds);
    }

    [Fact]
    public async Task CollidingMarks_FallBackToTheMoreConfidentReading_WhenTheReReadSettlesNothing()
    {
        // A tiebreak, not evidence - but it beats leaving a player two chapter entries at the same
        // position, and it is deterministic.
        var (chapters, log, _) = await SettleAsync(
            [new(11, 100), new(12, 200, 0.4), new(13, 200.01, 0.9), new(14, 300)]);

        Assert.Equal([11, 13, 14], chapters.Select(c => c.Number));
        Assert.Contains(log, l => l.Contains("re-reading settled nothing") &&
                                  l.Contains("keeping chapter 13"));
    }

    [Fact]
    public async Task CollidingMarks_LeaveOrdinarilySpacedChaptersAlone()
    {
        // The other side of the threshold: nothing is asked and nothing is dropped for chapters
        // that merely follow each other closely. A short chapter is still a chapter.
        var (chapters, log, asked) = await SettleAsync(
            [new(11, 100), new(12, 200), new(13, 260), new(14, 300)], _ => 12);

        Assert.Equal([11, 12, 13, 14], chapters.Select(c => c.Number));
        Assert.Empty(asked);
        Assert.Empty(log);
    }

    [Fact]
    public async Task CollidingMarks_KeepLookingAfterSettlingOne()
    {
        // Three marks on one announcement: dropping the loser of the first pair must not hide the
        // pair the winner then forms with what follows it.
        var (chapters, _, asked) = await SettleAsync(
            [new(11, 100), new(12, 200), new(13, 200.01), new(14, 200.02), new(15, 400)],
            _ => null);

        Assert.Equal([11, 12, 15], chapters.Select(c => c.Number));
        Assert.Equal(2, asked.Count);
    }

    /// <summary>Runs the sequence repair with a scripted re-read, recording what it was asked.</summary>
    private static async Task<(List<DetectedChapter> Chapters, List<string> Log, List<(int Number, NumberBounds Bounds)> Asked)>
        RepairAsync(List<DetectedChapter> found, Func<NumberBounds, int?>? reread = null)
    {
        var log = new List<string>();
        var asked = new List<(int, NumberBounds)>();
        var chapters = await ChapterDetector.RepairSequenceOutliersAsync(
            found, expectedStartChapter: null, log.Add,
            (outlier, bounds, _) =>
            {
                asked.Add((outlier.Number, bounds));
                return Task.FromResult(reread?.Invoke(bounds));
            },
            CancellationToken.None);
        return (chapters, log, asked);
    }

    [Fact]
    public async Task SequenceRepair_RenumbersFromTheSequenceAlone_WhenOnlyOneNumberFits()
    {
        // The whole point of doing this after Probe rather than during it: chapter 15 at 200 and
        // everything above it were not yet known when the announcement at 150 was misread as 40.
        // Between 13 and 15 there is exactly one number to be had, so no audio need be consulted.
        var (chapters, log, asked) = await RepairAsync(
            [new(13, 100), new(40, 150), new(15, 200), new(16, 300)]);

        Assert.Equal([13, 14, 15, 16], chapters.Select(c => c.Number));
        Assert.Equal(150, chapters.Single(c => c.Number == 14).TimeSeconds);
        Assert.Empty(asked);
        Assert.Contains(log, l => l.Contains("chapter 40 at 0:02:30.00 contradicts its neighbours") &&
                                  l.Contains("the gap between chapters 13 and 15"));
        Assert.Contains(log, l => l.Contains("chapter 14 the only number that fits"));
    }

    [Fact]
    public async Task SequenceRepair_AsksTheAudio_WhenTheSequenceLeavesSeveralPossibilities()
    {
        // A two-chapter hole cannot be resolved by arithmetic, so the audio is asked again - now
        // held to a far tighter rule than anything available when the number was first read.
        var (chapters, _, asked) = await RepairAsync(
            [new(13, 100), new(40, 150), new(16, 200)], bounds => bounds.Admits(15) ? 15 : null);

        Assert.Equal([13, 15, 16], chapters.Select(c => c.Number));
        Assert.Equal([(40, new NumberBounds(13, 16))], asked);
    }

    [Fact]
    public async Task SequenceRepair_DropsAnOutlierItCannotPlace_WithoutTouchingTheRest()
    {
        // Nothing to renumber it to and the audio offers nothing either. The mark is lost, which is
        // exactly what Normalize alone would have done - the chapters around it are not.
        var (chapters, log, _) = await RepairAsync([new(13, 100), new(40, 150), new(16, 200)]);

        Assert.Equal([13, 16], chapters.Select(c => c.Number));
        Assert.Contains(log, l => l.Contains("chapter 40 at 0:02:30.00 could not be placed in the sequence"));
    }

    [Fact]
    public async Task SequenceRepair_LeavesADuplicateAlone()
    {
        // A second detection of a number already in the sequence is one announcement heard by two
        // overlapping windows. Normalize drops the later one and there is nothing to repair, so the
        // audio must not be asked about it.
        var (chapters, _, asked) = await RepairAsync([new(1, 10), new(2, 100), new(2, 120), new(3, 200)]);

        Assert.Equal([1, 2, 3], chapters.Select(c => c.Number));
        Assert.Equal(100, chapters.Single(c => c.Number == 2).TimeSeconds);
        Assert.Empty(asked);
    }

    [Fact]
    public async Task SequenceRepair_LeavesAnAscendingSequenceUntouched()
    {
        List<DetectedChapter> found = [new(1, 10), new(2, 100), new(3, 200)];
        var (chapters, log, asked) = await RepairAsync(found);

        Assert.Equal(found, chapters);
        Assert.Empty(asked);
        Assert.Empty(log);
    }

    [Fact]
    public async Task SequenceRepair_RefusesARereadThatCollidesWithAChapterAlreadyPlaced()
    {
        // The re-read is not infallible, and a number another mark already carries would make the
        // sequence ambiguous rather than repaired - so it is refused and the outlier dropped.
        var (chapters, _, _) = await RepairAsync(
            [new(13, 100), new(40, 150), new(16, 200), new(17, 300)], _ => 16);

        Assert.Equal([13, 16, 17], chapters.Select(c => c.Number));
    }

    /// <summary>Runs the named-mark echo sweep, collecting its log.</summary>
    /// <param name="chapters">The sequence, ascending in time.</param>
    /// <param name="named">The file's named marks.</param>
    private static (List<DetectedChapter> Chapters, List<string> Log) DropEchoes(
        List<DetectedChapter> chapters, List<DetectedMark> named, int? expectedStartChapter = null)
    {
        var log = new List<string>();
        return (ChapterDetector.DropNamedMarkEchoes(chapters, named, expectedStartChapter, log.Add), log);
    }

    /// <summary>An epilogue mark for the echo-sweep tests.</summary>
    /// <param name="timeSeconds">Where it sits.</param>
    private static DetectedMark Epilogue(double timeSeconds)
        => new("epilogue", "Epilogue", timeSeconds);

    [Fact]
    public void NamedMarkEchoes_DropTheYearThatHeadsAnEpilogue()
    {
        // "Corsa nello spazio" (2026-08-06) in miniature: its epilogue is headed "Epilogo / 2179 /
        // Spazio profondo", and under --chapter-phrase none the year is an announcement by
        // definition. Nothing about the number gives it away - it was heard perfectly, by every
        // probe - so it reached the written file as a chapter mark 2.86 s behind the epilogue's own.
        var (chapters, log) = DropEchoes(
            [new(64, 63524), new(65, 63780), new(2179, 65939.97)], [Epilogue(65937.11)]);

        Assert.Equal([64, 65], chapters.Select(c => c.Number));
        Assert.Contains(log, l => l.Contains("chapter 2179 at 18:18:59.97, 2.86 s from the epilogue mark") &&
                                  l.Contains("dropped as part of that announcement"));
    }

    [Fact]
    public void NamedMarkEchoes_KeepAChapterThatContinuesTheSequence()
    {
        // Proximity alone proves nothing. A book may well start its next chapter seconds after a
        // prologue ends, and that chapter's number says so by continuing the sequence.
        var (chapters, log) = DropEchoes(
            [new(1, 100), new(2, 1202)], [new("prologue", "Prologue", 1200)]);

        Assert.Equal([1, 2], chapters.Select(c => c.Number));
        Assert.Empty(log);
    }

    [Fact]
    public void NamedMarkEchoes_KeepANumberThatMerelyFitsNowhere()
    {
        // The other half. An implausible number away from any named mark is the ordinary mishearing
        // every other defence works on, and one that survived all of them is likelier a real chapter
        // with undetected ones in front of it than a phantom - so it keeps its mark.
        var (chapters, log) = DropEchoes([new(1, 100), new(90, 1202)], [Epilogue(3000)]);

        Assert.Equal([1, 90], chapters.Select(c => c.Number));
        Assert.Empty(log);
    }

    [Fact]
    public void NamedMarkEchoes_NeverDropTheSequencesOwnFirstChapter()
    {
        // A split-book part legitimately starting at chapter 12 has no chapter below it, so its
        // lower bound is the assumption "1" rather than a measurement - and every such book would
        // fail the fit test on that alone.
        var (chapters, _) = DropEchoes([new(12, 1202), new(13, 2000)], [new("prologue", "Prologue", 1200)]);

        Assert.Equal([12, 13], chapters.Select(c => c.Number));
    }

    [Fact]
    public void NamedMarkEchoes_LeaveAFileWithoutNamedMarksUntouched()
    {
        List<DetectedChapter> found = [new(1, 100), new(90, 1202)];
        var (chapters, log) = DropEchoes(found, []);

        Assert.Same(found, chapters);
        Assert.Empty(log);
    }

    [Fact]
    public void FindGaps_OpensNoGapBeneathAnUnverifiedNumber()
    {
        // The 25 minutes "Corsa nello spazio" spent transcribing the stretch between its last real
        // chapter and a spoken year. A number the mender could not corroborate keeps its mark but
        // does not get to say that everything under it is missing.
        List<DetectedChapter> chapters =
            [new(1, 100), new(2, 200), new(2179, 3000, 1.0, NumberUnverified: true)];

        Assert.Empty(GapPlanning.FindGaps(chapters, Duration));
        var (highest, missing) = GapPlanning.ChapterProgress(chapters);
        Assert.Equal([2], highest);
        Assert.Empty(missing);
    }

    [Fact]
    public void ChapterProgress_ReportsNothingMissing_WhenNothingAtAllIsCorroborated()
    {
        // Measured on a 38-minute clip of "Corsa nello spazio" (2026-08-07): Probe found the year
        // heading the epilogue and nothing else, so there was no corroborated span for anything to
        // be missing from - and the progress line still announced 2114 missing chapters. The number
        // is reported, because a mark really was written under it; the shortfall is not.
        var (highest, missing) = GapPlanning.ChapterProgress(
            [new(2179, 2189, 1.0, NumberUnverified: true)], expectedStartChapter: 65);

        Assert.Equal([2179], highest);
        Assert.Empty(missing);
    }

    [Fact]
    public void FindGaps_StillOpensTheGapsAroundAnUnverifiedNumber()
    {
        // Skipped, not filtered out: the entry is a real position in the book, so its neighbours
        // must not be paired across it either. The genuine hole at 3 is still raised.
        List<DetectedChapter> chapters =
            [new(2, 200), new(4, 400), new(2179, 3000, 1.0, NumberUnverified: true), new(2180, 3200)];

        Assert.Equal([new GapPlanning.GapRegion(200, 400)], GapPlanning.FindGaps(chapters, Duration));
    }

    [Fact]
    public async Task ANumberNothingCouldMend_KeepsItsMark_AndDeclaresNothingMissing()
    {
        // Every re-framing hears "Chapter ninety" too, so the mender has nothing better to offer and
        // leaves the reading alone. Before this, the sequence took that at face value and committed
        // Scan to transcribing everything between chapters 1 and 90 in search of the 88 it thought
        // were missing.
        var (result, log, _) = await DetectWithLogAsync(
            Options("--quick-marks"),
            [new(595, 600)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(600, Seg(2.5, " Chapter ninety."));
            });

        AssertChapters([new(1, 0.25), new(90, 602.25)], result.Chapters);
        Assert.False(result.GapRemains);
        Assert.Empty(result.MissingNumbers);
        Assert.Contains(log, l => l.Contains("no sequence-continuing number read"));
        Assert.Contains(log, l => l.Contains("chapter 90 still does not fit the sequence after re-reading") &&
                                  l.Contains("chapters under it not counted as missing"));
    }

    [Fact]
    public async Task AChapterMarkOnAnEpiloguesOwnHeading_IsDroppedByTheRealPipeline()
    {
        // The unit tests above settle the rule; this one settles the wiring, which they cannot -
        // that the file's named marks are in hand at both the stage before Re-probe and the one
        // after Scan. Scripted as "Corsa nello spazio" reads: the epilogue's heading runs on into
        // a number, and under a bare-number reading that number is an announcement by definition.
        // The cap is raised past the real book's 2179 so that the echo rule is what drops the mark:
        // DefaultChapterCount would otherwise discard it several steps earlier and leave this test
        // proving nothing about the wiring it exists for.
        var (result, log, _) = await DetectWithLogAsync(
            Options("--quick-marks", "--max-chapter-number", "5000"),
            [new(595, 600), new(1195, 1200)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(600, Seg(0.4, " Chapter two."));
                s.Add(1200, Seg(2.5, " Epilogue."), Seg(4.0, " Chapter 2179."));
            });

        Assert.Equal([1, 2], result.Chapters.Select(c => c.Number));
        Assert.Single(result.NamedMarks);
        Assert.False(result.GapRemains);
        Assert.Contains(log, l => l.Contains("from the epilogue mark") &&
                                  l.Contains("dropped as part of that announcement"));
    }

    [Fact]
    public void FindGaps_SkipsLeadingRegion_WhenFirstChapterIsNearTheStart()
    {
        // Even with an expected start of 1, a first chapter within the first 10 s is taken as-is
        // (e.g. a book starting mid-series) rather than triggering a Scan search.
        var chapters = new List<DetectedChapter> { new(2, 8) };
        Assert.Empty(GapPlanning.FindGaps(chapters, Duration, expectedStartChapter: 1));
    }

    [Fact]
    public async Task AutoLanguage_ResolvesProfileFromDetection_AndAppliesItThroughoutTheFile()
    {
        // No --lang given: the default is "auto". The transcriber "detects" German with high
        // confidence from the first probe window, so the whole file - including the gap-fill
        // pass - must be parsed as German ("Erstes Kapitel" / "Zweites Kapitel").
        var (result, transcriber) = await DetectWithTranscriberAsync(
            Options(),
            [new(595, 600)],
            s =>
            {
                s.DetectedLanguage = ("de", 0.9f);
                s.Add(0, Seg(0.5, " Erstes Kapitel."));
                s.Add(600, Seg(0.3, " Zweites Kapitel."));
            });

        AssertChapters([new(1, 0.25), new(2, 600.05)], result.Chapters);
        Assert.Equal("de", result.Profile.Language);
        Assert.Equal("Kapitel", result.Profile.Title);
        Assert.Equal("de", result.DetectedLanguage);
        Assert.Equal(0.9, result.DetectedProbability, 3);
        Assert.Equal(1, transcriber.DetectLanguageCalls);
        Assert.Equal(["de"], transcriber.LanguageChanges);
    }

    [Fact]
    public async Task AutoLanguage_SamplesNarration_NotTheFilesOpeningSeconds()
    {
        // The regression this whole path exists for ("Das Mutantenkorps", 2026-08-03): the language
        // sample used to be the first probe window, i.e. the file from 0 - which on an audiobook is
        // label music often enough to matter. It must now come from somewhere inside the book.
        var (_, transcriber, audio) = await DetectFullAsync(
            Options(),
            [new(595, 600)],
            s =>
            {
                s.DetectedLanguage = ("de", 0.9f);
                s.Add(0, Seg(0.5, " Erstes Kapitel."));
            });

        Assert.Equal(1, transcriber.DetectLanguageCalls);
        Assert.Equal(0.2 * Duration, audio.DecodeStarts[0]); // the first anchor, a fifth in
        Assert.NotEqual(0.0, audio.DecodeStarts[0]);
    }

    [Fact]
    public async Task AutoLanguage_ReProbesElsewhere_UntilOneSampleClearsTheThreshold()
    {
        // Two weak answers, then a confident one. The confident one settles the file and stops the
        // loop, so the two doubtful readings never get a vote - which is the point of re-probing
        // rather than voting on whatever the first sample happened to say.
        var (result, transcriber, audio) = await DetectFullAsync(
            Options(),
            [new(595, 600)],
            s =>
            {
                s.LanguageAnswers.Enqueue(("en", 0.35f));
                s.LanguageAnswers.Enqueue(("fr", 0.5f)); // still under 0.6
                s.LanguageAnswers.Enqueue(("de", 0.88f));
                s.Add(0, Seg(0.5, " Erstes Kapitel."));
                s.Add(600, Seg(0.3, " Zweites Kapitel."));
            });

        Assert.Equal(3, transcriber.DetectLanguageCalls);
        Assert.Equal("de", result.Profile.Language);
        Assert.Equal(0.88, result.DetectedProbability, 3);
        AssertChapters([new(1, 0.25), new(2, 600.05)], result.Chapters);
        // Three different places in the book, not three reads of the same audio.
        Assert.Equal(3, audio.DecodeStarts.Take(3).Distinct().Count());
    }

    [Fact]
    public async Task AutoLanguage_StopsAfterFiveProbes_AndTakesThePluralityVote()
    {
        // Nothing ever clears 0.6, so all five samples vote. German leads 3-2 and wins, even though
        // no single probe was ever confident enough to be believed on its own.
        var (result, transcriber) = await DetectWithTranscriberAsync(
            Options(),
            [new(595, 600)],
            s =>
            {
                s.LanguageAnswers.Enqueue(("de", 0.4f));
                s.LanguageAnswers.Enqueue(("en", 0.55f));
                s.LanguageAnswers.Enqueue(("de", 0.45f));
                s.LanguageAnswers.Enqueue(("en", 0.3f));
                s.LanguageAnswers.Enqueue(("de", 0.5f));
                s.LanguageAnswers.Enqueue(("de", 0.99f)); // never asked for: the cap is five
                s.Add(0, Seg(0.5, " Erstes Kapitel."));
                s.Add(600, Seg(0.3, " Zweites Kapitel."));
            });

        Assert.Equal(5, transcriber.DetectLanguageCalls);
        Assert.Equal("de", result.Profile.Language);
        Assert.Equal("Kapitel", result.Profile.Title);
        Assert.Equal("de", result.DetectedLanguage);
        Assert.Equal(0.5, result.DetectedProbability, 3); // the winner's best, not the vote's
        AssertChapters([new(1, 0.25), new(2, 600.05)], result.Chapters);
    }

    [Fact]
    public async Task AutoLanguage_FallsBackToEnglish_WhenTheVoteTies()
    {
        // Two languages level at the top: the samples disagree and nothing breaks the deadlock, so
        // there is no signal to act on and English is all that is left.
        var (result, _) = await DetectWithTranscriberAsync(
            Options(),
            [new(595, 600)],
            s =>
            {
                s.LanguageAnswers.Enqueue(("tr", 0.3f));
                s.LanguageAnswers.Enqueue(("nl", 0.45f));
                s.LanguageAnswers.Enqueue(("tr", 0.35f));
                s.LanguageAnswers.Enqueue(("nl", 0.4f));
                s.LanguageAnswers.Enqueue(("pl", 0.2f));
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(600, Seg(0.3, " Chapter two."));
            });

        AssertChapters([new(1, 0.25), new(2, 600.05)], result.Chapters);
        Assert.Equal("en", result.Profile.Language);
        Assert.Equal("Chapter", result.Profile.Title);
        // The strongest raw guess is still reported, disagreeing with the profile on purpose.
        Assert.Equal("nl", result.DetectedLanguage);
        Assert.Equal(0.45, result.DetectedProbability, 3);
    }

    [Fact]
    public async Task AutoLanguage_UnanimousWeakSamples_StillDecideTheLanguage()
    {
        // The single-sample version of this was the old behaviour's worst case: one 0.36 reading
        // was enough to run a German book as English. Five agreeing reads of the same strength now
        // decide it, none of them individually convincing.
        var (result, _) = await DetectWithTranscriberAsync(
            Options(),
            [new(595, 600)],
            s =>
            {
                s.DetectedLanguage = ("de", 0.36f);
                s.Add(0, Seg(0.5, " Erstes Kapitel."));
                s.Add(600, Seg(0.3, " Zweites Kapitel."));
            });

        AssertChapters([new(1, 0.25), new(2, 600.05)], result.Chapters);
        Assert.Equal("de", result.Profile.Language);
    }

    [Fact]
    public async Task ExplicitLang_NeverCallsLanguageDetection()
    {
        var (result, transcriber) = await DetectWithTranscriberAsync(
            Options("--lang", "de"),
            [new(595, 600)],
            s =>
            {
                s.Add(0, Seg(0.5, " Erstes Kapitel."));
                s.Add(600, Seg(0.3, " Zweites Kapitel."));
            });

        AssertChapters([new(1, 0.25), new(2, 600.05)], result.Chapters);
        Assert.Equal(0, transcriber.DetectLanguageCalls);
        Assert.Null(result.DetectedLanguage);
        Assert.Equal("de", result.Profile.Language);
        Assert.Equal(["de"], transcriber.LanguageChanges); // still (re-)asserted defensively
    }

    /// <summary>Runs --verify against the given pre-existing chapter marks and script.</summary>
    private async Task<VerifyResult> VerifyAsync(
        CliOptions options, IReadOnlyList<Chapter> existingChapters, Action<ScriptedTranscriber> script)
        => (await VerifyWithTranscriberAsync(options, existingChapters, script)).Result;

    /// <summary>Runs --verify, also returning the transcriber for language-detection assertions
    /// and the tracker for progress-bar assertions.</summary>
    private async Task<(VerifyResult Result, ScriptedTranscriber Transcriber, WorkTracker Tracker)> VerifyWithTranscriberAsync(
        CliOptions options, IReadOnlyList<Chapter> existingChapters, Action<ScriptedTranscriber> script)
    {
        var audio = new FakeAudioSource();
        var transcriber = new ScriptedTranscriber(audio);
        script(transcriber);
        var detector = new ChapterDetector(options, audio, transcriber);
        var info = new MediaInfo(Duration, (long)Duration, existingChapters.Count,
            ExistingChapterList: existingChapters);
        var tracker = new WorkTracker();
        var result = await detector.VerifyExistingChaptersAsync(_file, info, tracker, default, CancellationToken.None);
        return (result, transcriber, tracker);
    }

    [Fact]
    public async Task Verify_ConfirmsExistingMarks_WhenThePhraseAndNumberAreFoundNearby()
    {
        // Marks at 10 s and 610 s; --verify probes 10 s before each, so windows start at 0 and 600.
        var result = await VerifyAsync(
            Options(),
            [new Chapter(10, "Chapter 1"), new Chapter(610, "Chapter 2")],
            s =>
            {
                s.Add(0, Seg(10, " Chapter 1."));
                s.Add(600, Seg(10, " Chapter 2."));
            });

        Assert.True(result.Passed);
        Assert.Equal(2, result.Checked);
        Assert.Equal(0, result.Failed);
    }

    /// <summary>--fix's whole point: a mark whose announcement checks out but sits well away
    /// from it is moved onto it, instead of the run only reporting that it checked out.</summary>
    [Fact]
    public async Task VerifyFix_MovesAConfirmedMarkOntoItsAnnouncement()
    {
        // Mark at 10 s, window [0, 60), the announcement actually at 40 s.
        var result = await VerifyAsync(
            Options("--verify", "--fix"),
            [new Chapter(10, "Chapter 1")],
            s => s.Add(0, Seg(40, " Chapter 1.")));

        Assert.True(result.Passed);
        var mark = Assert.Single(result.Outcomes);
        Assert.NotNull(mark.CorrectedStartSeconds);
        Assert.Equal(40 - PinnedMarkLeadSeconds, mark.CorrectedStartSeconds!.Value, 2);
        // The confirmed chapter is handed on at the corrected position, so a run that also
        // gap-recovers writes the fixed marks rather than the old ones.
        Assert.Equal(40 - PinnedMarkLeadSeconds, Assert.Single(result.ConfirmedChapters).TimeSeconds, 2);
    }

    /// <summary>Rewriting a whole audiobook to move a mark by a tenth of a second is not worth
    /// it, and the figure being moved is inside the refinement's own accuracy anyway.</summary>
    [Fact]
    public async Task VerifyFix_LeavesAMarkAlreadyCloseEnoughAlone()
    {
        var result = await VerifyAsync(
            Options("--verify", "--fix"),
            [new Chapter(10, "Chapter 1")],
            s => s.Add(0, Seg(10.1, " Chapter 1.")));

        Assert.True(result.Passed);
        Assert.Null(Assert.Single(result.Outcomes).CorrectedStartSeconds);
    }

    /// <summary>A mark tens of seconds from its announcement is not one that drifted - it means
    /// something else, and dragging it onto the nearest matching phrase would destroy that.</summary>
    [Fact]
    public async Task VerifyFix_LeavesAMarkTooFarFromItsAnnouncementAlone()
    {
        var (result, _, _) = await VerifyWithTranscriberAsync(
            Options("--verify", "--fix"),
            [new Chapter(10, "Chapter 1")],
            s => s.Add(0, Seg(55, " Chapter 1.")));

        Assert.True(result.Passed);
        Assert.Null(Assert.Single(result.Outcomes).CorrectedStartSeconds);
        Assert.Equal(10, Assert.Single(result.ConfirmedChapters).TimeSeconds);
    }

    /// <summary>Without --fix, --verify reports and changes nothing - the behaviour every
    /// existing use of it relies on.</summary>
    [Fact]
    public async Task Verify_WithoutFix_NeverCorrectsAMark()
    {
        var result = await VerifyAsync(
            Options("--verify"),
            [new Chapter(10, "Chapter 1")],
            s => s.Add(0, Seg(40, " Chapter 1.")));

        Assert.True(result.Passed);
        Assert.Null(Assert.Single(result.Outcomes).CorrectedStartSeconds);
        Assert.Equal(10, Assert.Single(result.ConfirmedChapters).TimeSeconds);
    }

    [Fact]
    public async Task Verify_Fails_WhenThePhraseIsNotFoundNearby()
    {
        var result = await VerifyAsync(
            Options(),
            [new Chapter(10, "Chapter 1"), new Chapter(610, "Chapter 2")],
            s => s.Add(0, Seg(10, " Chapter 1."))); // nothing scripted near the second mark

        Assert.False(result.Passed);
        Assert.Equal(2, result.Checked);
        Assert.Equal(1, result.Failed);
    }

    [Fact]
    public async Task Verify_Fails_WhenTheNumberNearbyDoesNotMatch()
    {
        var result = await VerifyAsync(
            Options(),
            [new Chapter(10, "Chapter 1")],
            s => s.Add(0, Seg(10, " Chapter 2."))); // wrong number for this mark

        Assert.False(result.Passed);
        Assert.Equal(1, result.Checked);
        Assert.Equal(1, result.Failed);
    }

    [Fact]
    public async Task Verify_SkipsMarks_WithNoParseableExpectedNumber()
    {
        // "Intro" has no digit and no recognizable number word, so it cannot be checked;
        // with nothing else to disprove, verification passes trivially.
        var result = await VerifyAsync(Options(), [new Chapter(0, "Intro")], _ => { });

        Assert.True(result.Passed);
        Assert.Equal(0, result.Checked);
        Assert.Equal(0, result.Failed);
    }

    [Fact]
    public async Task Verify_UnderstandsSpelledOutNumbers_ForTheGivenLanguage()
    {
        var result = await VerifyAsync(
            Options("--lang", "de"),
            [new Chapter(10, "Erstes Kapitel")],
            s => s.Add(0, Seg(10, " Erstes Kapitel.")));

        Assert.True(result.Passed);
        Assert.Equal(1, result.Checked);
    }

    [Fact]
    public async Task Verify_ChecksTitlesWithTheNumberBehindTheChapterWord()
    {
        // The ordinary written form, and the one that used to make --verify report a file as
        // having nothing checkable at all: the number is the title's *second* word, which the
        // transcript-tail parser this once borrowed never looks at (see ExistingMarkTitle).
        // Everything here is about the count - one mark checked and confirmed rather than
        // silently passed over.
        var result = await VerifyAsync(
            Options(),
            [new Chapter(10, "Chapter Two: Seven Days Later")],
            s => s.Add(0, Seg(10, " Chapter 2.")));

        Assert.True(result.Passed);
        Assert.Equal(1, result.Checked);
        Assert.Equal(0, result.Failed);
    }

    [Fact]
    public async Task Verify_WithAutoLanguage_ResolvesLanguageUpfront_BeforeParsingAnyTitle()
    {
        // Both marks' titles are only parseable as German ordinals ("Erstes"/"Zweites") - not
        // as English number words. Resolving the language lazily, only after some mark's
        // title happened to parse under an "en" placeholder, would never get past the very first
        // mark here: its title fails to parse as English, so it would be skipped without ever
        // being decoded - and since decoding is what triggers language detection, "de" would
        // never be discovered, silently skipping every mark (Checked == 0, a false pass)
        // instead of verifying the book. Resolving upfront, from the first mark with a
        // decodable window regardless of its title, must check both.
        var (result, transcriber, _) = await VerifyWithTranscriberAsync(
            Options(),
            [new Chapter(10, "Erstes Kapitel"), new Chapter(610, "Zweites Kapitel")],
            s =>
            {
                s.DetectedLanguage = ("de", 0.9f);
                s.Add(0, Seg(10, " Erstes Kapitel."));
                s.Add(600, Seg(10, " Zweites Kapitel."));
            });

        Assert.True(result.Passed);
        Assert.Equal(2, result.Checked);
        Assert.Equal(0, result.Failed);
        // Detected once, upfront - not re-detected per mark.
        Assert.Equal(1, transcriber.DetectLanguageCalls);
    }

    [Fact]
    public async Task Verify_TracksHighestConfirmedChapter_AndCountsUnconfirmedOnesAsMissing()
    {
        // Chapter 2 fails to confirm; 1 and 3 do. Same display convention as Probe/3: the
        // tracker should read the highest *confirmed* number, with the unconfirmed one below it
        // counted as a "(-N)" gap - not the highest pre-existing mark regardless of outcome.
        var (_, _, tracker) = await VerifyWithTranscriberAsync(
            Options(),
            [new Chapter(10, "Chapter 1"), new Chapter(610, "Chapter 2"), new Chapter(1210, "Chapter 3")],
            s =>
            {
                s.Add(0, Seg(10, " Chapter 1."));
                // nothing scripted near the second mark - Chapter 2 will not confirm.
                s.Add(1200, Seg(10, " Chapter 3."));
            });

        Assert.Equal([3], tracker.HighestChapters);
        Assert.Equal(1, tracker.MissingChapters);
    }

    [Fact]
    public async Task Verify_RetriesLongGapsBetweenSegments_AndConfirmsIfThePhraseIsThere()
    {
        // Reproduces a real failure: the first-pass transcript of the --verify window has a
        // ~19 s gap between two segments (9.1-28.1, window-relative) where Whisper silently
        // skipped the chapter phrase entirely, even though detection's own original run over
        // this same audio found it. A focused re-transcribe of just that gap (padded by
        // VerifyGapPaddingSeconds on each side) should recover the phrase and confirm the mark.
        var result = await VerifyAsync(
            Options(),
            [new Chapter(10, "Chapter 2")],
            s =>
            {
                s.Add(0,
                    new TranscriptSegment(0, 9.1, " Something before."),
                    new TranscriptSegment(28.1, 44.2, " Something after."));
                // Gap [9.1, 28.1] padded by 2s on each side -> re-decoded from windowStart + 7.1.
                s.Add(7.1, Seg(5, " Chapter 2."));
            });

        Assert.True(result.Passed);
        Assert.Equal(1, result.Checked);
        Assert.Equal(0, result.Failed);
    }

    [Fact]
    public async Task Verify_ScansAGapInSeveralChunks_AndFindsThePhraseInALaterOne()
    {
        // The padded gap [7.1, 30.1] is scanned in overlapping 8s chunks stepped by 6s (8s minus
        // 2s overlap): 7.1, 13.1, 19.1, 25.1. The phrase sits only in the third chunk - proving
        // this is a genuine multi-chunk scan, not a lucky single re-transcribe of the whole gap
        // (which, per the real failure this feature fixes, is exactly the case Whisper can miss
        // by judging a long mixed chunk as non-speech on average).
        var result = await VerifyAsync(
            Options(),
            [new Chapter(10, "Chapter 2")],
            s =>
            {
                s.Add(0,
                    new TranscriptSegment(0, 9.1, " Something before."),
                    new TranscriptSegment(28.1, 44.2, " Something after."));
                s.Add(7.1, Seg(0, " still nothing here."));
                s.Add(13.1, Seg(0, " nor here."));
                s.Add(19.1, Seg(3, " Chapter 2."));
            });

        Assert.True(result.Passed);
        Assert.Equal(1, result.Checked);
        Assert.Equal(0, result.Failed);
    }

    [Fact]
    public async Task Verify_StillFails_WhenTheGapRetryAlsoFindsNothing()
    {
        // Same shape as the recovery case above, but the phrase genuinely is not there - the
        // gap retry must not manufacture a false confirmation.
        var result = await VerifyAsync(
            Options(),
            [new Chapter(10, "Chapter 2")],
            s => s.Add(0,
                new TranscriptSegment(0, 9.1, " Something before."),
                new TranscriptSegment(28.1, 44.2, " Something after.")));
        // No script entry for the gap retry decode (~7.1) - ScriptedTranscriber returns [].

        Assert.False(result.Passed);
        Assert.Equal(1, result.Checked);
        Assert.Equal(1, result.Failed);
    }

    [Fact]
    public void BuildGapRegions_BuildsOneRegion_ForAnInteriorUnconfirmedRun()
    {
        var marks = new List<ExistingMarkOutcome>
        {
            new(10, 1, true), new(30, 2, false), new(50, 3, true),
        };
        var plan = GapPlanning.BuildGapRegions(marks, Duration);

        Assert.Equal([new(10, 50, 1, 3)], plan.Regions);
        Assert.Null(plan.TrailingFrom);
        Assert.Empty(plan.TrailingTargets);
    }

    [Fact]
    public void BuildGapRegions_BuildsATrailingRegion_WhenTheLastCheckableMarkIsUnconfirmed()
    {
        var marks = new List<ExistingMarkOutcome> { new(10, 1, true), new(610, 2, false) };
        var plan = GapPlanning.BuildGapRegions(marks, Duration);

        Assert.Equal([new(10, Duration, 1, null)], plan.Regions);
        Assert.Equal(10, plan.TrailingFrom);
        Assert.Equal([2], plan.TrailingTargets);
    }

    [Fact]
    public void BuildGapRegions_GroupsConsecutiveUnconfirmedMarks_IntoOneRun()
    {
        var marks = new List<ExistingMarkOutcome>
        {
            new(10, 1, true), new(20, 2, false), new(30, 3, false), new(40, 4, true),
        };
        var plan = GapPlanning.BuildGapRegions(marks, Duration);

        Assert.Equal([new(10, 40, 1, 4)], plan.Regions);
    }

    [Fact]
    public void BuildGapRegions_KeepsSeparateRunsAsSeparateRegions()
    {
        var marks = new List<ExistingMarkOutcome>
        {
            new(10, 1, true), new(20, 2, false), new(30, 3, true), new(40, 4, false), new(50, 5, true),
        };
        var plan = GapPlanning.BuildGapRegions(marks, Duration);

        Assert.Equal([new(10, 30, 1, 3), new(30, 50, 3, 5)], plan.Regions);
    }

    [Fact]
    public void BuildGapRegions_AbsorbsAnUnparseableMark_WithoutBreakingTheRun()
    {
        // The middle mark has no parseable number (Confirmed is always false for those, but
        // that must not itself make the surrounding run look "broken" into two).
        var marks = new List<ExistingMarkOutcome>
        {
            new(10, 1, true), new(20, 2, false), new(25, null, false), new(30, 3, false), new(40, 4, true),
        };
        var plan = GapPlanning.BuildGapRegions(marks, Duration);

        Assert.Equal([new(10, 40, 1, 4)], plan.Regions);
    }

    [Fact]
    public void BuildGapRegions_ReturnsNoRegions_WhenEveryCheckableMarkIsConfirmed()
    {
        var marks = new List<ExistingMarkOutcome> { new(10, 1, true), new(610, 2, true) };
        var plan = GapPlanning.BuildGapRegions(marks, Duration);

        Assert.Empty(plan.Regions);
        Assert.Null(plan.TrailingFrom);
    }

    /// <summary>Runs gap-scoped recovery (DetectGapsAsync) against a hand-built VerifyResult.</summary>
    private async Task<(DetectionResult Result, FakeAudioSource Audio)> DetectGapsAsync(
        CliOptions options, VerifyResult verify, List<Silence> silences, Action<ScriptedTranscriber> script)
    {
        var audio = new FakeAudioSource { Silences = silences };
        var transcriber = new ScriptedTranscriber(audio);
        script(transcriber);
        var detector = new ChapterDetector(options, audio, transcriber);
        var result = await detector.DetectGapsAsync(_file, Info, new WorkTracker(), default, verify, CancellationToken.None);
        return (result, audio);
    }

    [Fact]
    public async Task DetectGapsAsync_RecoversAnInteriorGap_ViaAGapScopedProbe_AndTrustsConfirmedMarksVerbatim()
    {
        // Chapter 1 (@10) and chapter 3 (@50) were already confirmed by --verify; chapter 2
        // (mark @30) was not. Only the region between the two confirmed marks' own
        // timestamps [10, 50) is probed - a single synthetic candidate at its own start (10),
        // exactly like the whole-file case's own start-of-file candidate.
        var verify = new VerifyResult(false, 2, 1,
            [new(1, 10), new(3, 50)],
            [new(10, 1, true), new(30, 2, false), new(50, 3, true)],
            Options().DefaultProfile, null, 0);

        var (result, audio) = await DetectGapsAsync(
            Options("--quick-marks", "--min-silence-length", "1.5"),
            verify, [new(28, 30)],
            s => s.Add(30, Seg(0.3, " Chapter 2.")));

        Assert.False(result.GapRemains);
        AssertChapters([new(1, 10), new(2, 30.05), new(3, 50)], result.Chapters);
        // Confirmed marks are trusted verbatim - the only decodes are the gap region's own
        // synthetic start and the fresh tail past the seam its window shares with the silence
        // candidate behind it; nothing probes near the confirmed marks' own timestamps.
        Assert.Equal([10.0, 29.0], audio.DecodeStarts);
    }

    [Fact]
    public async Task DetectGapsAsync_RecoversATrailingGap_ViaTheScan_WhenAGapScopedProbeMissesIt()
    {
        // Chapter 3 (mark @1210, the last one in file order) was not confirmed. The phrase
        // sits 300 s into the decode starting at 610 - far past Probe's PhraseLatestStart rule
        // (and there is no anchor silence to rescue it, since none are scripted), so the
        // region-scoped Probe window [610, 622) rejects it; Scan has no such window-relative
        // timing rule, so its own chunk [610, 1210) still finds it - the trailing fallback is the
        // only mechanism that can notice a still-missing *trailing* chapter at all.
        var verify = new VerifyResult(false, 2, 1,
            [new(1, 10), new(2, 610)],
            [new(10, 1, true), new(610, 2, true), new(1210, 3, false)],
            Options().DefaultProfile, null, 0);

        var (result, _) = await DetectGapsAsync(
            Options(), verify, [],
            s => s.Add(610, Seg(300, " Chapter 3.")));

        Assert.False(result.GapRemains);
        Assert.Equal(3, result.Chapters.Count);
        Assert.Equal(3, result.Chapters[^1].Number);
    }

    [Fact]
    public async Task DetectGapsAsync_UpperBoundGuard_PreventsAGapScopedProbeFromDisplacingTheNextConfirmedChapter()
    {
        // A (contrived) Probe window inside the [10, 50) gap picks up chapter 3's own phrase -
        // exactly the failure mode the region's UpperNumber guard exists for: without it, this
        // would add a second, wrongly-timed chapter 3 entry that Normalize's earliest-timestamp-
        // wins rule would then prefer over the correctly confirmed one at 50.
        var verify = new VerifyResult(false, 2, 1,
            [new(1, 10), new(3, 50)],
            [new(10, 1, true), new(30, 2, false), new(50, 3, true)],
            Options().DefaultProfile, null, 0);

        var (result, _) = await DetectGapsAsync(
            Options(), verify, [],
            s => s.Add(10, Seg(0.3, " Chapter 3.")));

        // Chapter 3 must keep its correct, confirmed timestamp - not the gap probe's mistaken one.
        AssertContainsChapter(new DetectedChapter(3, 50), result.Chapters);
        // Chapter 2 was never actually found (only chapter 3's phrase was scripted, deliberately,
        // to isolate the guard) - the file correctly reports it as still missing rather than
        // silently accepting the wrong chapter 3 in its place.
        Assert.True(result.GapRemains);
        Assert.Contains(2, result.MissingNumbers);
    }

    /// <summary>Runs ResumeMissingMarksAsync against the given committed marks (as if probed
    /// from a ".missing-marks-..." file) and script.</summary>
    private async Task<(DetectionResult Result, FakeAudioSource Audio, ScriptedTranscriber Transcriber)> ResumeMissingMarksAsync(
        CliOptions options, IReadOnlyList<Chapter> existingChapters, List<Silence> silences, Action<ScriptedTranscriber> script)
    {
        var audio = new FakeAudioSource { Silences = silences };
        var transcriber = new ScriptedTranscriber(audio);
        script(transcriber);
        var detector = new ChapterDetector(options, audio, transcriber);
        var info = new MediaInfo(Duration, (long)Duration, existingChapters.Count,
            ExistingChapterList: existingChapters);
        var result = await detector.ResumeMissingMarksAsync(_file, info, new WorkTracker(), default, CancellationToken.None);
        return (result, audio, transcriber);
    }

    [Fact]
    public async Task ResumeMissingMarksAsync_RecoversAnInteriorGap_ViaAGapScopedProbe_AndTrustsCommittedMarksVerbatim()
    {
        // Chapters 1 (@10) and 3 (@50) are already committed on the tagged file; chapter 2 is
        // still missing between them. Only the gap [10, 50) is probed - a single synthetic
        // candidate at its own start (10), exactly like DetectGapsAsync's own gap-scoped region.
        // An explicit --lang sidesteps the upfront language-resolution decode near chapter 1's own
        // mark (@10, so its own window would start at 0) that --lang auto would otherwise add,
        // keeping the decode-start assertion below solely about the gap-scoped Probe region.
        var (result, audio, _) = await ResumeMissingMarksAsync(
            Options("--quick-marks", "--lang", "en", "--min-silence-length", "1.5"),
            [new Chapter(10, "Chapter 1"), new Chapter(50, "Chapter 3")], [new(28, 30)],
            s => s.Add(30, Seg(0.3, " Chapter 2.")));

        Assert.False(result.GapRemains);
        AssertChapters([new(1, 10), new(2, 30.05), new(3, 50)], result.Chapters);
        // The committed marks are trusted verbatim - nothing probes near their own timestamps,
        // only the gap region's own synthetic start and the fresh tail past the seam it shares
        // with the silence candidate behind it.
        Assert.Equal([10.0, 29.0], audio.DecodeStarts);
    }

    [Fact]
    public async Task ResumeMissingMarksAsync_LeavesGapRemains_WithTheStillMissingNumbers_WhenTheGapIsNotFound()
    {
        var (result, _, _) = await ResumeMissingMarksAsync(
            Options(),
            [new Chapter(10, "Chapter 1"), new Chapter(50, "Chapter 3")], [],
            _ => { }); // nothing scripted for the gap - chapter 2 stays missing

        Assert.True(result.GapRemains);
        Assert.Equal([2], result.MissingNumbers);
        AssertChapters([new(1, 10), new(3, 50)], result.Chapters);
    }

    [Fact]
    public async Task ResumeMissingMarksAsync_SkipsAnUnparseableIntroMark_WithoutTreatingItAsAGapBoundary()
    {
        // The intro entry BuildChapters inserts on a partial commit has no parseable number - it
        // must be dropped from the trusted set entirely (not treated as chapter 0), so the gap is
        // still correctly bounded by chapter 1 (@10) and chapter 3 (@50), exactly as if the intro
        // mark were not present at all.
        var (result, _, _) = await ResumeMissingMarksAsync(
            Options("--min-silence-length", "1.5"),
            [new Chapter(0, "Intro"), new Chapter(10, "Chapter 1"), new Chapter(50, "Chapter 3")],
            [new(28, 30)],
            s => s.Add(30, Seg(0.3, " Chapter 2.")));

        Assert.False(result.GapRemains);
        AssertChapters([new(1, 10), new(2, 30.05), new(3, 50)], result.Chapters);
    }

    [Fact]
    public async Task ResumeMissingMarksAsync_WithAutoLanguage_ResolvesLanguageFromTheCommittedMarksWindow()
    {
        // Mirrors Verify_WithAutoLanguage_ResolvesLanguageUpfront_BeforeParsingAnyTitle: with
        // --lang auto, the language must be resolved from a committed mark's own window (here,
        // the same "de" ResolveProfileFromExistingMarksAsync helper both methods now share), regardless
        // of whether the gap itself ends up recovered.
        var (result, _, transcriber) = await ResumeMissingMarksAsync(
            Options(),
            [new Chapter(10, "Chapter 1"), new Chapter(50, "Chapter 3")], [],
            s => s.DetectedLanguage = ("de", 0.9f));

        Assert.Equal("de", result.Profile.Language);
        Assert.Contains("de", transcriber.LanguageChanges);
        Assert.Equal(1, transcriber.DetectLanguageCalls);
    }

    /// <summary>Creates a <see cref="PreciseMarkRefiner"/> wired directly to a fake audio source
    /// and scripted transcriber, bypassing the full detection pipeline - <see
    /// cref="PreciseMarkRefiner.VerifyMarkBeforeJingleAsync"/>'s own search span typically
    /// overlaps the span precise marking's first (pre-walk) correction already searches, so
    /// exercising it through <see cref="DetectAsync"/> risks that earlier stage confirming the
    /// same scripted "phrase found" entries first and never reaching --mark-before-jingle's walk
    /// at all (confirmed while designing these tests). Testing the method directly sidesteps that
    /// entirely.</summary>
    private (PreciseMarkRefiner Refiner, LanguageProfile Profile) MakeVerifier(ScriptedTranscriber transcriber)
        => (new PreciseMarkRefiner(transcriber.Audio, Options(), default,
                (samples, ct) => transcriber.TranscribeAsync(samples, ct)),
            Options().ResolveProfile("en"));

    [Fact]
    public async Task VerifyMarkBeforeJingleAsync_MovesTheMarkBackWhenTheAnnouncementIsStillAudible()
    {
        // The chapter-1/8/10 shape (2026-07-26): a blip inside the jingle, falsely corroborated as
        // genuine trailing narration, stops ComputeMarkBeforeJingle's own walk at its end (646.2)
        // rather than the true jingle start - but the corroborating text was in truth the
        // announcement's own quiet opening, so a direct precise marking check right there still
        // hears "Chapter two." as the very first thing heard. VerifyMarkBeforeJingleAsync catches
        // this and searches backward: the nearest VAD candidate (645, the blip's own start) is no
        // longer inside the announcement, confirming it as the corrected mark.
        var transcriber = new ScriptedTranscriber(new FakeAudioSource());
        transcriber.Add(646.1, Seg(0, " Chapter two.")); // check @ 646.2 (the walked mark): still the phrase
        transcriber.Add(644.9, Seg(0, " Er nickte."));    // check @ 645 (nearest VAD candidate): not the phrase
        var (refiner, profile) = MakeVerifier(transcriber);

        var result = await refiner.VerifyMarkBeforeJingleAsync(
            646.2, 659.75, _file, null, profile.ChapterPattern,
            [new(0, 640), new(645, 646.2), new(660, 3600)], CancellationToken.None);

        Assert.Equal(645, result);
    }

    [Fact]
    public async Task VerifyMarkBeforeJingleAsync_ReturnsTheWalkedMarkUnchanged_WhenTheAnnouncementIsNoLongerAudible()
    {
        // The common case: the walk already reached clear of the announcement, so the very first
        // check (at the walked mark itself) fails and the mark is trusted outright - no candidate
        // search performed at all, proven here by asserting only that one check was ever decoded.
        var transcriber = new ScriptedTranscriber(new FakeAudioSource()); // nothing scripted: every check finds no phrase
        var (refiner, profile) = MakeVerifier(transcriber);

        var result = await refiner.VerifyMarkBeforeJingleAsync(
            640, 659.75, _file, null, profile.ChapterPattern, [new(0, 640), new(660, 3600)], CancellationToken.None);

        Assert.Equal(640, result);
        Assert.Single(transcriber.Audio.DecodeStarts);
    }

    [Fact]
    public async Task VerifyMarkBeforeJingleAsync_LeavesTheMarkUnchanged_WhenNoBackwardCandidateEverClears()
    {
        // The extreme, never-observed-on-real-audio case the doc comment calls out: the
        // announcement is still audible at the walked mark, but there is nothing earlier left to
        // search at all (the mark already sits at the very start of the file), so the search must
        // give up and leave it exactly as walked rather than looping or throwing.
        var transcriber = new ScriptedTranscriber(new FakeAudioSource());
        transcriber.Add(0, Seg(0, " Chapter two.")); // phrase audible all the way back to the start
        var (refiner, profile) = MakeVerifier(transcriber);

        var result = await refiner.VerifyMarkBeforeJingleAsync(
            0.05, 20, _file, null, profile.ChapterPattern, [], CancellationToken.None);

        Assert.Equal(0.05, result);
    }

    [Fact]
    public async Task VerifyMarkBeforeJingleAsync_SkipsTheCheckEntirely_WhenTheWalkStoppedInsideTheProbeWindow()
    {
        // The guard: the walk landed only 1.5 s behind the pre-walk mark, so the announcement the
        // walk retreated from (0.25 s past that mark) sits well inside the 4 s the probe would
        // decode forward from the walked position. "Still audible" there would be structurally
        // guaranteed - true of a short jingle and of the deliberate "no jingle here" outcome just
        // as much as of a failed walk - so nothing is probed at all and the walk stands. Asserting
        // on the decode count is the point: a probe here would drag a good mark backward.
        var transcriber = new ScriptedTranscriber(new FakeAudioSource());
        transcriber.Add(658.15, Seg(0, " Chapter two.")); // would fire the correction, if ever asked
        var (refiner, profile) = MakeVerifier(transcriber);

        var result = await refiner.VerifyMarkBeforeJingleAsync(
            658.25, 659.75, _file, null, profile.ChapterPattern,
            [new(0, 650), new(655, 658.25), new(660, 3600)], CancellationToken.None);

        Assert.Equal(658.25, result);
        Assert.Empty(transcriber.Audio.DecodeStarts);
    }

    [Fact]
    public async Task RefinePreciseMarkAsync_ReportsThePhraseAsHeard_WhenTheRefinementConfirmsIt()
    {
        // The flag --mark-before-jingle's verification is gated on: a confirmed refinement has
        // measured the announcement's onset, which is what makes the walk's result trustworthy
        // without any further probing.
        var transcriber = new ScriptedTranscriber(new FakeAudioSource());
        transcriber.Add(660, Seg(0, " Chapter two."));
        var (refiner, profile) = MakeVerifier(transcriber);

        var result = await refiner.RefinePreciseMarkAsync(
            659.75, _file, null, profile.ChapterPattern, profile.Language, 660, 663, 700, [], null,
            CancellationToken.None);

        Assert.True(result.PhraseHeard);
        Assert.Equal(659.75, result.Mark);
    }

    [Fact]
    public async Task RefinePreciseMarkAsync_ReportsThePhraseAsNotHeard_WhenNothingCouldBeConfirmed()
    {
        // The one case that still leaves a mark of unknown accuracy behind, and therefore the one
        // case --mark-before-jingle's verification is still worth paying for.
        var transcriber = new ScriptedTranscriber(new FakeAudioSource()); // nothing scripted: no check ever hears the phrase
        var (refiner, profile) = MakeVerifier(transcriber);

        var result = await refiner.RefinePreciseMarkAsync(
            659.75, _file, null, profile.ChapterPattern, profile.Language, 660, 663, 700, [], null,
            CancellationToken.None);

        Assert.False(result.PhraseHeard);
        Assert.Equal(659.75, result.Mark);
    }

    [Fact]
    public async Task Debug_RecordsEverySilence_IncludingThoseBelowTheThreshold()
    {
        // The point of the dump: --min-silence-length decides which silences Probe works from, and
        // "why was there no candidate here" is answerable only if the rejected ones are in the file
        // too - flagged, so the working subset stays readable.
        var debug = await DetectWithDebugAsync(
            Options("--debug", "--min-silence-length", "3"),
            [new(595, 600), new(1199, 1200)],
            s => s.Add(0, Seg(0.5, " Chapter one.")));

        Assert.Contains(debug, l => l.Contains("silence 0:09:55.00-0:10:00.00") && l.EndsWith("*"));
        Assert.Contains(debug, l => l.Contains("silence 0:19:59.00-0:20:00.00") && !l.EndsWith("*"));
    }

    [Fact]
    public async Task Debug_RecordsTheVadSpeechSegmentsAndNonSpeechRegions()
    {
        var debug = await DetectWithDebugAsync(
            Options("--debug", "--mark-before-jingle"),
            [new(595, 600)],
            s => s.Add(0, Seg(0.5, " Chapter one.")),
            new FakeVad { Speech = [new(0, 640), new(651, 3600)] });

        Assert.Contains(debug, l => l.Contains("speech 0:00:00.00-0:10:40.00"));
        Assert.Contains(debug, l => l.Contains("non-speech 0:10:40.00-0:10:51.00"));
    }

    [Fact]
    public async Task Debug_RecordsWholeTranscripts_WithoutVerboseTranscripts()
    {
        // --verbose-transcripts exists because the segment dump drowns the ordinary log; the debug
        // file has no such constraint, and a transcript nobody kept is the one thing a
        // troubleshooting log cannot reconstruct afterwards.
        var debug = await DetectWithDebugAsync(
            Options("--debug"),
            [new(595, 600)],
            s => s.Add(0, Seg(0.5, " Chapter one.")));

        Assert.Contains(debug, l => l.Contains("probe") && l.Contains("\"Chapter one.\""));
        // Once, not twice: the bare header the ordinary log gets must not precede it here. Every
        // probe line in the debug file carries its transcript (or says there was none).
        Assert.DoesNotContain(debug, l => l.StartsWith("probe ") && !l.Contains(": "));
    }

    [Fact]
    public async Task Debug_AlsoReceivesEveryOrdinaryLogLine()
    {
        // The debug file is meant to be the union of both streams, so nobody has to read two files
        // side by side to reconstruct one run.
        var debug = await DetectWithDebugAsync(
            Options("--debug"),
            [new(595, 600)],
            s => s.Add(0, Seg(0.5, " Chapter one.")));

        Assert.Contains(debug, l => l.Contains("chapter 1 detected"));
    }

    [Fact]
    public async Task Debug_RecordsTheMarkRefinementProbes_WhichNothingElseShows()
    {
        // These decodes only ever hand a yes/no answer upwards, so before --debug they could be
        // reconstructed only by re-running them by hand in a throwaway harness.
        var debug = await DetectWithDebugAsync(
            Options("--debug"),
            [new(595, 600)],
            s =>
            {
                s.Add(600, Seg(0.3, " Chapter one."));
                s.Add(600.25, Seg(0.3, " Chapter one."));
            });

        Assert.Contains(debug, l => l.Contains("onset probe") && l.Contains("-> phrase"));
    }

    /// <summary>
    /// The failure the denoiser rescue exists for: the recognizer keeps a chapter's number and
    /// loses the word beside it, so no wording of the phrase can match and the window yields
    /// nothing. Measured on "De vandrande djaknarne", where "Forsta kapitlet Djaknetag" came back
    /// as "1. Jaknuktag" and cost the book its first chapter.
    /// <para>
    /// The fixture's audio is digital silence, which the fidelity check reports as unmeasurable
    /// rather than dull, so permission is refused here - but the request having been made is what
    /// these tests are about. The denoising itself is covered against the real model in
    /// <see cref="SpeechDenoiserTests"/>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ANumberWithoutItsChapterWord_AsksToBeDenoised()
    {
        var (_, log, _) = await DetectWithLogAsync(
            Options(), [new(595, 600)], s => s.Add(600, Seg(0.5, " 1. Jaknuktag")));

        Assert.Contains(log, l => l.Contains("heard a chapter number but not the word"));
        Assert.Contains(log, l => l.Contains("could not measure"));
    }

    /// <summary>A window that produced a mark is never re-read: the rescue is a fallback for a lost
    /// announcement, not a second opinion on a found one.</summary>
    [Fact]
    public async Task AnAnnouncementThatWasHeardProperly_IsNotDenoised()
    {
        var (_, log, _) = await DetectWithLogAsync(
            Options(), [new(595, 600)], s => s.Add(600, Seg(0.5, " Chapter one.")));

        Assert.DoesNotContain(log, l => l.Contains("heard a chapter number but not the word"));
    }

    /// <summary>Narration with no number in it is a different kind of empty window, and has its own
    /// remedies; denoising it would spend a decode on nothing.</summary>
    [Fact]
    public async Task AWindowWithNoNumberAtAll_IsNotDenoised()
    {
        var (_, log, _) = await DetectWithLogAsync(
            Options(), [new(595, 600)], s => s.Add(600, Seg(0.5, " and he walked on into the evening.")));

        Assert.DoesNotContain(log, l => l.Contains("heard a chapter number but not the word"));
    }

    /// <summary>A number inside a sentence is prose rather than a heading standing on its own. The
    /// strict reading is what keeps this rescue off ordinary text, which it would otherwise ask
    /// about on every empty window of every dull-sounding book.</summary>
    [Fact]
    public async Task ANumberInsideASentence_IsNotDenoised()
    {
        var (_, log, _) = await DetectWithLogAsync(
            Options(), [new(595, 600)], s => s.Add(600, Seg(0.5, " he had walked 1 mile that evening.")));

        Assert.DoesNotContain(log, l => l.Contains("heard a chapter number but not the word"));
    }

    /// <summary>--no-denoise switches the whole thing off, the request included.</summary>
    [Fact]
    public async Task NoDenoise_SuppressesTheRequestEntirely()
    {
        var (_, log, _) = await DetectWithLogAsync(
            Options("--no-denoise"), [new(595, 600)], s => s.Add(600, Seg(0.5, " 1. Jaknuktag")));

        Assert.DoesNotContain(log, l => l.Contains("heard a chapter number but not the word"));
    }

    /// <summary>
    /// A book announcing every chapter after music, which is the shape the J-probe/S-probe split exists
    /// for: three jingles, three chapters, and two ordinary pauses in between that hold nothing. The
    /// jingles are read first, the sequence comes out complete, and neither pause is ever decoded -
    /// which is the whole saving, since on a real book of this shape those pauses number in the
    /// thousands.
    /// </summary>
    [Fact]
    public async Task JingleFirst_SkipsThePausesBetweenTwoConsecutiveChapters()
    {
        var (result, _, audio) = await DetectFullAsync(
            Options(),
            [new(500, 503), new(1500, 1503)],
            s =>
            {
                s.Add(102, Seg(8, " Chapter one."));
                s.Add(1002, Seg(8, " Chapter two."));
                s.Add(2002, Seg(8, " Chapter three."));
            },
            new FakeVad { Speech = [new(0, 100), new(110, 1000), new(1010, 2000), new(2010, 3600)] });

        Assert.Equal([1, 2, 3], result.Chapters.Select(c => c.Number));
        Assert.DoesNotContain(500.0, audio.DecodeStarts);
        Assert.DoesNotContain(1500.0, audio.DecodeStarts);
    }

    /// <summary>The other half of the bargain: a chapter the music did not announce leaves a hole,
    /// and the pause half walks exactly that hole and finds it.</summary>
    [Fact]
    public async Task JingleFirst_StillFindsAChapterAnnouncedAfterAPause()
    {
        var (result, _, audio) = await DetectFullAsync(
            Options(),
            [new(1000, 1003)],
            s =>
            {
                s.Add(102, Seg(8, " Chapter one."));
                s.Add(1000, Seg(3.2, " Chapter two."));
                s.Add(2002, Seg(8, " Chapter three."));
            },
            new FakeVad { Speech = [new(0, 100), new(110, 2000), new(2010, 3600)] });

        Assert.Equal([1, 2, 3], result.Chapters.Select(c => c.Number));
        Assert.Contains(1000.0, audio.DecodeStarts);
    }

    /// <summary>
    /// The prologue, which is the reason the pause half walks the head of the file at all - and the
    /// reason a phrase's scope is measured by position rather than by how many chapters have been
    /// accepted so far. Every chapter of this book is already in hand when its opening minute is
    /// finally read, and a prologue there is still a prologue.
    /// </summary>
    [Fact]
    public async Task JingleFirst_StillFindsAPrologueBeforeTheFirstChapter()
    {
        var result = await DetectAsync(
            Options(),
            [new(50, 53)],
            s =>
            {
                s.Add(50, Seg(3.2, " Prologue."));
                s.Add(102, Seg(8, " Chapter one."));
                s.Add(1002, Seg(8, " Chapter two."));
            },
            new FakeVad { Speech = [new(0, 100), new(110, 1000), new(1010, 3600)] });

        Assert.Equal([1, 2], result.Chapters.Select(c => c.Number));
        AssertNamed([("prologue", "Prologue", 53)], result);
    }

    /// <summary>And the epilogue, which is why it walks the tail.</summary>
    [Fact]
    public async Task JingleFirst_StillFindsAnEpilogueAfterTheLastChapter()
    {
        var result = await DetectAsync(
            Options(),
            [new(3000, 3003)],
            s =>
            {
                s.Add(102, Seg(8, " Chapter one."));
                s.Add(1002, Seg(8, " Chapter two."));
                s.Add(3000, Seg(3.2, " Epilogue."));
            },
            new FakeVad { Speech = [new(0, 100), new(110, 1000), new(1010, 3600)] });

        Assert.Equal([1, 2], result.Chapters.Select(c => c.Number));
        AssertNamed([("epilogue", "Epilogue", 3003)], result);
    }

    /// <summary>What --verbose says about it - the shape a file ran under is otherwise invisible,
    /// and it decides which windows the whole pass opened.</summary>
    [Fact]
    public async Task JingleFirst_IsAnnouncedInTheLog()
    {
        var (_, log, _) = await DetectWithLogAsync(
            Options(),
            [new(500, 503)],
            s => s.Add(102, Seg(8, " Chapter one.")),
            new FakeVad { Speech = [new(0, 100), new(110, 3600)] });

        Assert.Contains(log, l => l.StartsWith("reading the music first: 1 jingle(s), 1.0 per hour"));
        Assert.Contains(log, l => l.StartsWith("J-probe finished, "));
    }

    /// <summary>A book with no music at all is not read music-first; it is handed to the other
    /// two-part shape instead, which reads its longest pauses first.</summary>
    [Fact]
    public async Task WithoutJingles_TheLongestPausesAreReadFirst()
    {
        var (_, log, _) = await DetectWithLogAsync(
            Options(),
            [new(500, 503)],
            s => s.Add(500, Seg(3.2, " Chapter one.")),
            new FakeVad { Speech = [new(0, 3600)] });

        Assert.DoesNotContain(log, l => l.Contains("jingle-first"));
        Assert.Contains(log, l => l.StartsWith("reading the longest pauses first"));
    }

    /// <summary>
    /// The order itself, which is the one thing about this shape a fixture can show directly: the
    /// 8 s pause in the middle of the file is read before the 2 s pause near its start, where the
    /// walk that reads in file order would have taken them the other way round. Everything is still
    /// found, the two pauses the descent stopped short of being read by the walk that follows.
    /// <para>
    /// What a fixture cannot show is the saving, which is a whole-book effect: on five of the six
    /// pause-announced corpus books the file-order gate ends up at its 0.8 s floor and probes very
    /// nearly every candidate there is, and the descent's own floor is what keeps it from doing the
    /// same. See <see cref="DescendingSilenceScan"/>'s notes for those measurements.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Descending_ReadsTheLongestPauseFirstAndSkipsWhatThatSettles()
    {
        var (result, log, audio) = await DetectWithLogAsync(
            Options(),
            [new(100, 104), new(1000, 1008), new(1500, 1503.5), new(2000, 2006.5)],
            s =>
            {
                s.Add(101, Seg(3, " Chapter one."));
                s.Add(1005, Seg(3, " Chapter two."));
                s.Add(2003.5, Seg(3, " Chapter three."));
            },
            new FakeVad { Speech = [new(0, 3600)] });

        Assert.Equal([1, 2, 3], result.Chapters.Select(c => c.Number));
        // Both indices asserted present first: IndexOf returning -1 for a window that was never
        // decoded would make the ordering check below pass without meaning anything.
        Assert.Contains(1005, audio.DecodeStarts);
        Assert.Contains(101, audio.DecodeStarts);
        Assert.True(audio.DecodeStarts.IndexOf(1005) < audio.DecodeStarts.IndexOf(101),
            "the 8 s pause should have been read before the earlier 4 s one");
        // Chapters 2 and 3 are consecutive, so nothing can be announced between them and the 3.5 s
        // pause that sits there is never read - which is the whole of what the shape saves.
        Assert.Contains(log, l => l.Contains("1 stretch(es) closed by consecutive chapter numbers"));
        Assert.DoesNotContain(1500.5, audio.DecodeStarts);
    }

    /// <summary>
    /// The other half of that bargain: a pause too short for the descent to reach is still read by
    /// the walk itself. Only a stretch the readings closed is passed over, and chapters 1 and 3
    /// close nothing between them.
    /// </summary>
    [Fact]
    public async Task Descending_StillReadsAPauseTooShortForTheDescentToReach()
    {
        var (result, log, _) = await DetectWithLogAsync(
            Options(),
            [new(100, 105), new(1000, 1002), new(2000, 2005)],
            s =>
            {
                s.Add(102, Seg(3, " Chapter one."));
                s.Add(1000, Seg(3, " Chapter two."));
                s.Add(2002, Seg(3, " Chapter three."));
            },
            new FakeVad { Speech = [new(0, 3600)] });

        Assert.Equal([1, 2, 3], result.Chapters.Select(c => c.Number));
        Assert.Contains(log, l => l.StartsWith("SD-probe: down to 2 s pauses, below the 3.75 s"));
        Assert.DoesNotContain(log, l => l.Contains("stretch(es) closed"));
    }

    /// <summary>
    /// A window the descent read is not decoded again when the walk reaches it - the walk makes its
    /// marks from the transcript already in hand. Without that the shape would pay twice for every
    /// window it looked at first.
    /// </summary>
    [Fact]
    public async Task Descending_DoesNotDecodeAWindowItAlreadyRead()
    {
        var (_, _, audio) = await DetectWithLogAsync(
            Options(),
            [new(100, 105), new(2000, 2005)],
            s => s.Add(2002, Seg(3, " Chapter one.")),
            new FakeVad { Speech = [new(0, 3600)] });

        // The descent read the window at 102 and found nothing there; the walk goes over it too,
        // and between them that is one decode.
        Assert.Equal(1, audio.DecodeWindows.Count(w => w.Start is >= 100 and < 106));
    }

    /// <summary>
    /// A file that announces nothing gives up on --early-abort's own budget rather than reading its
    /// whole candidate list out of order first - see <see cref="RegionProber.DryStartBudget"/>. The
    /// verdict itself still comes from the walk that reads the file in order, which is the one that
    /// can honestly say nothing was found in its first minutes.
    /// </summary>
    [Fact]
    public async Task Descending_WithNothingAnnounced_GivesUpOnTheEarlyAbortBudget()
    {
        var (result, log, _) = await DetectWithLogAsync(
            Options("--early-abort", "10"),
            [new(100, 105), new(300, 304), new(1000, 1006), new(2000, 2005)],
            _ => { },
            new FakeVad { Speech = [new(0, 3600)] });

        Assert.Empty(result.Chapters);
        Assert.Contains(log, l => l.StartsWith("SD-probe: nothing announced at the 3 longest"));
        Assert.Contains(log, l => l.StartsWith("early-abort: "));
    }

    /// <summary>
    /// The gate's other half, shared with the jingle-first shape: a <c>--custom</c> mapping that may
    /// be announced between two chapters is the one thing that could be hiding in the pauses this
    /// shape skips, so it keeps the file in one sweep.
    /// </summary>
    [Fact]
    public async Task Descending_ACustomMappingBetweenChapters_KeepsTheOneSweepShape()
    {
        var (_, log, _) = await DetectWithLogAsync(
            Options("--custom", "/interlude/:Interlude"),
            [new(500, 503)],
            s => s.Add(500, Seg(3.2, " Chapter one.")),
            new FakeVad { Speech = [new(0, 3600)] });

        Assert.Contains(log, l => l.Contains("may be announced between chapters"));
        Assert.DoesNotContain(log, l => l.StartsWith("SD-probe"));
    }

    /// <summary>
    /// An explicit --min-silence-length is the user naming the pauses worth probing, and it also
    /// switches off the arithmetic the descent stops against. Reordering the list without a stop
    /// rule is all cost and no saving, so the shape declines.
    /// </summary>
    [Fact]
    public async Task Descending_IsDeclinedWhenTheSilenceLengthWasGivenExplicitly()
    {
        var (result, log, _) = await DetectWithLogAsync(
            Options("--min-silence-length", "1.0"),
            [new(500, 503)],
            s => s.Add(500, Seg(3.2, " Chapter one.")),
            new FakeVad { Speech = [new(0, 3600)] });

        Assert.Equal([1], result.Chapters.Select(c => c.Number));
        Assert.DoesNotContain(log, l => l.StartsWith("SD-probe"));
        Assert.DoesNotContain(log, l => l.StartsWith("reading the longest pauses first"));
    }

    /// <summary>
    /// --jingle-first on a file the gate would have declined. With no music the jingle half probes
    /// only the region start, so the pause half is handed the whole book - the ordinary Probe in
    /// all but name, which is what makes forcing the shape safe enough to be worth measuring with.
    /// </summary>
    [Fact]
    public async Task JingleFirstOption_ForcesTheShapeAndStillFindsEverything()
    {
        var (result, log, _) = await DetectWithLogAsync(
            Options("--jingle-first"),
            [new(500, 503), new(1500, 1503)],
            s =>
            {
                s.Add(500, Seg(3.2, " Chapter one."));
                s.Add(1500, Seg(3.2, " Chapter two."));
            },
            new FakeVad { Speech = [new(0, 3600)] });

        Assert.Equal([1, 2], result.Chapters.Select(c => c.Number));
        Assert.Contains(log, l => l.Contains("reading the music first (--jingle-first)"));
    }

    /// <summary>
    /// --early-abort is asked once, by the half that has actually looked at the pauses. The jingle
    /// half reads the music and nothing else, so "nothing found yet" says nothing there - and this
    /// book's first jingle sits well past the abort threshold, which is exactly the framing that
    /// would have given up on a book that does yield chapters.
    /// </summary>
    [Fact]
    public async Task JingleFirst_LeavesTheEarlyAbortToThePauseHalf()
    {
        var result = await DetectAsync(
            Options("--early-abort", "10"),
            [new(500, 503)],
            s =>
            {
                s.Add(500, Seg(3.2, " Chapter one."));
                s.Add(1002, Seg(8, " Chapter two."));
            },
            new FakeVad { Speech = [new(0, 1000), new(1010, 3600)] });

        Assert.False(result.EarlyAborted);
        Assert.Equal([1, 2], result.Chapters.Select(c => c.Number));
    }
}
