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
/// full transcription (pass 3), jingle anchoring, and the pure helper functions.
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
            string file, double durationSeconds, double minSilenceSeconds, int noiseDb,
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
            string file, double durationSeconds, double minSilenceSeconds, int noiseDb,
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
        private readonly List<(double Start, double MaxWindowSeconds, List<TranscriptSegment> Segments)> _script = [];

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
            => _script.Add((start, double.PositiveInfinity, [.. segments]));

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
            => _script.Add((start, maxWindowSeconds, [.. segments]));

        /// <summary>Called at the start of every <see cref="TranscribeAsync"/>, so a test can
        /// sample detector-external state (progress, say) at the exact moments this transcriber
        /// is being used.</summary>
        public Action? OnTranscribe { get; set; }

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
        public Task<List<TranscriptSegment>> TranscribeAsync(float[] samples, CancellationToken ct)
        {
            OnTranscribe?.Invoke();
            var (start, duration) = _audio.DecodeWindows[^1];
            var end = duration is { } seconds ? start + seconds : double.PositiveInfinity;
            // Decode starts are arithmetic (mark minus a lead, plus a step), so a segment scripted
            // to sit exactly on one lands a rounding error either side of it.
            const double epsilon = 1e-9;
            var segments = _script
                .Where(entry => end - start <= entry.MaxWindowSeconds + epsilon)
                .SelectMany(entry => entry.Segments.Select(seg => (Absolute: entry.Start + seg.StartSeconds,
                                                                   End: entry.Start + seg.EndSeconds, seg)))
                .Where(x => x.Absolute >= start - epsilon && x.Absolute < end)
                .OrderBy(x => x.Absolute)
                .Select(x => new TranscriptSegment(
                    x.Absolute - start, x.End - start, x.seg.Text, x.seg.Probability))
                .ToList();
            return Task.FromResult(segments);
        }

        /// <summary>Language auto-detection result to return; defaults to a confident "en".</summary>
        public (string Language, float Probability) DetectedLanguage { get; set; } = ("en", 0.99f);

        /// <summary>Languages this transcriber was told to switch to, in call order.</summary>
        public List<string> LanguageChanges { get; } = [];

        /// <summary>Number of times <see cref="DetectLanguageWithProbability"/> was called.</summary>
        public int DetectLanguageCalls { get; private set; }

        /// <inheritdoc/>
        public Task<(string Language, float Probability)> DetectLanguageWithProbability(float[] samples, CancellationToken ct)
        {
            DetectLanguageCalls++;
            return Task.FromResult(DetectedLanguage);
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

    /// <summary>Builds validated options with the temp file as target, at
    /// <see cref="PinnedMarkLeadSeconds"/> unless the test asks for a lead of its own.</summary>
    private CliOptions Options(params string[] args)
        => CliOptions.Parse(args.Contains("--mark-lead") || args.Contains("-k")
            ? [.. args, _file]
            : [.. args, "--mark-lead", $"{PinnedMarkLeadSeconds}", _file])!;

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
    /// may sit up to one <see cref="DetectionTuning.PreciseMarkFixedStepSeconds"/> <em>before</em>
    /// the expected position but never after it.
    /// <para>
    /// The expected values are where the heuristic alone would put the mark - the phrase onset the
    /// script states, less <see cref="PinnedMarkLeadSeconds"/>. Precise marking
    /// does not trust that position; it brackets the true onset by bisection and reports the last
    /// probe that still confirmed the phrase, which lands within one step below the real edge
    /// rather than exactly on it. Pinning these tests to the bisection's own arithmetic would make
    /// eighty-odd assertions - most of them about gap tracking or language handling, not placement -
    /// churn on every tuning change, and would assert an artifact instead of the contract precise
    /// marking actually owes its callers.
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

    /// <summary>The one-sided tolerance every mark assertion shares.</summary>
    private static void AssertMarkTime(string what, double expected, double actual)
    {
        // A mark at 0 is clamped rather than measured, so it has no tolerance to spend.
        var floor = expected == 0 ? 0 : expected - DetectionTuning.PreciseMarkFixedStepSeconds;
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
    /// Runs the detector with a separate transcriber for pass 3 (as <c>--pass3-model</c> sets up):
    /// pass 2 uses <paramref name="pass2Script"/>, pass 3 uses <paramref name="pass3Script"/>, both
    /// keyed off the same fake audio source. Returns the result plus both transcribers, so a test
    /// can prove that a gap was filled by the pass-3 transcriber rather than the pass-2 one.
    /// </summary>
    private async Task<(DetectionResult Result, ScriptedTranscriber Pass2, ScriptedTranscriber Pass3)> DetectWithPass3TranscriberAsync(
        CliOptions options, List<Silence> silences,
        Action<ScriptedTranscriber> pass2Script, Action<ScriptedTranscriber> pass3Script)
    {
        var audio = new FakeAudioSource { Silences = silences };
        var pass2 = new ScriptedTranscriber(audio);
        var pass3 = new ScriptedTranscriber(audio);
        pass2Script(pass2);
        pass3Script(pass3);
        var detector = new ChapterDetector(options, audio, pass2, vad: null, pass3Transcriber: pass3);
        var result = await detector.DetectAsync(_file, Info, new WorkTracker(), default, CancellationToken.None);
        return (result, pass2, pass3);
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
            Options("--max-jingle-length", "0"),
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
            Options("--max-jingle-length", "0"),
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
            Options("--max-jingle-length", "0"),
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
            Options("--max-jingle-length", "0"),
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
            Options("--max-jingle-length", "0"),
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
            Options("--max-jingle-length", "0", "--early-abort", "0"),
            [new(595, 600)],
            s => s.Add(0, Seg(0.5, " Prologue.")));

        Assert.Empty(result.Chapters);
        Assert.Empty(result.NamedMarks);
    }

    [Fact]
    public async Task NamedMarks_AreNotDetected_WhenTheirPhraseIsSwitchedOff()
    {
        var result = await DetectAsync(
            Options("--max-jingle-length", "0", "--prologue-phrase", "", "--epilogue-phrase", ""),
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
            Options("--lang", "de", "--max-jingle-length", "0"),
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
            Options("--max-jingle-length", "0"),
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
            Options("--max-jingle-length", "0", "--custom", "zwischenspiel:Zwischenspiel"),
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
    public async Task CustomPhrase_IsNotBoundByTheChapterSequence()
    {
        // Both before the first chapter and after the last: neither scope rule applies to it.
        var result = await DetectAsync(
            Options("--max-jingle-length", "0", "--custom", "/zeit[- ]?tafel/:Zeittafel"),
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
            Options("--max-jingle-length", "0", "--custom", "/(interlude|intermezzo)/:The $1"),
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
            Options("--max-jingle-length", "0", "--min-silence-length", "1",
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
            Options("--max-jingle-length", "0", "--ignore-chapter-numbers",
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
            Options("--max-jingle-length", "0", "--ignore-chapter-numbers"),
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
            Options("--max-jingle-length", "0", "--ignore-chapter-numbers", "--early-abort", "0"),
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
            Options("--max-jingle-length", "0", "--ignore-chapter-numbers"),
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
            Options("--max-jingle-length", "0", "--ignore-chapter-numbers", "--early-abort", "1",
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
            Options("--lang", "de", "--max-jingle-length", "0"),
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
            Options("-c", @"/chapter (\d+)/", "--max-jingle-length", "0"),
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
        audio.AddPcm(600, Enumerable.Repeat(0.5f, 4000).ToArray()); // chapter 2's finished mark
        var transcriber = new ScriptedTranscriber(audio);
        transcriber.Add(0, Seg(0.5, " Chapter one."));
        transcriber.Add(600, Seg(0.25, " Chapter two."));
        var log = new List<string>();
        var detector = new ChapterDetector(Options("--max-jingle-length", "0"), audio, transcriber);

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
            Options("--max-jingle-length", "0", "--quick-marks"), [new(595, 600)],
            s => s.Add(0, Seg(0.5, " Chapter one.")));

        Assert.Equal(0.25, Assert.Single(result.Chapters).TimeSeconds);
        Assert.DoesNotContain(0.25, audio.DecodeStarts);
    }

    [Fact]
    public async Task ChapterNumberAboveTheCap_IsDiscarded()
    {
        // "Chapter five hundred and ten" in a three-chapter book is a mishearing, not a chapter:
        // with --max-chapter-number it never enters the sequence, so nothing is left to hunt for.
        var (result, log, _) = await DetectWithLogAsync(
            Options("--max-jingle-length", "0", "--max-chapter-number", "12"),
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
        // Pass 2 drops a number that does not top the last accepted one and keeps scanning, which
        // is right - but the number *was* heard, so a --verbose run has to say why it did not
        // become a mark. Without the line, this is indistinguishable from the phrase matcher
        // having missed it entirely.
        var (result, log, _) = await DetectWithLogAsync(
            Options("--max-jingle-length", "0"),
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
            l.Contains("not above the last accepted chapter 2") &&
            l.Contains("(in-text mention?)"));
    }

    [Fact]
    public async Task ChapterNumberEqualToTheLastAccepted_IsLoggedWithoutTheInTextHint()
    {
        // A re-detection of the chapter just marked is a different story from a regression: it is
        // the same announcement seen again, not a mention buried in the narration, so the hint
        // that would send someone looking for one is left off.
        var (_, log, _) = await DetectWithLogAsync(
            Options("--max-jingle-length", "0"),
            [new(595, 600), new(1195, 1200)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(600, Seg(0.4, " Chapter two."));
                s.Add(1200, Seg(0.3, " Chapter two."));
            });

        Assert.Contains(log, l =>
            l.Contains("skipped chapter 2 at 0:20:00.30") &&
            l.Contains("not above the last accepted chapter 2"));
        Assert.DoesNotContain(log, l => l.Contains("in-text mention?"));
    }

    [Fact]
    public async Task ChapterNumberAboveTheCap_IsAccepted_WithoutTheCap()
    {
        // The same script without --max-chapter-number: the mishearing becomes a chapter of its
        // own and turns everything below it into a gap - exactly what the cap exists to prevent.
        var result = await DetectAsync(
            Options("--max-jingle-length", "0"),
            [new(595, 600), new(1195, 1200)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(600, Seg(0.3, " Chapter five hundred ten."));
                s.Add(1200, Seg(0.2, " Chapter two."));
            });

        Assert.Contains(result.Chapters, c => c.Number == 510);
        Assert.True(result.GapRemains);
    }

    [Fact]
    public async Task ChapterNumberAtTheCap_IsStillAccepted()
    {
        var result = await DetectAsync(
            Options("--max-jingle-length", "0", "--max-chapter-number", "2"),
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
            Options("--max-jingle-length", "0", "--mark-lead", $"{lead}"),
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
            Options("--max-jingle-length", "0"),
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
            Options("--max-jingle-length", "0", "--early-abort", "10"),
            NoChapterSilences,
            _ => { });

        Assert.True(result.EarlyAborted);
        Assert.Empty(result.Chapters);
        // Candidates at 0 and 300 s are probed (both under the 600 s/10 min threshold);
        // the candidate at 600 s triggers the abort before it is ever probed.
        Assert.Equal([0.0, 300.0], audio.DecodeStarts);
    }

    [Fact]
    public async Task EarlyAbort_Zero_DisablesTheFeature_AndProbesTheWholeFile()
    {
        var (result, _, audio) = await DetectFullAsync(
            Options("--max-jingle-length", "0", "--early-abort", "0"),
            NoChapterSilences,
            _ => { });

        Assert.False(result.EarlyAborted);
        Assert.Empty(result.Chapters);
        Assert.Contains(3300.0, audio.DecodeStarts);
    }

    [Fact]
    public async Task EarlyAbort_DoesNotFire_OnceAChapterHasBeenFound()
    {
        var (result, _, audio) = await DetectFullAsync(
            Options("--max-jingle-length", "0", "--early-abort", "10"),
            NoChapterSilences,
            s => s.Add(300, Seg(0.3, " Chapter one.")));

        Assert.False(result.EarlyAborted);
        AssertChapters([new DetectedChapter(1, 300.05)], result.Chapters);
        Assert.Contains(3300.0, audio.DecodeStarts);
    }

    [Fact]
    public async Task ExpectedStartChapter_Aborts_WhenFirstChapterFoundIsBelowExpectation()
    {
        var result = await DetectAsync(
            Options("--max-jingle-length", "0", "--expected-start-chapter", "15"),
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
            Options("--max-jingle-length", "0", "--expected-start-chapter", "3"),
            [new(595, 600)],
            s => s.Add(0, Seg(0.5, " Chapter three.")));

        Assert.Null(result.BelowExpectedStartNumber);
        AssertChapters([new DetectedChapter(3, 0.25)], result.Chapters);
    }

    [Fact]
    public async Task ExpectedStartChapter_HuntsAndFillsLeadingGap_ForANonOneStart()
    {
        // Pass 2 only finds chapter 13; with --expected-start-chapter 12, pass 3 must hunt the
        // leading gap for chapter 12 and finds it in the very first chunk.
        var (result, _, audio) = await DetectFullAsync(
            Options("--max-jingle-length", "0", "--expected-start-chapter", "12"),
            [new(1195, 1200)],
            s =>
            {
                s.Add(1200, Seg(0.2, " Chapter thirteen."));
                s.Add(0, Seg(10, " Chapter twelve.")); // pass-3 chunk 1 [0, 600], phrase at 10
            });

        Assert.False(result.GapRemains);
        AssertChapters([new(12, 9.75), new(13, 1199.95)], result.Chapters);
        Assert.DoesNotContain(590.0, audio.DecodeStarts);
    }

    [Fact]
    public async Task ExpectedStartChapter_ReportsGapRemains_WhenPass3CannotFillTheLeadingGap()
    {
        // Pass 2 finds only chapter 4; with --expected-start-chapter 1, pass 3 hunts for 1-3, but
        // the audio never actually says them, so the leading gap stays unresolved.
        var result = await DetectAsync(
            Options("--max-jingle-length", "0", "--expected-start-chapter", "1"),
            [new(1195, 1200)],
            s => s.Add(1200, Seg(0.2, " Chapter four.")));

        Assert.True(result.GapRemains);
        Assert.Equal([1, 2, 3], result.MissingNumbers);
        AssertChapters([new(4, 1199.95)], result.Chapters);
    }

    [Fact]
    public async Task SequenceGap_IsResolved_ByFullTranscription()
    {
        // The probe after the first silence hears nothing, so pass 2 yields chapters 1 and 3;
        // pass 3 must transcribe the region in between and find chapter 2 at 600 s. The first
        // pass-3 chunk's border (natural end 600.5) snaps to the [595, 600] silence's mid-point
        // (597.5), so the second chunk starts exactly there - that is where the phrase is heard.
        var result = await DetectAsync(
            Options("--quick-marks", "--max-jingle-length", "0"),
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
            Options("--max-jingle-length", "0"),
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
        Assert.Equal(2, highest);
        Assert.Empty(missing);
    }

    [Fact]
    public void ChapterProgress_StillReportsInteriorGaps_WithoutAnExpectedStartChapter()
    {
        var chapters = new List<DetectedChapter> { new(2, 500), new(3, 900), new(6, 2000) };
        var (highest, missing) = GapPlanning.ChapterProgress(chapters);
        Assert.Equal(6, highest);
        Assert.Equal([4, 5], missing);
    }

    [Fact]
    public void ChapterProgress_ReportsLeadingGap_WhenExpectedStartChapterIsGiven()
    {
        var chapters = new List<DetectedChapter> { new(2, 500) };
        var (highest, missing) = GapPlanning.ChapterProgress(chapters, expectedStartChapter: 1);
        Assert.Equal(2, highest);
        Assert.Equal([1], missing);
    }

    [Fact]
    public void ChapterProgress_ReturnsNoMissingChapters_WhenNoneFoundYet()
    {
        var (highest, missing) = GapPlanning.ChapterProgress([]);
        Assert.Equal(0, highest);
        Assert.Empty(missing);
    }

    [Fact]
    public void ChapterProgress_DoesNotThrow_WhenExpectedStartChapterExceedsHighestFound()
    {
        // The very first chapter Pass 2 finds can transiently be numbered below
        // expectedStartChapter for one ChapterProgress call, right before ChapterDetector's own
        // "below expectation" check aborts the run - must not crash on a negative-length range.
        var chapters = new List<DetectedChapter> { new(2, 500) };
        var (highest, missing) = GapPlanning.ChapterProgress(chapters, expectedStartChapter: 5);
        Assert.Equal(2, highest);
        Assert.Empty(missing);
    }

    [Fact]
    public void FindGaps_RaisesNoLeadingGap_WithoutAnExpectedStartChapter()
    {
        // Without --expected-start-chapter, a first-found chapter numbered above 1 is trusted
        // outright - there is no way to tell a legitimate split-book start from a Pass 2 miss, so
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
    public async Task RegionBeforeFirstChapter_IsSearched_WhenItStartsAboveOne()
    {
        // Only chapter 2 is found by the probes; with --expected-start-chapter 1 given, pass 3
        // transcribes the file start looking for chapter 1 (without it, FindGaps would never
        // raise this leading gap at all - see FindGaps_* below). It is not in the first chunk
        // [0, 600] but past its end, so the search must
        // continue into the second chunk: that chunk's border (natural end 600) has no seam target
        // within reach, so the unsnapped fallback keeps the 10-second overlap and the second chunk
        // starts at 590, not 600 - and that is where chapter 1 (phrase at 610) is found. (Were
        // chapter 1 already in the first chunk, the gap's sole missing number would be complete and
        // pass 3 would stop before decoding the second chunk at all - see GapCompletes_* below.)
        var (result, _, audio) = await DetectFullAsync(
            Options("--max-jingle-length", "0", "--expected-start-chapter", "1"),
            [new(1195, 1200)],
            s =>
            {
                s.Add(1200, Seg(0.2, " Chapter two."));
                s.Add(590, Seg(20, " Chapter one.")); // pass-3 chunk 2 (window start 590), phrase at 610
            });

        Assert.False(result.GapRemains);
        AssertChapters([new(1, 609.75), new(2, 1199.95)], result.Chapters);
        Assert.Contains(590.0, audio.DecodeStarts);
    }

    [Fact]
    public async Task GapCompletes_WhenAllExpectedChaptersAreFound_StopsBeforeTheNextChunk()
    {
        // Same leading-gap setup as above, but chapter 1's phrase sits in the *first* pass-3 chunk
        // [0, 600] (at 10). The gap's sole missing number is then complete after that chunk, so
        // transcription stops immediately - the second chunk at 590 is never decoded.
        var (result, _, audio) = await DetectFullAsync(
            Options("--max-jingle-length", "0", "--expected-start-chapter", "1"),
            [new(1195, 1200)],
            s =>
            {
                s.Add(1200, Seg(0.2, " Chapter two."));
                s.Add(0, Seg(10, " Chapter one.")); // pass-3 chunk 1 [0, 600], phrase at 10
            });

        Assert.False(result.GapRemains);
        AssertChapters([new(1, 9.75), new(2, 1199.95)], result.Chapters);
        Assert.DoesNotContain(590.0, audio.DecodeStarts);
    }

    [Fact]
    public async Task TrailingScan_FindsAChapterAfterTheLastOneDetected()
    {
        // Chapter 3 is announced at 1799.95, past the last chapter pass 2 found and with nothing
        // above it - the one hole FindGaps structurally cannot see, since a sequence gap needs a
        // known chapter on either side. --trailing-scan transcribes from chapter 2's mark to the
        // end of the file and picks it up. (The scan's second chunk carries it: its first, starting
        // at chapter 2's own mark, is too close to that chapter's probe window for the scripted
        // transcriber to tell the two decodes apart.)
        var (result, _, audio) = await DetectFullAsync(
            Options("--max-jingle-length", "0", "--trailing-scan"),
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
        // reason --trailing-scan exists.
        var (result, _, audio) = await DetectFullAsync(
            Options("--max-jingle-length", "0"),
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
        // the entire book, which is pass 2's job. This also covers the --early-abort and
        // --expected-start-chapter aborts, both of which leave the chapter list empty.
        var (result, _, audio) = await DetectFullAsync(
            Options("--max-jingle-length", "0", "--trailing-scan"),
            [new(595, 600)],
            s => { });

        Assert.Empty(result.Chapters);
        // Nothing but probe-sized decodes: no 600 s pass-3 chunk was ever transcribed.
        Assert.All(audio.DecodeWindows, w => Assert.True(w.Duration is null or <= 60));
    }

    [Fact]
    public async Task TrailingScan_IgnoresANumberNotAboveEveryChapterAlreadyFound()
    {
        // An open-ended scan has no expected-number list to test a match against, so the only thing
        // that makes one new is topping every chapter already known. Here pass 2 finds 1 and 3 and
        // pass 3 fails to fill the gap, so chapter 2 is genuinely still missing - but hearing it
        // announced *after* chapter 3 is an in-text mention, not a chapter start. Accepting it would
        // report a find that Normalize then quietly drops again.
        var (result, log, _) = await DetectWithLogAsync(
            Options("--max-jingle-length", "0", "--trailing-scan"),
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
                                  l.Contains("not above every chapter already found"));
        Assert.DoesNotContain(log, l => l.Contains("chapter 2 found in gap"));
    }

    [Fact]
    public async Task ANumberLeavingALargeGap_IsReReadWithThePass3Model()
    {
        // BARDIOC.m4b, 2026-07-30: "neunzehn" (19) came back as 90 right after chapter 18, declaring
        // seventy chapters missing. Here the pass-2 transcriber hears 90 where chapter 2 is, and only
        // the pass-3 model reads it correctly - so the corrected number can only have come from the
        // re-read, and the mark keeps the position the original reading gave it.
        var (result, _, pass3) = await DetectWithPass3TranscriberAsync(
            Options("--model", "base", "--pass3-model", "large", "--max-jingle-length", "0",
                    "--quick-marks"),
            [new(595, 600)],
            pass2 =>
            {
                pass2.Add(0, Seg(0.5, " Chapter one."));
                pass2.Add(600, Seg(0.5, " Chapter ninety."));
            },
            pass3 => pass3.Add(600, Seg(0.5, " Chapter two.")));

        Assert.False(result.GapRemains);
        AssertChapters([new(1, 0.25), new(2, 600.25)], result.Chapters);
        // The re-read went through the pass-3 model with the file's own language applied.
        Assert.Contains("en", pass3.LanguageChanges);
    }

    [Fact]
    public async Task ANumberLeavingALargeGap_IsReReadFromAWiderWindow_WithNoPass3Model()
    {
        // Without --pass3-model there is no better recognizer to consult, so the same audio is asked
        // again through differently sized windows - which is a real second reading, since what Whisper
        // writes depends on the window a stretch arrives in. Chapter 2's announcement is scripted 3 s
        // before the probe window starts, so only the 45 s re-framing (which leads the announcement by
        // 12 s) ever sees it: the probe window itself starts at 600 and the 15 s re-framing at 600.5,
        // both past it.
        var (result, log, _) = await DetectWithLogAsync(
            Options("--max-jingle-length", "0", "--quick-marks"),
            [new(595, 600)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(600, Seg(2.5, " Chapter ninety."));  // the probe's own reading, phrase at 602.5
                s.Add(599.5, Seg(0, " Chapter two."));     // 3 s ahead of it, outside the probe window
            });

        Assert.False(result.GapRemains);
        AssertChapters([new(1, 0.25), new(2, 602.25)], result.Chapters);
        Assert.Contains(log, l => l.Contains("chapter 90 at 0:10:02.50 does not continue the sequence") &&
                                  l.Contains("would leave 88 missing"));
        Assert.Contains(log, l => l.Contains("a 45 s window read it as 2 instead of 90"));
    }

    [Fact]
    public async Task ANumberBelowTheSequence_IsReReadInsteadOfDiscarded()
    {
        // The mirror mishearing, and the more damaging one: a number heard *below* the sequence is
        // indistinguishable from an in-text mention of an earlier chapter, so it used to be dropped
        // without appeal and the chapter went missing. Re-reading first recovers it.
        var (result, log, _) = await DetectWithLogAsync(
            Options("--max-jingle-length", "0", "--quick-marks"),
            [new(595, 600), new(1195, 1200)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter five."));
                s.Add(1200, Seg(2.5, " Chapter two."));   // heard below chapter 5, phrase at 1202.5
                s.Add(1199.5, Seg(0, " Chapter six."));   // what the wider re-framing hears there
            },
            null);

        AssertChapters([new(5, 0.25), new(6, 1202.25)], result.Chapters);
        Assert.Contains(log, l => l.Contains("chapter 2 at 0:20:02.50 does not continue the sequence") &&
                                  l.Contains("it is not above it"));
        Assert.Contains(log, l => l.Contains("a 45 s window read it as 6 instead of 2"));
    }

    [Fact]
    public async Task AnOrdinaryGap_AndARepeatedAnnouncement_AreNotReRead()
    {
        // The two cases that must stay cheap. A gap of two chapters is the ordinary kind the re-probe
        // and pass 2.5/3 exist for, and a number equal to the last accepted one is an overlapping
        // window re-hearing a mark already placed - questioning either would spend transcriptions with
        // nothing to gain (and, for the repeat, could only "improve" by inventing the next number).
        var (result, log, _) = await DetectWithLogAsync(
            Options("--max-jingle-length", "0", "--quick-marks"),
            [new(595, 600), new(1195, 1200)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(600, Seg(0.5, " Chapter four."));   // leaves 2 and 3 missing: ordinary
                s.Add(1200, Seg(0.5, " Chapter four."));  // the same number again
            });

        Assert.True(result.GapRemains);
        AssertChapters([new(1, 0.25), new(4, 600.25)], result.Chapters);
        Assert.DoesNotContain(log, l => l.Contains("does not continue the sequence"));
        Assert.Contains(log, l => l.Contains("skipped chapter 4") &&
                                  l.Contains("not above the last accepted chapter 4"));
    }

    [Fact]
    public async Task Pass3_UsesTheSeparatePass3Transcriber_WhenOneIsGiven()
    {
        // Pass 2 finds only chapters 1 and 3 (its transcriber never hears chapter 2), leaving a
        // sequence gap. Chapter 2 lives *solely* in the pass-3 transcriber's script, so the gap can
        // only be filled if pass 3 actually routed through it - exactly what --pass3-model sets up.
        var (result, _, pass3) = await DetectWithPass3TranscriberAsync(
            Options("--max-jingle-length", "0"),
            [new(595, 600), new(1195, 1200)],
            pass2 =>
            {
                pass2.Add(0, Seg(0.5, " Chapter one."));
                pass2.Add(1200, Seg(0.2, " Chapter three."));
            },
            pass3 => pass3.Add(597.5, Seg(2.5, " Chapter two."))); // snapped gap-chunk seam

        Assert.False(result.GapRemains);
        AssertChapters([new(1, 0.25), new(2, 599.75), new(3, 1199.95)], result.Chapters);
        // The pass-3 transcriber had its language set before it was used (auto-detected "en").
        Assert.Contains("en", pass3.LanguageChanges);
    }

    [Fact]
    public async Task Pass25_ClosesTheGapWithACheapReProbe_BeforePass3EverTranscribesTheRegion()
    {
        // The whole point of pass 2.5: pass 2's own candidate probe at the gap's silence was right
        // on top of chapter 2's announcement, it just misheard it. Scripting chapter 2 into the
        // pass-3 transcriber at the *probe* position (600, the silence end) rather than at a
        // gap-chunk seam means only a pass-2-style re-probe can find it - a full pass-3
        // transcription decodes from the gap's start (0) instead and would come up empty.
        var (result, _, pass3) = await DetectWithPass3TranscriberAsync(
            Options("--model", "base", "--pass3-model", "large", "--max-jingle-length", "0"),
            [new(595, 600), new(1195, 1200)],
            pass2 =>
            {
                pass2.Add(0, Seg(0.5, " Chapter one."));
                pass2.Add(1200, Seg(0.2, " Chapter three."));
            },
            pass3 => pass3.Add(600, Seg(0.5, " Chapter two.")));

        Assert.False(result.GapRemains);
        AssertChapters([new(1, 0.25), new(2, 600.25), new(3, 1199.95)], result.Chapters);
        Assert.Contains(600, pass3.Audio.DecodeStarts);
        // Pass 3 proper never ran: every decode stayed probe-sized (12 s plain probe window), so
        // nothing was ever transcribed in pass 3's 600 s gap chunks. This is the saving pass 2.5
        // exists for - asserting on decode *lengths* rather than positions, since a gap region
        // starts at the bounding chapter's own mark and pass 2.5 legitimately probes there too.
        Assert.All(pass3.Audio.DecodeWindows, w => Assert.True(w.Duration is null or <= 60));
    }

    [Fact]
    public async Task Pass25_IsSkipped_WhenThePass3ModelIsNotAnUpgrade()
    {
        // A lighter (or equal) --pass3-model means a re-probe would only reach the same conclusion
        // more slowly, so pass 2.5 must not run at all - the gap goes straight to pass 3, which
        // here decodes the region from its start and finds chapter 2 there instead.
        var (result, _, pass3) = await DetectWithPass3TranscriberAsync(
            Options("--model", "large", "--pass3-model", "base", "--max-jingle-length", "0"),
            [new(595, 600), new(1195, 1200)],
            pass2 =>
            {
                pass2.Add(0, Seg(0.5, " Chapter one."));
                pass2.Add(1200, Seg(0.2, " Chapter three."));
            },
            pass3 => pass3.Add(597.5, Seg(2.5, " Chapter two."))); // snapped gap-chunk seam

        Assert.False(result.GapRemains);
        AssertChapters([new(1, 0.25), new(2, 599.75), new(3, 1199.95)], result.Chapters);
        // The gap-chunk decode from the gap's own start, i.e. pass 3 - not a pass 2.5 probe.
        AssertDecodedFrom(pass3, result.Chapters[0].TimeSeconds);
    }

    [Fact]
    public async Task Pass25_FallsThroughToPass3_WhenTheReProbeFindsNothing()
    {
        // Pass 2.5 runs (large beats base) but its probe hears nothing, so pass 3 must still get
        // its turn on the very same gap and close it from the full transcription.
        var (result, _, pass3) = await DetectWithPass3TranscriberAsync(
            Options("--model", "base", "--pass3-model", "large", "--max-jingle-length", "0"),
            [new(595, 600), new(1195, 1200)],
            pass2 =>
            {
                pass2.Add(0, Seg(0.5, " Chapter one."));
                pass2.Add(1200, Seg(0.2, " Chapter three."));
            },
            pass3 => pass3.Add(597.5, Seg(2.5, " Chapter two."))); // only findable by pass 3's chunking

        Assert.False(result.GapRemains);
        AssertChapters([new(1, 0.25), new(2, 599.75), new(3, 1199.95)], result.Chapters);
        Assert.Contains(0.25, pass3.Audio.DecodeStarts);
    }

    [Fact]
    public async Task Pass25_NeverAcceptsAChapterNumberOutsideTheGapItIsRecovering()
    {
        // The gap between chapters 1 and 3 expects only chapter 2. A re-probe that mishears its
        // way to "chapter 7" must not be able to plant it here - the region's own bounds reject
        // anything at or above the chapter that closes the gap, so the gap simply stays open.
        var (result, _, _) = await DetectWithPass3TranscriberAsync(
            Options("--model", "base", "--pass3-model", "large", "--max-jingle-length", "0"),
            [new(595, 600), new(1195, 1200)],
            pass2 =>
            {
                pass2.Add(0, Seg(0.5, " Chapter one."));
                pass2.Add(1200, Seg(0.2, " Chapter three."));
            },
            pass3 => pass3.Add(600, Seg(0.5, " Chapter seven.")));

        Assert.True(result.GapRemains);
        AssertChapters([new(1, 0.25), new(3, 1199.95)], result.Chapters);
    }

    [Fact]
    public async Task Pass25_ReportsProgressRelativeToTheGapsItBudgetedFor_NotAbsoluteFilePosition()
    {
        // Pass 2.5's phase total is the summed length of the gaps it will re-probe, so its progress
        // has to be measured in the same currency. Reporting the probe's absolute file position
        // instead pegged the bar at 100 % for the whole pass whenever the gap sat late in the file -
        // here a 999.7 s gap starting at 2400.25 s, where an absolute 3100 s would read as 310 %.
        var audio = new FakeAudioSource { Silences = [new(2395, 2400), new(3095, 3100), new(3395, 3400)] };
        var pass2 = new ScriptedTranscriber(audio);
        var pass3 = new ScriptedTranscriber(audio);
        pass2.Add(0, Seg(0.5, " Chapter one."));
        pass2.Add(2400, Seg(0.25, " Chapter two."));
        pass2.Add(3400, Seg(0.05, " Chapter four."));
        pass3.Add(3100, Seg(0.5, " Chapter three."));

        var tracker = new WorkTracker();
        var during = new List<double>();
        pass3.OnTranscribe = () =>
        {
            if (tracker.PhaseLabel == "Pass 2.5")
                during.Add(tracker.Fraction);
        };

        var detector = new ChapterDetector(
            Options("--model", "base", "--pass3-model", "large", "--max-jingle-length", "0", "--quick-marks"),
            audio, pass2, vad: null, pass3Transcriber: pass3);
        var result = await detector.DetectAsync(_file, Info, tracker, default, CancellationToken.None);

        // Pass 2.5 really ran and closed the gap (so the samples below are not an empty list).
        Assert.False(result.GapRemains);
        Assert.Contains(result.Chapters, c => c.Number == 3);
        Assert.NotEmpty(during);
        // The probe at 3100 s sits 699.75 s into a 999.7 s budget - nowhere near the clamp.
        Assert.All(during, f => Assert.InRange(f, 0.0, 0.8));
        // And the pass still lands exactly on 100 % when its last gap is done; nothing else began
        // a phase afterwards, since pass 3 found no gap left to fill.
        Assert.Equal("Pass 2.5", tracker.PhaseLabel);
        Assert.Equal(1.0, tracker.Fraction, 6);
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

    [Fact]
    public async Task AutoMinSilence_TightensThreshold_AndSkipsShorterSilences()
    {
        // Default --min-silence-length auto. Chapter 2's triggering silence is 5 s, tightening
        // the threshold to 3.75 s (0.75x); the 3 s silence at 700-703 falls below it and must
        // not be probed at all, but the 5 s silence at 900-905 still is, finding chapter 3.
        var (result, _, audio) = await DetectFullAsync(
            Options("--max-jingle-length", "0"),
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
        // detected, since pass 2 would never find a second chapter to compare against).
        // Tightening must only start once the second mark is found, using its own (genuine
        // inter-chapter) triggering silence instead.
        var (result, _, audio) = await DetectFullAsync(
            Options("--max-jingle-length", "0"),
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
        // closing the gap without needing pass 3 at all.
        var (result, _, audio) = await DetectFullAsync(
            Options("--max-jingle-length", "0"),
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
            Options("--max-jingle-length", "0"),
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
            Options("--max-jingle-length", "0"),
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

    [Fact]
    public async Task AutoMinSilence_AfterAGapRecovery_TheThresholdAccountsForTheGapMarksShorterSilence()
    {
        // Chapter 2 (5 s anchor) tightens the threshold to 3.75 s; chapter 3's 3 s silence is
        // skipped, chapter 4 is found -> sequence gap -> re-probe recovers chapter 3. Its 3 s
        // anchor must fold into the threshold (0.75 x 3 = 2.25 s), so chapter 5's 2.5 s
        // silence - below chapter 2's 3.75 s but above 2.25 s - is still probed and found.
        // Chapter 5 is the last mark, so nothing could recover it if it were skipped.
        var (result, _, audio) = await DetectFullAsync(
            Options("--max-jingle-length", "0"),
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
    }

    [Fact]
    public async Task AutoMaxJingle_AfterAGap_ReprobesAProbedCandidateAtTheCeilingWindow()
    {
        // The narrowed-window hole: chapter 2's 3 s jingle sizes the probe window down to
        // 1.25 x 3 + 5 = 8.75 s, but chapter 3's announcement sits 20 s after its silence, past
        // that window's end - so the candidate at 900 is probed, hears nothing, and would once have
        // been forgotten because only *skipped* candidates were remembered for a gap re-probe.
        // Chapter 4 reveals the gap, and the re-probe must re-decode 900 at the full 50 s ceiling
        // (--max-jingle-length's 45 s default plus the 5 s phrase margin), which does reach 920.
        var (result, log, audio) = await DetectWithLogAsync(
            Options("--verbose"),
            [new(595, 600), new(895, 900), new(1195, 1200)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(600, Seg(3.0, " Chapter two."));
                s.Add(900, Seg(20.0, " Chapter three."));
                s.Add(1200, Seg(3.0, " Chapter four."));
            },
            // Continuous speech: the VAD pre-pass runs (it must, or the window never adapts) but
            // contributes no non-speech region, so the candidates are the silences alone.
            new FakeVad { Speech = [new(0, 3600)] });

        Assert.False(result.GapRemains);
        Assert.Equal([1, 2, 3, 4], result.Chapters.Select(c => c.Number));
        // The narrowed first probe and the widened re-probe of the very same candidate. The pair is
        // what proves Pass 2 recovered the chapter: Pass 3 chunks the whole 600-1200 region and
        // never decodes exactly 50 s from 900.
        Assert.Contains((900.0, (double?)8.75), audio.DecodeWindows);
        Assert.Contains((900.0, (double?)50.0), audio.DecodeWindows);
        Assert.Contains(log, l => l.Contains("sequence gap between chapter 2 and 4") &&
                                  l.Contains("0 skipped, 1 at a wider window"));
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
            Options("--verbose", "--max-jingle-length", "0"),
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
        // The first re-probe candidate ran; the two behind it never did.
        Assert.Contains(700.0, audio.DecodeStarts);
        Assert.DoesNotContain(705.0, audio.DecodeStarts);
        Assert.DoesNotContain(710.0, audio.DecodeStarts);
        Assert.Single(log, l => l.Contains("chapter 3 detected"));
        Assert.Contains(log, l => l.Contains("gap before chapter 4 closed") &&
                                  l.Contains("stopped after 1 of 3 candidate(s)"));
    }

    [Fact]
    public async Task AGapRecoveredChapter_WidensTheJingleWindow_ByAtMostTheGrowthCap()
    {
        // Chapter 2's 3 s jingle narrows the window to 8.75 s. Chapter 3's announcement sits 20 s
        // after its silence, so the narrowed window misses it and only the ceiling re-probe finds it.
        // The 1 s silence at 919 is what makes this the BARDIOC.m4b shape rather than a case the
        // existing machinery already handles: the mark anchors to *that* silence, so the jingle
        // observation measures ~0 s, falls under its 2 s floor and is discarded - the reach is the
        // only thing left to learn from. The reach it reports (27 s) is far above what one recovery may
        // apply, so the growth cap holds the window to 1.25 x 8.75 = 10.9 s. Chapter 5's announcement,
        // 9.5 s after its own silence, is the proof something was learned all the same: out of reach at
        // 8.75 s, inside the capped width, and being the last mark nothing could recover it if it were
        // missed.
        var (result, log, _) = await DetectWithLogAsync(
            Options("--verbose"),
            [new(595, 600), new(895, 900), new(919, 920), new(1195, 1200), new(1495, 1500)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(600, Seg(3.0, " Chapter two."));
                s.Add(900, Seg(20.0, " Chapter three."));
                s.Add(1200, Seg(3.0, " Chapter four."));
                s.Add(1500, Seg(9.5, " Chapter five."));
            },
            new FakeVad { Speech = [new(0, 3600)] });

        Assert.False(result.GapRemains);
        Assert.Equal([1, 2, 3, 4, 5], result.Chapters.Select(c => c.Number));
        // 22 s of reach (the phrase ends 2 s after its 20 s onset) plus the 5 s phrase margin.
        Assert.Contains(log, l => l.Contains("chapter 3 needed 22 s of probe window") &&
                                  l.Contains("widened to 10.9 s (capped from 27 s)"));
        Assert.Contains(log, l => l.Contains("jingle probe window restored to 10.9 s"));
    }

    [Fact]
    public async Task AGapRecoveredChapter_WithinTheGrowthCap_GetsItsFullReach()
    {
        // The counterpart to the capped case: chapter 2's 20 s jingle puts the window at 30 s, so
        // chapter 3's 32.2 s reach asks for 37.2 s - under the 37.5 s one recovery may grant, and
        // therefore honoured in full and without the "capped from" note. Reaches only fit under the cap
        // once the window is past 20 s (4 x the phrase margin); below that a miss is always further out
        // than one step of growth can follow, which is what the capped test covers.
        var (result, log, _) = await DetectWithLogAsync(
            Options("--verbose"),
            [new(595, 600), new(895, 900), new(929, 930), new(1195, 1200)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(600, Seg(20.0, " Chapter two."));
                s.Add(900, Seg(30.2, " Chapter three."));
                s.Add(1200, Seg(0.3, " Chapter four."));
            },
            new FakeVad { Speech = [new(0, 3600)] });

        Assert.False(result.GapRemains);
        Assert.Equal([1, 2, 3, 4], result.Chapters.Select(c => c.Number));
        Assert.Contains(log, l => l.Contains("chapter 3 needed 32.2 s of probe window") &&
                                  l.Contains("widened to 37.2 s") && !l.Contains("capped"));
    }

    [Fact]
    public async Task AfterAGap_WithNothingSkippedOrNarrowed_SaysSoRatherThanReprobing()
    {
        // Every candidate was probed at the full window (--max-jingle-length 0 pins it, an explicit
        // --min-silence-length skips nothing), so a gap leaves Pass 2 with nothing to retry. The log
        // has to say that: without the note, this case and "a candidate was declined" look identical
        // from the outside, which is the first thing worth knowing when a chapter goes missing.
        var (result, log, _) = await DetectWithLogAsync(
            Options("--verbose", "--max-jingle-length", "0", "--min-silence-length", "1.5"),
            [new(595, 600), new(1195, 1200)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(600, Seg(0.3, " Chapter two."));
                s.Add(1200, Seg(0.3, " Chapter four."));
            });

        Assert.True(result.GapRemains);
        Assert.Contains(log, l => l.Contains("sequence gap between chapter 2 and 4") &&
                                  l.Contains("nothing to re-probe since the last mark"));
        Assert.DoesNotContain(log, l => l.Contains("re-probing"));
    }

    [Fact]
    public async Task ExplicitMinSilenceLength_NeverSkipsAnyDetectedSilence()
    {
        // With an explicit numeric --min-silence-length, adaptive tightening is off: every
        // silence from pass 1 is probed regardless of length or what was found before it.
        var (result, _, audio) = await DetectFullAsync(
            Options("--min-silence-length", "1.5", "--max-jingle-length", "0"),
            [new(595, 600), new(700, 703), new(900, 905)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(600, Seg(0.3, " Chapter two."));
                s.Add(905, Seg(0.2, " Chapter three."));
            });

        AssertChapters([new(1, 0.25), new(2, 600.05), new(3, 904.95)], result.Chapters);
        Assert.Contains(703, audio.DecodeStarts);
    }

    [Fact]
    public async Task InTextMentions_OfEarlierChapters_AreDropped()
    {
        // "chapter two" spoken inside chapter 3's probe window is a regression and must
        // not override the already detected chapter sequence.
        var result = await DetectAsync(
            Options("--max-jingle-length", "0"),
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
        // at all; silencedetect alone would never produce a Pass 2 candidate here. The mark
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
    public async Task JingleWithLeadingSilence_WalksBackToTheSilenceEnd_AndVadDoesNotDoubleProbe()
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
        // second's).
        var (result, _, audio) = await DetectFullAsync(
            Options("--quick-marks", "--mark-before-jingle"),
            [new(695, 700)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(700, Seg(3.2, " Chapter two."));
            },
            new FakeVad { Speech = [new(0, 695), new(703, 3600)] });

        AssertContainsChapter(new DetectedChapter(2, 700), result.Chapters);
        // Exact 700.0 is the anchor probe itself; the walked mark now also landing at 700
        // means the final quiet-point snap's own decode sits nearby (699.85) but is a distinct
        // value, so this still isolates "no duplicate probe decode" without being confused by it.
        Assert.Single(audio.DecodeStarts, d => d == 700.0);
    }

    [Fact]
    public async Task DefaultMode_PlacesMarkAtAFixedOffsetBeforeThePhrase_AndWidensTheProbeWindow()
    {
        // No options at all: --mark-before-jingle is off (so the mark is not anchored to the
        // triggering silence at all) but --max-jingle-length keeps its 45 s default, so Pass 2's
        // probe window is jingle-ceiling-wide (50 s) even though nothing jingle-related was
        // named explicitly - jingle-aware probing is now the unconditional default. The mark
        // still lands at a flat 0.25 s before the phrase, "no matter what exists there".
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
            w => w.Start == 600 && w.Duration is { } d && Math.Abs(d - 50) < 0.01);
    }

    [Fact]
    public async Task MaxJingleLengthZero_NarrowsTheProbeWindowBackToPlain_ButKeepsTheNewOffset()
    {
        // The same layout as DefaultMode_PlacesMarkAtAFixedOffsetBeforeThePhrase_..., but with
        // --max-jingle-length 0: "no jingle expected at all" narrows Pass 2's probe window back
        // to the plain 12 s width - this is what makes the option "essentially plain-mode" - yet
        // the mark placement formula is unaffected either way, since it never depended on the
        // probe width to begin with.
        var (result, _, audio) = await DetectFullAsync(
            Options("--max-jingle-length", "0"),
            [new(595, 600)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(600, Seg(0.3, " Chapter two."));
            });

        AssertChapters([new(1, 0.25), new(2, 600.05)], result.Chapters);
        Assert.Contains(audio.DecodeWindows,
            w => w.Start == 600 && w.Duration is { } d && Math.Abs(d - 12) < 0.01);
    }

    [Fact]
    public async Task MarkBeforeJingleWithMaxJingleLengthZero_StillAnchorsViaVad_ButKeepsTheNarrowWindow()
    {
        // --mark-before-jingle turns on the VAD pre-pass and its own backward-walk mark
        // placement (see JingleWithLeadingSilence_WalksBackToTheSilenceEnd_... for why this lands
        // at 700, the stored silence's own end and the jingle's true start), but
        // --max-jingle-length 0 says no jingle is expected, so Pass 2's probe window must stay at
        // the plain 12 s width rather than widening to the jingle ceiling - the two options are
        // independent: one controls mark placement, the other the probe width.
        var (result, _, audio) = await DetectFullAsync(
            Options("--quick-marks", "--mark-before-jingle", "--max-jingle-length", "0"),
            [new(695, 700)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(700, Seg(3.2, " Chapter two."));
            },
            new FakeVad { Speech = [new(0, 695), new(703, 3600)] });

        AssertContainsChapter(new DetectedChapter(2, 700), result.Chapters);
        Assert.Contains(audio.DecodeWindows,
            w => w.Start == 700 && w.Duration is { } d && Math.Abs(d - 12) < 0.01);
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
        // Same false-pause-before-silence-less-jingle shape, now with --max-jingle-length auto
        // and (default) --min-silence-length auto, laid out so that both auto mechanisms would
        // visibly misbehave if the false pause were mistaken for chapter two's anchor:
        //
        //   * --min-silence-length: the false pause is 3 s, so the buggy path tightens the
        //     threshold to 2.25 s (0.75x). Chapter three's genuine 2 s inter-chapter silence
        //     (1000-1002) would then be skipped and lost. The correct path takes chapter two's
        //     anchor from the VAD region (Silence = null), tightens nothing, and finds chapter
        //     three - so the result must be [1, 2, 3], not [1, 2].
        //   * --max-jingle-length: the buggy path measures the jingle as phrase - pause.End =
        //     32 s and keeps the window at ~45 s; the correct path measures the VAD region (5 s)
        //     and narrows it to ~11 s. The spurious 20 s music-bed region at 700-720 must then
        //     be skipped (too long to be this book's jingle) rather than probed.
        var (result, _, audio) = await DetectFullAsync(
            Options("--quick-marks", "--mark-before-jingle", "--max-jingle-length", "auto"),
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
        Assert.DoesNotContain(700.0, audio.DecodeStarts);                    // window narrowed to ~11 s
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
        // alone inside a jingle and the candidate's window runs the full 50 s ceiling, past Whisper's
        // 30 s decode chunk - at which point the lone word is dropped from the transcript entirely
        // while the same audio from the same position is transcribed correctly over 17.5 s or 23.5 s.
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
                s.AddWithin(30, 655, Seg(0, " Chapter two.")); // window [613, ...] is 50 s and misses it
            },
            new FakeVad { Speech = [new(0, 610), new(613, 640), new(654.8, 655.3), new(660, 3600)] });

        Assert.False(result.GapRemains);
        AssertChapters([new(1, 0.25), new(2, 640)], result.Chapters);
        Assert.Contains(log, l => l.Contains("re-reading it in a shorter window"));
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

        Assert.DoesNotContain(log, l => l.Contains("re-reading it in a shorter window"));
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
        // start timestamp (638) pulled back before the VAD region (640-660) even begins, so
        // plain containment finds no region and the mark would fall back to the false in-text
        // pause that triggered the probe (610-613, marking at 612.5). The segment's span betrays
        // the smear: it overlaps the region by 18 s, so the region is rescued as the jingle and
        // the mark lands at its start (640).
        var result = await DetectAsync(
            Options("--quick-marks", "--mark-before-jingle", "--min-silence-length", "1.5"),
            [new(610, 613)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(613, new TranscriptSegment(25, 45, " Chapter two.", 1.0)); // abs 638-658, smeared
            },
            new FakeVad { Speech = [new(0, 640), new(660, 3600)] });

        Assert.False(result.GapRemains);
        AssertChapters([new(1, 0.25), new(2, 640)], result.Chapters);
    }

    [Fact]
    public async Task DefaultMode_PhraseSmearedAcrossTheJingle_FloorsAtTheRegionEnd_NotBeforeIt()
    {
        // The default-mode (no --mark-before-jingle) counterpart to the smeared-phrase test above:
        // --mark-before-jingle's ComputeJingleMark never trusts phraseAbs once a jingle anchor is
        // resolved, but the default path used to trust it blindly, landing the mark 0.25 s before
        // the smeared timestamp (637.75) - inside the *previous* chapter's narration, well before
        // the jingle (640-660) even starts. With no VAD speech blip inside the region to pinpoint
        // the true onset (see ResolveDefaultPhraseOnset's other test below for that case), it falls
        // back to flooring phraseAbs at the resolved region's own end (660), so the mark instead
        // lands at 659.75 - late into the jingle rather than early into the wrong chapter.
        var result = await DetectAsync(
            Options("--quick-marks", "--min-silence-length", "1.5"),
            [new(610, 613)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(613, new TranscriptSegment(25, 45, " Chapter two.", 1.0)); // abs 638-658, smeared
            },
            new FakeVad { Speech = [new(0, 640), new(660, 3600)] });

        Assert.False(result.GapRemains);
        AssertChapters([new(1, 0.25), new(2, 659.75)], result.Chapters);
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
        // and merges it into the surrounding non-speech run, so the resolved region's end (657)
        // lands exactly *between* the two blips, mid-word. Whisper's own timestamp for the smeared
        // "Chapter two." segment (638-658) is no help - it is the reason the region had to be
        // rescued via the segment-span overlap in the first place. But the swallowed blip itself
        // (656-656.6) is real VAD data pinpointing the announcement's true onset: the mark must
        // land 0.25s before its start (655.75), not at the region's end (656.75) as a plain floor
        // would give.
        var result = await DetectAsync(
            Options("--quick-marks", "--min-silence-length", "1.5"),
            [new(610, 613)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(613, new TranscriptSegment(25, 45, " Chapter two.", 1.0)); // abs 638-658, smeared
            },
            new FakeVad { Speech = [new(0, 640), new(656, 656.6), new(657, 658.2), new(660, 3600)] });

        Assert.False(result.GapRemains);
        AssertChapters([new(1, 0.25), new(2, 655.75)], result.Chapters);
    }

    [Fact]
    public async Task DefaultMode_AnnouncementSplitAcrossTwoAdjacentBlips_MarksAtTheFirstBlipStart_NotTheLast()
    {
        // Real-world confirmed bug (Perry Rhodan "Die Dritte Macht", chapter 31, 2026-07-23):
        // unlike chapter 35 above (only "Kapitel" was swallowed - "35" itself was long enough to
        // end the region), here BOTH short words of "Kapitel 31" got swallowed into the same
        // merged region, 0.2s apart. The prior fix took only the *last* swallowed blip (656-656.6,
        // "31"'s stand-in here), landing the mark right after "Kapitel" (655.2-655.8) had already
        // been spoken - confirmed live by re-transcribing 5.25s starting at that mark and getting
        // unrelated narration instead of the phrase. Clustering the swallowed blips by the same
        // short-gap threshold that grouped them into one region, then anchoring to the *first*
        // blip of the *last* cluster, lands on "Kapitel"'s own onset (655.2) instead of "31"'s
        // (656).
        var result = await DetectAsync(
            Options("--quick-marks", "--min-silence-length", "1.5"),
            [new(610, 613)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(613, new TranscriptSegment(25, 45, " Chapter two.", 1.0)); // abs 638-658, smeared
            },
            new FakeVad
            {
                Speech = [new(0, 640), new(655.2, 655.8), new(656, 656.6), new(657, 658.2), new(660, 3600)],
            });

        Assert.False(result.GapRemains);
        AssertChapters([new(1, 0.25), new(2, 654.95)], result.Chapters);
    }

    [Fact]
    public async Task DefaultMode_IsolatedEarlierBlip_SeparatedByALongGap_IsNotTreatedAsPartOfTheAnnouncement()
    {
        // Guard for the clustering fix's other half: an early speech blip inside the jingle region,
        // separated from the true announcement blip by a gap well over MergeShortSpeechGapSeconds
        // (here 50.5s - an incidental musical vocal transient near the jingle's start, not part of
        // "Chapter two"), must form its own separate cluster and be ignored, not pull the mark back
        // to it. The mark must still land at the true (later) cluster's own start (656), matching
        // the single-blip case above.
        var result = await DetectAsync(
            Options("--quick-marks", "--min-silence-length", "1.5"),
            [new(610, 613)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(613, new TranscriptSegment(25, 45, " Chapter two.", 1.0)); // abs 638-658, smeared
            },
            new FakeVad
            {
                Speech = [new(0, 600), new(605, 605.5), new(656, 656.6), new(657, 658.2), new(660, 3600)],
            });

        Assert.False(result.GapRemains);
        AssertChapters([new(1, 0.25), new(2, 655.75)], result.Chapters);
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
            Options("--min-silence-length", "1.5", "--max-jingle-length", "30"),
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
        // Why the search takes no help at all from VAD: the announcement at 645 sits where no VAD
        // speech segment starts, and the two segments VAD does offer (656, 657) carry unrelated
        // music. Bracketing the matched segment and narrowing in on the phrase finds it anyway,
        // starting from a mark the smeared abs-638 transcript put 7 s early.
        var result = await DetectAsync(
            Options("--min-silence-length", "1.5", "--max-jingle-length", "30"),
            [new(610, 613)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(613, new TranscriptSegment(25, 45, " Chapter two.", 1.0)); // abs 638-658, smeared
                s.Add(645, Seg(0, " Chapter two."));                        // the real announcement onset
                s.Add(656, new TranscriptSegment(0, 0.5, " Music", 1.0));   // VAD candidate, not the phrase
                s.Add(657, new TranscriptSegment(0, 1.2, " Music", 1.0));   // VAD candidate, not the phrase
            },
            new FakeVad { Speech = [new(0, 640), new(656, 656.6), new(657, 658.2), new(660, 3600)] });

        Assert.False(result.GapRemains);
        AssertChapters([new(1, 0.25), new(2, 644.75)], result.Chapters);
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
            659.75, _file, null, profile.PhraseRegex, 650, 662, 700, CancellationToken.None);

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
            641.06, _file, null, profile.PhraseRegex, 645.2, 647.2, 651.7, CancellationToken.None);

        Assert.True(result.PhraseHeard);
        AssertMarkTime("chapter two", 639.75, result.Mark);
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

        // Anchor 54.19: one phrase margin past the segment end would be 59.2, but the transcript
        // ends first - the clamp that produced the too-short probes.
        var result = await refiner.RefinePreciseMarkAsync(
            52.9, _file, null, profile.PhraseRegex, 52.7, 54.2, 54.19, CancellationToken.None);

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

    [Fact]
    public async Task AutoMaxJingle_MeasuresJingleUpToThePhrase_NotTheInflatedRegionEnd()
    {
        // Chapter two's jingle is really only 5 s (800-805), but its VAD non-speech region runs
        // to 825 - inflated because the short "Chapter two" announcement, spoken over the music,
        // got merged back into the region (the ComputeNonSpeechRegions short-speech-gap merge).
        // The phrase (at 805) sits inside that region. --max-jingle-length auto must measure the
        // jingle up to the phrase (min(regionEnd, phrase) - regionStart = 5 s), not the full 25 s
        // region span: 5 s resizes the window to ~11 s, so chapter three's 15 s decoy region at
        // 900 is skipped (too long for this book's jingle); the inflated 25 s would have kept the
        // window wide enough to probe it. A false pause (770-773) triggers the probe so the jingle
        // is resolved by region lookup.
        var (result, _, audio) = await DetectFullAsync(
            Options("--mark-before-jingle", "--max-jingle-length", "auto"),
            [new(770, 773)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(773, Seg(32, " Chapter two.")); // window [773, ...], phrase at 805
            },
            new FakeVad { Speech = [new(0, 800), new(825, 900), new(915, 3600)] });

        Assert.Equal([1, 2], result.Chapters.Select(c => c.Number));
        AssertContainsChapter(new DetectedChapter(2, 800), result.Chapters); // jingle start, clipped length fed to auto
        Assert.DoesNotContain(900.0, audio.DecodeStarts);              // window narrowed to ~11 s, decoy skipped
    }

    [Fact]
    public async Task AutoMaxJingle_DoesNotResizeFromTheFirstMark()
    {
        // Chapter one's own silence-less jingle (region 50-53, 3 s) is found via a candidate
        // whose start (50) is not the file's absolute beginning - exactly what happens whenever
        // a book has any preface/intro before the first chapter announcement. That observation
        // must not resize the probe window, for the same reason the intro-to-chapter-1 silence
        // must not tighten --min-silence-length: it is not a real inter-chapter jingle length yet
        // (there has been no second mark to compare it against). If it wrongly did, the window
        // would narrow to ~8.75 s (1.25 * 3 + 5), and chapter two's own, much longer (20 s)
        // silence-less jingle region (600-620) would then be skipped outright (too long for that
        // wrongly-narrowed window) - so chapter two must still be found.
        // The file-start window [0, 50] abuts the jingle region and gets its end forward-snapped
        // to the region's mid-point (51.5), so chapter one's announcement (right after the
        // jingle, at 53.2) is heard by that very first probe; the region candidate at 50 is
        // then skipped as part of chapter one's overlap sequence.
        var (result, _, audio) = await DetectFullAsync(
            Options("--mark-before-jingle", "--max-jingle-length", "auto"),
            [],
            s =>
            {
                s.Add(0, Seg(53.2, " Chapter one."));
                s.Add(600, Seg(0.3, " Chapter two."));
            },
            new FakeVad { Speech = [new(0, 50), new(53, 600), new(620, 3600)] });

        Assert.Equal([1, 2], result.Chapters.Select(c => c.Number));
        Assert.Contains(600.0, audio.DecodeStarts);
    }

    [Fact]
    public async Task AutoMaxJingle_NeverNarrowsTheWindow_AfterALongerJingleWasObserved()
    {
        // The mirror image of the monotonic --min-silence-length rule: chapter 2's 8 s jingle
        // sizes the window to 15 s (1.25 x 8 + 5); chapter 3's shorter 4 s jingle must NOT
        // narrow it back down to 10 s - a window below an already observed jingle length would
        // be too short for exactly the kind of jingle this book has proven to play. Chapter 4's
        // 12 s jingle region (over the wrongly-narrowed 10 s, under the correct 15 s) must
        // therefore still be probed and found - and being the last chapter, it could never be
        // recovered by a gap re-probe if it were skipped.
        var (result, _, audio) = await DetectFullAsync(
            Options("--mark-before-jingle", "--max-jingle-length", "auto"),
            [],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(600, Seg(8, " Chapter two."));
                s.Add(1000, Seg(4, " Chapter three."));
                s.Add(1400, Seg(12, " Chapter four."));
            },
            new FakeVad { Speech = [new(0, 600), new(608, 1000), new(1004, 1400), new(1412, 3600)] });

        Assert.Equal([1, 2, 3, 4], result.Chapters.Select(c => c.Number));
        Assert.Contains(1400.0, audio.DecodeStarts);
    }

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
    public async Task LowConfidenceSegment_CarriesConfidence_AndIsFlagged()
    {
        var result = await DetectAsync(
            Options("--max-jingle-length", "0"),
            [new(595, 600)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one.", confidence: 0.95));
                s.Add(600, Seg(0.3, " Chapter two.", confidence: 0.2));
            });

        AssertChapters(
            [new(1, 0.25, 0.95), new(2, 600.05, 0.2)],
            result.Chapters);
        Assert.Equal([2], result.LowConfidenceNumbers);
    }

    [Fact]
    public async Task HighConfidenceSegments_YieldNoLowConfidenceFlags()
    {
        var result = await DetectAsync(
            Options("--max-jingle-length", "0"),
            [new(595, 600)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(600, Seg(0.3, " Chapter two."));
            });

        Assert.Empty(result.LowConfidenceNumbers);
    }

    [Fact]
    public async Task NoSpeechAnywhere_YieldsNoChapters()
    {
        var result = await DetectAsync(Options("--max-jingle-length", "0"), [new(595, 600)], _ => { });
        Assert.Empty(result.Chapters);
        Assert.False(result.GapRemains);
    }

    [Fact]
    public async Task OverlappingProbe_ReusesTheCachedTranscript_InsteadOfReDecodingTheOverlap()
    {
        // Two silences 6 s apart give overlapping 12 s probe windows ([600, 612] and [606, 618]).
        // Chapter one is found by the first probe - deliberately at low confidence, so the
        // overlap-sequence skip stays out of the way (a low-confidence mark must not skip the
        // windows that could re-detect the transition it may have gotten wrong) and the reuse
        // path is actually exercised. The second probe must not re-decode the shared [606, 612]
        // span: neither silence lies fully within window 2 ([606, 618]), so
        // FindOverlapSplitPoint falls back to the original border (612, no snap, no reach-back) -
        // that is where the fresh tail decode starts, and the candidate's own start (606) never is.
        // The detected chapter is unaffected by the optimization.
        var (result, _, audio) = await DetectFullAsync(
            Options("--min-silence-length", "1.5", "--max-jingle-length", "0"),
            [new(595, 600), new(601, 606)],
            s => s.Add(600, Seg(0.5, " Chapter one.", confidence: 0.3)));

        AssertChapters([new DetectedChapter(1, 600.25, 0.3)], result.Chapters);
        Assert.Contains(612.0, audio.DecodeStarts);        // fresh tail only (fallback: the border itself)
        Assert.DoesNotContain(606.0, audio.DecodeStarts);  // the overlap was reused, not re-decoded
    }

    [Fact]
    public async Task ConfidentMark_SkipsTheRemainingWindowsOfItsOverlapSequence()
    {
        // Same layout as above, but chapter one is found confidently: the mark settles the
        // whole overlapping window sequence, so the candidate at 606 is skipped outright - no
        // fresh tail decode at the border (612), no decode at the candidate start (606), and
        // this works with an explicit --min-silence-length (no adaptive skipping involved).
        var (result, _, audio) = await DetectFullAsync(
            Options("--min-silence-length", "1.5", "--max-jingle-length", "0"),
            [new(595, 600), new(601, 606)],
            s => s.Add(600, Seg(0.5, " Chapter one.")));

        AssertChapters([new DetectedChapter(1, 600.25)], result.Chapters);
        Assert.DoesNotContain(612.0, audio.DecodeStarts);
        Assert.DoesNotContain(606.0, audio.DecodeStarts);
    }

    [Fact]
    public async Task SequenceSkippedWindow_IsReProbed_WhenASequenceGapTurnsUp()
    {
        // Chapter two's confident mark skips the overlapping window at 608 - and chapter three's
        // announcement hides inside it, at 615.4 (the "sequence spans two transitions" case the
        // skip bets against). It sits past 612, where chapter two's own 12 s window ends, so the
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
        // The window at 608 was skipped first and only decoded by the gap re-probe, i.e.
        // after the probe at 1200 that revealed the gap.
        Assert.True(audio.DecodeStarts.IndexOf(608) > audio.DecodeStarts.IndexOf(1200));
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
            Options("--min-silence-length", "1.5", "--max-jingle-length", "0"),
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
            Options("--min-silence-length", "1.5", "--max-jingle-length", "0"),
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
        // Nothing at all separates the phrase at 609 from the window start, so there is no anchor
        // to pinpoint a mark at. The number was heard, though, so the log has to say so - "never
        // heard" and "heard but unanchorable" call for completely different fixes.
        var (result, log, _) = await DetectWithLogAsync(
            Options("--min-silence-length", "1.5", "--max-jingle-length", "0"),
            [new(595, 600)],
            s => s.Add(600, Seg(9, " Chapter one.")));

        Assert.Empty(result.Chapters);
        Assert.Contains(log, l =>
            l.Contains("skipped chapter 1 at 0:10:09.00") &&
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
        // Nothing separates the mention at 1209 from its window start at 1200, so there is no
        // anchor for it, exactly as in DeepPhrase_WithNoSilenceBeforeIt_LogsWhyItWasSkipped.
        var word = value.Split(':')[0];
        var (result, log, _) = await DetectWithLogAsync(
            Options(option, value, "--min-silence-length", "1.5", "--max-jingle-length", "0"),
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
            l.Contains("no silence precedes it inside the probe window"));
    }

    [Fact]
    public async Task NamedPhrase_DirectlyAfterItsSilence_IsStillAccepted()
    {
        // The other half of the rule: tightening acceptance must not cost a real announcement its
        // mark. This one starts 0.2 s into its window, well inside the 5 s the timing rule grants.
        var result = await DetectAsync(
            Options("--custom", "zeittafel:Zeittafel", "--max-jingle-length", "0"),
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
            Options("--min-silence-length", "1.5", "--max-jingle-length", "0"),
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
            Options("--min-silence-length", "1.5", "--max-jingle-length", "0"),
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
            Options("--max-jingle-length", "0"),
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
        // Window 1 (candidate 600, --min-silence-length 1.5) naturally spans [600, 612];
        // window 2 (candidate 606) spans [606, 618] and overlaps it. A short 0.6 s silence at
        // [608, 608.6] is well below the 1.5 s candidate threshold - it never becomes a Pass 2
        // candidate of its own - but is still retained down to the 0.5 s floor
        // (MinStoredSilenceSeconds) purely as a seam target, and it lies inside window 2.
        // The window-end plan (PlanWindowEnd) must move the shared border to its
        // mid-point (608.3) before anything is decoded: window 1's decode itself ends there
        // (8.3 s instead of the natural 12), and window 2's fresh tail starts exactly there -
        // never at the raw border (612) or the candidate start (606). Chapter one is scripted
        // at low confidence so the overlap-sequence skip stays out of the way and window 2 is
        // actually probed.
        var (_, _, audio) = await DetectFullAsync(
            Options("--min-silence-length", "1.5"),
            [new(595, 600), new(603, 606), new(608, 608.6)],
            s => s.Add(600, Seg(0.5, " Chapter one.", confidence: 0.3)));

        Assert.Contains(608.3, audio.DecodeStarts);
        Assert.DoesNotContain(612.0, audio.DecodeStarts);
        Assert.DoesNotContain(606.0, audio.DecodeStarts);
        Assert.Contains(audio.DecodeWindows,
            w => w.Start == 600 && w.Duration is { } d && Math.Abs(d - 8.3) < 0.01);
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
            Options("--mark-before-jingle"),
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
        // Window 1 naturally spans [600, 612], window 2 [606, 618] (border 612). The only seam
        // target lies entirely *beyond* the border ([613, 614], mid-point 613.5). Because
        // window 1's end is planned before window 1 is decoded (PlanWindowEnd), its decode is
        // simply *extended* to 613.5 and window 2's fresh tail starts exactly there: the plan moved the border
        // itself, so no [612, 613.5) hole can exist and nothing is cut mid-word at 612.
        // Chapter one is scripted at low confidence so the overlap-sequence skip stays out
        // of the way and window 2 is actually probed.
        var (_, _, audio) = await DetectFullAsync(
            Options("--min-silence-length", "1.5"),
            [new(595, 600), new(603, 606), new(613, 614)],
            s => s.Add(600, Seg(0.5, " Chapter one.", confidence: 0.3)));

        Assert.Contains(613.5, audio.DecodeStarts);
        Assert.DoesNotContain(612.0, audio.DecodeStarts);
        Assert.DoesNotContain(606.0, audio.DecodeStarts);
        Assert.Contains(audio.DecodeWindows,
            w => w.Start == 600 && w.Duration is { } d && Math.Abs(d - 13.5) < 0.01);
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
        // (split at 608.3), but this asserts on the --verbose-transcripts log itself: the tail
        // probe's log line must show only what was actually decoded from 608.3 onward, at Whisper's
        // own (0-based) timestamps - not the reused segment restated at window-relative time, and
        // not a span reaching all the way to the window's nominal end (618). Chapter one is scripted
        // at low confidence so the overlap-sequence skip stays out of the way.
        var (_, log, _) = await DetectWithLogAsync(
            Options("--verbose-transcripts", "--min-silence-length", "1.5", "--max-jingle-length", "0"),
            [new(595, 600), new(603, 606), new(608, 608.6)],
            s =>
            {
                s.Add(600, Seg(0.5, " Chapter one.", confidence: 0.3));
                s.Add(608.3, Seg(1.0, " some fresh words"));
            });

        // The label carries the actually decoded length: split at 608.3, window end 618 -> 9.7 s.
        var tailLine = Assert.Single(log, l => l.StartsWith($"probe {9.7:0.#}s@0:10:08.30 (tail)"));
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

        var (_, plain, _) = await DetectWithLogAsync(Options("--max-jingle-length", "0"), silences, script);
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
        // "one." only exists in window 2's freshly decoded tail (from the 608.3 split point).
        // Extracting the chapter number therefore has to reach across the cache/fresh boundary -
        // FindPhraseMatches must flag that detection, and DetectAsync must log it.
        var (result, log, _) = await DetectWithLogAsync(
            Options("--min-silence-length", "1.5", "--max-jingle-length", "0"),
            [new(595, 600), new(603, 606), new(608, 608.6)],
            s =>
            {
                s.Add(600, Seg(6.5, " Chapter")); // abs 606.5 - reused by window 2
                s.Add(608.3, Seg(0, " one."));     // abs 608.3 - fresh tail of window 2
            });

        AssertChapters([new DetectedChapter(1, 606.25)], result.Chapters);
        Assert.Contains(log, l => l.Contains("chapter 1 detection spans the reused/fresh transcript merge"));
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
        // Integration check for the stand-alone end snap: the probe window at 600 has no
        // overlapping neighbor, and the stored 0.8 s silence at [613.2, 614] (sub-threshold,
        // seam target only) sits within the 5 s forward search past the natural end (612) -
        // the decode itself must run 13.6 s, up to the mid-point (613.6).
        var (result, _, audio) = await DetectFullAsync(
            Options("--min-silence-length", "1.5", "--max-jingle-length", "0"),
            [new(595, 600), new(613.2, 614)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(600, Seg(0.3, " Chapter two."));
            });

        AssertChapters([new(1, 0.25), new(2, 600.05)], result.Chapters);
        Assert.Contains(audio.DecodeWindows,
            w => w.Start == 600 && w.Duration is { } d && Math.Abs(d - 13.6) < 0.01);
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
        // backward walk lands the mark right at that silence's own end (831.0, the jingle's true
        // start per silencedetect's amplitude-based measurement) rather than at VAD's slightly
        // jittery speech-segment boundary a moment earlier (830.5) - the same silence-anchoring
        // preference default-mode placement already has via LeadingSilence, and the same shape
        // validated in JingleWithLeadingSilence_WalksBackToTheSilenceEnd_....
        var result = await DetectAsync(
            Options("--quick-marks", "--mark-before-jingle"),
            [new(595, 600), new(820, 823), new(830.3, 831.0)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(823, new TranscriptSegment(7.3, 27.3, " Chapter two.", 1.0)); // abs [830.3, 850.3]
            },
            new FakeVad { Speech = [new(0, 830.5), new(836, 3600)] });

        AssertContainsChapter(new DetectedChapter(2, 831.0), result.Chapters);
        Assert.DoesNotContain(result.Chapters, c => c.Number == 2 && c.TimeSeconds < 830);
    }

    [Fact]
    public async Task Pass3_SnapsChunkBordersToSeams_AndBridgesAPhraseAcrossTheSeam()
    {
        // Pass 2 finds chapters 1 and 3, so pass 3 transcribes [0.5, 1200]. The first chunk's
        // natural border (600.5) snaps to the stored 1 s silence at [598, 599] (mid-point
        // 598.5), so the chunks abut there with no overlap - and the announcement straddles
        // that very seam: "Chapter" ends just before it (in chunk 1), "two." starts just after
        // it (in chunk 2). Only the bridge - chunk 1's trailing segments carried into chunk
        // 2's matching - can assemble the phrase; chunk 1 alone has no number, chunk 2 alone
        // no phrase. The detection must be flagged as seam-spanning in the log, and the chunk
        // decodes must reflect the snapped borders: [0.5, 598.5] and [598.5, 1197.5] (the
        // second border snaps to [1195, 1200]'s mid-point).
        var (result, log, audio) = await DetectWithLogAsync(
            Options("--min-silence-length", "1.5", "--max-jingle-length", "0"),
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
        Assert.Contains(log, l => l.Contains("chapter 2 detection spans a Pass 3 chunk seam"));
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
    public async Task Pass3_IgnoresAReDetectionOfTheGapsBoundingChapter_AtBothEnds()
    {
        // Pass 2 finds chapters 1 (at 0.5, no preceding silence) and 3 (at 500, pinned to the
        // silence [495, 500] preceding it), leaving a gap for chapter 2. Pass 3 then transcribes
        // [0.5, 500] as one chunk and - besides the genuine "Chapter two." - Whisper also
        // re-hears chapter 1's own announcement right at the chunk's start and chapter 3's right
        // near its end, at 495 instead of 3's real, silence-anchored position of 500. Neither
        // re-detection is new information: both must be ignored outright, with no log line and
        // without nudging chapter 3's mark from 500 down to 495.
        var (result, log, _) = await DetectWithLogAsync(
            Options("--max-jingle-length", "0"),
            [new(495, 500)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(500, Seg(0.3, " Chapter three."));
                // Pass 3's gap chunk, decoded from 0.25 (chapter 1's mark): re-hears chapter 1
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
    public async Task Pass3_RetriesAStoredSilenceItsOwnChunkTranscriptSkippedEntirely()
    {
        // Same shape as the test above (chapter 1 at 0.5, chapter 3 pinned to the silence
        // [495, 500]), but this time the gap chunk's own transcript has nothing at all covering
        // the stored silence [200, 206] - Whisper silently dropped chapter 2's phrase there
        // rather than mis-hearing it. The gap retry re-scans padded around just that silence
        // ([198, 208]) rather than the whole [2.5, 494.5] stretch between the chunk's two
        // segments, and finds the phrase in the first 8 s sub-chunk (starting at 198).
        var (result, log, _) = await DetectWithLogAsync(
            Options("--quick-marks", "--max-jingle-length", "0"),
            [new(495, 500), new(200, 206)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(500, Seg(0.3, " Chapter three."));
                // Pass 3's gap chunk [0.5, 500]: re-hears both endpoints, nothing in between.
                s.Add(0.5, Seg(0, " Chapter one."), Seg(494, " Chapter three."));
                // Gap retry around the qualifying silence, padded to [198, 208].
                s.Add(198, Seg(2, " Chapter 2."));
            });

        Assert.False(result.GapRemains);
        AssertChapters([new(1, 0.25), new(2, 199.75), new(3, 500.05)], result.Chapters);
        Assert.Contains(log, l => l.Contains("chapter 2 found in gap"));
    }

    [Fact]
    public async Task Pass3_GapRetryStaysScopedToTheSilence_NotTheWholeStretchBetweenSegments()
    {
        // Same shape again, but the two segments bracketing the qualifying silence [200, 206]
        // are now far apart (the chunk's own transcript is sparse, as real narration sometimes
        // is over a full 600 s Pass 3 chunk) - the raw stretch between them spans almost the
        // whole [0.5, 500] chunk. The retry must stay scoped to just the silence's own bounds
        // (padded to [198, 208]), not fan out across that whole stretch: only decode starts near
        // 198 are expected, never something like 50 or 300 that a naive "scan the whole gap"
        // approach would also have visited.
        var (result, _, audio) = await DetectFullAsync(
            Options("--quick-marks", "--max-jingle-length", "0"),
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
        // not a jingle. This is the false-positive class that flooded Pass 2 with bogus VAD
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
    public void FindGaps_ReportsMissingRegions()
    {
        var chapters = new List<DetectedChapter> { new(2, 500), new(3, 900), new(6, 2000) };
        Assert.Equal(
            [new(0, 500), new(900, 2000)],
            GapPlanning.FindGaps(chapters, Duration, expectedStartChapter: 1));
    }

    [Fact]
    public void FindGaps_SkipsLeadingRegion_WhenFirstChapterIsNearTheStart()
    {
        // Even with an expected start of 1, a first chapter within the first 10 s is taken as-is
        // (e.g. a book starting mid-series) rather than triggering a Pass 3 search.
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
            Options("--max-jingle-length", "0"),
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
    public async Task AutoLanguage_FallsBackToEnglish_WhenDetectionIsBelowThreshold()
    {
        var (result, _) = await DetectWithTranscriberAsync(
            Options("--max-jingle-length", "0"),
            [new(595, 600)],
            s =>
            {
                s.DetectedLanguage = ("tr", 0.3f); // below the 0.5 threshold
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(600, Seg(0.3, " Chapter two."));
            });

        AssertChapters([new(1, 0.25), new(2, 600.05)], result.Chapters);
        Assert.Equal("en", result.Profile.Language);
        Assert.Equal("Chapter", result.Profile.Title);
        Assert.Equal("tr", result.DetectedLanguage); // the raw guess is still reported
        Assert.Equal(0.3, result.DetectedProbability, 3);
    }

    [Fact]
    public async Task ExplicitLang_NeverCallsLanguageDetection()
    {
        var (result, transcriber) = await DetectWithTranscriberAsync(
            Options("--lang", "de", "--max-jingle-length", "0"),
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

    /// <summary>Runs --verify against the given pre-existing chapter markings and script.</summary>
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
    public async Task Verify_ConfirmsMarkings_WhenThePhraseAndNumberAreFoundNearby()
    {
        // Markings at 10 s and 610 s; --verify probes 10 s before each, so windows start at 0 and 600.
        var result = await VerifyAsync(
            Options("--max-jingle-length", "0"),
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

    [Fact]
    public async Task Verify_Fails_WhenThePhraseIsNotFoundNearby()
    {
        var result = await VerifyAsync(
            Options("--max-jingle-length", "0"),
            [new Chapter(10, "Chapter 1"), new Chapter(610, "Chapter 2")],
            s => s.Add(0, Seg(10, " Chapter 1."))); // nothing scripted near the second marking

        Assert.False(result.Passed);
        Assert.Equal(2, result.Checked);
        Assert.Equal(1, result.Failed);
    }

    [Fact]
    public async Task Verify_Fails_WhenTheNumberNearbyDoesNotMatch()
    {
        var result = await VerifyAsync(
            Options("--max-jingle-length", "0"),
            [new Chapter(10, "Chapter 1")],
            s => s.Add(0, Seg(10, " Chapter 2."))); // wrong number for this marking

        Assert.False(result.Passed);
        Assert.Equal(1, result.Checked);
        Assert.Equal(1, result.Failed);
    }

    [Fact]
    public async Task Verify_SkipsMarkings_WithNoParseableExpectedNumber()
    {
        // "Intro" has no digit and no recognizable number word, so it cannot be checked;
        // with nothing else to disprove, verification passes trivially.
        var result = await VerifyAsync(Options("--max-jingle-length", "0"), [new Chapter(0, "Intro")], _ => { });

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
    public async Task Verify_WithAutoLanguage_ResolvesLanguageUpfront_BeforeParsingAnyTitle()
    {
        // Both markings' titles are only parseable as German ordinals ("Erstes"/"Zweites") - not
        // as English number words. Resolving the language lazily, only after some marking's
        // title happened to parse under an "en" placeholder, would never get past the very first
        // marking here: its title fails to parse as English, so it would be skipped without ever
        // being decoded - and since decoding is what triggers language detection, "de" would
        // never be discovered, silently skipping every marking (Checked == 0, a false pass)
        // instead of verifying the book. Resolving upfront, from the first marking with a
        // decodable window regardless of its title, must check both.
        var (result, transcriber, _) = await VerifyWithTranscriberAsync(
            Options("--max-jingle-length", "0"),
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
        // Detected once, upfront - not re-detected per marking.
        Assert.Equal(1, transcriber.DetectLanguageCalls);
    }

    [Fact]
    public async Task Verify_TracksHighestConfirmedChapter_AndCountsUnconfirmedOnesAsMissing()
    {
        // Chapter 2 fails to confirm; 1 and 3 do. Same display convention as Pass 2/3: the
        // tracker should read the highest *confirmed* number, with the unconfirmed one below it
        // counted as a "(-N)" gap - not the highest pre-existing marking regardless of outcome.
        var (_, _, tracker) = await VerifyWithTranscriberAsync(
            Options("--max-jingle-length", "0"),
            [new Chapter(10, "Chapter 1"), new Chapter(610, "Chapter 2"), new Chapter(1210, "Chapter 3")],
            s =>
            {
                s.Add(0, Seg(10, " Chapter 1."));
                // nothing scripted near the second marking - Chapter 2 will not confirm.
                s.Add(1200, Seg(10, " Chapter 3."));
            });

        Assert.Equal(3, tracker.HighestChapter);
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
            Options("--max-jingle-length", "0"),
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
            Options("--max-jingle-length", "0"),
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
            Options("--max-jingle-length", "0"),
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
        var markings = new List<VerifyMarkingOutcome>
        {
            new(10, 1, true), new(30, 2, false), new(50, 3, true),
        };
        var plan = GapPlanning.BuildGapRegions(markings, Duration);

        Assert.Equal([new(10, 50, 1, 3)], plan.Regions);
        Assert.Null(plan.TrailingFrom);
        Assert.Empty(plan.TrailingTargets);
    }

    [Fact]
    public void BuildGapRegions_BuildsATrailingRegion_WhenTheLastCheckableMarkingIsUnconfirmed()
    {
        var markings = new List<VerifyMarkingOutcome> { new(10, 1, true), new(610, 2, false) };
        var plan = GapPlanning.BuildGapRegions(markings, Duration);

        Assert.Equal([new(10, Duration, 1, null)], plan.Regions);
        Assert.Equal(10, plan.TrailingFrom);
        Assert.Equal([2], plan.TrailingTargets);
    }

    [Fact]
    public void BuildGapRegions_GroupsConsecutiveUnconfirmedMarkings_IntoOneRun()
    {
        var markings = new List<VerifyMarkingOutcome>
        {
            new(10, 1, true), new(20, 2, false), new(30, 3, false), new(40, 4, true),
        };
        var plan = GapPlanning.BuildGapRegions(markings, Duration);

        Assert.Equal([new(10, 40, 1, 4)], plan.Regions);
    }

    [Fact]
    public void BuildGapRegions_KeepsSeparateRunsAsSeparateRegions()
    {
        var markings = new List<VerifyMarkingOutcome>
        {
            new(10, 1, true), new(20, 2, false), new(30, 3, true), new(40, 4, false), new(50, 5, true),
        };
        var plan = GapPlanning.BuildGapRegions(markings, Duration);

        Assert.Equal([new(10, 30, 1, 3), new(30, 50, 3, 5)], plan.Regions);
    }

    [Fact]
    public void BuildGapRegions_AbsorbsAnUnparseableMarking_WithoutBreakingTheRun()
    {
        // The middle marking has no parseable number (Confirmed is always false for those, but
        // that must not itself make the surrounding run look "broken" into two).
        var markings = new List<VerifyMarkingOutcome>
        {
            new(10, 1, true), new(20, 2, false), new(25, null, false), new(30, 3, false), new(40, 4, true),
        };
        var plan = GapPlanning.BuildGapRegions(markings, Duration);

        Assert.Equal([new(10, 40, 1, 4)], plan.Regions);
    }

    [Fact]
    public void BuildGapRegions_ReturnsNoRegions_WhenEveryCheckableMarkingIsConfirmed()
    {
        var markings = new List<VerifyMarkingOutcome> { new(10, 1, true), new(610, 2, true) };
        var plan = GapPlanning.BuildGapRegions(markings, Duration);

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
    public async Task DetectGapsAsync_RecoversAnInteriorGap_ViaGapScopedPass2_AndTrustsConfirmedMarkingsVerbatim()
    {
        // Chapter 1 (@10) and chapter 3 (@50) were already confirmed by --verify; chapter 2
        // (marking @30) was not. Only the region between the two confirmed markings' own
        // timestamps [10, 50) is probed - a single synthetic candidate at its own start (10),
        // exactly like the whole-file case's own start-of-file candidate.
        var verify = new VerifyResult(false, 2, 1,
            [new(1, 10), new(3, 50)],
            [new(10, 1, true), new(30, 2, false), new(50, 3, true)],
            Options().DefaultProfile, null, 0);

        var (result, audio) = await DetectGapsAsync(
            Options("--quick-marks", "--max-jingle-length", "0"), verify, [],
            s => s.Add(10, Seg(0.3, " Chapter 2.")));

        Assert.False(result.GapRemains);
        AssertChapters([new(1, 10), new(2, 10.05), new(3, 50)], result.Chapters);
        // Confirmed markings are trusted verbatim - the only decode is the gap region's own
        // single synthetic candidate; nothing probes near the confirmed markings' own timestamps.
        Assert.Equal([10.0], audio.DecodeStarts);
    }

    [Fact]
    public async Task DetectGapsAsync_RecoversATrailingGap_ViaPass3Fallback_WhenGapScopedPass2MissesIt()
    {
        // Chapter 3 (marking @1210, the last one in file order) was not confirmed. The phrase
        // sits 300 s into the decode starting at 610 - far past Pass 2's PhraseLatestStart rule
        // (and there is no anchor silence to rescue it, since none are scripted), so the
        // region-scoped Pass 2 window [610, 622) rejects it; Pass 3 has no such window-relative
        // timing rule, so its own chunk [610, 1210) still finds it - the trailing fallback is the
        // only mechanism that can notice a still-missing *trailing* chapter at all.
        var verify = new VerifyResult(false, 2, 1,
            [new(1, 10), new(2, 610)],
            [new(10, 1, true), new(610, 2, true), new(1210, 3, false)],
            Options().DefaultProfile, null, 0);

        var (result, _) = await DetectGapsAsync(
            Options("--max-jingle-length", "0"), verify, [],
            s => s.Add(610, Seg(300, " Chapter 3.")));

        Assert.False(result.GapRemains);
        Assert.Equal(3, result.Chapters.Count);
        Assert.Equal(3, result.Chapters[^1].Number);
    }

    [Fact]
    public async Task DetectGapsAsync_UpperBoundGuard_PreventsGapScopedPass2FromDisplacingTheNextConfirmedChapter()
    {
        // A (contrived) Pass 2 window inside the [10, 50) gap picks up chapter 3's own phrase -
        // exactly the failure mode the region's UpperNumber guard exists for: without it, this
        // would add a second, wrongly-timed chapter 3 entry that Normalize's earliest-timestamp-
        // wins rule would then prefer over the correctly confirmed one at 50.
        var verify = new VerifyResult(false, 2, 1,
            [new(1, 10), new(3, 50)],
            [new(10, 1, true), new(30, 2, false), new(50, 3, true)],
            Options().DefaultProfile, null, 0);

        var (result, _) = await DetectGapsAsync(
            Options("--max-jingle-length", "0"), verify, [],
            s => s.Add(10, Seg(0.3, " Chapter 3.")));

        // Chapter 3 must keep its correct, confirmed timestamp - not the gap probe's mistaken one.
        AssertContainsChapter(new DetectedChapter(3, 50), result.Chapters);
        // Chapter 2 was never actually found (only chapter 3's phrase was scripted, deliberately,
        // to isolate the guard) - the file correctly reports it as still missing rather than
        // silently accepting the wrong chapter 3 in its place.
        Assert.True(result.GapRemains);
        Assert.Contains(2, result.MissingNumbers);
    }

    /// <summary>Runs ResumeMissingMarksAsync against the given committed markings (as if probed
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
    public async Task ResumeMissingMarksAsync_RecoversAnInteriorGap_ViaGapScopedPass2_AndTrustsCommittedMarkingsVerbatim()
    {
        // Chapters 1 (@10) and 3 (@50) are already committed on the tagged file; chapter 2 is
        // still missing between them. Only the gap [10, 50) is probed - a single synthetic
        // candidate at its own start (10), exactly like DetectGapsAsync's own gap-scoped region.
        // An explicit --lang sidesteps the upfront language-resolution decode near chapter 1's own
        // marking (@10, so its own window would start at 0) that --lang auto would otherwise add,
        // keeping the decode-start assertion below solely about the gap-scoped Pass 2 region.
        var (result, audio, _) = await ResumeMissingMarksAsync(
            Options("--quick-marks", "--lang", "en", "--max-jingle-length", "0"),
            [new Chapter(10, "Chapter 1"), new Chapter(50, "Chapter 3")], [],
            s => s.Add(10, Seg(0.3, " Chapter 2.")));

        Assert.False(result.GapRemains);
        AssertChapters([new(1, 10), new(2, 10.05), new(3, 50)], result.Chapters);
        // The committed markings are trusted verbatim - nothing probes near their own timestamps,
        // only the gap region's own synthetic candidate.
        Assert.Equal([10.0], audio.DecodeStarts);
    }

    [Fact]
    public async Task ResumeMissingMarksAsync_LeavesGapRemains_WithTheStillMissingNumbers_WhenTheGapIsNotFound()
    {
        var (result, _, _) = await ResumeMissingMarksAsync(
            Options("--max-jingle-length", "0"),
            [new Chapter(10, "Chapter 1"), new Chapter(50, "Chapter 3")], [],
            _ => { }); // nothing scripted for the gap - chapter 2 stays missing

        Assert.True(result.GapRemains);
        Assert.Equal([2], result.MissingNumbers);
        AssertChapters([new(1, 10), new(3, 50)], result.Chapters);
    }

    [Fact]
    public async Task ResumeMissingMarksAsync_SkipsAnUnparseableIntroMarking_WithoutTreatingItAsAGapBoundary()
    {
        // The intro entry BuildChapters inserts on a partial commit has no parseable number - it
        // must be dropped from the trusted set entirely (not treated as chapter 0), so the gap is
        // still correctly bounded by chapter 1 (@10) and chapter 3 (@50), exactly as if the intro
        // marking were not present at all.
        var (result, _, _) = await ResumeMissingMarksAsync(
            Options("--max-jingle-length", "0"),
            [new Chapter(0, "Intro"), new Chapter(10, "Chapter 1"), new Chapter(50, "Chapter 3")], [],
            s => s.Add(10, Seg(0.3, " Chapter 2.")));

        Assert.False(result.GapRemains);
        AssertChapters([new(1, 10), new(2, 10.05), new(3, 50)], result.Chapters);
    }

    [Fact]
    public async Task ResumeMissingMarksAsync_WithAutoLanguage_ResolvesLanguageFromTheCommittedMarkingsWindow()
    {
        // Mirrors Verify_WithAutoLanguage_ResolvesLanguageUpfront_BeforeParsingAnyTitle: with
        // --lang auto, the language must be resolved from a committed marking's own window (here,
        // the same "de" ResolveProfileFromMarkingsAsync helper both methods now share), regardless
        // of whether the gap itself ends up recovered.
        var (result, _, transcriber) = await ResumeMissingMarksAsync(
            Options("--max-jingle-length", "0"),
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
        => (new PreciseMarkRefiner(transcriber.Audio, Options(), default, transcriber.TranscribeAsync),
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
            646.2, 659.75, _file, null, profile.PhraseRegex,
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
            640, 659.75, _file, null, profile.PhraseRegex, [new(0, 640), new(660, 3600)], CancellationToken.None);

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
            0.05, 20, _file, null, profile.PhraseRegex, [], CancellationToken.None);

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
            658.25, 659.75, _file, null, profile.PhraseRegex,
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
            659.75, _file, null, profile.PhraseRegex, 660, 663, 700, CancellationToken.None);

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
            659.75, _file, null, profile.PhraseRegex, 660, 663, 700, CancellationToken.None);

        Assert.False(result.PhraseHeard);
        Assert.Equal(659.75, result.Mark);
    }

    [Fact]
    public async Task Debug_RecordsEverySilence_IncludingThoseBelowTheThreshold()
    {
        // The point of the dump: --min-silence-length decides which silences Pass 2 works from, and
        // "why was there no candidate here" is answerable only if the rejected ones are in the file
        // too - flagged, so the working subset stays readable.
        var debug = await DetectWithDebugAsync(
            Options("--debug", "--max-jingle-length", "0", "--min-silence-length", "3"),
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
            Options("--debug", "--max-jingle-length", "0"),
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
            Options("--debug", "--max-jingle-length", "0"),
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
            Options("--debug", "--max-jingle-length", "0"),
            [new(595, 600)],
            s =>
            {
                s.Add(600, Seg(0.3, " Chapter one."));
                s.Add(600.25, Seg(0.3, " Chapter one."));
            });

        Assert.Contains(debug, l => l.Contains("onset probe") && l.Contains("-> phrase"));
    }
}
