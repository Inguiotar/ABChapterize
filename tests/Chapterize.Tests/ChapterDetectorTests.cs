// Chapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Xunit;

namespace Chapterize.Tests;

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
        _dir = Path.Combine(Path.GetTempPath(), $"chapterize-test-{Guid.NewGuid():N}");
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
            return Task.FromResult(new float[16000]);
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
    }

    /// <summary>One speech segment starting at the given offset within the decode window.</summary>
    private static TranscriptSegment Seg(double startSeconds, string text)
        => new(startSeconds, startSeconds + 2, text);

    /// <summary>Builds validated options with the temp file as target.</summary>
    private CliOptions Options(params string[] args)
        => CliOptions.Parse([.. args, _file])!;

    /// <summary>Runs the detector against the given silences and script.</summary>
    private async Task<DetectionResult> DetectAsync(
        CliOptions options, List<Silence> silences, Action<ScriptedTranscriber> script)
    {
        var audio = new FakeAudioSource { Silences = silences };
        var transcriber = new ScriptedTranscriber(audio);
        script(transcriber);
        var detector = new ChapterDetector(options, audio, transcriber);
        return await detector.DetectAsync(_file, Info, new WorkTracker(), null, CancellationToken.None);
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
    public async Task NoSpeechAnywhere_YieldsNoChapters()
    {
        var result = await DetectAsync(Options(), [new(595, 600)], _ => { });
        Assert.Empty(result.Chapters);
        Assert.False(result.GapRemains);
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
}
