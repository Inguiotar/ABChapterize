// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Xunit;
using ABChapterize.Cli;
using ABChapterize.Errors;
using ABChapterize.Processing;
using ABChapterize.Ui;

namespace ABChapterize.Tests;

/// <summary>
/// Tests for <c>--revert</c>: which backups it restores, and what it does when one of them will
/// not move. Contents are stand-in text rather than audio - the whole of this mode is renames, and
/// nothing in it opens a file.
/// </summary>
[Collection(ConsoleCapture.Name)]
public sealed class RevertTests : IDisposable
{
    private readonly string _dir;

    /// <summary>Creates an empty temp directory; each test fills it with what it needs.</summary>
    public RevertTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"abchapterize-revert-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    /// <summary>Removes the temp directory.</summary>
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>Lays out one audiobook and the backup beside it, both named after it.</summary>
    /// <param name="name">The audiobook's file name.</param>
    private void GivenBackedUp(string name)
    {
        File.WriteAllText(Path.Combine(_dir, name), $"marked {name}");
        File.WriteAllText(Path.Combine(_dir, name + ".bak"), $"original {name}");
    }

    /// <summary>Reverts the temp directory.</summary>
    private async Task RevertAsync()
    {
        var options = CliOptions.Parse(["--revert", "--quiet", _dir])!;
        using var progress = new ProgressRenderer(quiet: true, noBar: true);
        await new FileProcessor(options, progress).RunAsync(CancellationToken.None);
    }

    /// <summary>What one file holds now, or null when it is not there at all.</summary>
    /// <param name="name">The file's name within the temp directory.</param>
    private string? Contents(string name)
    {
        var path = Path.Combine(_dir, name);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    [Fact]
    public async Task EveryBackup_IsRestoredOverTheFileBesideIt()
    {
        GivenBackedUp("a.m4b");
        GivenBackedUp("b.m4b");

        await RevertAsync();

        Assert.Equal("original a.m4b", Contents("a.m4b"));
        Assert.Equal("original b.m4b", Contents("b.m4b"));
        Assert.Null(Contents("a.m4b.bak"));
        Assert.Null(Contents("b.m4b.bak"));
    }

    /// <summary>
    /// The rule this mode shares with <see cref="CleanupRunner"/>: one book that will not move is
    /// no reason to leave the others as this tool left them, so the rest are still restored - but
    /// the run ends in an error, so a script cannot mistake a partial revert for a complete one.
    /// </summary>
    /// <remarks>
    /// The unmovable one is a directory sitting where the audiobook should be, which is the one
    /// way to make a rename fail that needs no second process holding a handle. Windows reports it
    /// as <see cref="UnauthorizedAccessException"/> and Unix as <see cref="IOException"/>; the
    /// catch takes both.
    /// </remarks>
    [Fact]
    public async Task ABackupThatWillNotMove_LeavesTheRestRestored_AndFailsTheRun()
    {
        GivenBackedUp("a.m4b");
        GivenBackedUp("b.m4b");
        GivenBackedUp("c.m4b");
        File.Delete(Path.Combine(_dir, "b.m4b"));
        Directory.CreateDirectory(Path.Combine(_dir, "b.m4b"));

        var error = await Assert.ThrowsAsync<AppError>(RevertAsync);

        Assert.Contains("1 backup(s) could not be restored", error.Message);
        Assert.Equal("original a.m4b", Contents("a.m4b"));
        Assert.Equal("original c.m4b", Contents("c.m4b"));
        // The one that failed keeps its backup: nothing was thrown away on the way to the error.
        Assert.Equal("original b.m4b", Contents("b.m4b.bak"));
    }
}
