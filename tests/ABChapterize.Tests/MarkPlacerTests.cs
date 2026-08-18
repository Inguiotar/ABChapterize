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
/// The geometry underneath it - <see cref="AnnouncementIsolation.DepthInsideSpeech"/> - is covered
/// by <see cref="AnnouncementIsolationTests"/> against the same real timeline. What is tested here
/// is the decision built on that geometry, and in particular the two properties whose loss would be
/// silent: that a declined guard hands back the refined mark untouched, and that a reverted onset
/// is a real position rather than null. Null there would send the isolation check to a fallback
/// that is itself null for a bare number found mid-segment, turning a mark a little out of place
/// into a chapter dropped outright - a regression that would compile and pass everything else.
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
    /// <param name="markLead">The run's <c>--mark-lead</c>, which is what a reverted onset is
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

    [Fact]
    public void ARefinedMarkInsideTheReadersCredit_IsGivenBackToTheDefaultPosition()
    {
        // The refinement's survival walk stepped over the 0.55 s pause and converged 1.00 s into
        // "Inläsning av Lars Rolander"; the default-mode mark at 1:37:20.06 sits in the pause and is
        // the position the user confirmed by ear.
        var (time, onset, reverted) = Placer().KeepOutOfSpeech(
            5838.66, 5840.06, 5839.06, 6, Context(ReaderCreditTimeline()));

        Assert.True(reverted);
        Assert.Equal(5840.06, time, 4);
        Assert.Contains(_log, l => l.Contains("inside speech") && l.Contains("chapter 6"));
    }

    [Fact]
    public void ARevertedOnset_IsAPositionAndNotNull()
    {
        // Load-bearing: null here routes the isolation check to a fallback that is null for a bare
        // number found mid-segment, and the mark is then dropped rather than merely moved.
        var (_, onset, _) = Placer(markLead: 0.35).KeepOutOfSpeech(
            5838.66, 5840.06, 5839.06, 6, Context(ReaderCreditTimeline()));

        Assert.NotNull(onset);
        Assert.Equal(5840.41, onset!.Value, 4);
    }

    [Fact]
    public void ARevertedOnset_FollowsTheRunsOwnMarkLead()
    {
        // The onset is where the announcement is believed to start, and the default mark is one
        // --mark-lead in front of it - so the reconstruction has to use the run's lead, not the
        // default one.
        var (_, onset, _) = Placer(markLead: 1.5).KeepOutOfSpeech(
            5838.66, 5840.06, 5839.06, 6, Context(ReaderCreditTimeline()));

        Assert.Equal(5841.56, onset!.Value, 4);
    }

    [Fact]
    public void AMarkOnlyShallowlyInsideSpeech_IsKept()
    {
        // The threshold sits halfway across an empty band: 441 of 443 corpus marks measure exactly
        // zero and the only two non-zero ones were 1.00 s and 1.42 s. A mark a few hundredths in -
        // which a VAD boundary produces routinely - must not be second-guessed.
        var (time, onset, reverted) = Placer().KeepOutOfSpeech(
            5840.42, 5840.06, 5840.77, 6, Context(ReaderCreditTimeline()));

        Assert.False(reverted);
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
        var (time, onset, reverted) = Placer().KeepOutOfSpeech(30.0, 20.0, 30.4, 6, Context(timeline));

        Assert.False(reverted);
        Assert.Equal(30.0, time, 4);
        Assert.Equal(30.4, onset!.Value, 4);
        Assert.Empty(_log);
    }

    [Fact]
    public void WithoutAVadPrePass_NothingIsSecondGuessed()
    {
        // No segments, no verdict: a run without VAD is exactly as well off as before the guard
        // existed.
        var (time, onset, reverted) = Placer().KeepOutOfSpeech(5838.66, 5840.06, 5839.06, 6, Context([]));

        Assert.False(reverted);
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
}
