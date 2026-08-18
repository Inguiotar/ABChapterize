// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Cli;
using ABChapterize.Detection;
using Xunit;
using static ABChapterize.Detection.GapPlanning;

namespace ABChapterize.Tests;

/// <summary>
/// The two halves of the jingle-first shape as pure decisions: which files get it, and which
/// stretches of a file its pause half then walks. The end-to-end behaviour - what is decoded, what
/// is still found - is in <see cref="ChapterDetectorTests"/>, where the scripted audio lives.
/// </summary>
public sealed class JingleFirstScanTests : IDisposable
{
    private readonly string _dir;
    private readonly string _file;

    /// <summary>Creates a temp .m4b file so <see cref="CliOptions.Parse"/> accepts the target.</summary>
    public JingleFirstScanTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"abchapterize-jf-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "book.m4b");
        File.WriteAllText(_file, "x");
    }

    /// <summary>Removes the temp directory.</summary>
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>Builds validated options with the temp file as target.</summary>
    /// <param name="args">The option list.</param>
    private CliOptions Options(params string[] args) => CliOptions.Parse([.. args, _file])!;

    /// <summary>A census of <paramref name="count"/> entries, spaced far enough apart to be
    /// separate transitions. Only the count is ever read by the gate.</summary>
    /// <param name="count">How many jingles the file has.</param>
    private static List<Jingle> Census(int count)
        => [.. Enumerable.Range(0, count).Select(i => new Jingle(i * 600, i * 600 + 8, i * 600 + 8, 0))];

    /// <summary>Runs the gate over one file's worth of music.</summary>
    /// <param name="options">The run's options.</param>
    /// <param name="jingles">How many jingles the census found.</param>
    /// <param name="hours">The file's play time in hours.</param>
    /// <param name="freshRun">Whether this is a fresh whole-file detection.</param>
    private static JingleFirstScan.Verdict Decide(
        CliOptions options, int jingles, double hours, bool freshRun = true)
        => JingleFirstScan.Decide(
            options, Census(jingles), hours * 3600, options.DefaultProfile, freshRun);

    [Fact]
    public void OneJinglePerHour_IsEnough()
    {
        var verdict = Decide(Options(), jingles: 15, hours: 15);
        Assert.True(verdict.Run);
        Assert.Contains("15 jingle(s), 1.0 per hour", verdict.Note);
    }

    [Fact]
    public void LessThanOnePerHour_KeepsTheOrdinaryShape()
    {
        var verdict = Decide(Options(), jingles: 14, hours: 15);
        Assert.False(verdict.Run);
        // Nothing to say: this is every ordinary book, and a line per file about a shape that was
        // never in question is noise.
        Assert.Null(verdict.Note);
    }

    /// <summary>A file with no music at all is the case the gate exists to keep out, and the one
    /// where dividing by the play time must not misbehave either.</summary>
    [Fact]
    public void NoJinglesAtAll_KeepsTheOrdinaryShape()
    {
        Assert.False(Decide(Options(), jingles: 0, hours: 15).Run);
        Assert.False(Decide(Options(), jingles: 0, hours: 0).Run);
    }

    /// <summary>
    /// The half of the gate that protects the user's own mappings: skipping the pauses between two
    /// consecutive chapters is safe only while nothing else can be announced there, and an untagged
    /// mapping may be announced anywhere.
    /// </summary>
    [Fact]
    public void ACustomMappingThatMayFallBetweenChapters_KeepsTheOrdinaryShape()
    {
        var verdict = Decide(Options("--custom", "zwischenspiel:Zwischenspiel"), jingles: 30, hours: 15);
        Assert.False(verdict.Run);
        // The one declined outcome that gets a log line: the same run over the same book behaves
        // differently for a reason stated nowhere else.
        Assert.Contains("may be announced between chapters", verdict.Note);
        Assert.Contains("--jingle-first", verdict.Note);
    }

    /// <summary>"after-first-chapter" does not exclude the middle of the book, only the front
    /// matter - so it counts as between-chapters exactly as an untagged mapping does.</summary>
    [Fact]
    public void AnAfterFirstChapterMapping_AlsoKeepsTheOrdinaryShape()
    {
        Assert.False(Decide(
            Options("--custom", "[after-first-chapter]zwischenspiel:Zwischenspiel"),
            jingles: 30, hours: 15).Run);
    }

    /// <summary>The two scopes that name a place the pause half walks anyway - the head of the file
    /// and its tail - cost the shape nothing.</summary>
    [Theory]
    [InlineData("before-first-chapter")]
    [InlineData("after-last-chapter")]
    public void AMappingScopedToTheHeadOrTheTail_LeavesTheShapeAlone(string scope)
    {
        Assert.True(Decide(
            Options("--custom", $"[{scope}]vorwort:Vorwort"), jingles: 30, hours: 15).Run);
    }

    /// <summary>The built-in prologue and epilogue are the head and the tail by definition, so a
    /// plain run is never held back by them - which is what makes the mapping check worth having at
    /// all.</summary>
    [Fact]
    public void TheBuiltInPrologueAndEpilogue_DoNotCount()
        => Assert.Null(JingleFirstScan.BetweenChapters(Options().DefaultProfile));

    [Fact]
    public void JingleFirst_ForcesTheShapeOnAFileWithNoMusic()
    {
        var verdict = Decide(Options("--jingle-first"), jingles: 0, hours: 15);
        Assert.True(verdict.Run);
        Assert.Contains("--jingle-first", verdict.Note);
    }

    /// <summary>The override reaches the mapping half of the gate too: it is the switch for running
    /// the experiment on a book that qualifies for neither reason.</summary>
    [Fact]
    public void JingleFirst_OverridesABetweenChaptersMapping()
        => Assert.True(Decide(
            Options("--jingle-first", "--custom", "zwischenspiel:Zwischenspiel"),
            jingles: 0, hours: 15).Run);

    /// <summary>A --verify or ".missing-marks" recovery probes bounded gaps already; there is no run
    /// of settled chapters to skip the pauses of, and the head and tail stretches this plans would
    /// mean something else entirely there.</summary>
    [Fact]
    public void ARecoveryRun_NeverRunsJingleFirst()
    {
        Assert.False(Decide(Options(), jingles: 30, hours: 15, freshRun: false).Run);
        Assert.False(Decide(Options("--jingle-first"), jingles: 30, hours: 15, freshRun: false).Run);
    }

    /// <summary>The whole file, when the jingles yielded nothing - which is the ordinary Probe in
    /// all but name, and the right answer for a book whose music carries no announcements.</summary>
    [Fact]
    public void WithNoChapterFound_TheWholeRegionIsUnsettled()
    {
        var region = new DetectionRegion(0, 3600, 0, null);
        Assert.Equal([region], JingleFirstScan.UnsettledStretches([], region));
    }

    /// <summary>A complete sequence leaves the head and the tail and nothing in between: the pauses
    /// between two consecutive chapters are exactly what this shape does not read.</summary>
    [Fact]
    public void AConsecutiveSequence_LeavesOnlyTheHeadAndTheTail()
    {
        var stretches = JingleFirstScan.UnsettledStretches(
            [new(1, 100), new(2, 1000), new(3, 2000)], new DetectionRegion(0, 3600, 0, null));

        Assert.Equal(
            [(0.0, 100.0, 0, (int?)1), (2000.0, 3600.0, 3, null)],
            stretches.Select(s => (s.FromSeconds, s.ToSeconds, s.LowerNumber, s.UpperNumber)));
    }

    /// <summary>A hole in the numbering is a stretch of its own, bracketed by the chapters either
    /// side of it - the same bounds a --verify gap region carries, and what holds a window there to
    /// the numbers that hole can hold.</summary>
    [Fact]
    public void AHoleInTheNumbering_BecomesItsOwnStretch()
    {
        var stretches = JingleFirstScan.UnsettledStretches(
            [new(1, 100), new(4, 2000)], new DetectionRegion(0, 3600, 0, null));

        Assert.Equal(
            [(0.0, 100.0, 0, (int?)1), (100.0, 2000.0, 1, 4), (2000.0, 3600.0, 4, null)],
            stretches.Select(s => (s.FromSeconds, s.ToSeconds, s.LowerNumber, s.UpperNumber)));
    }

    /// <summary>
    /// A part boundary: the two numbers belong to different sequences and nothing can be compared
    /// across them, so the stretch gets no upper bound and is read forward exactly as the primary
    /// scan reads a book - which is also what leaves the restart tracking able to run there.
    /// </summary>
    [Fact]
    public void APartBoundary_LeavesTheStretchOpenAtTheTop()
    {
        var stretches = JingleFirstScan.UnsettledStretches(
            [new(1, 100), new(1, 2000, Sequence: 1)], new DetectionRegion(0, 3600, 0, null));

        Assert.Equal(
            [(0.0, 100.0, 0, (int?)1), (100.0, 2000.0, 1, null), (2000.0, 3600.0, 1, null)],
            stretches.Select(s => (s.FromSeconds, s.ToSeconds, s.LowerNumber, s.UpperNumber)));
    }

    /// <summary>Under a second there is no candidate to walk - a pause candidate is held clear of a
    /// stretch's last second - so the stretch is not planned at all rather than probed for
    /// nothing.</summary>
    [Fact]
    public void AStretchTooShortToHoldACandidate_IsDropped()
    {
        var stretches = JingleFirstScan.UnsettledStretches(
            [new(1, 0.4), new(3, 3599.8)], new DetectionRegion(0, 3600, 0, null));

        // The head (0-0.4) and the tail (3599.8-3600) both fall away; only the hole is walked.
        Assert.Equal(
            [(0.4, 3599.8, 1, (int?)3)],
            stretches.Select(s => (s.FromSeconds, s.ToSeconds, s.LowerNumber, s.UpperNumber)));
    }
}
