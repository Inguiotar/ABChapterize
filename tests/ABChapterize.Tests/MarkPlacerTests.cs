// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Xunit;
using ABChapterize.Audio;
using ABChapterize.Cli;
using ABChapterize.Detection;
using ABChapterize.Language;
using ABChapterize.Language.Phrases;
using ABChapterize.Transcription;
using ABChapterize.Vad;

namespace ABChapterize.Tests;

/// <summary>
/// Tests for <see cref="MarkPlacer.KeepOutOfSpeech"/>, the guard that refuses a refined mark which
/// landed inside somebody else's words.
/// </summary>
/// <remarks>
/// The geometry underneath it - <see cref="AnnouncementIsolation.DepthInsideSpeech"/> and
/// <see cref="AnnouncementIsolation.NextSpeechOnsetAfter"/> - is covered by
/// <see cref="AnnouncementIsolationTests"/> against the same real timeline. What is tested here is
/// the decision built on that geometry, and in particular the properties whose loss would be
/// silent: that a declined guard hands back the refined mark untouched, that a displaced onset is a
/// real position rather than null, and that the correction moves a mark <em>out</em> of speech
/// rather than onto whatever the default happened to be. Null onset there would send the isolation
/// check to a fallback that is itself null for a bare number found mid-segment, turning a mark a
/// little out of place into a chapter dropped outright - a regression that would compile and pass
/// everything else.
/// </remarks>
public sealed class MarkPlacerTests : IDisposable
{
    private readonly string _dir;
    private readonly string _file;

