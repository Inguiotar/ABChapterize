// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Xunit;
using ABChapterize.Cli;
using ABChapterize.Errors;

namespace ABChapterize.Tests;

/// <summary>
/// Tests for the per-folder <c>.abchapterize-config</c> and <c>.abchapterize-custom</c>: which
/// folders are read for a given file, how their settings layer, and what a folder may not change.
/// </summary>
public sealed class FolderConfigTests : IDisposable
{
    private readonly string _root;

    /// <summary>Builds a small library: a root, a sub-folder, and a book in each.</summary>
    public FolderConfigTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"abchapterize-folder-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_root, "german"));
        File.WriteAllText(Path.Combine(_root, "top.m4b"), "x");
        File.WriteAllText(Path.Combine(_root, "german", "buch.m4b"), "x");
    }

    /// <summary>Removes the library.</summary>
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Sub => Path.Combine(_root, "german");
    private string TopBook => Path.Combine(_root, "top.m4b");
    private string SubBook => Path.Combine(_root, "german", "buch.m4b");

    private static void Write(string folder, string name, params string[] lines)
        => File.WriteAllLines(Path.Combine(folder, name), lines);

    /// <summary>The run's options for a recursive run over the library root.</summary>
    private CliOptions Run(params string[] options)
        => CliOptions.Parse([.. options, "--recurse", _root])!;

    [Fact]
    public void AFoldersOwnConfig_AppliesToTheBooksInIt()
    {
        Write(Sub, FolderConfig.ConfigName, "# this shelf is German", "--lang de", "--mark-lead 0.5");
        var o = FolderConfig.ResolveForFile(Run(), SubBook, _root);
        Assert.Equal("de", o.Language);
        Assert.Equal(0.5, o.MarkLeadSeconds);
    }

    [Fact]
    public void AFileInAnotherFolder_IsUntouchedByIt()
    {
        Write(Sub, FolderConfig.ConfigName, "--lang de");
        var run = Run();
        Assert.Same(run, FolderConfig.ResolveForFile(run, TopBook, _root));
    }

    [Fact]
    public void ASubFoldersSetting_BeatsItsParents()
    {
        Write(_root, FolderConfig.ConfigName, "--lang en", "--mark-lead 0.2");
        Write(Sub, FolderConfig.ConfigName, "--lang de");
        var o = FolderConfig.ResolveForFile(Run(), SubBook, _root);
        Assert.Equal("de", o.Language);
        // Inherited rather than lost: the sub-folder said nothing about it.
        Assert.Equal(0.2, o.MarkLeadSeconds);
    }

    [Fact]
    public void TheCommandLine_BeatsEveryFolder()
    {
        Write(_root, FolderConfig.ConfigName, "--lang en");
        Write(Sub, FolderConfig.ConfigName, "--lang de");
        Assert.Equal("fr", FolderConfig.ResolveForFile(Run("--lang", "fr"), SubBook, _root).Language);
    }

    [Fact]
    public void AFolderCustomFile_IsReadAsCustomMappings_AndAccumulatesDownTheChain()
    {
        Write(_root, FolderConfig.CustomName, "/^vorwort/:Vorwort");
        Write(Sub, FolderConfig.CustomName, "/^zeittafel/:Zeittafel");
        var o = FolderConfig.ResolveForFile(Run(), SubBook, _root);
        Assert.Equal(2, o.CustomMappings.Count);
        Assert.Contains(o.CustomMappings, m => m.Title == "Vorwort");
        Assert.Contains(o.CustomMappings, m => m.Title == "Zeittafel");
    }

    [Fact]
    public void OnlyTheFoldersTheRunReachedThrough_AreRead()
    {
        // The parent of the target is outside what was asked for, so its settings are not the
        // run's to pick up - a stray config higher in someone's library cannot reach in.
        var outer = Directory.GetParent(_root)!.FullName;
        var strayName = Path.Combine(outer, FolderConfig.ConfigName);
        var existed = File.Exists(strayName);
        if (existed)
            return;                             // somebody else's file; do not touch it
        try
        {
            File.WriteAllLines(strayName, ["--lang de"]);
            var run = Run();
            Assert.Same(run, FolderConfig.ResolveForFile(run, TopBook, _root));
        }
        finally
        {
            File.Delete(strayName);
        }
    }

    [Fact]
    public void AFileNamedDirectly_GetsItsOwnFoldersSettings()
    {
        Write(Sub, FolderConfig.ConfigName, "--lang de");
        var run = CliOptions.Parse([SubBook])!;
        Assert.Equal("de", FolderConfig.ResolveForFile(run, SubBook, SubBook).Language);
    }

    [Fact]
    public void ARunWideOption_IsRefused_NamingItAndTheFile()
    {
        Write(Sub, FolderConfig.ConfigName, "--model turbo");
        var ex = Assert.Throws<AppError>(() => FolderConfig.ResolveForFile(Run(), SubBook, _root));
        Assert.Contains("--model", ex.Message);
        Assert.Contains("cannot be set per folder", ex.Message);
        Assert.Contains(FolderConfig.ConfigName, ex.Message);
    }

    [Theory]
    [InlineData("--filter", "m4b")]
    [InlineData("--debug")]
    [InlineData("--backup")]
    // Reads like a per-book setting and is not one: the commit path that acts on it works from the
    // run's options, so a folder's value would be accepted and then ignored.
    [InlineData("--no-rename")]
    [InlineData("--set:DetectionTuning.WhisperChunkSeconds=25")]
    public void EverySortOfRunWideOption_IsRefused(params string[] line)
    {
        Write(Sub, FolderConfig.ConfigName, string.Join(' ', line));
        Assert.Throws<AppError>(() => FolderConfig.ResolveForFile(Run(), SubBook, _root));
    }

    [Fact]
    public void AMalformedLine_IsReportedWithItsFileAndLine()
    {
        Write(Sub, FolderConfig.ConfigName, "--lang de", "this is not an option");
        var ex = Assert.Throws<AppError>(() => FolderConfig.ResolveForFile(Run(), SubBook, _root));
        Assert.Contains("line 2", ex.Message);
        Assert.Contains(FolderConfig.ConfigName, ex.Message);
    }

    [Fact]
    public void TheFolderChain_RunsFromTheTargetDownToTheFile()
    {
        Assert.Equal([_root, Sub], FolderConfig.FoldersFor(SubBook, _root));
        Assert.Equal([_root], FolderConfig.FoldersFor(TopBook, _root));
        // A directly named file is its own root, so nothing above it is in scope.
        Assert.Equal([Sub], FolderConfig.FoldersFor(SubBook, SubBook));
    }
}
