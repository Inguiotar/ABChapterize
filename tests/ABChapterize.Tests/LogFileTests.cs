// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Xunit;
using ABChapterize.Errors;
using ABChapterize.Ui;

namespace ABChapterize.Tests;

/// <summary>
/// Tests for <see cref="LogFile"/> and the <c>--log-file</c> routing in
/// <see cref="ProgressRenderer"/>: what ends up in the file, what stays on the console, and that
/// a second run does not erase the first one's record.
/// </summary>
[Collection(ConsoleCapture.Name)]
public sealed class LogFileTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    /// <summary>Creates a temp directory to write log files into.</summary>
    public LogFileTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"abchapterize-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "run.log");
    }

    /// <summary>Removes the temp directory.</summary>
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>Runs <paramref name="use"/> against a renderer writing to the log file, with the
    /// console captured, and returns both what the console and the file received.</summary>
    /// <param name="use">Exercises the renderer.</param>
    /// <param name="quiet">Whether the renderer runs in --quiet mode.</param>
    /// <param name="verbose">Whether --verbose was given alongside the log file.</param>
    private (string Console, string File) Capture(
        Action<ProgressRenderer> use, bool quiet = false, bool verbose = false)
    {
        var original = Console.Out;
        var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            using (var log = LogFile.Open(_path))
            using (var renderer = new ProgressRenderer(quiet, verbose, noBar: true, logFile: log))
                use(renderer);
        }
        finally
        {
            Console.SetOut(original);
        }
        return (writer.ToString(), File.ReadAllText(_path));
    }

    [Fact]
    public void LogLines_GoToTheFile_AndNotToTheConsole()
    {
        var (console, file) = Capture(r => r.Log("probing at 0:12:34"));
        Assert.Contains("probing at 0:12:34", file);
        Assert.DoesNotContain("probing at 0:12:34", console);
    }

    [Fact]
    public void LogLines_StayOffTheConsole_EvenWithVerbose()
    {
        // --log-file means "instead of the console", so it wins over --verbose rather than
        // producing the same lines twice.
        var (console, file) = Capture(r => r.Log("probing at 0:12:34"), verbose: true);
        Assert.Contains("probing at 0:12:34", file);
        Assert.DoesNotContain("probing at 0:12:34", console);
    }

    [Fact]
    public void SummaryLines_ReachBothDestinations()
    {
        var (console, file) = Capture(r => r.FinishWithSummary(new WorkTracker(), "book.m4b: 12 chapter(s) written"));
        Assert.Contains("12 chapter(s) written", console);
        Assert.Contains("12 chapter(s) written", file);
    }

    [Fact]
    public void SummaryLines_ReachTheFile_EvenWhenQuietHoldsThemBackFromTheConsole()
    {
        var (console, file) = Capture(
            r => r.FinishWithSummary(new WorkTracker(), "book.m4b: skipped"), quiet: true);
        Assert.DoesNotContain("skipped", console);
        Assert.Contains("skipped", file);
    }

    [Fact]
    public void RunLevelLines_ReachBothDestinations()
    {
        // The model banner and the --summary block belong in the file as much as on screen: a log
        // that omits how the run ended is a log nobody can act on.
        var (console, file) = Capture(r => r.Announce("Summary: 3 file(s) encountered"));
        Assert.Contains("Summary: 3 file(s) encountered", console);
        Assert.Contains("Summary: 3 file(s) encountered", file);
    }

    /// <summary>
    /// The heading of a run that was cut short has to say so on its own line - a log read a day
    /// later is all there is, and figures that look like a finished run's would be read as one.
    /// </summary>
    [Fact]
    public void AnUnfinishedRunsHeading_SaysSoOnBothDestinations()
    {
        var (console, file) = Capture(r =>
        {
            r.AnnounceSummaryHeading("3 file(s) encountered", finished: true);
            r.AnnounceSummaryHeading("2 of 3 file(s) reached", finished: false);
        });

        foreach (var written in new[] { console, file })
        {
            Assert.Contains("Summary: 3 file(s) encountered", written);
            Assert.Contains("Summary (run did not finish): 2 of 3 file(s) reached", written);
        }
    }

    [Fact]
    public void EveryLineCarriesADateAndTime()
    {
        var (_, file) = Capture(r => r.Log("probing"));
        Assert.Matches(@"\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\] probing", file);
    }

    [Fact]
    public void ASecondRun_AppendsRatherThanOverwriting()
    {
        Capture(r => r.Log("first run"));
        var (_, file) = Capture(r => r.Log("second run"));
        Assert.Contains("first run", file);
        Assert.Contains("second run", file);
    }

    [Fact]
    public void EachRun_IsBracketedByItsOwnHeaderAndFooter()
    {
        var (_, file) = Capture(r => r.Log("working"));
        Assert.Contains("run started", file);
        Assert.Contains("run finished", file);
    }

    /// <summary>
    /// The header names the machine as well as the build. A log is read detached from both, and
    /// this is the half of its provenance that nothing else in the file can be made to give up -
    /// see <see cref="ABChapterize.Cli.HostPlatform"/>.
    /// </summary>
    [Fact]
    public void TheHeader_NamesThePlatform()
    {
        var (_, file) = Capture(r => r.Log("working"));
        Assert.Contains($"=== {ABChapterize.Cli.HostPlatform.Description} ===", file);
    }

    [Fact]
    public void AnUnwritablePath_FailsImmediately()
    {
        // The directory itself: opening it as a file cannot work on any platform.
        Assert.Throws<AppError>(() => LogFile.Open(_dir));
    }
}
