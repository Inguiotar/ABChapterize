// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Xunit;
using ABChapterize.Cli;
using ABChapterize.Processing;
using ABChapterize.Ui;

namespace ABChapterize.Tests;

/// <summary>
/// Tests for <see cref="FileProcessor.EnumerateTargets"/>: extension matching, --recurse,
/// --filter regexps, and the exclusion of this tool's own temporary and backup files.
/// </summary>
public sealed class FileEnumerationTests : IDisposable
{
    private readonly string _dir;

    /// <summary>Creates a temp directory tree with a mix of supported and foreign files.</summary>
    public FileEnumerationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"abchapterize-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_dir, "sub"));
        foreach (var name in new[]
                 {
                     "alpha.m4b", "beta.MP3", "notes.txt", "old.m4b.bak",
                     "alpha.m4b.abchapterize.tmp.m4b", Path.Combine("sub", "gamma.opus"),
                 })
            File.WriteAllText(Path.Combine(_dir, name), "x");
    }

    /// <summary>Removes the temp directory.</summary>
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>Builds a processor for the given options followed by the temp directory.</summary>
    private FileProcessor Processor(params string[] options)
    {
        var parsed = CliOptions.Parse([.. options, _dir])!;
        return new FileProcessor(parsed, new ProgressRenderer(quiet: true));
    }

    /// <summary>Enumerates and reduces the results to bare file names for easy comparison.</summary>
    private static List<string> Names(FileProcessor p, string[] suffixes)
        => p.EnumerateTargets(suffixes).Select(Path.GetFileName).ToList()!;

    [Fact]
    public void TopLevel_FindsSupportedFiles_CaseInsensitively_Sorted()
    {
        var names = Names(Processor(), [".m4b", ".mp3", ".opus"]);
        Assert.Equal(["alpha.m4b", "beta.MP3"], names);
    }

    [Fact]
    public void Recurse_IncludesSubdirectories()
    {
        var names = Names(Processor("--recurse"), [".m4b", ".mp3", ".opus"]);
        Assert.Equal(["alpha.m4b", "beta.MP3", "gamma.opus"], names);
    }

    [Fact]
    public void OwnTemporaryFiles_AreAlwaysExcluded()
    {
        var names = Names(Processor(), [".m4b"]);
        Assert.DoesNotContain("alpha.m4b.abchapterize.tmp.m4b", names);
    }

    [Fact]
    public void FilterRegex_RestrictsTheMatches()
    {
        var names = Names(Processor("--recurse", "--filter", "/gam+a/"), [".m4b", ".mp3", ".opus"]);
        Assert.Equal(["gamma.opus"], names);
    }

    [Fact]
    public void RevertMode_FindsBackupsBySuffix()
    {
        var names = Names(Processor("--revert"), [".m4b.bak"]);
        Assert.Equal(["old.m4b.bak"], names);
    }

    [Fact]
    public void SingleFileTarget_IsReturnedDirectly()
    {
        var file = Path.Combine(_dir, "alpha.m4b");
        var parsed = CliOptions.Parse([file])!;
        var p = new FileProcessor(parsed, new ProgressRenderer(quiet: true));
        Assert.Equal([file], p.EnumerateTargets([".m4b"]));
    }

    [Fact]
    public void SeveralTargets_AreEnumeratedInCommandLineOrder()
    {
        var parsed = CliOptions.Parse([Path.Combine(_dir, "sub"), Path.Combine(_dir, "alpha.m4b")])!;
        var p = new FileProcessor(parsed, new ProgressRenderer(quiet: true));
        Assert.Equal(["gamma.opus", "alpha.m4b"], Names(p, [".m4b", ".opus"]));
    }

    [Fact]
    public void AFileCoveredByAListedDirectory_IsEnumeratedOnlyOnce()
    {
        // The directory comes first, so it is the one that claims the file.
        var parsed = CliOptions.Parse(["--recurse", _dir, Path.Combine(_dir, "sub", "gamma.opus")])!;
        var p = new FileProcessor(parsed, new ProgressRenderer(quiet: true));
        Assert.Equal(["alpha.m4b", "beta.MP3", "gamma.opus"], Names(p, [".m4b", ".mp3", ".opus"]));
    }

    [Fact]
    public void TargetGroups_KeepEachTargetsFilesSeparate()
    {
        var parsed = CliOptions.Parse([Path.Combine(_dir, "sub"), Path.Combine(_dir, "alpha.m4b")])!;
        var p = new FileProcessor(parsed, new ProgressRenderer(quiet: true));
        var groups = p.EnumerateTargetGroups([".m4b", ".opus"]);
        Assert.Equal(2, groups.Count);
        Assert.True(groups[0].Target.IsDirectory);
        Assert.Equal(["gamma.opus"], groups[0].Files.Select(Path.GetFileName));
        Assert.False(groups[1].Target.IsDirectory);
        Assert.Equal(["alpha.m4b"], groups[1].Files.Select(Path.GetFileName));
    }

    [Fact]
    public void MissingMarksPath_TagsTheStillMissingChapters_BeforeTheExtension()
    {
        var path = FileProcessor.MissingMarksPath(Path.Combine("lib", "Book.m4b"), [3, 7, 8]);
        Assert.Equal(Path.Combine("lib", "Book.missing-marks-3-7-8.m4b"), path);
    }

    [Fact]
    public void MissingMarksPath_ReplacesAnExistingTag_RatherThanStackingIt()
    {
        // Re-tagging an already-tagged file (e.g. after a second incomplete run) must not produce
        // "Book.missing-marks-3-7.missing-marks-7.m4b".
        var path = FileProcessor.MissingMarksPath(Path.Combine("lib", "Book.missing-marks-3-7.m4b"), [7]);
        Assert.Equal(Path.Combine("lib", "Book.missing-marks-7.m4b"), path);
    }

    [Fact]
    public void HasMissingMarksTag_RecognizesATaggedFileName()
    {
        Assert.True(FileProcessor.HasMissingMarksTag(Path.Combine("lib", "Book.missing-marks-3-7-8.m4b")));
    }

    [Fact]
    public void HasMissingMarksTag_IsFalse_ForAnOrdinaryFileName()
    {
        Assert.False(FileProcessor.HasMissingMarksTag(Path.Combine("lib", "Book.m4b")));
    }

    [Fact]
    public void MissingMarksPath_OmitsTheNumbers_WhenTooManyChaptersAreMissing()
    {
        // Eleven missing chapters is one past the cap - the name must not spell any of them out.
        var path = FileProcessor.MissingMarksPath(
            Path.Combine("lib", "Book.m4b"), [.. Enumerable.Range(5, 11)]);
        Assert.Equal(Path.Combine("lib", "Book.missing-marks.m4b"), path);
    }

    [Fact]
    public void MissingMarksPath_StillNamesExactlyTheCappedCount()
    {
        var path = FileProcessor.MissingMarksPath(
            Path.Combine("lib", "Book.m4b"), [.. Enumerable.Range(5, FileProcessor.MaxNamedMissingNumbers)]);
        Assert.Equal(Path.Combine("lib", "Book.missing-marks-5-6-7-8-9-10-11-12-13-14.m4b"), path);
    }

    [Fact]
    public void MissingMarksPath_ReplacesAnUnnumberedTag_RatherThanStackingIt()
    {
        var path = FileProcessor.MissingMarksPath(Path.Combine("lib", "Book.missing-marks.m4b"), [7]);
        Assert.Equal(Path.Combine("lib", "Book.missing-marks-7.m4b"), path);
    }

    [Fact]
    public void HasMissingMarksTag_IsFalse_ForAnUnnumberedTag()
    {
        // The unnumbered form is deliberately outside the auto-resume scope: a gap that wide
        // wants a human look, not another automatic run.
        Assert.False(FileProcessor.HasMissingMarksTag(Path.Combine("lib", "Book.missing-marks.m4b")));
    }

    [Fact]
    public void FormatMissingList_ListsEverythingUpToTheCap()
    {
        Assert.Equal("3, 7, 8", FileProcessor.FormatMissingList([3, 7, 8]));
    }

    [Fact]
    public void FormatMissingList_SummarizesTheRest_BeyondTheCap()
    {
        Assert.Equal("1, 2, 3, 4, 5, 6, 7, 8, 9, 10 and 2 more",
            FileProcessor.FormatMissingList([.. Enumerable.Range(1, 12)]));
    }

    /// <summary>Runs --no-op against the temp directory and returns everything written to
    /// Console.Out, restoring the original writer afterward regardless of outcome.</summary>
    private async Task<string> RunNoOpAsync(params string[] extraArgs)
    {
        var parsed = CliOptions.Parse([.. extraArgs, _dir])!;
        var p = new FileProcessor(parsed, new ProgressRenderer(quiet: true));
        var original = Console.Out;
        var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            await p.RunAsync(CancellationToken.None);
        }
        finally
        {
            Console.SetOut(original);
        }
        return writer.ToString();
    }

    [Fact]
    public async Task NoOp_ListsOnlyTheFilesTheFilterMatches_WithoutProcessingAnything()
    {
        var output = await RunNoOpAsync("--no-op", "--filter", "m4b");
        Assert.Contains(Path.Combine(_dir, "alpha.m4b"), output);
        Assert.DoesNotContain("beta.MP3", output);
        Assert.DoesNotContain("gamma.opus", output); // excluded: no --recurse
    }

    [Fact]
    public async Task NoOp_WithRecurse_IncludesSubdirectoryMatches()
    {
        var output = await RunNoOpAsync("--no-op", "--recurse", "--filter", "opus");
        Assert.Contains(Path.Combine(_dir, "sub", "gamma.opus"), output);
    }

    [Fact]
    public async Task NoOp_Quiet_SuppressesTheListing_ButNotTheSummary()
    {
        var output = await RunNoOpAsync("--no-op", "--filter", "m4b", "--quiet", "--summary");
        Assert.DoesNotContain("alpha.m4b", output);
        Assert.Contains("1 file(s) would be processed", output);
    }

    [Fact]
    public async Task NoOp_NoMatches_ReportsNoneFound()
    {
        var output = await RunNoOpAsync("--no-op", "--filter", "opus"); // no top-level .opus file
        Assert.Contains("No audio files matching --filter found.", output);
    }
}
