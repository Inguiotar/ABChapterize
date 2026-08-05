// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Xunit;
using ABChapterize.Cli;
using ABChapterize.Detection;
using ABChapterize.Errors;

namespace ABChapterize.Tests;

/// <summary>
/// Tests for the automatic silence threshold: what it makes of a given level histogram, and that
/// the frame levels it builds one from are measured the way the calibration assumed.
/// </summary>
public sealed class SilenceThresholdProbeTests
{
    /// <summary>
    /// A level histogram shaped like an audiobook's: <paramref name="floorDb"/> for the pauses,
    /// <paramref name="speechDb"/> for the narration, mixed so that the two land on the percentiles
    /// the probe reads (5 and 75). The 30/70 split puts the boundary between the populations at the
    /// 30th percentile, clear of both.
    /// </summary>
    /// <param name="floorDb">Level of the quiet frames.</param>
    /// <param name="speechDb">Level of the loud frames.</param>
    private static List<double> Histogram(double floorDb, double speechDb)
        => [.. Enumerable.Repeat(floorDb, 300), .. Enumerable.Repeat(speechDb, 700)];

    /// <summary>The whole point of the automatic mode being on by default: an ordinary master gets
    /// exactly the threshold the fixed default always gave it. Levels from "Wintersmith" and
    /// "The Forever War" (2026-08-05 corpus measurement).</summary>
    [Theory]
    [InlineData(-84.5, -20.8)]   // Wintersmith
    [InlineData(-95.2, -22.7)]   // The Forever War
    [InlineData(-50.1, -19.8)]   // The Philosopher's Stone: the loudest room tone in the corpus
    [InlineData(-96.5, -25.1)]   // I Shall Wear Midnight: the quietest speech in the corpus
    public void OrdinaryMaster_KeepsTheDefaultThreshold(double floorDb, double speechDb)
    {
        var reading = SilenceThresholdProbe.FromFrameLevels(Histogram(floorDb, speechDb));
        Assert.Equal(DetectionTuning.DefaultSilenceNoiseDb, reading.ThresholdDb);
        Assert.False(reading.Adjusted);
        Assert.Equal(floorDb, reading.FloorDb);
        Assert.Equal(speechDb, reading.SpeechDb);
    }

    /// <summary>An audible-hiss master: at the default threshold nothing would ever read as
    /// silence, so no chapter would ever be probed for. The threshold rises to clear the hiss.</summary>
    [Fact]
    public void NoisyMaster_RaisesTheThresholdAboveTheHiss()
    {
        // Room tone at -45, a few dB above the noisiest master in the corpus.
        var reading = SilenceThresholdProbe.FromFrameLevels(Histogram(-45, -20));
        Assert.Equal(-31, reading.ThresholdDb);
        Assert.True(reading.Adjusted);
    }

    /// <summary>A very quietly mastered book: at the default threshold the narration itself would
    /// read as silence and pass 1 would return a candidate for every gap between two words.</summary>
    [Fact]
    public void QuietMaster_LowersTheThresholdBelowTheNarration()
    {
        var reading = SilenceThresholdProbe.FromFrameLevels(Histogram(-80, -38));
        Assert.Equal(-46, reading.ThresholdDb);
        Assert.True(reading.Adjusted);
    }

    /// <summary>With less range between room tone and narration than the two headrooms need, no
    /// threshold satisfies both bounds - so it goes halfway, the point furthest from either
    /// mistake, rather than picking a side.</summary>
    [Fact]
    public void MasterWithNoGap_SplitsTheDifference()
    {
        var reading = SilenceThresholdProbe.FromFrameLevels(Histogram(-40, -30));
        Assert.Equal(-35, reading.ThresholdDb);
        // Equal to the default here by arithmetic rather than by decision, which is exactly the
        // case Adjusted must not claim credit for.
        Assert.False(reading.Adjusted);
    }

    /// <summary>A reading far outside the plausible range means the excerpts were not
    /// representative, and the fixed default has the better record than an outlier.</summary>
    [Fact]
    public void ExtremeReading_IsClampedToThePlausibleRange()
    {
        Assert.Equal(-20, SilenceThresholdProbe.FromFrameLevels(Histogram(-8, -2)).ThresholdDb);
        Assert.Equal(-60, SilenceThresholdProbe.FromFrameLevels(Histogram(-130, -70)).ThresholdDb);
    }

