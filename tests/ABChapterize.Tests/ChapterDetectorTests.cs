// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Xunit;

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
            return Task.FromResult(new float[16000]);
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
        private readonly List<(double Start, List<TranscriptSegment> Segments)> _script = [];

        /// <summary>Creates a transcriber that follows the decode requests of <paramref name="audio"/>.</summary>
        public ScriptedTranscriber(FakeAudioSource audio) => _audio = audio;

        /// <summary>Scripts the transcript for the decode window starting near <paramref name="start"/>.</summary>
        public void Add(double start, params TranscriptSegment[] segments)
            => _script.Add((start, [.. segments]));

        /// <inheritdoc/>
        public Task<List<TranscriptSegment>> TranscribeAsync(float[] samples, CancellationToken ct)
        {
            var start = _audio.DecodeStarts[^1];
            var hit = _script.FirstOrDefault(e => Math.Abs(e.Start - start) < 0.25);
            return Task.FromResult(hit.Segments ?? []);
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

    /// <summary>Builds validated options with the temp file as target.</summary>
    private CliOptions Options(params string[] args)
        => CliOptions.Parse([.. args, _file])!;

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
        var result = await detector.DetectAsync(_file, Info, new WorkTracker(), null, CancellationToken.None);
        return (result, transcriber, audio);
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
        var result = await detector.DetectAsync(_file, Info, new WorkTracker(), log.Add, CancellationToken.None);
        return (result, log, audio);
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
        Assert.Equal(
            [new(1, 0.5), new(2, 600), new(3, 1200)],
            result.Chapters);
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

        Assert.Equal([new(1, 0.5), new(2, 600)], result.Chapters);
    }

    [Fact]
    public async Task RegexPhrase_WithCaptureGroup_ParsesTheNumber()
    {
        var result = await DetectAsync(
            Options("-c", @"/chapter (\d+)/"),
            [new(595, 600)],
            s => s.Add(600, Seg(0.3, " Chapter 12 begins.")));

        Assert.Equal([new(12, 600)], result.Chapters);
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

        Assert.Equal([new DetectedChapter(1, 0.5)], result.Chapters);
        Assert.False(result.GapRemains);
    }

    [Fact]
    public async Task SequenceGap_IsResolved_ByFullTranscription()
    {
        // The probe after the first silence hears nothing, so pass 2 yields chapters 1 and 3;
        // pass 3 must transcribe the region in between and find chapter 2 at 600 s.
        var result = await DetectAsync(
            Options(),
            [new(595, 600), new(1195, 1200)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(1200, Seg(0.2, " Chapter three."));
                s.Add(590.5, Seg(9.5, " Chapter two.")); // gap chunk starting at 0.5 + 590
            });

        Assert.False(result.GapRemains);
        Assert.Equal(
            [new(1, 0.5), new(2, 600), new(3, 1200)],
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
    public async Task RegionBeforeFirstChapter_IsSearched_WhenItStartsAboveOne()
    {
        // Only chapter 2 is found by the probes, so pass 3 transcribes the file start
        // and finds chapter 1 in the middle of the audio.
        var result = await DetectAsync(
            Options(),
            [new(1195, 1200)],
            s =>
            {
                s.Add(1200, Seg(0.2, " Chapter two."));
                s.Add(0, Seg(10, " Chapter one.")); // also serves as the pass-3 chunk at 0
            });

        Assert.False(result.GapRemains);
        Assert.Equal([new(1, 10), new(2, 1200)], result.Chapters);
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
        Assert.Equal(
            [new(1, 0.5), new(2, 600), new(3, 905)],
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
            Options(),
            [new(10, 40), new(595, 600), new(900, 905)],
            s =>
            {
                s.Add(40, Seg(0.3, " Chapter one."));
                s.Add(600, Seg(0.3, " Chapter two."));
                s.Add(905, Seg(0.2, " Chapter three."));
            });

        Assert.False(result.GapRemains);
        Assert.Equal(
            [new(1, 40), new(2, 600), new(3, 905)],
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
        Assert.Equal(
            [new(1, 0.5), new(2, 600), new(3, 703), new(4, 905)],
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

    [Fact]
    public async Task AutoMinSilence_AfterAGapRecovery_TheThresholdAccountsForTheGapMarksShorterSilence()
    {
        // Chapter 2 (5 s anchor) tightens the threshold to 3.75 s; chapter 3's 3 s silence is
        // skipped, chapter 4 is found -> sequence gap -> re-probe recovers chapter 3. Its 3 s
        // anchor must fold into the threshold (0.75 x 3 = 2.25 s), so chapter 5's 2.5 s
        // silence - below chapter 2's 3.75 s but above 2.25 s - is still probed and found.
        // Chapter 5 is the last mark, so nothing could recover it if it were skipped.
        var (result, _, audio) = await DetectFullAsync(
            Options(),
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
    public async Task ExplicitMinSilenceLength_NeverSkipsAnyDetectedSilence()
    {
        // With an explicit numeric --min-silence-length, adaptive tightening is off: every
        // silence from pass 1 is probed regardless of length or what was found before it.
        var (result, _, audio) = await DetectFullAsync(
            Options("--min-silence-length", "1.5"),
            [new(595, 600), new(700, 703), new(900, 905)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(600, Seg(0.3, " Chapter two."));
                s.Add(905, Seg(0.2, " Chapter three."));
            });

        Assert.Equal([new(1, 0.5), new(2, 600), new(3, 905)], result.Chapters);
        Assert.Contains(703, audio.DecodeStarts);
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

        Assert.Equal(
            [new(1, 0.5), new(2, 600), new(3, 1200)],
            result.Chapters);
    }

    [Fact]
    public async Task JingleMark_IsAnchoredBeforeTheLatestSilenceBeforeThePhrase()
    {
        // Probe window at 600: jingle until 615, short silence 615-618, phrase at 618.2.
        // The mark belongs 0.5 s before the end of the silence directly preceding the phrase.
        var result = await DetectAsync(
            Options("--jingle"),
            [new(595, 600), new(615, 618)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(600, Seg(18.2, " Chapter two."));
            });

        Assert.Contains(new DetectedChapter(2, 617.5), result.Chapters);
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
            Options("--jingle"),
            [],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(700, Seg(0.3, " Chapter two."));
            },
            new FakeVad { Speech = [new(0, 700), new(705, 3600)] });

        Assert.False(result.GapRemains);
        Assert.Contains(new DetectedChapter(2, 700), result.Chapters);
        Assert.Contains(700.0, audio.DecodeStarts);
    }

    [Fact]
    public async Task JingleWithLeadingSilence_MarkUnchanged_AndVadDoesNotDoubleProbe()
    {
        // A silence precedes the jingle - the existing silence-based candidate already probes
        // this transition, so the VAD non-speech region covering the same silence+jingle span
        // must not add a second, duplicate candidate (dedup): the silence path stays primary,
        // and the mark lands 0.5 s before it exactly as it would without VAD at all.
        var (result, _, audio) = await DetectFullAsync(
            Options("--jingle"),
            [new(695, 700)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(700, Seg(3.2, " Chapter two."));
            },
            new FakeVad { Speech = [new(0, 695), new(703, 3600)] });

        Assert.Contains(new DetectedChapter(2, 699.5), result.Chapters);
        Assert.Single(audio.DecodeStarts, d => Math.Abs(d - 700) < 0.5);
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
            Options("--jingle"),
            [new(610, 613)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(613, Seg(32, " Chapter two.")); // probe window [613, 663], phrase at 645
            },
            new FakeVad { Speech = [new(0, 610), new(613, 640), new(645, 3600)] });

        Assert.False(result.GapRemains);
        Assert.Equal([new(1, 0), new(2, 640)], result.Chapters);
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
            Options("--jingle", "--max-jingle-length", "auto"),
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
        Assert.Contains(new DetectedChapter(2, 640), result.Chapters);       // jingle start, not 612.5
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
            Options("--jingle"),
            [new(610, 613)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(613, Seg(32, " Chapter two.")); // phrase at 645, VAD resumes at 645.3
            },
            new FakeVad { Speech = [new(0, 610), new(613, 640), new(645.3, 3600)] });

        Assert.False(result.GapRemains);
        Assert.Equal([new(1, 0), new(2, 640)], result.Chapters);
    }

    [Fact]
    public async Task AutoMaxJingle_ObservesLengthFromVadBoundaries_NotPhraseOffset()
    {
        // Chapter two's phrase starts 20 s into its probe window, but the VAD region itself
        // (the true jingle) is only 5 s long. If the resize wrongly used the phrase-relative
        // offset (20 s) instead of the VAD boundaries, the window would resize to ~30 s and
        // still probe chapter three's 15 s-long region; using the correct 5 s observation
        // resizes to ~11 s instead, so that region must be skipped (too long to be this
        // book's jingle) and chapter three must not be found.
        var (result, _, audio) = await DetectFullAsync(
            Options("--jingle", "--max-jingle-length", "auto"),
            [],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(800, Seg(20, " Chapter two."));
            },
            new FakeVad { Speech = [new(0, 800), new(805, 900), new(915, 3600)] });

        Assert.Equal([1, 2], result.Chapters.Select(c => c.Number));
        Assert.DoesNotContain(900.0, audio.DecodeStarts);
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
        var (result, _, audio) = await DetectFullAsync(
            Options("--jingle", "--max-jingle-length", "auto"),
            [],
            s =>
            {
                s.Add(50, Seg(0.3, " Chapter one."));
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
            Options("--jingle", "--max-jingle-length", "auto"),
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
            Options("--jingle"),
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
            Options(),
            [new(595, 600)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one.", confidence: 0.95));
                s.Add(600, Seg(0.3, " Chapter two.", confidence: 0.2));
            });

        Assert.Equal(
            [new(1, 0.5, 0.95), new(2, 600, 0.2)],
            result.Chapters);
        Assert.Equal([2], result.LowConfidenceNumbers);
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

        Assert.Empty(result.LowConfidenceNumbers);
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
        // Two silences 6 s apart give overlapping 12 s probe windows ([600, 612] and [606, 618]).
        // Chapter one is found by the first probe. The second probe must not re-decode the shared
        // [606, 612] span: neither silence lies fully within window 2 ([606, 618]), so
        // FindOverlapSplitPoint falls back to the original border (612, no snap, no reach-back) -
        // that is where the fresh tail decode starts, and the candidate's own start (606) never is.
        // The detected chapter is unaffected by the optimization.
        var (result, _, audio) = await DetectFullAsync(
            Options("--min-silence-length", "1.5"),
            [new(595, 600), new(601, 606)],
            s => s.Add(600, Seg(0.5, " Chapter one.")));

        Assert.Equal([new DetectedChapter(1, 600)], result.Chapters);
        Assert.Contains(612.0, audio.DecodeStarts);        // fresh tail only (fallback: the border itself)
        Assert.DoesNotContain(606.0, audio.DecodeStarts);  // the overlap was reused, not re-decoded
    }

    [Fact]
    public async Task OverlappingProbe_RecoversASecondChapter_MissedByTheFirstWindowsEarlyReturn()
    {
        // One probe window stops at its first phrase (one chapter per window), so a second phrase
        // further along the same window is never marked by that probe. Here chapter one's wide
        // --jingle window [600, 650] also contains chapter two's announcement 40 s in; the probe
        // returns chapter one and leaves chapter two unseen. The overlapping candidate at 640 must
        // recover chapter two from the reused transcript - a naive "transcribe only the new tail"
        // scheme would never see it, since it sits inside the already-transcribed overlap. Neither
        // silence lies fully within window 2 ([640, 690]), and there are no VAD non-speech regions
        // (one continuous speech segment), so FindOverlapSplitPoint falls back to the original
        // border (650) - the tail decode lands there, never at the candidate start (640).
        var (result, _, audio) = await DetectFullAsync(
            Options("--jingle"),
            [new(598, 600), new(638, 640)],
            s => s.Add(600, Seg(2, " Chapter one."), Seg(40, " Chapter two.")),
            new FakeVad { Speech = [new(0, 3600)] });

        Assert.Equal([1, 2], result.Chapters.Select(c => c.Number));
        Assert.Contains(new DetectedChapter(2, 639.5), result.Chapters);
        Assert.Contains(650.0, audio.DecodeStarts);
        Assert.DoesNotContain(640.0, audio.DecodeStarts);
    }

    [Fact]
    public async Task OverlappingProbe_RecoversAPhrase_RejectedByTheEarlierWindowsPhraseTimingRule()
    {
        // Without a jingle the phrase must start within 5 s of the triggering silence. Chapter one's
        // announcement sits 9 s into the first probe's window ([600, 612], phrase at 609), so that
        // probe rejects it as too late. The next candidate at 606 sees the very same phrase only 3 s
        // in ([606, 618], phrase at 609) and must accept it. That phrase lives in the
        // already-transcribed overlap, so only the reused transcript - not a tail-only re-decode -
        // can surface it. Neither silence lies fully within window 2 ([606, 618]), so
        // FindOverlapSplitPoint falls back to the original border (612). The chapter is recovered
        // and the overlap is not re-decoded (612 is, 606 is not).
        var (result, _, audio) = await DetectFullAsync(
            Options("--min-silence-length", "1.5"),
            [new(595, 600), new(603, 606)],
            s => s.Add(600, Seg(9, " Chapter one.")));

        Assert.Equal([new DetectedChapter(1, 606)], result.Chapters);
        Assert.Contains(612.0, audio.DecodeStarts);
        Assert.DoesNotContain(606.0, audio.DecodeStarts);
    }

    [Fact]
    public async Task OverlappingProbe_SnapsTheSplitToASilenceMidpointWithinWindowTwo()
    {
        // Window 1 (candidate 600, --min-silence-length 1.5) naturally spans [600, 612];
        // window 2 (candidate 606) spans [606, 618] and overlaps it. A short 0.6 s silence at
        // [608, 608.6] is well below the 1.5 s candidate threshold - it never becomes a Pass 2
        // candidate of its own - but is still retained down to the 0.5 s floor
        // (MinStoredSilenceSeconds) purely as a seam target, and it lies inside window 2.
        // The up-front window plan (PlanWindowEnds) must move the shared border to its
        // mid-point (608.3) before anything is decoded: window 1's decode itself ends there
        // (8.3 s instead of the natural 12), and window 2's fresh tail starts exactly there -
        // never at the raw border (612) or the candidate start (606).
        var (_, _, audio) = await DetectFullAsync(
            Options("--min-silence-length", "1.5"),
            [new(595, 600), new(603, 606), new(608, 608.6)],
            s => s.Add(600, Seg(0.5, " Chapter one.")));

        Assert.Contains(608.3, audio.DecodeStarts);
        Assert.DoesNotContain(612.0, audio.DecodeStarts);
        Assert.DoesNotContain(606.0, audio.DecodeStarts);
        Assert.Contains(audio.DecodeWindows,
            w => w.Start == 600 && w.Duration is { } d && Math.Abs(d - 8.3) < 0.01);
    }

    [Fact]
    public async Task OverlappingProbe_SnapsTheSplitToAVadRegion_WhenNoSilenceQualifies()
    {
        // --jingle, three overlapping windows: candidate 600 (natural span [600, 650]),
        // candidate 640, and the VAD candidate the [648, 655] non-speech region itself spawns
        // at 648. No silence offers a seam anywhere, so the plan snaps every shared border to
        // the region's mid-point (651.5, jingle mode only): window 640's end lands there, and
        // window 600's border search - seeing window 640 end at 651.5 - accepts the very same
        // seam at its neighbor's end, extending window 600's decode to 651.5 and leaving
        // window 640 fully contained in its cache (no decode of its own). The VAD candidate's
        // fresh tail then starts exactly at the seam: 651.5 is decoded, the raw border (650)
        // and the swallowed candidate start (640) never are.
        var (_, _, audio) = await DetectFullAsync(
            Options("--jingle"),
            [new(598, 600), new(638, 640)],
            s => s.Add(600, Seg(2, " Chapter one.")),
            new FakeVad { Speech = [new(0, 648), new(655, 3600)] });

        Assert.Contains(651.5, audio.DecodeStarts);
        Assert.DoesNotContain(650.0, audio.DecodeStarts);
        Assert.DoesNotContain(640.0, audio.DecodeStarts);
    }

    [Fact]
    public async Task OverlappingProbe_SnapsBeyondTheBorder_ByExtendingWindowOnesDecode()
    {
        // Window 1 naturally spans [600, 612], window 2 [606, 618] (border 612). The only seam
        // target lies entirely *beyond* the border ([613, 614], mid-point 613.5). Because the
        // whole window list - snapped shared borders included - is planned before Pass 2
        // decodes anything (PlanWindowEnds), window 1's decode is simply *extended* to 613.5 up
        // front and window 2's fresh tail starts exactly there: the plan moved the border
        // itself, so no [612, 613.5) hole can exist and nothing is cut mid-word at 612.
        var (_, _, audio) = await DetectFullAsync(
            Options("--min-silence-length", "1.5"),
            [new(595, 600), new(603, 606), new(613, 614)],
            s => s.Add(600, Seg(0.5, " Chapter one.")));

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
        // the 1.5 s candidate threshold, so it is retained purely as a seam target.)
        var (_, _, audio) = await DetectFullAsync(
            Options("--min-silence-length", "1.5"),
            [new(595, 600), new(603, 606), new(611.6, 612.6)],
            s => s.Add(600, Seg(0.5, " Chapter one.")));

        Assert.Contains(612.1, audio.DecodeStarts);
        Assert.DoesNotContain(612.0, audio.DecodeStarts);
        Assert.DoesNotContain(606.0, audio.DecodeStarts);
    }

    [Fact]
    public async Task OverlappingProbe_LogsOnlyTheFreshTail_AtItsOwnTimestamps()
    {
        // Same split-snapping setup as OverlappingProbe_SnapsTheSplitToASilenceMidpointWithinWindowTwo
        // (split at 608.3), but this asserts on the --verbose log itself: the tail probe's log line
        // must show only what was actually decoded from 608.3 onward, at Whisper's own (0-based)
        // timestamps - not the reused segment restated at window-relative time, and not a span
        // reaching all the way to the window's nominal end (618).
        var (_, log, _) = await DetectWithLogAsync(
            Options("--min-silence-length", "1.5"),
            [new(595, 600), new(603, 606), new(608, 608.6)],
            s =>
            {
                s.Add(600, Seg(0.5, " Chapter one."));
                s.Add(608.3, Seg(1.0, " some fresh words"));
            });

        // The label carries the actually decoded length: split at 608.3, window end 618 -> 9.7 s.
        var tailLine = Assert.Single(log, l => l.StartsWith($"probe tail {9.7:0.#} s @0:10:08.30"));
        Assert.Contains($"{1.0:0.0}-{3.0:0.0}", tailLine); // Whisper's own 0-based timestamp for the fresh segment
        Assert.DoesNotContain("Chapter one", tailLine); // that segment was reused, not re-decoded
    }

    [Fact]
    public async Task OverlappingProbe_LogsFullyReusedWindows_WithoutASegmentDump()
    {
        // Near the end of the file the probe window is capped at the file's duration (3600 s),
        // so two close-together candidates can end up with the very same (capped) window end -
        // window 2 is then fully contained in window 1's cache and no Whisper call happens at
        // all. The log must say so plainly rather than dumping a (nonexistent) transcript.
        var (_, log, audio) = await DetectWithLogAsync(
            Options("--min-silence-length", "1.5"),
            [new(3585, 3590), new(3593, 3595)],
            s => s.Add(3590, Seg(0.5, " Chapter one.")));

        Assert.Contains("probe @0:59:55.00: fully reused, no new transcription", log);
        Assert.DoesNotContain(3595.0, audio.DecodeStarts);
    }

    [Fact]
    public async Task OverlappingProbe_FlagsADetectionThatSpansTheCacheFreshMerge()
    {
        // The "Chapter" segment (abs 606.5) is reused from window 1's cache; the number word
        // "one." only exists in window 2's freshly decoded tail (from the 608.3 split point).
        // Extracting the chapter number therefore has to reach across the cache/fresh boundary -
        // FindPhraseMatches must flag that detection, and DetectAsync must log it.
        var (result, log, _) = await DetectWithLogAsync(
            Options("--min-silence-length", "1.5"),
            [new(595, 600), new(603, 606), new(608, 608.6)],
            s =>
            {
                s.Add(600, Seg(6.5, " Chapter")); // abs 606.5 - reused by window 2
                s.Add(608.3, Seg(0, " one."));     // abs 608.3 - fresh tail of window 2
            });

        Assert.Equal([new DetectedChapter(1, 606)], result.Chapters);
        Assert.Contains(log, l => l.Contains("chapter 1 detection spans the reused/fresh transcript merge"));
    }

    [Fact]
    public void PlanWindowEnds_KeepsNaturalEnds_WhenNoWindowsOverlap()
    {
        var ends = ChapterDetector.PlanWindowEnds(
            [0, 600, 1200], 12, 3600, [new(608, 608.6)], [], jingle: false);
        Assert.Equal([12, 612, 1212], ends);
    }

    [Fact]
    public void PlanWindowEnds_SnapsASharedBorder_ToTheSeamNearestTheBorder()
    {
        // Both [604, 605] (mid 604.5) and [610, 611] (mid 610.5) lie within window 2
        // ([603, 615]); the shared border (window 1's natural end, 612) snaps to the nearer
        // mid-point, shortening window 1's decode.
        var ends = ChapterDetector.PlanWindowEnds(
            [600, 603], 12, 3600, [new(604, 605), new(610, 611)], [], jingle: false);
        Assert.Equal([610.5, 615], ends);
    }

    [Fact]
    public void PlanWindowEnds_ExtendsAWindow_WhenTheOnlySeamLiesBeyondItsNaturalEnd()
    {
        // The only target ([613, 614]) sits past window 1's natural end (612) - the plan may
        // move the border itself, so window 1 is extended to the mid-point (613.5) and the
        // next window's fresh decode will start exactly there. No hole, no mid-word cut.
        var ends = ChapterDetector.PlanWindowEnds(
            [600, 606], 12, 3600, [new(613, 614)], [], jingle: false);
        Assert.Equal([613.5, 618], ends);
    }

    [Fact]
    public void PlanWindowEnds_FallsBackToTheNaturalEnd_WhenNoSeamTargetExists()
    {
        // The only silences lie at or before window 2's start - nothing inside (606, 618] to
        // snap to, so the shared border stays the natural end: the raw-border joint is the
        // only kind of overlap the plan leaves behind.
        var ends = ChapterDetector.PlanWindowEnds(
            [600, 606], 12, 3600, [new(595, 600), new(601, 606)], [], jingle: false);
        Assert.Equal([612, 618], ends);
    }

    [Fact]
    public void PlanWindowEnds_PlansAChain_AgainstEachNeighborsFinalSpan()
    {
        // Right-to-left planning: window 3 keeps its natural end (624); window 2's border
        // (618) snaps to [616, 617]'s mid-point inside window 3; window 1's border (612) then
        // snaps to [610, 611]'s mid-point inside window 2's *final* - already snapped -
        // span [606, 616.5].
        var ends = ChapterDetector.PlanWindowEnds(
            [600, 606, 612], 12, 3600, [new(610, 611), new(616, 617)], [], jingle: false);
        Assert.Equal([610.5, 616.5, 624], ends);
    }

    [Fact]
    public void PlanWindowEnds_LeavesABorderAlone_WhenTheNextWindowEndsWithinThisOne()
    {
        // Clamped to the file end, both windows end at 3600, so the later one is fully
        // contained in the earlier - there is no shared border to snap even though a target
        // would be available; the contained window is served from cache instead.
        var ends = ChapterDetector.PlanWindowEnds(
            [3590, 3595], 12, 3600, [new(3596, 3597)], [], jingle: false);
        Assert.Equal([3600, 3600], ends);
    }

    [Fact]
    public void PlanWindowEnds_UsesVadRegions_OnlyInJingleMode()
    {
        // A VAD non-speech region is a valid seam target with --jingle, but plain mode has no
        // VAD data worth trusting - the same layout must snap only in jingle mode.
        List<ChapterDetector.NonSpeechRegion> regions = [new(608, 609)];
        var plain = ChapterDetector.PlanWindowEnds([600, 606], 12, 3600, [], regions, jingle: false);
        var jingle = ChapterDetector.PlanWindowEnds([600, 606], 12, 3600, [], regions, jingle: true);
        Assert.Equal([612, 618], plain);
        Assert.Equal([608.5, 618], jingle);
    }

    [Fact]
    public void ComputeNonSpeechRegions_MergesRegionsSeparatedByAShortSpeechBlip()
    {
        // A 0.5 s "speech" blip - short enough to be a vocal-like transient inside otherwise
        // instrumental jingle music, not a genuine return to narration - must not fragment the
        // jingle: the non-speech regions on either side merge into one spanning both.
        var speech = new List<SpeechSegment> { new(0, 100), new(110, 110.5), new(122.5, 200) };
        Assert.Equal(
            [new ChapterDetector.NonSpeechRegion(100, 122.5)],
            ChapterDetector.ComputeNonSpeechRegions(speech));
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
            [new ChapterDetector.NonSpeechRegion(100, 120)],
            ChapterDetector.ComputeNonSpeechRegions(speech));
    }

    [Fact]
    public void ComputeNonSpeechRegions_DoesNotMerge_WhenTheSpeechGapIsNotShort()
    {
        // A 1.5 s speech segment is a genuine return to narration, not VAD noise - the two
        // non-speech regions must stay separate (both are otherwise well above the 2 s floor).
        var speech = new List<SpeechSegment> { new(0, 100), new(110, 111.5), new(120, 200) };
        Assert.Equal(
            [new ChapterDetector.NonSpeechRegion(100, 110), new ChapterDetector.NonSpeechRegion(111.5, 120)],
            ChapterDetector.ComputeNonSpeechRegions(speech));
    }

    [Fact]
    public void ComputeNonSpeechRegions_DropsRegionsShorterThanTheFloor_AfterMerging()
    {
        // A 1.2 s non-speech region, not adjacent to anything it could merge with, never reaches
        // the 2 s floor and must be dropped entirely rather than surfacing as a candidate.
        var speech = new List<SpeechSegment> { new(0, 100), new(101.2, 200) };
        Assert.Empty(ChapterDetector.ComputeNonSpeechRegions(speech));
    }

    [Fact]
    public void ComputeNonSpeechRegions_KeepsRegionsAtExactlyTheThresholds()
    {
        // A speech gap of exactly 1 s is not "shorter than" the merge threshold, so the regions
        // must stay separate; a region of exactly 2 s is not "shorter than" the drop floor, so it
        // must be kept.
        var speech = new List<SpeechSegment> { new(0, 100), new(102, 103), new(105, 200) };
        Assert.Equal(
            [new ChapterDetector.NonSpeechRegion(100, 102), new ChapterDetector.NonSpeechRegion(103, 105)],
            ChapterDetector.ComputeNonSpeechRegions(speech));
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
        Assert.Empty(ChapterDetector.ComputeNonSpeechRegions(speech));
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
            [new ChapterDetector.NonSpeechRegion(100, 106)],
            ChapterDetector.ComputeNonSpeechRegions(speech));
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
            ChapterDetector.Normalize(raw));
    }

    [Fact]
    public void FindGaps_ReportsMissingRegions()
    {
        var chapters = new List<DetectedChapter> { new(2, 500), new(3, 900), new(6, 2000) };
        Assert.Equal(
            [new(0, 500), new(900, 2000)],
            ChapterDetector.FindGaps(chapters, Duration));
    }

    [Fact]
    public void FindGaps_SkipsLeadingRegion_WhenFirstChapterIsNearTheStart()
    {
        // A chapter > 1 within the first 30 s is taken as-is (e.g. a book starting mid-series).
        var chapters = new List<DetectedChapter> { new(2, 10) };
        Assert.Empty(ChapterDetector.FindGaps(chapters, Duration));
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

        Assert.Equal([new(1, 0.5), new(2, 600)], result.Chapters);
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
            Options(),
            [new(595, 600)],
            s =>
            {
                s.DetectedLanguage = ("tr", 0.3f); // below the 0.5 threshold
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(600, Seg(0.3, " Chapter two."));
            });

        Assert.Equal([new(1, 0.5), new(2, 600)], result.Chapters);
        Assert.Equal("en", result.Profile.Language);
        Assert.Equal("Chapter", result.Profile.Title);
        Assert.Equal("tr", result.DetectedLanguage); // the raw guess is still reported
        Assert.Equal(0.3, result.DetectedProbability, 3);
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

        Assert.Equal([new(1, 0.5), new(2, 600)], result.Chapters);
        Assert.Equal(0, transcriber.DetectLanguageCalls);
        Assert.Null(result.DetectedLanguage);
        Assert.Equal("de", result.Profile.Language);
        Assert.Equal(["de"], transcriber.LanguageChanges); // still (re-)asserted defensively
    }

    /// <summary>Runs --verify against the given pre-existing chapter markings and script.</summary>
    private async Task<VerifyResult> VerifyAsync(
        CliOptions options, IReadOnlyList<Chapter> existingChapters, Action<ScriptedTranscriber> script)
    {
        var audio = new FakeAudioSource();
        var transcriber = new ScriptedTranscriber(audio);
        script(transcriber);
        var detector = new ChapterDetector(options, audio, transcriber);
        var info = new MediaInfo(Duration, (long)Duration, existingChapters.Count,
            ExistingChapterList: existingChapters);
        return await detector.VerifyExistingChaptersAsync(_file, info, new WorkTracker(), null, CancellationToken.None);
    }

    [Fact]
    public async Task Verify_ConfirmsMarkings_WhenThePhraseAndNumberAreFoundNearby()
    {
        // Markings at 10 s and 610 s; --verify probes 5 s before each, so windows start at 5 and 605.
        var result = await VerifyAsync(
            Options(),
            [new Chapter(10, "Chapter 1"), new Chapter(610, "Chapter 2")],
            s =>
            {
                s.Add(5, Seg(5, " Chapter 1."));
                s.Add(605, Seg(5, " Chapter 2."));
            });

        Assert.True(result.Passed);
        Assert.Equal(2, result.Checked);
        Assert.Equal(0, result.Failed);
    }

    [Fact]
    public async Task Verify_Fails_WhenThePhraseIsNotFoundNearby()
    {
        var result = await VerifyAsync(
            Options(),
            [new Chapter(10, "Chapter 1"), new Chapter(610, "Chapter 2")],
            s => s.Add(5, Seg(5, " Chapter 1."))); // nothing scripted near the second marking

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
            s => s.Add(5, Seg(5, " Chapter 2."))); // wrong number for this marking

        Assert.False(result.Passed);
        Assert.Equal(1, result.Checked);
        Assert.Equal(1, result.Failed);
    }

    [Fact]
    public async Task Verify_SkipsMarkings_WithNoParseableExpectedNumber()
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
            s => s.Add(5, Seg(5, " Erstes Kapitel.")));

        Assert.True(result.Passed);
        Assert.Equal(1, result.Checked);
    }
}
