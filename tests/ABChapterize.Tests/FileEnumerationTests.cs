// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Xunit;

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
}
