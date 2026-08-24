// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Xunit;
using ABChapterize.Abs;
using ABChapterize.Cli;
using ABChapterize.Errors;

namespace ABChapterize.Tests;

/// <summary>
/// Tests for what the command line does with ABS mode: the selectors that replace the target
/// paths, the combinations refused, and the two derived values a run depends on.
/// </summary>
/// <remarks>
/// Clears the connection variables the way <see cref="AbsConnectionTests"/> does and for the same
/// reason - a developer machine with them exported would otherwise change what these tests prove.
/// </remarks>
public sealed class AbsCliTests : IDisposable
{
    private static readonly string[] Variables =
    [
        AbsConnection.UrlVariable, AbsConnection.KeyVariable,
        AbsConnection.UserVariable, AbsConnection.PasswordVariable, AbsWorkspace.TempVariable,
    ];

    private readonly Dictionary<string, string?> _saved = [];
    private readonly string _dir;
    private readonly string _file;

    /// <summary>Empties the environment and creates one local audio file to aim non-ABS runs at.</summary>
    public AbsCliTests()
    {
        foreach (var variable in Variables)
        {
            _saved[variable] = Environment.GetEnvironmentVariable(variable);
            Environment.SetEnvironmentVariable(variable, null);
        }
        _dir = Path.Combine(Path.GetTempPath(), $"abchapterize-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "book.m4b");
        File.WriteAllText(_file, "x");
    }

    /// <summary>Restores the environment and removes the temp directory.</summary>
    public void Dispose()
    {
        foreach (var (variable, value) in _saved)
            Environment.SetEnvironmentVariable(variable, value);
        Directory.Delete(_dir, recursive: true);
    }

    /// <summary>The connection options every ABS command line needs, in front of the given ones.</summary>
    /// <param name="rest">The rest of the command line, selectors last.</param>
    private static CliOptions Parse(params string[] rest)
        => CliOptions.Parse(["--abs-url", "host:9", "--abs-key", "k", .. rest])!;

    [Fact]
    public void TrailingArguments_BecomeSelectorsRatherThanPaths()
    {
        var options = Parse("--abs", "library:Discworld", "Mort");

        Assert.True(options.Abs);
        Assert.True(options.UsesAbs);
        Assert.Empty(options.Targets);
        Assert.Equal(
            [(AbsSelectorKind.Library, "Discworld"), (AbsSelectorKind.Title, "Mort")],
            options.AbsSelectors.Select(s => (s.Kind, s.Value)));
    }

    [Fact]
    public void ShortFormA_IsAbsMode()
        => Assert.True(Parse("-A", "all").Abs);

    [Fact]
    public void RepeatedSelectors_AreCollapsed()
        => Assert.Single(Parse("-A", "title:Mort", "TITLE:mort").AbsSelectors);

    [Fact]
    public void SelectorsAreNotCheckedAgainstTheFileSystem()
    {
        // The point of the branch: in ABS mode nothing here is a path, so target resolution - which
        // would report every one of these as a file that does not exist - must not run.
        var options = Parse("-A", "library:Nothing On This Disk");
        Assert.Empty(options.Targets);
    }

    [Fact]
    public void ConnectionIsResolvedAtParseTime()
    {
        var options = Parse("-A", "all");
        Assert.NotNull(options.AbsServer);
        Assert.Equal("http://host:9", options.AbsServer.Root);
    }

    /// <summary>A missing server is a command line error, not a failure an hour into a batch.</summary>
    [Fact]
    public void WithoutAServer_ParseFails()
        => Assert.Throws<CliError>(() => CliOptions.Parse(["--abs", "all"]));

    [Fact]
    public void AbsTemp_FallsBackToTheEnvironment()
    {
        Environment.SetEnvironmentVariable(AbsWorkspace.TempVariable, _dir);
        Assert.Equal(_dir, Parse("-A", "all").AbsTemp);
    }

    [Fact]
    public void AbsTemp_OnTheCommandLineWins()
    {
        Environment.SetEnvironmentVariable(AbsWorkspace.TempVariable, _dir);
        Assert.Equal(_dir, Parse("-A", "--abs-temp", _dir, "all").AbsTemp);
    }

    [Fact]
    public void PushOnly_WorksWithoutAbsModeAndKeepsItsPaths()
    {
        var options = CliOptions.Parse(["--abs-url", "host:9", "--abs-key", "k", "--push-only", _file])!;

        Assert.True(options.PushOnly);
        Assert.False(options.Abs);
        Assert.True(options.UsesAbs);
        Assert.Single(options.Targets);
    }

    [Theory]
    [InlineData("--import")]
    [InlineData("--export")]
    [InlineData("--backup")]
    [InlineData("--recurse")]
    public void AbsMode_RefusesWhatOnlyMeansSomethingLocally(string option)
        => Assert.Throws<CliError>(() => Parse("-A", option, "all"));

    [Theory]
    [InlineData("--revert")]
    [InlineData("--cleanup")]
    public void LocalOnlyModes_RefuseAServer(string mode)
        => Assert.Throws<CliError>(() => CliOptions.Parse(["--abs-url", "host:9", "--abs-key", "k", mode, "--push-only", "x"]));

    [Theory]
    [InlineData("--force")]
    [InlineData("--import")]
    [InlineData("--export")]
    [InlineData("--backup")]
    // Reached through the shared detection-setting list rather than named on its own.
    [InlineData("--verify")]
    [InlineData("--lang")]
    public void PushOnly_RefusesWhatItWouldIgnore(string option)
    {
        var line = option == "--lang"
            ? new[] { "--push-only", "--lang", "de", "x" }
            : ["--push-only", option, "x"];
        Assert.Throws<CliError>(() => CliOptions.Parse(["--abs-url", "host:9", "--abs-key", "k", .. line]));
    }

    /// <summary>The options describe a server, so one of the two modes that talks to one has to be
    /// there - otherwise they would quietly do nothing at all.</summary>
    [Fact]
    public void ConnectionOptionsWithoutAMode_AreRefused()
        => Assert.Throws<CliError>(() => CliOptions.Parse(["--abs-url", "host:9", "--abs-key", "k", "x"]));

    [Fact]
    public void NoOp_NeedsNoFilterInAbsMode()
    {
        Assert.NotNull(Parse("-A", "--no-op", "all"));
        // ...but still does everywhere else.
        Assert.Throws<CliError>(() => CliOptions.Parse(["--no-op", _file]));
    }

    [Fact]
    public void ChapterCount_TakesExactlyOneSelector()
    {
        Assert.NotNull(Parse("-A", "--chapter-count", "20", "title:Mort"));
        Assert.Throws<CliError>(() => Parse("-A", "--chapter-count", "20", "title:Mort", "title:Eric"));
    }

    /// <summary>
    /// Without this, a --push-only sweep over a folder would record its files in the directory
    /// checkpoint as done, and the detection run afterwards would skip every one of them.
    /// </summary>
    [Fact]
    public void RunFingerprint_TellsAPushOnlySweepFromADetectionRun()
    {
        var pushing = CliOptions.Parse(["--abs-url", "host:9", "--abs-key", "k", "--push-only", _file])!;
        var detecting = CliOptions.Parse([_file])!;

        Assert.NotEqual(detecting.RunFingerprint, pushing.RunFingerprint);
    }

    [Fact]
    public void RunFingerprint_TellsTwoServersApart()
    {
        var here = CliOptions.Parse(["--abs-url", "host-a:9", "--abs-key", "k", "--push-only", _file])!;
        var there = CliOptions.Parse(["--abs-url", "host-b:9", "--abs-key", "k", "--push-only", _file])!;

        Assert.NotEqual(here.RunFingerprint, there.RunFingerprint);
    }

    /// <summary>
    /// A book whose container cannot hold chapter marks is still workable in ABS mode, so a
    /// <c>--filter</c> naming one of those formats has to be accepted.
    /// </summary>
    /// <remarks>
    /// The same command line without <c>--abs</c> is refused, and rightly so: there the marks
    /// have nowhere to go. Both halves are asserted here rather than only the new one, because
    /// what makes this correct is the difference between them.
    /// </remarks>
    [Fact]
    public void FilterExtensionList_MayNameAFormatChapterMarksCannotBeWrittenTo()
    {
        var abs = Parse("--abs", "--filter", "flac,ogg", "all");

        Assert.Equal([".flac", ".ogg"], abs.FilterExtensions!);

        var ex = Assert.Throws<CliError>(() => CliOptions.Parse(["--filter", "flac", _dir]));
        Assert.Contains(".flac", ex.Message);
    }

    /// <summary>
    /// The usage text is where <c>--abs</c> is discovered, and it is also the only place the
    /// environment variables are written down - so a rename that missed one of them would leave the
    /// documented name pointing at nothing.
    /// </summary>
    [Fact]
    public void UsageText_NamesTheModeAndEveryEnvironmentVariable()
    {
        var usage = CliOptions.UsageText;
        Assert.Contains("-A, --abs", usage);
        Assert.Contains("--push-only", usage);
        foreach (var variable in Variables)
            Assert.Contains(variable, usage);
    }
}
