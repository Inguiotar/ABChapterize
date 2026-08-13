// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Xunit;
using ABChapterize.Audio;
using ABChapterize.Cli;
using ABChapterize.Processing;
using ABChapterize.Ui;

namespace ABChapterize.Tests;

/// <summary>
/// Tests for <c>--cleanup</c>: which files it recognizes, and what it does to a folder full of
/// them. The backup length check is stubbed out throughout - it is the one part needing a real
/// ffprobe and a real audiobook, and the interesting behavior around it (which partner is looked
/// for, what happens when there is none) is decided before it ever runs.
/// </summary>
[Collection(ConsoleCapture.Name)]
public sealed class CleanupRunnerTests : IDisposable
{
    private readonly string _dir;

    /// <summary>Creates an empty temp directory; each test fills it with what it needs.</summary>
    public CleanupRunnerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"abchapterize-cleanup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    /// <summary>Removes the temp directory.</summary>
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>Creates the named files (relative to the temp directory) with token content.</summary>
    private void Given(params string[] names)
    {
        foreach (var name in names)
        {
            var path = Path.Combine(_dir, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "x");
        }
    }

    /// <summary>The temp directory's file names, sorted, for comparison against an expectation.</summary>
    private List<string> Remaining()
        => [.. Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(_dir, f).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)];

    /// <summary>
    /// Runs a cleanup over the temp directory with the given extra options, with every backup
    /// reported as matching its file unless <paramref name="backupMismatch"/> says otherwise.
    /// </summary>
    /// <param name="options">Options preceding the target, without --cleanup itself.</param>
    /// <param name="backupMismatch">Reason every backup fails its length check, or null for a
    /// clean match.</param>
    private async Task CleanupAsync(string[] options, string? backupMismatch = null)
    {
        var parsed = CliOptions.Parse([.. options, "--cleanup", "--yes", _dir])!;
        var runner = new CleanupRunner(
            parsed, new ProgressRenderer(quiet: true),
            (_, _, _) => Task.FromResult(backupMismatch));
        await runner.RunAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData("Book.m4b.abchapterize.orig", CleanupArtifact.Parked, "Book.m4b")]
    [InlineData("Book.m4b.abchapterize.tmp.m4b", CleanupArtifact.Temp, "Book.m4b")]
    [InlineData("Book.m4b.debug.log", CleanupArtifact.DebugLog, "Book.m4b")]
    [InlineData("Book.m4b.bak", CleanupArtifact.Backup, "Book.m4b")]
    [InlineData(".abchapterize-progress", CleanupArtifact.BatchProgress, "")]
    [InlineData("Book.missing-marks-3-7.m4b", CleanupArtifact.TaggedAudio, "Book.missing-marks-3-7.m4b")]
    [InlineData("Book.missing-marks.m4b", CleanupArtifact.TaggedAudio, "Book.missing-marks.m4b")]
    [InlineData("Book.m4b", CleanupArtifact.None, "")]
    [InlineData("cover.jpg", CleanupArtifact.None, "")]
    public void Classify_RecognizesEveryArtifactAndItsBook(string name, CleanupArtifact kind, string audio)
    {
        var match = CleanupMatch.Of(Path.Combine("lib", name));
        Assert.Equal(kind, match.Kind);
        Assert.Equal(audio.Length == 0 ? "" : Path.Combine("lib", audio), match.AudioPath);
    }

    /// <summary>A folder named like a temp file must not put everything under it up for deletion -
    /// only an extension may follow the marker.</summary>
    [Fact]
    public void Classify_DirectoryNamedLikeATempFile_IsNotAnArtifact()
    {
        var path = Path.Combine("lib" + FfmpegClient.TempInfix, "Book.m4b");
        Assert.Equal(CleanupArtifact.None, CleanupMatch.Of(path).Kind);
    }

    [Theory]
    [InlineData("yes", true)]
    [InlineData("YES", true)]
    [InlineData(" y ", true)]
    [InlineData("", false)]
    [InlineData("no", false)]
    [InlineData("yeah", false)]
    [InlineData(null, false)]
    public void IsClearYes_AcceptsOnlyAYes(string? answer, bool expected)
        => Assert.Equal(expected, CleanupRunner.IsClearYes(answer));

    [Fact]
    public async Task Cleanup_RemovesLeftoversAndUntagsTheBook()
    {
        Given("Book.missing-marks-3-7.m4b", "Book.m4b.debug.log",
              "Book.m4b.abchapterize.tmp.m4b", ".abchapterize-progress");
        await CleanupAsync([]);
        Assert.Equal(["Book.m4b"], Remaining());
    }

    /// <summary>The backup goes only once its file has been found next to it - which, for a tagged
    /// book, is the name the untagging in the same run has just produced.</summary>
    [Fact]
    public async Task Cleanup_DeletesTheBackupOfAnUntaggedBook()
    {
        Given("Book.missing-marks-3.m4b", "Book.m4b.bak");
        await CleanupAsync([]);
        Assert.Equal(["Book.m4b"], Remaining());
    }

    [Fact]
    public async Task Cleanup_KeepsABackupWhoseFileIsGone()
    {
        Given("Book.m4b.bak", "Other.m4b");
        await CleanupAsync([]);
        Assert.Equal(["Book.m4b.bak", "Other.m4b"], Remaining());
    }

    /// <summary>A ".bak" of a different recording is somebody's only copy of that recording, so a
    /// length mismatch keeps it however tidy the folder would look without it.</summary>
    [Fact]
    public async Task Cleanup_KeepsABackupOfADifferentLength()
    {
        Given("Book.m4b", "Book.m4b.bak");
        await CleanupAsync([], backupMismatch: "lengths differ");
        Assert.Equal(["Book.m4b", "Book.m4b.bak"], Remaining());
    }

    /// <summary>An original parked by a write that was killed between the two renames is the
    /// audiobook itself, and gets its name back instead of being deleted as scratch.</summary>
    [Fact]
    public async Task Cleanup_RestoresAParkedOriginalWhoseBookIsMissing()
    {
        Given("Book.m4b.abchapterize.orig");
        await CleanupAsync([]);
        Assert.Equal(["Book.m4b"], Remaining());
    }

    [Fact]
    public async Task Cleanup_DeletesAParkedOriginalWhoseBookIsBack()
    {
        Given("Book.m4b", "Book.m4b.abchapterize.orig");
        await CleanupAsync([]);
        Assert.Equal(["Book.m4b"], Remaining());
    }

    /// <summary>Chapter marks are real work; an untagging that would land on somebody else's
    /// file leaves the tag on rather than overwrite it.</summary>
    [Fact]
    public async Task Cleanup_KeepsTheTagWhenTheOriginalNameIsTaken()
    {
        Given("Book.m4b", "Book.missing-marks-3.m4b");
        await CleanupAsync([]);
        Assert.Equal(["Book.m4b", "Book.missing-marks-3.m4b"], Remaining());
    }

    /// <summary>With --revert the backup goes back over the processed file - and under the book's
    /// plain name, the tag having been added after the backup was taken.</summary>
    [Fact]
    public async Task CleanupRevert_RestoresTheBackupUnderTheUntaggedName()
    {
        Given("Book.missing-marks-3.m4b", "Book.m4b.bak", "Book.m4b.debug.log");
        await CleanupAsync(["--revert"]);
        Assert.Equal(["Book.m4b"], Remaining());
        Assert.Equal("x", File.ReadAllText(Path.Combine(_dir, "Book.m4b")));
    }

    [Fact]
    public async Task Cleanup_LeavesForeignFilesAlone()
    {
        Given("Book.m4b", "cover.jpg", "notes.txt", "Book.m4b.chapters.txt");
        await CleanupAsync([]);
        Assert.Equal(["Book.m4b", "Book.m4b.chapters.txt", "cover.jpg", "notes.txt"], Remaining());
    }

    /// <summary>Without --recurse a cleanup stops at the top level, exactly as processing does.</summary>
    [Fact]
    public async Task Cleanup_WithoutRecurse_LeavesSubdirectoriesAlone()
    {
        Given("Book.m4b.debug.log", "sub/Other.m4b.debug.log");
        await CleanupAsync([]);
        Assert.Equal(["sub/Other.m4b.debug.log"], Remaining());
    }

    [Fact]
    public async Task Cleanup_WithRecurse_ReachesSubdirectories()
    {
        Given("Book.m4b.debug.log", "sub/Other.m4b.debug.log");
        await CleanupAsync(["--recurse"]);
        Assert.Empty(Remaining());
    }

    /// <summary>--filter selects audiobooks and takes their leftovers along, rather than asking
    /// for a regexp that also has to match ".debug.log".</summary>
    [Fact]
    public async Task Cleanup_FilterRegexp_IsMatchedAgainstTheBookNotTheLeftover()
    {
        Given("Keep.m4b.debug.log", "Drop.m4b.debug.log");
        await CleanupAsync(["--filter", "/drop\\.m4b$/"]);
        Assert.Equal(["Keep.m4b.debug.log"], Remaining());
    }

    /// <summary>A directory's checkpoint belongs to no book, so a filter that selects books cannot
    /// speak for it - and throwing away an interrupted batch's resume point is not something a
    /// narrowed cleanup should do behind the user's back.</summary>
    [Fact]
    public async Task Cleanup_WithAFilter_KeepsTheBatchProgressFile()
    {
        Given("Book.m4b.debug.log", ".abchapterize-progress");
        await CleanupAsync(["--filter", "m4b"]);
        Assert.Equal([".abchapterize-progress"], Remaining());
    }

    /// <summary>Naming a book rather than a folder cleans that book's leftovers and nothing
    /// else - under either spelling of its name, since the user has no reason to know which one a
    /// given log was written under.</summary>
    [Fact]
    public async Task Cleanup_OfOneNamedBook_LeavesTheOtherBooksAlone()
    {
        Given("Book.missing-marks-3.m4b", "Book.m4b.debug.log", "Other.m4b", "Other.m4b.debug.log");
        var parsed = CliOptions.Parse(
            ["--cleanup", "--yes", Path.Combine(_dir, "Book.missing-marks-3.m4b")])!;
        await new CleanupRunner(parsed, new ProgressRenderer(quiet: true),
            (_, _, _) => Task.FromResult<string?>(null)).RunAsync(CancellationToken.None);
        Assert.Equal(["Book.m4b", "Other.m4b", "Other.m4b.debug.log"], Remaining());
    }
}
