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
        var result = await detector.DetectAsync(_file, Info, new WorkTracker(), null, CancellationToken.None);
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
        var result = await detector.DetectAsync(_file, Info, new WorkTracker(), log.Add, CancellationToken.None);
        return (result, log, audio);
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
        Assert.Equal(
            [new(1, 0.25), new(2, 600.05), new(3, 1199.95)],
            result.Chapters);
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

        Assert.Equal([new(1, 0.25), new(2, 600.05)], result.Chapters);
    }

    [Fact]
    public async Task RegexPhrase_WithCaptureGroup_ParsesTheNumber()
    {
        var result = await DetectAsync(
            Options("-c", @"/chapter (\d+)/", "--max-jingle-length", "0"),
            [new(595, 600)],
            s => s.Add(600, Seg(0.3, " Chapter 12 begins.")));

        Assert.Equal([new(12, 600.05)], result.Chapters);
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

        Assert.Equal([new DetectedChapter(1, 0.25)], result.Chapters);
        Assert.False(result.GapRemains);
    }

    [Fact]
    public async Task SequenceGap_IsResolved_ByFullTranscription()
    {
        // The probe after the first silence hears nothing, so pass 2 yields chapters 1 and 3;
        // pass 3 must transcribe the region in between and find chapter 2 at 600 s. The first
        // pass-3 chunk's border (natural end 600.5) snaps to the [595, 600] silence's mid-point
        // (597.5), so the second chunk starts exactly there - that is where the phrase is heard.
        var result = await DetectAsync(
            Options("--max-jingle-length", "0"),
            [new(595, 600), new(1195, 1200)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(1200, Seg(0.2, " Chapter three."));
                s.Add(597.5, Seg(2.5, " Chapter two.")); // gap chunk starting at the snapped seam
            });

        Assert.False(result.GapRemains);
        Assert.Equal(
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
        var gaps = ChapterDetector.FindGaps(chapters, Duration); // (0, 500) and (900, 2000)
        Assert.Equal([1], ChapterDetector.MissingNumbersInGap(chapters, gaps[0]));    // leading gap: 1
        Assert.Equal([4, 5], ChapterDetector.MissingNumbersInGap(chapters, gaps[1])); // 3 -> 6: 4, 5
    }

    [Fact]
    public async Task RegionBeforeFirstChapter_IsSearched_WhenItStartsAboveOne()
    {
        // Only chapter 2 is found by the probes, so pass 3 transcribes the file start looking for
        // chapter 1. It is not in the first chunk [0, 600] but past its end, so the search must
        // continue into the second chunk: that chunk's border (natural end 600) has no seam target
        // within reach, so the unsnapped fallback keeps the 10-second overlap and the second chunk
        // starts at 590, not 600 - and that is where chapter 1 (phrase at 610) is found. (Were
        // chapter 1 already in the first chunk, the gap's sole missing number would be complete and
        // pass 3 would stop before decoding the second chunk at all - see GapCompletes_* below.)
        var (result, _, audio) = await DetectFullAsync(
            Options("--max-jingle-length", "0"),
            [new(1195, 1200)],
            s =>
            {
                s.Add(1200, Seg(0.2, " Chapter two."));
                s.Add(590, Seg(20, " Chapter one.")); // pass-3 chunk 2 (window start 590), phrase at 610
            });

        Assert.False(result.GapRemains);
        Assert.Equal([new(1, 609.75), new(2, 1199.95)], result.Chapters);
        Assert.Contains(590.0, audio.DecodeStarts);
    }

    [Fact]
    public async Task GapCompletes_WhenAllExpectedChaptersAreFound_StopsBeforeTheNextChunk()
    {
        // Same leading-gap setup as above, but chapter 1's phrase sits in the *first* pass-3 chunk
        // [0, 600] (at 10). The gap's sole missing number is then complete after that chunk, so
        // transcription stops immediately - the second chunk at 590 is never decoded.
        var (result, _, audio) = await DetectFullAsync(
            Options("--max-jingle-length", "0"),
            [new(1195, 1200)],
            s =>
            {
                s.Add(1200, Seg(0.2, " Chapter two."));
                s.Add(0, Seg(10, " Chapter one.")); // pass-3 chunk 1 [0, 600], phrase at 10
            });

        Assert.False(result.GapRemains);
        Assert.Equal([new(1, 9.75), new(2, 1199.95)], result.Chapters);
        Assert.DoesNotContain(590.0, audio.DecodeStarts);
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
        Assert.Equal([new(1, 0.25), new(2, 599.75), new(3, 1199.95)], result.Chapters);
        // The pass-3 transcriber had its language set before it was used (auto-detected "en").
        Assert.Contains("en", pass3.LanguageChanges);
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
        Assert.Equal(
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
        Assert.Equal(
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
        Assert.Equal(
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

        Assert.Equal([new(1, 0.25), new(2, 600.05), new(3, 904.95)], result.Chapters);
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

        Assert.Equal(
            [new(1, 0.25), new(2, 600.05), new(3, 1199.95)],
            result.Chapters);
    }

    [Fact]
    public async Task JingleMark_IsAnchoredBeforeTheLatestSilenceBeforeThePhrase()
    {
        // Probe window at 600: jingle until 615, short silence 615-618, phrase at 618.2.
        // The mark belongs 0.5 s before the end of the silence directly preceding the phrase.
        var result = await DetectAsync(
            Options("--mark-before-jingle"),
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
            Options("--mark-before-jingle"),
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
            Options("--mark-before-jingle"),
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

        Assert.Equal([new(1, 0.25), new(2, 600.05)], result.Chapters);
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

        Assert.Equal([new(1, 0.25), new(2, 600.05)], result.Chapters);
        Assert.Contains(audio.DecodeWindows,
            w => w.Start == 600 && w.Duration is { } d && Math.Abs(d - 12) < 0.01);
    }

    [Fact]
    public async Task MarkBeforeJingleWithMaxJingleLengthZero_StillAnchorsViaVad_ButKeepsTheNarrowWindow()
    {
        // --mark-before-jingle turns on the VAD pre-pass and its 0.5 s jingle-anchor placement,
        // but --max-jingle-length 0 says no jingle is expected, so Pass 2's probe window must
        // stay at the plain 12 s width rather than widening to the jingle ceiling - the two
        // options are independent: one controls mark placement, the other the probe width.
        var (result, _, audio) = await DetectFullAsync(
            Options("--mark-before-jingle", "--max-jingle-length", "0"),
            [new(695, 700)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(700, Seg(3.2, " Chapter two."));
            },
            new FakeVad { Speech = [new(0, 695), new(703, 3600)] });

        Assert.Contains(new DetectedChapter(2, 699.5), result.Chapters);
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
            Options("--mark-before-jingle"),
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
            Options("--mark-before-jingle", "--max-jingle-length", "auto"),
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
            Options("--mark-before-jingle"),
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
            Options("--mark-before-jingle"),
            [new(610, 613)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(613, Seg(32, " Chapter two.")); // window [613, ...], phrase at 645
            },
            new FakeVad { Speech = [new(0, 640), new(650, 3600)] }); // jingle region 640-650 envelops 645

        Assert.False(result.GapRemains);
        Assert.Equal([new(1, 0), new(2, 640)], result.Chapters);
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
        // in the sibling tests above.
        var result = await DetectAsync(
            Options("--mark-before-jingle"),
            [new(610, 613), new(654.5, 655)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(613, Seg(42, " Chapter two.")); // window [613, ...], phrase at 655
            },
            new FakeVad { Speech = [new(0, 610), new(613, 640), new(660, 3600)] }); // jingle region 640-660, unbroken

        Assert.False(result.GapRemains);
        Assert.Equal([new(1, 0), new(2, 640)], result.Chapters);
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
        Assert.Equal([new(1, 0), new(2, 643.2)], result.Chapters);
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
        Assert.Equal([new(1, 0), new(2, 643.6)], result.Chapters);
    }

    [Fact]
    public async Task JingleSplitByAnUntranscribedVocal_IsBridgedBackToItsRealStart()
    {
        // The chapter-20 shape: a vocal-like passage in the jingle's own music (blip 645-646.2,
        // just over the 1 s merge limit) splits one continuous jingle into two VAD regions
        // ([640, 645] and [646.2, 660]). The phrase (655) selects the later fragment, whose start
        // sits mid-jingle. Whisper transcribed no words over the splitting blip - by the
        // only-speech-in-a-jingle-is-the-phrase rule that makes it music, so the region must be
        // bridged backward across it and the mark placed at the true jingle start (640).
        var result = await DetectAsync(
            Options("--mark-before-jingle", "--min-silence-length", "1.5"),
            [],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(640, Seg(15, " Chapter two.")); // phrase at 655, inside the later fragment
            },
            new FakeVad { Speech = [new(0, 640), new(645, 646.2), new(660, 3600)] });

        Assert.False(result.GapRemains);
        Assert.Equal([new(1, 0), new(2, 640)], result.Chapters);
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
        Assert.Equal([new(1, 0), new(2, 646.5)], result.Chapters);
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
            Options("--mark-before-jingle", "--min-silence-length", "1.5"),
            [new(610, 613)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(613, new TranscriptSegment(25, 45, " Chapter two.", 1.0)); // abs 638-658, smeared
            },
            new FakeVad { Speech = [new(0, 640), new(660, 3600)] });

        Assert.False(result.GapRemains);
        Assert.Equal([new(1, 0), new(2, 640)], result.Chapters);
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
            Options("--min-silence-length", "1.5"),
            [new(610, 613)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(613, new TranscriptSegment(25, 45, " Chapter two.", 1.0)); // abs 638-658, smeared
            },
            new FakeVad { Speech = [new(0, 640), new(660, 3600)] });

        Assert.False(result.GapRemains);
        Assert.Equal([new(1, 0.25), new(2, 659.75)], result.Chapters);
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
            Options(),
            [new(610, 613)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(613, Seg(32, " Chapter two.")); // window [613, ...], phrase at 645
            },
            new FakeVad { Speech = [new(0, 640), new(650, 3600)] }); // jingle region 640-650 envelops 645

        Assert.False(result.GapRemains);
        Assert.Equal([new(1, 0.25), new(2, 649.75)], result.Chapters);
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
            Options("--min-silence-length", "1.5"),
            [new(610, 613)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(613, new TranscriptSegment(25, 45, " Chapter two.", 1.0)); // abs 638-658, smeared
            },
            new FakeVad { Speech = [new(0, 640), new(656, 656.6), new(657, 658.2), new(660, 3600)] });

        Assert.False(result.GapRemains);
        Assert.Equal([new(1, 0.25), new(2, 655.75)], result.Chapters);
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
            Options("--min-silence-length", "1.5"),
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
        Assert.Equal([new(1, 0.25), new(2, 654.95)], result.Chapters);
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
            Options("--min-silence-length", "1.5"),
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
        Assert.Equal([new(1, 0.25), new(2, 655.75)], result.Chapters);
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
        Assert.Contains(new DetectedChapter(2, 800), result.Chapters); // jingle start, clipped length fed to auto
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

        Assert.Equal(
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

        Assert.Equal([new DetectedChapter(1, 600.25, 0.3)], result.Chapters);
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

        Assert.Equal([new DetectedChapter(1, 600.25)], result.Chapters);
        Assert.DoesNotContain(612.0, audio.DecodeStarts);
        Assert.DoesNotContain(606.0, audio.DecodeStarts);
    }

    [Fact]
    public async Task SequenceSkippedWindow_IsReProbed_WhenASequenceGapTurnsUp()
    {
        // Chapter two's confident mark skips the overlapping window at 608 - which is exactly
        // where chapter three's announcement hides (the "sequence spans two transitions" case
        // the skip bets against). The later chapter-four mark exposes the gap, and the skipped
        // window must then be re-probed and chapter three recovered - even with an explicit
        // --min-silence-length, where the gap re-probe used to be unreachable (nothing was
        // ever skipped before the sequence skip existed).
        var (result, _, audio) = await DetectFullAsync(
            Options("--min-silence-length", "1.5"),
            [new(595, 600), new(606, 608), new(1195, 1200)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(600, Seg(0.3, " Chapter two."));
                s.Add(608, Seg(0.4, " Chapter three."));
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
        // silence ([638, 640], mark 0.5 s before its end) even though the window was triggered
        // by chapter one's silence. The confident marks then settle the overlapping window
        // sequence, so the candidate at 640 is never probed at all - neither its start (640)
        // nor the shared border (650) is ever decoded.
        var (result, _, audio) = await DetectFullAsync(
            Options("--mark-before-jingle"),
            [new(598, 600), new(638, 640)],
            s => s.Add(600, Seg(2, " Chapter one."), Seg(40, " Chapter two.")),
            new FakeVad { Speech = [new(0, 3600)] });

        Assert.Equal([1, 2], result.Chapters.Select(c => c.Number));
        Assert.Contains(new DetectedChapter(2, 639.5), result.Chapters);
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

        Assert.Equal([new DetectedChapter(1, 608.75)], result.Chapters);
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

        Assert.Equal([new DetectedChapter(1, 0.25)], result.Chapters);
        Assert.False(result.GapRemains);
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
        Assert.Equal(
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
    public async Task OverlappingProbe_LogsFullyReusedWindows_WithoutASegmentDump()
    {
        // Near the end of the file the probe window is capped at the file's duration (3600 s),
        // so two close-together candidates can end up with the very same (capped) window end -
        // window 2 is then fully contained in window 1's cache and no Whisper call happens at
        // all. The log must say so plainly rather than dumping a (nonexistent) transcript.
        // Chapter one is scripted at low confidence so the overlap-sequence skip stays out of
        // the way and the fully-contained window is actually visited.
        var (_, log, audio) = await DetectWithLogAsync(
            Options("--min-silence-length", "1.5"),
            [new(3585, 3590), new(3593, 3595)],
            s => s.Add(3590, Seg(0.5, " Chapter one.", confidence: 0.3)));

        Assert.Contains("probe @0:59:55.00: fully reused, no new transcription", log);
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

        Assert.Equal([new DetectedChapter(1, 606.25)], result.Chapters);
        Assert.Contains(log, l => l.Contains("chapter 1 detection spans the reused/fresh transcript merge"));
    }

    [Fact]
    public void PlanWindowEnd_KeepsTheNaturalEnd_WhenTheWindowsDoNotOverlap()
    {
        // The next window starts far past this one's natural end (612), and the only stored
        // silence ([608, 608.6], mid 608.3) lies before it - nothing in the (612, 617]
        // forward search either, so the window keeps its natural length.
        var end = ChapterDetector.PlanWindowEnd(
            600, 1200, 12, 3600, [new(608, 608.6)], [], jingle: false);
        Assert.Equal(612, end);
    }

    [Fact]
    public void PlanWindowEnd_SnapsASharedBorder_ToTheSeamNearestTheBorder()
    {
        // Both [604, 605] (mid 604.5) and [610, 611] (mid 610.5) lie within window 2
        // ([603, 615]); the shared border (window 1's natural end, 612) snaps to the nearer
        // mid-point, shortening window 1's decode.
        var end = ChapterDetector.PlanWindowEnd(
            600, 603, 12, 3600, [new(604, 605), new(610, 611)], [], jingle: false);
        Assert.Equal(610.5, end);
    }

    [Fact]
    public void PlanWindowEnd_ExtendsAWindow_WhenTheOnlySeamLiesBeyondItsNaturalEnd()
    {
        // The only target ([613, 614]) sits past window 1's natural end (612) - the plan may
        // move the border itself, so window 1 is extended to the mid-point (613.5) and the
        // next window's fresh decode will start exactly there. No hole, no mid-word cut.
        var end = ChapterDetector.PlanWindowEnd(
            600, 606, 12, 3600, [new(613, 614)], [], jingle: false);
        Assert.Equal(613.5, end);
    }

    [Fact]
    public void PlanWindowEnd_FallsBackToTheNaturalEnd_WhenNoSeamTargetExists()
    {
        // The only silences lie at or before window 2's start - nothing inside (606, 618] to
        // snap to, so the shared border stays the natural end: the raw-border joint is the
        // only kind of overlap the plan leaves behind.
        var end = ChapterDetector.PlanWindowEnd(
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
        var first = ChapterDetector.PlanWindowEnd(600, 606, 12, 3600, silences, [], jingle: false);
        var second = ChapterDetector.PlanWindowEnd(606, 612, 12, 3600, silences, [], jingle: false);
        Assert.Equal(610.5, first);
        Assert.Equal(616.5, second);
    }

    [Fact]
    public void PlanWindowEnd_LeavesABorderAlone_WhenTheNextWindowEndsWithinThisOne()
    {
        // Clamped to the file end, both windows end at 3600, so the later one is fully
        // contained in the earlier - there is no shared border to snap even though a target
        // would be available; the contained window is served from cache instead.
        var end = ChapterDetector.PlanWindowEnd(
            3590, 3595, 12, 3600, [new(3596, 3597)], [], jingle: false);
        Assert.Equal(3600, end);
    }

    [Fact]
    public void PlanWindowEnd_UsesVadRegions_OnlyInJingleMode()
    {
        // A VAD non-speech region is a valid seam target when the VAD pre-pass ran, but
        // without it there's no VAD data worth trusting - the same layout must snap only
        // when the region is present.
        List<ChapterDetector.NonSpeechRegion> regions = [new(608, 609)];
        var plain = ChapterDetector.PlanWindowEnd(600, 606, 12, 3600, [], regions, jingle: false);
        var jingle = ChapterDetector.PlanWindowEnd(600, 606, 12, 3600, [], regions, jingle: true);
        Assert.Equal(612, plain);
        Assert.Equal(608.5, jingle);
    }

    [Fact]
    public void PlanWindowEnd_ExtendsAStandAloneEnd_ToASeamShortlyAfterIt()
    {
        // This window's end (12) does not lie inside the next window ([600, 612]) - no shared
        // border - but a silence sits just past it: the end is extended to its mid-point
        // (13.5) so the decode stops word-safely.
        var end = ChapterDetector.PlanWindowEnd(
            0, 600, 12, 3600, [new(13, 14)], [], jingle: false);
        Assert.Equal(13.5, end);
    }

    [Fact]
    public void PlanWindowEnd_KeepsAStandAloneEnd_WhenNoSeamLiesWithinTheForwardSearch()
    {
        // Neither a target whose mid-point lies before the natural end (extension only - the
        // window must never shrink below its natural span) nor one past the 5 s search limit
        // ([18, 19], mid-point 18.5 > 17) may move the end: it stays at the natural 12.
        var end = ChapterDetector.PlanWindowEnd(
            0, null, 12, 3600, [new(8, 9), new(18, 19)], [], jingle: false);
        Assert.Equal(12, end);
    }

    [Fact]
    public void PlanWindowEnd_StandAloneEndSnap_UsesVadRegionsOnlyInJingleMode()
    {
        List<ChapterDetector.NonSpeechRegion> regions = [new(13, 14)];
        var plain = ChapterDetector.PlanWindowEnd(0, null, 12, 3600, [], regions, jingle: false);
        var jingle = ChapterDetector.PlanWindowEnd(0, null, 12, 3600, [], regions, jingle: true);
        Assert.Equal(12, plain);
        Assert.Equal(13.5, jingle);
    }

    [Fact]
    public void PlanWindowEnd_StandAloneEndSnap_StopsAtTheFileEnd()
    {
        // The natural end is already clamped to the file end - there is no room to extend
        // into, so the forward search must come up empty regardless of nearby targets.
        var end = ChapterDetector.PlanWindowEnd(
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

        Assert.Equal([new(1, 0.25), new(2, 600.05)], result.Chapters);
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
        var trimmed = ChapterDetector.TrimLeadingNonSpeech(
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
        var trimmed = ChapterDetector.TrimLeadingNonSpeech(
            segments, [new(45, 49)], [], jingle: false);
        Assert.Equal(50, trimmed[0].StartSeconds);
    }

    [Fact]
    public void TrimLeadingNonSpeech_UsesJingleRegions_OnlyInJingleMode()
    {
        // A VAD non-speech region leads the segment, but only when the VAD pre-pass ran is
        // that data trusted: without it the region is ignored and the start stays put.
        var segments = new List<TranscriptSegment> { new(830, 850, " Chapter two.", 1.0) };
        var plain = ChapterDetector.TrimLeadingNonSpeech(segments, [], [new(830, 835)], jingle: false);
        var jingle = ChapterDetector.TrimLeadingNonSpeech(segments, [], [new(830, 835)], jingle: true);
        Assert.Equal(830, plain[0].StartSeconds);
        Assert.Equal(835, jingle[0].StartSeconds);
    }

    [Fact]
    public void TrimLeadingNonSpeech_ToleratesASilenceThatStartsJustAfterTheSegment()
    {
        // Whisper's segment start (830) can sit a hair before silencedetect's frame-precise
        // onset (830.4); the small tolerance still recognises the silence as leading it.
        var segments = new List<TranscriptSegment> { new(830, 840, " Chapter two.", 1.0) };
        var trimmed = ChapterDetector.TrimLeadingNonSpeech(
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
        // is the true anchor: the mark lands 0.5 s before its end, at 830.5.
        var result = await DetectAsync(
            Options("--mark-before-jingle"),
            [new(595, 600), new(820, 823), new(830.3, 831.0)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(823, new TranscriptSegment(7.3, 27.3, " Chapter two.", 1.0)); // abs [830.3, 850.3]
            },
            new FakeVad { Speech = [new(0, 830.5), new(836, 3600)] });

        Assert.Contains(new DetectedChapter(2, 830.5), result.Chapters);
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
        Assert.Contains(new DetectedChapter(2, 596.75), result.Chapters); // pinpointed at the phrase start
        Assert.Contains(log, l => l.Contains("chapter 2 detection spans a Pass 3 chunk seam"));
        Assert.Contains(audio.DecodeWindows,
            w => w.Start == 0.25 && w.Duration is { } d && Math.Abs(d - 598.25) < 0.01);
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
        Assert.Equal([new(1, 0.25), new(2, 200.25), new(3, 500.05)], result.Chapters);
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
            Options("--max-jingle-length", "0"),
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
        Assert.Equal([new(1, 0.25), new(2, 199.75), new(3, 500.05)], result.Chapters);
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
            Options("--max-jingle-length", "0"),
            [new(495, 500), new(200, 206)],
            s =>
            {
                s.Add(0, Seg(0.5, " Chapter one."));
                s.Add(500, Seg(0.3, " Chapter three."));
                s.Add(0.5, Seg(0, " Chapter one."), Seg(494, " Chapter three."));
                s.Add(198, Seg(2, " Chapter 2."));
            });

        Assert.False(result.GapRemains);
        Assert.Equal([new(1, 0.25), new(2, 199.75), new(3, 500.05)], result.Chapters);
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
    public void AdvancePastNonSpeech_ReturnsTheOnsetOfTheNextQualifyingSpeechSegment()
    {
        // Scanning forward from a point still in non-speech (100) must land on the start of the
        // next speech segment that clears the noise floor (105), not its end or midpoint.
        var speech = new List<SpeechSegment> { new(0, 50), new(105, 108) };
        Assert.Equal(105, ChapterDetector.AdvancePastNonSpeech(100, speech, 0.1));
    }

    [Fact]
    public void AdvancePastNonSpeech_SkipsATransientShorterThanTheFloor()
    {
        // A 0.05 s blip between the scan start and the true onset is detector noise-floor jitter,
        // not real speech - it must be skipped over (resuming the scan from its own end), landing
        // on the next, genuine (3 s) segment's start instead.
        var speech = new List<SpeechSegment> { new(0, 50), new(102, 102.05), new(105, 108) };
        Assert.Equal(105, ChapterDetector.AdvancePastNonSpeech(100, speech, 0.1));
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
        Assert.Equal(106, ChapterDetector.AdvancePastNonSpeech(100, speech, 0.1));
    }

    [Fact]
    public void AdvancePastNonSpeech_TreatsExactlyTheFloorDurationAsGenuine()
    {
        // A blip of exactly the floor duration is not "shorter than" it, so it counts as a real
        // onset rather than being skipped as a transient. Integer bounds avoid the binary64
        // rounding that a literal like 102.1 - 102 would introduce right at the comparison edge.
        var speech = new List<SpeechSegment> { new(0, 50), new(102, 103) };
        Assert.Equal(102, ChapterDetector.AdvancePastNonSpeech(100, speech, 1.0));
    }

    [Fact]
    public void AdvancePastNonSpeech_DoesNotMoveBackward_WhenAlreadyInsideAQualifyingSegment()
    {
        // The scan start (105) already sits inside a genuine speech segment (100-110) - it must be
        // returned unchanged, never snapped back to the segment's own start.
        var speech = new List<SpeechSegment> { new(0, 50), new(100, 110) };
        Assert.Equal(105, ChapterDetector.AdvancePastNonSpeech(105, speech, 0.1));
    }

    [Fact]
    public void AdvancePastNonSpeech_DoesNotMoveBackward_WhenAlreadyInsideATransient()
    {
        // The scan start (100.02) happens to fall inside a short (0.05 s) blip that would
        // otherwise be treated as a transient - "never move backward past a point already at or
        // beyond a segment" wins over transient-skipping, so it is still returned unchanged rather
        // than being pushed forward past this blip to the next segment.
        var speech = new List<SpeechSegment> { new(0, 50), new(100, 100.05), new(105, 108) };
        Assert.Equal(100.02, ChapterDetector.AdvancePastNonSpeech(100.02, speech, 0.1));
    }

    [Fact]
    public void AdvancePastNonSpeech_ReturnsNull_WhenTheSpeechDataDoesNotReachFarEnough()
    {
        // No segment in the given data ends after the scan start (100), so there is nothing left
        // to find a qualifying onset in - the caller must be told to look further (e.g. by
        // re-running VAD over a wider window) rather than being given a false answer.
        var speech = new List<SpeechSegment> { new(0, 50) };
        Assert.Null(ChapterDetector.AdvancePastNonSpeech(100, speech, 0.1));
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
        // A chapter > 1 within the first 10 s is taken as-is (e.g. a book starting mid-series).
        var chapters = new List<DetectedChapter> { new(2, 8) };
        Assert.Empty(ChapterDetector.FindGaps(chapters, Duration));
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

        Assert.Equal([new(1, 0.25), new(2, 600.05)], result.Chapters);
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

        Assert.Equal([new(1, 0.25), new(2, 600.05)], result.Chapters);
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

        Assert.Equal([new(1, 0.25), new(2, 600.05)], result.Chapters);
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
        var result = await detector.VerifyExistingChaptersAsync(_file, info, tracker, null, CancellationToken.None);
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
        var plan = ChapterDetector.BuildGapRegions(markings, Duration);

        Assert.Equal([new(10, 50, 1, 3)], plan.Regions);
        Assert.Null(plan.TrailingFrom);
        Assert.Empty(plan.TrailingTargets);
    }

    [Fact]
    public void BuildGapRegions_BuildsATrailingRegion_WhenTheLastCheckableMarkingIsUnconfirmed()
    {
        var markings = new List<VerifyMarkingOutcome> { new(10, 1, true), new(610, 2, false) };
        var plan = ChapterDetector.BuildGapRegions(markings, Duration);

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
        var plan = ChapterDetector.BuildGapRegions(markings, Duration);

        Assert.Equal([new(10, 40, 1, 4)], plan.Regions);
    }

    [Fact]
    public void BuildGapRegions_KeepsSeparateRunsAsSeparateRegions()
    {
        var markings = new List<VerifyMarkingOutcome>
        {
            new(10, 1, true), new(20, 2, false), new(30, 3, true), new(40, 4, false), new(50, 5, true),
        };
        var plan = ChapterDetector.BuildGapRegions(markings, Duration);

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
        var plan = ChapterDetector.BuildGapRegions(markings, Duration);

        Assert.Equal([new(10, 40, 1, 4)], plan.Regions);
    }

    [Fact]
    public void BuildGapRegions_ReturnsNoRegions_WhenEveryCheckableMarkingIsConfirmed()
    {
        var markings = new List<VerifyMarkingOutcome> { new(10, 1, true), new(610, 2, true) };
        var plan = ChapterDetector.BuildGapRegions(markings, Duration);

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
        var result = await detector.DetectGapsAsync(_file, Info, new WorkTracker(), null, verify, CancellationToken.None);
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
            Options("--max-jingle-length", "0"), verify, [],
            s => s.Add(10, Seg(0.3, " Chapter 2.")));

        Assert.False(result.GapRemains);
        Assert.Equal([new(1, 10), new(2, 10.05), new(3, 50)], result.Chapters);
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
        Assert.Contains(new DetectedChapter(3, 50), result.Chapters);
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
        var result = await detector.ResumeMissingMarksAsync(_file, info, new WorkTracker(), null, CancellationToken.None);
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
            Options("--lang", "en", "--max-jingle-length", "0"),
            [new Chapter(10, "Chapter 1"), new Chapter(50, "Chapter 3")], [],
            s => s.Add(10, Seg(0.3, " Chapter 2.")));

        Assert.False(result.GapRemains);
        Assert.Equal([new(1, 10), new(2, 10.05), new(3, 50)], result.Chapters);
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
        Assert.Equal([new(1, 10), new(3, 50)], result.Chapters);
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
        Assert.Equal([new(1, 10), new(2, 10.05), new(3, 50)], result.Chapters);
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
}
