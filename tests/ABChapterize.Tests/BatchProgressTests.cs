// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Xunit;
using ABChapterize.Processing;

namespace ABChapterize.Tests;

/// <summary>
/// Tests for <see cref="BatchProgress"/>: recording finished files, resuming from what a
/// previous run left behind, refusing progress written under different options, and deleting
/// the progress file once a directory is finished.
/// </summary>
public sealed class BatchProgressTests : IDisposable
{
    private readonly string _dir;
    private readonly string _progressFile;

    /// <summary>Creates an empty temp directory to keep progress files in.</summary>
    public BatchProgressTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"abchapterize-progress-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _progressFile = Path.Combine(_dir, BatchProgress.FileName);
    }

    /// <summary>Removes the temp directory.</summary>
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>Full path of a file name inside the temp directory (nothing is created).</summary>
    private string File(string name) => Path.Combine(_dir, name);

    /// <summary>Opens a checkpoint for the temp directory under the given options fingerprint.
    /// Bookkeeping warnings are collected in <see cref="_warnings"/> rather than printed.</summary>
    private BatchProgress Open(string fingerprint = "abc123", bool ignoreExisting = false)
        => BatchProgress.Open(_dir, fingerprint, ignoreExisting, _warnings.Add);

    /// <summary>Whatever the checkpoint warned about while a test ran.</summary>
    private readonly List<string> _warnings = [];

    [Fact]
    public void AnUnfinishedDirectory_KeepsItsProgressFile()
    {
        var progress = Open();
        progress.Begin(2);
        progress.MarkDone(File("a.m4b"), null);
        Assert.True(System.IO.File.Exists(_progressFile));
    }

    [Fact]
    public void AFinishedDirectory_LeavesNothingBehind()
    {
        var progress = Open();
        progress.Begin(2);
        progress.MarkDone(File("a.m4b"), null);
        progress.MarkDone(File("b.m4b"), null);
        Assert.False(System.IO.File.Exists(_progressFile));
    }

    [Fact]
    public void ADirectoryWithNothingLeftToDo_IsFinishedImmediately()
    {
        Open().Begin(0);
        Assert.False(System.IO.File.Exists(_progressFile));
    }

    [Fact]
    public void ASecondRun_SkipsWhatTheFirstOneFinished()
    {
        var first = Open();
        first.Begin(3);
        first.MarkDone(File("a.m4b"), null);

        var second = Open();
        Assert.True(second.IsDone(File("a.m4b")));
        Assert.False(second.IsDone(File("b.m4b")));
    }

    [Fact]
    public void AResumedRun_InterruptedAgain_AddsToTheRecordRatherThanReplacingIt()
    {
        // Resuming is not a one-shot: the run that picks up an existing record must append to it,
        // not start a fresh one, or the second interruption would silently discard everything the
        // first run had finished and send the third run over those files again.
        var first = Open();
        first.Begin(3);
        first.MarkDone(File("a.m4b"), null);

        var second = Open();
        second.Begin(2);
        second.MarkDone(File("b.m4b"), null);

        var third = Open();
        Assert.True(third.IsDone(File("a.m4b")));
        Assert.True(third.IsDone(File("b.m4b")));
        Assert.False(third.IsDone(File("c.m4b")));
    }

    [Fact]
    public void ARenamedFile_IsRecognizedUnderEitherName()
    {
        var first = Open();
        first.Begin(2);
        first.MarkDone(File("Book.m4b"), File("Book.missing-marks-3.m4b"));

        var second = Open();
        Assert.True(second.IsDone(File("Book.m4b")));
        Assert.True(second.IsDone(File("Book.missing-marks-3.m4b")));
    }

    [Fact]
    public void ProgressFromADifferentCommand_IsNotApplied()
    {
        var first = Open("aaaaaaaa");
        first.Begin(3);
        first.MarkDone(File("a.m4b"), null);

        var second = Open("bbbbbbbb");
        Assert.False(second.IsDone(File("a.m4b")));
    }

    [Fact]
    public void ProgressFromADifferentCommand_IsReplaced_NotAppendedTo()
    {
        var first = Open("aaaaaaaa");
        first.Begin(3);
        first.MarkDone(File("a.m4b"), null);

        var second = Open("bbbbbbbb");
        second.Begin(1);
        second.MarkDone(File("b.m4b"), null);
        // Both runs finished a file, but only the second one's record ever existed together
        // with the second fingerprint - and it deleted the file on completion.
        Assert.False(System.IO.File.Exists(_progressFile));
        var third = Open("bbbbbbbb");
        Assert.False(third.IsDone(File("a.m4b")));
    }

    [Fact]
    public void IgnoreExisting_DiscardsWhatThePreviousRunFinished()
    {
        var first = Open();
        first.Begin(3);
        first.MarkDone(File("a.m4b"), null);

        var second = Open(ignoreExisting: true);
        Assert.False(second.IsDone(File("a.m4b")));
    }

    [Fact]
    public void AGarbledProgressFile_IsIgnoredRatherThanTrusted()
    {
        System.IO.File.WriteAllText(_progressFile, "nonsense\na.m4b\n");
        Assert.False(Open().IsDone(File("a.m4b")));
    }

    [Fact]
    public void RecordedPathsAreRelative_SoTheDirectoryCanMove()
    {
        var progress = Open();
        progress.Begin(2);
        progress.MarkDone(Path.Combine(_dir, "sub", "a.m4b"), null);
        var text = System.IO.File.ReadAllText(_progressFile);
        Assert.Contains(Path.Combine("sub", "a.m4b"), text);
        Assert.DoesNotContain(_dir, text);
    }
}
