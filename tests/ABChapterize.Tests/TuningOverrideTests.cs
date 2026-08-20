// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Xunit;
using ABChapterize.Cli;
using ABChapterize.Detection;
using ABChapterize.Errors;
using ABChapterize.Vad;

namespace ABChapterize.Tests;

/// <summary>
/// Tests for <c>--set:</c>. Everything goes through the real <see cref="CliOptions.Parse"/>, since
/// what the option promises is that the constant has already changed by the time anything reads it.
/// </summary>
/// <remarks>
/// These write to process-global statics, which is exactly why <c>TuningOverrides.Apply</c> restores
/// the compiled-in values at the start of every parse. Without that a test here would change the
/// meaning of every test that ran after it, so the restore is itself under test below - and if it
/// ever breaks, the symptom will be unrelated fixtures failing in whatever order xunit picked.
/// </remarks>
public sealed class TuningOverrideTests : IDisposable
{
    private readonly string _dir;
    private readonly string _file;

    /// <summary>Creates a temp directory with one supported audio file to parse against.</summary>
    public TuningOverrideTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"abchapterize-set-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "book.m4b");
        File.WriteAllText(_file, "x");
    }

    /// <summary>Removes the temp directory and puts the tuning back, so a failure part-way through
    /// a test cannot leak a changed constant into the rest of the suite.</summary>
    public void Dispose()
    {
        CliOptions.Parse([_file]);
        Directory.Delete(_dir, recursive: true);
    }

    private CliOptions? ParseFile(params string[] options)
        => CliOptions.Parse([.. options, _file]);

    [Fact]
    public void AnOverride_ChangesTheConstant_AndIsReported()
    {
        var o = ParseFile("--set:DetectionTuning.WhisperChunkSeconds=25")!;
        Assert.Equal(25.0, DetectionTuning.WhisperChunkSeconds);
        Assert.Equal(["DetectionTuning.WhisperChunkSeconds=25"], o.TuningChanges);
    }

    [Fact]
    public void ADerivedConstant_FollowsWhateverItsInputWasSetTo()
    {
        // The reason derived values are computed properties rather than fields: as fields they
        // would keep the value they were initialized with, and would do so silently.
        ParseFile("--set:DetectionTuning.WhisperChunkSeconds=20");
        Assert.Equal(10.0, DetectionTuning.RescanShiftSeconds);
        Assert.Equal(20.0 - DetectionTuning.PhraseMarginSeconds, DetectionTuning.JingleRereadWindowSeconds);
    }

    [Fact]
    public void EveryConstant_IsBackToItsDefault_OnTheNextParse()
    {
        var normal = DetectionTuning.WhisperChunkSeconds;
        ParseFile("--set:DetectionTuning.WhisperChunkSeconds=25");
        Assert.Equal(25.0, DetectionTuning.WhisperChunkSeconds);
        ParseFile();
        Assert.Equal(normal, DetectionTuning.WhisperChunkSeconds);
    }

    [Fact]
    public void SeveralOverrides_AreAllApplied_AcrossClasses()
    {
        var o = ParseFile("--set:DetectionTuning.WhisperChunkSeconds=25",
                          "--set:VadSegmenter.Threshold=0.5",
                          "--set:SileroVadDetector.BlockSeconds=300")!;
        Assert.Equal(25.0, DetectionTuning.WhisperChunkSeconds);
        Assert.Equal(0.5f, VadSegmenter.Threshold);
        Assert.Equal(300.0, SileroVadDetector.BlockSeconds);
        Assert.Equal(3, o.TuningChanges.Count);
    }

    [Fact]
    public void AnIntConstant_TakesAWholeNumberAndNothingElse()
    {
        ParseFile("--set:DetectionTuning.MaxUnnumberedRetriesPerChunk=5");
        Assert.Equal(5, DetectionTuning.MaxUnnumberedRetriesPerChunk);
        Assert.Throws<CliError>(() => ParseFile("--set:DetectionTuning.MaxUnnumberedRetriesPerChunk=2.5"));
    }

    [Fact]
    public void ADecimalValue_TakesEitherSeparator_LikeEveryOtherDecimalOption()
    {
        ParseFile("--set:DetectionTuning.WhisperChunkSeconds=12,5");
        Assert.Equal(12.5, DetectionTuning.WhisperChunkSeconds);
    }

    [Fact]
    public void AnUnknownClassOrConstant_IsAnError_RatherThanBeingIgnored()
    {
        // Silently ignoring one would leave someone believing a run was tuned when it was not.
        var noClass = Assert.Throws<CliError>(() => ParseFile("--set:Nonsense.Foo=1"));
        Assert.Contains("no overridable class", noClass.Message);
        Assert.Contains("DetectionTuning", noClass.Message);

        var noConstant = Assert.Throws<CliError>(() => ParseFile("--set:DetectionTuning.Nonsense=1"));
        Assert.Contains("no overridable constant", noConstant.Message);
    }

    [Fact]
    public void ADerivedConstant_CannotBeSetOnItsOwn_AndTheErrorSaysSo()
    {
        var ex = Assert.Throws<CliError>(() => ParseFile("--set:DetectionTuning.RescanShiftSeconds=10"));
        Assert.Contains("derived from others", ex.Message);
    }

    [Fact]
    public void AMalformedArgument_IsAnError()
    {
        Assert.Throws<CliError>(() => ParseFile("--set:DetectionTuning.WhisperChunkSeconds"));
        Assert.Throws<CliError>(() => ParseFile("--set:WhisperChunkSeconds=25"));
        Assert.Throws<CliError>(() => ParseFile("--set:DetectionTuning.WhisperChunkSeconds=nonsense"));
        Assert.Throws<CliError>(() => ParseFile("--set:DetectionTuning.WhisperChunkSeconds=NaN"));
    }

    [Fact]
    public void TheOverride_ChangesTheRunFingerprint_SoAResumeCannotCrossTuning()
    {
        var plain = ParseFile()!.RunFingerprint;
        var tuned = ParseFile("--set:DetectionTuning.WhisperChunkSeconds=25")!.RunFingerprint;
        Assert.NotEqual(plain, tuned);
    }

    [Fact]
    public void TheOptionObeysTheSameRuleAsEveryOther_AndMustPrecedeTheTargets()
    {
        var ex = Assert.Throws<CliError>(
            () => CliOptions.Parse([_file, "--set:DetectionTuning.WhisperChunkSeconds=25"]));
        Assert.Contains("must precede", ex.Message);
    }

    [Fact]
    public void HelpIsStillAnswered_WhenTheOverrideIsRejected()
    {
        Assert.Null(CliOptions.Parse(["--set:Nonsense.Foo=1", "--help"]));
    }

    [Fact]
    public void AnOverride_MayComeFromAConfigFile_LikeAnyOtherOption()
    {
        var cfg = Path.Combine(_dir, "tuned.cfg");
        File.WriteAllLines(cfg, ["# a shelf that needs shorter windows",
                                 "--set:DetectionTuning.WhisperChunkSeconds=25"]);
        var o = ParseFile("--config", cfg)!;
        Assert.Equal(25.0, DetectionTuning.WhisperChunkSeconds);
        Assert.Single(o.TuningChanges);
    }
}