    /// <summary>Creates a temp .m4b file, since <see cref="CliOptions.Parse"/> checks its
    /// targets exist. Nothing ever reads it.</summary>
    public MarkPlacerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "abc-markplacer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "book.m4b");
        File.WriteAllBytes(_file, new byte[16]);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>The audio source is never reached: these tests call the decision directly, and it
    /// decides from VAD geometry alone. Every member therefore refuses rather than pretending.
    /// </summary>
    private sealed class UnusedAudio : IAudioSource
    {
        public Task<List<Silence>> DetectSilencesAsync(
            string file, double durationSeconds, double minSilenceSeconds, double noiseDb,
            Action<double>? progress, string? inputDecoder, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<float[]> DecodePcmAsync(
            string file, double startSeconds, double? durationSeconds, string? inputDecoder, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<List<Silence>> DetectSilencesAndStreamPcmAsync(
            string file, double durationSeconds, double minSilenceSeconds, double noiseDb,
            Func<IAsyncEnumerable<float[]>, CancellationToken, Task> consumePcm,
            Action<double>? progress, string? inputDecoder, CancellationToken ct)
            => throw new NotSupportedException();
    }

    /// <summary>Lines the guard logged, so a test can assert it announced what it did.</summary>
    private readonly List<string> _log = [];

    /// <summary>A placer over the given options, with nothing behind it that these tests reach.
    /// </summary>
    /// <param name="markLead">The run's <c>--mark-lead</c>, which is what a displaced onset is
    /// built from.</param>
    private MarkPlacer Placer(double markLead = 0.35)
    {
        var options = CliOptions.Parse(["--mark-lead", markLead.ToString("0.###"), _file])!;
        return new MarkPlacer(
            new UnusedAudio(), options, new DetectionLog(_log.Add, null),
            (_, _) => throw new NotSupportedException(),
            null,
            (_, _, _) => throw new NotSupportedException());
    }

    /// <summary>
    /// A placer with the refinement switched off, so <c>PlaceAsync</c> runs end to end without
    /// touching audio - which is what lets these tests reach the per-chapter measurements.
    /// </summary>
    private MarkPlacer QuickPlacer()
    {
        var options = CliOptions.Parse(["--quick-marks", _file])!;
        return new MarkPlacer(
            new UnusedAudio(), options, new DetectionLog(_log.Add, null),
            (_, _) => throw new NotSupportedException(),
            null,
            (_, _, _) => throw new NotSupportedException());
    }

    /// <summary>The Swedish profile the other helpers here build their phrase from.</summary>
    private LanguageProfile Profile() => CliOptions.Parse(["--lang", "sv", _file])!.DefaultProfile;

    /// <summary>The mark context the guard reads: only its speech segments matter here.</summary>
    /// <param name="segments">The VAD speech timeline.</param>
    private MarkContext Context(List<SpeechSegment> segments)
        => new(_file, null,
               AnnouncementMatcher.ForPattern(
                   PhraseCompiler.Compile(["/kapitlet/"], "sv", PhraseKind.Named, "test phrase")),
               [], segments, new TranscriptWindow([], 0, 0), "sv");

    /// <summary>
    /// "De vandrande djäknarne" chapter 6, the case the threshold was calibrated on: the reader's
    /// credit runs to 1:37:20.00, a 0.55 s pause follows, and the announcement starts at
    /// 1:37:20.41. Absolute times kept so the figures match the debug log.
    /// </summary>
    private static List<SpeechSegment> ReaderCreditTimeline() =>
    [
        new(5831.64, 5836.99), new(5837.66, 5840.00), new(5840.41, 5841.56), new(5842.14, 5843.93),
    ];

    /// <summary>
    /// "De vandrande djäknarne" chapter 11, the case that made the guard move a mark forward rather
    /// than revert it: the credit runs to 3:10:09.50, a 0.77 s pause follows, the announcement runs
    /// 3:10:10.27-3:10:13.05 in two segments, and the chapter body starts at 3:10:15.07 after a
    /// 2.02 s pause. Absolute times kept so the figures match the debug log.
    /// </summary>
    private static List<SpeechSegment> ChapterElevenTimeline() =>
    [
        new(11405.60, 11406.08), new(11407.20, 11409.50), new(11410.27, 11411.71),
        new(11412.06, 11413.05), new(11415.07, 11421.44),
    ];

    [Fact]
    public void ARefinedMarkInsideTheReadersCredit_IsMovedOutToTheFollowingOnset()
    {
        // The refinement's survival walk stepped over the 0.55 s pause and converged 1.00 s into
        // "Inläsning av Lars Rolander"; the announcement itself starts at 1:37:20.41, so the mark
        // belongs one --mark-lead in front of that - the position the user confirmed by ear.
        var (time, onset, displaced) = Placer().KeepOutOfSpeech(
            5838.66, 5840.06, 5839.06, 6, Context(ReaderCreditTimeline()));

        Assert.True(displaced);
        Assert.Equal(5840.06, time, 4);
        Assert.Equal(5840.41, onset!.Value, 4);
        Assert.Contains(_log, l => l.Contains("inside speech") && l.Contains("chapter 6"));
    }

    [Fact]
    public void ABadDefaultBehindTheAnnouncement_DoesNotDragTheMarkWithIt()
    {
        // Chapter 11 of the build-362 corpus run. A window that read the announcement at its tail
        // put the default-mode mark at 3:10:14.72 - past the whole announcement, on the first word
        // of the chapter body - and reverting onto it shipped the mark 4.8 s late. The refinement
        // had heard the announcement correctly; only its onset overshot backwards into the credit,
        // so the correction is the next speech onset, 3:10:10.27, giving 3:10:09.92.
        var (time, onset, displaced) = Placer().KeepOutOfSpeech(
            11409.12, 11414.72, 11409.47, 11, Context(ChapterElevenTimeline()));

        Assert.True(displaced);
        Assert.Equal(11409.92, time, 4);
        Assert.Equal(11410.27, onset!.Value, 4);
    }

    [Fact]
    public void ADisplacedOnset_IsAPositionAndNotNull()
    {
        // Load-bearing: null here routes the isolation check to a fallback that is null for a bare
        // number found mid-segment, and the mark is then dropped rather than merely moved.
        var (_, onset, _) = Placer(markLead: 0.35).KeepOutOfSpeech(
            5838.66, 5840.06, 5839.06, 6, Context(ReaderCreditTimeline()));

        Assert.NotNull(onset);
        Assert.Equal(5840.41, onset!.Value, 4);
    }

    [Fact]
    public void ADisplacedMark_FollowsTheRunsOwnMarkLead()
    {
        // The onset is where the announcement is believed to start, and the mark is one --mark-lead
        // in front of it - so the reconstruction has to use the run's lead, not the default one.
        var (time, onset, _) = Placer(markLead: 1.5).KeepOutOfSpeech(
            5838.66, 5840.06, 5839.06, 6, Context(ReaderCreditTimeline()));

        Assert.Equal(5840.41, onset!.Value, 4);
        Assert.Equal(5838.91, time, 4);
    }

    [Fact]
    public void AResumptionPastTheDefaultMark_FallsBackToTheDefault()
    {
        // The refinement walks backwards from the default, so an announcement it overshot lies
        // between the two. Speech resuming beyond the default is therefore not the announcement
        // being recovered - a jingle-anchored default in front of the music is the shape that
        // produces it - and moving the mark there would carry it further from the chapter, not
        // nearer. Refined mark 30.0 sits inside 28.0-32.0; the next onset is 40.0, well past the
        // default at 33.0.
        var timeline = new List<SpeechSegment> { new(28.0, 32.0), new(40.0, 50.0) };
        var (time, onset, displaced) = Placer().KeepOutOfSpeech(30.0, 33.0, 30.4, 6, Context(timeline));

        Assert.True(displaced);
        Assert.Equal(33.0, time, 4);
        Assert.Equal(33.35, onset!.Value, 4);
        Assert.Contains(_log, l => l.Contains("keeping the default"));
    }

    [Fact]
    public void AMarkOnlyShallowlyInsideSpeech_IsKept()
    {
        // The threshold sits halfway across an empty band: 441 of 443 corpus marks measure exactly
        // zero and the only two non-zero ones were 1.00 s and 1.42 s. A mark a few hundredths in -
        // which a VAD boundary produces routinely - must not be second-guessed.
        var (time, onset, displaced) = Placer().KeepOutOfSpeech(
            5840.42, 5840.06, 5840.77, 6, Context(ReaderCreditTimeline()));

        Assert.False(displaced);
        Assert.Equal(5840.42, time, 4);
        Assert.Equal(5840.77, onset!.Value, 4);
        Assert.Empty(_log);
    }

    [Fact]
    public void WhenTheDefaultMarkIsInsideSpeechToo_TheRefinedOneIsKept()
    {
        // Declines unless the default position is demonstrably better, rather than trading a
        // measured position for an unmeasured one.
        var timeline = new List<SpeechSegment> { new(0.0, 60.0) };
        var (time, onset, displaced) = Placer().KeepOutOfSpeech(30.0, 20.0, 30.4, 6, Context(timeline));

        Assert.False(displaced);
        Assert.Equal(30.0, time, 4);
        Assert.Equal(30.4, onset!.Value, 4);
        Assert.Empty(_log);
    }

    [Fact]
    public void WithoutAVadPrePass_NothingIsSecondGuessed()
    {
        // No segments, no verdict: a run without VAD is exactly as well off as before the guard
        // existed.
        var (time, onset, displaced) = Placer().KeepOutOfSpeech(5838.66, 5840.06, 5839.06, 6, Context([]));

        Assert.False(displaced);
        Assert.Equal(5838.66, time, 4);
        Assert.Equal(5839.06, onset!.Value, 4);
    }

    [Fact]
    public void ANamedMark_IsNamedAsSuchInTheLog()
    {
        // A prologue or epilogue reaches the same guard with no number to print.
        Placer().KeepOutOfSpeech(5838.66, 5840.06, 5839.06, null, Context(ReaderCreditTimeline()));

        Assert.Contains(_log, l => l.Contains("the named mark"));
    }
    /// <summary>
    /// A book that counts from one again in every part has as many chapter 1s as it has parts, and
    /// the per-chapter measurements have to tell them apart. Keyed on the number alone, the second
    /// part's chapter 1 replaced the first's and was then counted once per part, so the reported
    /// minimum was whichever measurement happened to be written last rather than the smallest.
    /// </summary>
    /// <remarks>
    /// Written so it fails on the old keying rather than merely passing on the new: part 1's
    /// chapter 1 sits on the shorter silence and is recorded first, so a dictionary keyed by number
    /// alone ends up holding part 2's 5.0 s for both and answers 5.0 where the truth is 2.0.
    /// </remarks>
    [Fact]
    public async Task ChapterMeasurements_TellTheParts_OfARestartingBookApart()
    {
        var placer = QuickPlacer();
        await placer.PlaceAsync(
            new NumberCheck(Sequence: 0, Number: 1, Profile(), new NumberBounds(0), null),
            100, 100, 101, new Silence(98, 100), null, Context([]), IsolationCheck.None,
            CancellationToken.None);
        await placer.PlaceAsync(
            new NumberCheck(Sequence: 1, Number: 1, Profile(), new NumberBounds(0), null),
            200, 200, 201, new Silence(195, 200), null, Context([]), IsolationCheck.None,
            CancellationToken.None);

        List<DetectedChapter> chapters =
            [new(Number: 1, TimeSeconds: 100, Sequence: 0), new(Number: 1, TimeSeconds: 200, Sequence: 1)];

        Assert.Equal(2.0, placer.MinSilenceSeconds(chapters)!.Value, 4);
    }
}