    [Fact]
    public void NothingAudible_KeepsTheDefault()
    {
        Assert.Equal(SilenceThresholdProbe.Unmeasured, SilenceThresholdProbe.FromFrameLevels([]));
        Assert.Equal(
            SilenceThresholdProbe.Unmeasured,
            SilenceThresholdProbe.FromFrameLevels(
                [.. Enumerable.Repeat(double.NegativeInfinity, 100)]));
    }

    /// <summary>Digital silence in the pauses is the common case, not an error: it imposes no lower
    /// bound on the threshold and everything else proceeds as usual.</summary>
    [Fact]
    public void DigitallySilentPauses_LeaveTheDefaultStanding()
    {
        var reading = SilenceThresholdProbe.FromFrameLevels(
            Histogram(double.NegativeInfinity, -20));
        Assert.Equal(DetectionTuning.DefaultSilenceNoiseDb, reading.ThresholdDb);
        Assert.Equal(double.NegativeInfinity, reading.FloorDb);
    }

    /// <summary>A full-scale square wave is 0 dBFS by definition, and half amplitude is -6.02;
    /// this pins the measurement itself, which every calibration figure above is stated in.</summary>
    [Fact]
    public void FrameLevels_AreRmsDbfs()
    {
        var samples = new float[16000];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = i % 2 == 0 ? 0.5f : -0.5f;
        var levels = new List<double>();
        SilenceThresholdProbe.AddFrameLevels(samples, levels);

        Assert.Equal(20, levels.Count); // 1 s at 50 ms per frame
        Assert.All(levels, db => Assert.Equal(-6.02, db, 2));
    }

    [Fact]
    public void FrameLevels_ReportDigitalSilenceAsNegativeInfinity()
    {
        var levels = new List<double>();
        SilenceThresholdProbe.AddFrameLevels(new float[16000], levels);
        Assert.All(levels, db => Assert.Equal(double.NegativeInfinity, db));
    }

    /// <summary>The excerpts stay inside the book's body: a label jingle at the front and the
    /// closing credits are not what it sounds like.</summary>
    [Fact]
    public void ExcerptStarts_SpanTheBodyOfTheFile()
    {
        var starts = SilenceThresholdProbe.ExcerptStarts(3600).ToList();
        Assert.Equal(DetectionTuning.NoiseProbeExcerpts, starts.Count);
        // First excerpt begins at the 5% mark, last one ends at the 95% mark.
        Assert.Equal(180, starts[0], 3);
        Assert.Equal(3420, starts[^1] + DetectionTuning.NoiseProbeExcerptSeconds, 3);
        Assert.Equal(starts.Order(), starts);
    }

    /// <summary>A file too short to hold eight distinct excerpts must not pay for eight identical
    /// decodes of the same audio.</summary>
    [Fact]
    public void ExcerptStarts_CollapseOnAVeryShortFile()
        => Assert.Equal([0.25], SilenceThresholdProbe.ExcerptStarts(5));

    [Fact]
    public void NoiseFloor_DefaultsToAuto()
    {
        var o = ParseFile();
        Assert.True(o.AutoNoiseFloor);
        Assert.Equal(DetectionTuning.DefaultSilenceNoiseDb, o.NoiseFloorDb);
    }

    [Fact]
    public void NoiseFloor_TakesAnExplicitLevel_WithEitherDecimalSeparator()
    {
        Assert.False(ParseFile("--noise-floor", "-42.5").AutoNoiseFloor);
        Assert.Equal(-42.5, ParseFile("--noise-floor", "-42.5").NoiseFloorDb);
        Assert.Equal(-42.5, ParseFile("--noise-floor", "-42,5").NoiseFloorDb);
        Assert.True(ParseFile("--noise-floor", "auto").AutoNoiseFloor);
    }

    [Fact]
    public void InvalidNoiseFloor_IsRejected()
    {
        Assert.Throws<CliError>(() => ParseFile("--noise-floor", "0"));
        Assert.Throws<CliError>(() => ParseFile("--noise-floor", "-200"));
        Assert.Throws<CliError>(() => ParseFile("--noise-floor", "12"));
        Assert.Throws<CliError>(() => ParseFile("--noise-floor", "quiet"));
    }

    /// <summary>Parses options with a throwaway .m4b as the target, which
    /// <see cref="CliOptions.Parse"/> insists on.</summary>
    /// <param name="options">The options under test.</param>
    private static CliOptions ParseFile(params string[] options)
    {
        var file = Path.Combine(Path.GetTempPath(), $"abchapterize-noise-{Guid.NewGuid():N}.m4b");
        File.WriteAllText(file, "x");
        try
        {
            return CliOptions.Parse([.. options, file])!;
        }
        finally
        {
            File.Delete(file);
        }
    }
}
