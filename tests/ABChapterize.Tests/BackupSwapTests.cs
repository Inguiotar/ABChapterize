// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Audio;
using ABChapterize.Processing;
using Xunit;

namespace ABChapterize.Tests;

/// <summary>
/// Tests for <see cref="FfmpegClient.SwapInto"/> - the step that puts a written file in place of
/// the original, with or without <c>--backup</c>. Contents are stand-in text rather than audio:
/// the question is only which bytes end up under which name, and every branch here is a rename.
/// </summary>
public class BackupSwapTests : IDisposable
{
    private readonly string _dir =
        Directory.CreateTempSubdirectory("abchapterize-backup-tests").FullName;

    /// <summary>The audiobook's path in the temp directory.</summary>
    private string File_ => Path.Combine(_dir, "book.m4b");

    /// <summary>Where <c>--backup</c> puts the original.</summary>
    private string Bak => File_ + ".bak";

    /// <summary>Lays out a file about to be swapped: the current audiobook and the verified
    /// replacement waiting beside it.</summary>
    /// <param name="current">Contents to give the audiobook.</param>
    /// <returns>The temporary replacement's path, as <see cref="FfmpegClient.SwapInto"/> takes it.</returns>
    private string Stage(string current)
    {
        File.WriteAllText(File_, current);
        var tmp = File_ + ".abchapterize.tmp.m4b";
        File.WriteAllText(tmp, "newly written");
        return tmp;
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void WithBackup_AndNoExistingBak_KeepsTheOriginalAsBak()
    {
        var earlierKept = FfmpegClient.SwapInto(File_, Stage("the original"), backup: true);

        Assert.False(earlierKept);
        Assert.Equal("newly written", File.ReadAllText(File_));
        Assert.Equal("the original", File.ReadAllText(Bak));
    }

    [Fact]
    public void WithBackup_AndAnExistingBak_LeavesThatBakExactlyAsItWas()
    {
        // The point of the whole change: the .bak is the book as it stood before this tool first
        // touched it. The file being replaced now is an earlier run's *output*, and letting it
        // become the backup would turn "undo everything" into "undo the last run".
        File.WriteAllText(Bak, "the untouched original");

        var earlierKept = FfmpegClient.SwapInto(File_, Stage("the first run's output"), backup: true);

        Assert.True(earlierKept);
        Assert.Equal("newly written", File.ReadAllText(File_));
        Assert.Equal("the untouched original", File.ReadAllText(Bak));
    }

    [Fact]
    public void WithBackup_AndAnExistingBak_SurvivesAnyNumberOfReRuns()
    {
        File.WriteAllText(Bak, "the untouched original");
        foreach (var run in new[] { "second", "third", "fourth" })
            Assert.True(FfmpegClient.SwapInto(File_, Stage($"the {run} run's output"), backup: true));

        Assert.Equal("the untouched original", File.ReadAllText(Bak));
    }

    [Fact]
    public void WithoutBackup_ReplacesTheFile_AndLeavesNothingBehind()
    {
        var earlierKept = FfmpegClient.SwapInto(File_, Stage("the original"), backup: false);

        Assert.False(earlierKept);
        Assert.Equal("newly written", File.ReadAllText(File_));
        Assert.False(File.Exists(Bak));
        // The parked original is the one temporary file a crash here could strand, so its absence
        // on the happy path is worth asserting rather than assuming.
        Assert.False(File.Exists(File_ + ".abchapterize.orig"));
    }

    [Fact]
    public void WithoutBackup_DoesNotDisturbABakLeftByAnEarlierRun()
    {
        File.WriteAllText(Bak, "from a --backup run");

        Assert.False(FfmpegClient.SwapInto(File_, Stage("current"), backup: false));
        Assert.Equal("from a --backup run", File.ReadAllText(Bak));
    }

    [Fact]
    public void TheSummaryNote_DistinguishesAFreshBackupFromAKeptOne()
    {
        // A reader has to be able to tell the two apart without --verbose: only in the second case
        // does --revert give back something older than this run's input.
        Assert.Equal(", backup kept", RunStatistics.FormatBackupNote(true, earlierKept: false));
        Assert.Equal(", earlier backup kept (predates this run)",
            RunStatistics.FormatBackupNote(true, earlierKept: true));
        Assert.Equal("", RunStatistics.FormatBackupNote(false, earlierKept: false));
    }
}
