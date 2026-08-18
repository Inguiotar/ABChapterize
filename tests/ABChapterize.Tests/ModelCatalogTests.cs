// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Xunit;
using ABChapterize.Cli;
using ABChapterize.Errors;
using ABChapterize.Transcription;

namespace ABChapterize.Tests;

/// <summary>
/// Tests for <see cref="ModelCatalog"/>'s network-free half: which models exist, how big they are
/// said to be, and how a <c>custom:</c> selector behaves.
/// </summary>
/// <remarks>
/// Nothing here downloads anything. What it guards is the pair of invariants the type system cannot
/// state - that the catalogue's entries and its name list describe the same six models, and that
/// their recorded sizes ascend in that list's order. The second is load-bearing: sizes are the whole
/// basis on which two models are ranked, and that ranking decides whether pass 2.5 runs at all, so a
/// new entry slipped in out of order would switch a pass off without a word.
/// </remarks>
public class ModelCatalogTests
{
    [Fact]
    public void EveryBuiltInName_HasACatalogueEntry()
    {
        // ApproximateSizeBytes answers 0 for a name it does not know, which is also its answer for a
        // custom file that has vanished - so a missing entry would rank the model below everything
        // rather than announce itself.
        foreach (var name in ModelCatalog.BuiltInNames)
            Assert.True(ModelCatalog.ApproximateSizeBytes(name) > 0, $"no catalogue entry for \"{name}\"");
    }

    [Fact]
    public void EveryBuiltInName_IsAcceptedOnTheCommandLine()
    {
        // The other direction of the same agreement: a name the catalogue holds but the command line
        // rejects makes a bundled model unselectable.
        using var temp = new TempBook();
        foreach (var name in ModelCatalog.BuiltInNames)
            Assert.Equal(name, CliOptions.Parse(["--model", name, temp.Path])!.Model);
    }

    [Fact]
    public void AnUnknownModelName_IsRefusedOnTheCommandLine()
    {
        using var temp = new TempBook();
        var ex = Assert.Throws<CliError>(() => CliOptions.Parse(["--model", "enormous", temp.Path]));
        Assert.Contains("Invalid model", ex.Message);
    }

    [Fact]
    public void TheRecordedSizes_AscendInTheCatalogueOrder()
    {
        // Pass3ModelIsUpgrade reads this order directly. "turbo" between "medium" and "large" is the
        // entry that makes it worth pinning: it is the one place where file size and release order
        // disagree with alphabetical or chronological intuition.
        var sizes = ModelCatalog.BuiltInNames.Select(ModelCatalog.ApproximateSizeBytes).ToList();
        for (var i = 1; i < sizes.Count; i++)
            Assert.True(sizes[i] > sizes[i - 1],
                $"\"{ModelCatalog.BuiltInNames[i]}\" is not larger than \"{ModelCatalog.BuiltInNames[i - 1]}\"");
    }

    [Fact]
    public void AnUnknownName_WeighsNothing()
        => Assert.Equal(0, ModelCatalog.ApproximateSizeBytes("enormous"));

    [Fact]
    public void ACustomSelector_IsRecognizedAndKeepsItsPath()
    {
        // A path with spaces and a drive letter is the ordinary shape on Windows, and the colon in
        // "C:" is the same character the prefix ends with - so a naive split would lose the path.
        const string selector = ModelCatalog.CustomPrefix + @"C:\my models\finetune.bin";
        Assert.True(ModelCatalog.IsCustom(selector));
        Assert.Equal(@"C:\my models\finetune.bin", ModelCatalog.CustomPath(selector));
    }

    [Fact]
    public void ABuiltInName_IsNotACustomSelector()
        => Assert.False(ModelCatalog.IsCustom("large"));

    [Fact]
    public void ACustomFileThatIsGone_WeighsNothingRatherThanThrowing()
    {
        // Documented contract: it ranks below everything instead of throwing, because the run's own
        // model load fails moments later with a far more useful message.
        var missing = Path.Combine(Path.GetTempPath(), "abc-no-such-model-" + Guid.NewGuid().ToString("N") + ".bin");
        Assert.Equal(0, ModelCatalog.ApproximateSizeBytes(ModelCatalog.CustomPrefix + missing));
    }

    [Fact]
    public void ACustomFileOnDisk_WeighsWhatItWeighs()
    {
        using var temp = new TempBook(bytes: 4242);
        Assert.Equal(4242, ModelCatalog.ApproximateSizeBytes(ModelCatalog.CustomPrefix + temp.Path));
    }

    /// <summary>A throwaway file on disk, since <see cref="CliOptions.Parse"/> checks that its
    /// targets exist and <see cref="ModelCatalog.ApproximateSizeBytes"/> measures a real one.
    /// </summary>
    private sealed class TempBook : IDisposable
    {
        private readonly string _dir;

        /// <summary>The file's path.</summary>
        public string Path { get; }

        /// <summary>Creates the file.</summary>
        /// <param name="bytes">How large to make it.</param>
        public TempBook(int bytes = 16)
        {
            _dir = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "abc-modelcatalog-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            Path = System.IO.Path.Combine(_dir, "book.m4b");
            File.WriteAllBytes(Path, new byte[bytes]);
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        }
    }
}
