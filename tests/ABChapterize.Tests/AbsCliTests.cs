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

    /// <summary>
    /// The pull modes work on local files, so the trailing arguments stay paths and no selector is
    /// parsed - and <c>--abs-pull-only</c> is a run in its own right where <c>--abs-pull</c> is a
    /// modifier on an ordinary one.
    /// </summary>
    [Fact]
    public void ThePullModes_WorkOnLocalFilesAndKeepTheirPaths()
    {
        foreach (var mode in new[] { "--abs-pull", "--abs-pull-only" })
        {
            var options = CliOptions.Parse(["--abs-url", "host:9", "--abs-key", "k", mode, _file])!;

            Assert.True(options.UsesAbsPull);
            Assert.True(options.UsesAbs);
            Assert.False(options.Abs);
            Assert.Single(options.Targets);
            Assert.Empty(options.AbsSelectors);
        }
    }

    /// <summary>
    /// The point of the pair: pulling and pushing together is a reconciliation, so it has to parse.
    /// </summary>
    [Fact]
    public void PullAndPush_AreTheOneCombinationOfModesThatIsAllowed()
    {
        var options = CliOptions.Parse(
            ["--abs-url", "host:9", "--abs-key", "k", "--abs-pull", "--abs-push", "--verify", _file])!;

        Assert.True(options.AbsPull);
        Assert.True(options.AbsPush);
        Assert.True(options.SendsMarksToAbs);
    }

    /// <summary>
    /// Every other pairing is refused rather than silently resolved: each would either undo the
    /// other or leave one of them looking as though it had been honoured.
    /// </summary>
    /// <param name="line">The command line after the connection options, space-separated.</param>
    [Theory]
    [InlineData("--abs --abs-pull all")]
    [InlineData("--abs --abs-pull-only all")]
    [InlineData("--abs-pull --abs-pull-only f")]
    [InlineData("--abs-pull --abs-push-only f")]
    [InlineData("--abs-pull-only --abs-push-only f")]
    [InlineData("--abs-pull-only --abs-push f")]
    [InlineData("--abs-pull --import f")]
    [InlineData("--abs-pull --export f")]
    [InlineData("--abs-pull-only --verify f")]
    [InlineData("--abs-pull-only --force f")]
    [InlineData("--abs-pull-only --lang de f")]
    // Neither half of --max-chapters means anything here: this mode replaces the file's marks
    // whatever they are, and it transcribes nothing for the implied number cap to cap.
    [InlineData("--abs-pull-only --max-chapters 60 f")]
    [InlineData("--abs-pull --no-op --filter m4b f")]
    public void PullCombinationsThatContradictThemselves_AreRefused(string line)
        => Assert.Throws<CliError>(() => CliOptions.Parse(
            ["--abs-url", "host:9", "--abs-key", "k", .. line.Split(' ')]));

    /// <summary>
    /// <c>--abs-pull-only</c> writes the file, so a backup of what it replaced is exactly the kind
    /// of thing to want - unlike <c>--abs-push-only</c>, which changes no file and refuses it.
    /// </summary>
    [Fact]
    public void PullOnly_AllowsABackupOfWhatItOverwrites()
    {
        var options = CliOptions.Parse(
            ["--abs-url", "host:9", "--abs-key", "k", "--abs-pull-only", "--backup", _file])!;

        Assert.True(options.Backup);
        Assert.True(options.AbsPullOnly);
    }

    /// <summary>
    /// The one mode that never writes to the server, which is what the up-front permission check
    /// reads: an account that may not update items can still pull.
    /// </summary>
    [Fact]
    public void PullOnly_NeverSendsMarksToTheServer()
    {
        var pulling = CliOptions.Parse(
            ["--abs-url", "host:9", "--abs-key", "k", "--abs-pull-only", _file])!;

        Assert.True(pulling.UsesAbs);
        Assert.False(pulling.SendsMarksToAbs);
    }

    /// <summary>
    /// <c>--abs-sync</c> is a spelling, not a mode: the run it resolves to has to be
    /// indistinguishable from the three options written out, down to the fingerprint an
    /// interrupted batch resumes on.
    /// </summary>
    [Fact]
    public void AbsSync_IsExactlyPullVerifyPush()
    {
        var sync = CliOptions.Parse(
            ["--abs-url", "host:9", "--abs-key", "k", "--abs-sync", _file])!;
        var spelledOut = CliOptions.Parse(
            ["--abs-url", "host:9", "--abs-key", "k", "--abs-pull", "--verify", "--abs-push", _file])!;

        Assert.True(sync.AbsPull);
        Assert.True(sync.AbsPush);
        Assert.True(sync.Verify);
        Assert.True(sync.SendsMarksToAbs);
        Assert.Equal(spelledOut.RunFingerprint, sync.RunFingerprint);
    }

    /// <summary>
    /// Repeating half of what <c>--abs-sync</c> already says is harmless: the flags are the same
    /// flags, and setting one twice is setting it once.
    /// </summary>
    [Theory]
    [InlineData("--abs-pull")]
    [InlineData("--abs-push")]
    [InlineData("--verify")]
    public void AbsSync_ToleratesItsOwnPartsBeingRepeated(string option)
    {
        var options = CliOptions.Parse(
            ["--abs-url", "host:9", "--abs-key", "k", "--abs-sync", option, _file])!;

        Assert.True(options.AbsPull);
        Assert.True(options.AbsPush);
        Assert.True(options.Verify);
    }

    /// <summary>
    /// The three modes it is not are refused - and the message names <c>--abs-sync</c> itself
    /// rather than the options it stands for, which is the whole reason that check sits ahead of
    /// the pairwise rules.
    /// </summary>
    /// <param name="line">The command line after the connection options, space-separated.</param>
    [Theory]
    [InlineData("--abs-sync --abs all")]
    [InlineData("--abs-sync --abs-push-only f")]
    [InlineData("--abs-sync --abs-pull-only f")]
    public void AbsSync_RefusesTheOtherModesInItsOwnWords(string line)
    {
        var error = Assert.Throws<CliError>(() => CliOptions.Parse(
            ["--abs-url", "host:9", "--abs-key", "k", .. line.Split(' ')]));

        Assert.Contains("--abs-sync", error.Message);
    }

    /// <summary>
    /// <c>--no-op</c> lists and exits, so it has nothing to reconcile. Refused by the general rule
    /// about processing options rather than by the ABS ones - <c>--verify</c> is one - which is
    /// why the message is the general one.
    /// </summary>
    [Fact]
    public void AbsSync_IsRefusedWithNoOp()
        => Assert.Throws<CliError>(() => CliOptions.Parse(
            ["--abs-url", "host:9", "--abs-key", "k", "--abs-sync", "--no-op", "--filter", "m4b", _file]));

    /// <summary>
    /// The retry budget defaults to three minutes and takes a value in minutes, decimal separator
    /// either way round like every other number this tool reads off a command line.
    /// </summary>
    [Fact]
    public void AbsRetry_DefaultsToThreeMinutesAndAcceptsEitherDecimalSeparator()
    {
        Assert.Equal(3, Parse("-A", "all").AbsRetryMinutes);
        Assert.Equal(0, Parse("-A", "--abs-retry", "0", "all").AbsRetryMinutes);
        Assert.Equal(1.5, Parse("-A", "--abs-retry", "1.5", "all").AbsRetryMinutes);
        Assert.Equal(1.5, Parse("-A", "--abs-retry", "1,5", "all").AbsRetryMinutes);
    }

    /// <summary>A budget that is not a number, or is negative, is a command line error.</summary>
    [Theory]
    [InlineData("-1")]
    [InlineData("soon")]
    [InlineData("1441")]
    public void AbsRetry_RejectsWhatCannotBeAWaitingTime(string value)
        => Assert.Throws<CliError>(() => CliOptions.Parse(
            ["--abs-url", "host:9", "--abs-key", "k", "--abs", "--abs-retry", value, "all"]));

    /// <summary>
    /// Like the rest of the <c>--abs-...</c> family, it describes a conversation with a server and
    /// is refused where there is none - including when it names the default, which is the case a
    /// naive "did the value change" test would let through.
    /// </summary>
    [Fact]
    public void AbsRetry_WithoutAnyServerMode_IsRefused()
    {
        var ex = Assert.Throws<CliError>(() => CliOptions.Parse(["--abs-retry", "3", _file]));
        Assert.Contains("--abs", ex.Message);
    }

    [Fact]
    public void AbsPushOnly_WorksWithoutAbsModeAndKeepsItsPaths()
    {
        var options = CliOptions.Parse(["--abs-url", "host:9", "--abs-key", "k", "--abs-push-only", _file])!;

        Assert.True(options.AbsPushOnly);
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
        => Assert.Throws<CliError>(() => CliOptions.Parse(["--abs-url", "host:9", "--abs-key", "k", mode, "--abs-push-only", "x"]));

    [Theory]
    [InlineData("--force")]
    [InlineData("--import")]
    [InlineData("--export")]
    [InlineData("--backup")]
    // Named on the list rather than reached through the detection settings, which it is not
    // one of: it decides whether a file's existing marks are bogus enough to discard, and
    // this mode is the one that sends them instead of judging them.
    [InlineData("--max-chapters")]
    // Reached through the shared detection-setting list rather than named on its own.
    [InlineData("--verify")]
    [InlineData("--lang")]
    public void AbsPushOnly_RefusesWhatItWouldIgnore(string option)
    {
        string[] line = option switch
        {
            "--lang" => ["--abs-push-only", "--lang", "de", "x"],
            "--max-chapters" => ["--abs-push-only", "--max-chapters", "60", "x"],
            _ => ["--abs-push-only", option, "x"],
        };
        Assert.Throws<CliError>(() => CliOptions.Parse(["--abs-url", "host:9", "--abs-key", "k", .. line]));
    }

    /// <summary>The options describe a server, so one of the three modes that talks to one has to
    /// be there - otherwise they would quietly do nothing at all.</summary>
    [Fact]
    public void ConnectionOptionsWithoutAMode_AreRefused()
        => Assert.Throws<CliError>(() => CliOptions.Parse(["--abs-url", "host:9", "--abs-key", "k", "x"]));

    [Fact]
    public void AbsPush_IsAnOrdinaryLocalRunThatAlsoTalksToAServer()
    {
        var options = CliOptions.Parse(["--abs-url", "host:9", "--abs-key", "k", "--abs-push", _file])!;

        Assert.True(options.AbsPush);
        Assert.False(options.Abs);
        Assert.False(options.AbsPushOnly);
        // The targets stay paths, and the detection options stay usable - that is the whole
        // difference from --abs-push-only, which detects nothing.
        Assert.Single(options.Targets);
        Assert.True(options.UsesAbs);
        Assert.NotNull(CliOptions.Parse(
            ["--abs-url", "host:9", "--abs-key", "k", "--abs-push", "--lang", "de", "--force", _file]));
    }

    /// <summary>
    /// The three modes each answer "where do the marks go" differently, and no two of those
    /// answers can hold at once.
    /// </summary>
    [Theory]
    [InlineData("--abs")]
    [InlineData("--abs-push-only")]
    public void AbsPush_RefusesTheOtherModes(string other)
        => Assert.Throws<CliError>(
            () => CliOptions.Parse(["--abs-url", "host:9", "--abs-key", "k", "--abs-push", other, "all"]));

    /// <summary>
    /// A listing that exits has nothing to send. <c>--abs</c> stays the exception, its --no-op
    /// being the listing of what the selectors picked.
    /// </summary>
    [Theory]
    [InlineData("--abs-push")]
    [InlineData("--abs-push-only")]
    public void NoOp_RefusesThePushModes(string mode)
    {
        Assert.Throws<CliError>(() => CliOptions.Parse(
            ["--abs-url", "host:9", "--abs-key", "k", "--no-op", "--filter", "m4b", mode, _file]));
        Assert.NotNull(Parse("-A", "--no-op", "all"));
    }

    /// <summary>
    /// A plain run leaves a file marked but unsent, so an --abs-push run over the same folder must
    /// not mistake the checkpoint that plain run left for work of its own that is already done.
    /// </summary>
    [Fact]
    public void RunFingerprint_TellsAnAbsPushRunFromAPlainOne()
    {
        var pushing = CliOptions.Parse(["--abs-url", "host:9", "--abs-key", "k", "--abs-push", _file])!;
        var plain = CliOptions.Parse([_file])!;

        Assert.NotEqual(plain.RunFingerprint, pushing.RunFingerprint);
    }

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
    /// Without this, an --abs-push-only sweep over a folder would record its files in the directory
    /// checkpoint as done, and the detection run afterwards would skip every one of them.
    /// </summary>
    [Fact]
    public void RunFingerprint_TellsAnAbsPushOnlySweepFromADetectionRun()
    {
        var pushing = CliOptions.Parse(["--abs-url", "host:9", "--abs-key", "k", "--abs-push-only", _file])!;
        var detecting = CliOptions.Parse([_file])!;

        Assert.NotEqual(detecting.RunFingerprint, pushing.RunFingerprint);
    }

    [Fact]
    public void RunFingerprint_TellsTwoServersApart()
    {
        var here = CliOptions.Parse(["--abs-url", "host-a:9", "--abs-key", "k", "--abs-push-only", _file])!;
        var there = CliOptions.Parse(["--abs-url", "host-b:9", "--abs-key", "k", "--abs-push-only", _file])!;

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
        Assert.Contains("--abs-push-only", usage);
        foreach (var variable in Variables)
            Assert.Contains(variable, usage);
    }

    /// <summary>Writes a book mapping file and returns its path.</summary>
    /// <param name="lines">Its entries.</param>
    private string MapFile(params string[] lines)
    {
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.abs");
        File.WriteAllLines(path, lines);
        return path;
    }

    [Fact]
    public void BookMappings_AreReadForAModeThatMatchesLocalFiles()
    {
        var options = CliOptions.Parse(
            ["--abs-url", "host:9", "--abs-key", "k", "--abs-push",
             "--abs-map", MapFile("book.m4b = item:li_one"), _file])!;

        Assert.Equal("li_one", Assert.Single(options.AbsBookMappings).Book?.Value);
    }

    /// <summary>
    /// It describes a server, so it needs one of the modes that talks to one - the same rule the
    /// connection options follow.
    /// </summary>
    [Fact]
    public void BookMappings_NeedAModeThatTalksToAServer()
        => Assert.Throws<CliError>(() => CliOptions.Parse(
            ["--abs-map", MapFile("book.m4b = item:li_one"), _file]));

    /// <summary>
    /// Refused rather than ignored: <c>--abs</c> works on books the selectors already named, so
    /// there is no local file to recognize and a mapping would never be consulted.
    /// </summary>
    [Fact]
    public void BookMappings_AreRefusedByAbsModeItself()
    {
        var ex = Assert.Throws<CliError>(() => Parse(
            "--abs", "--abs-map", MapFile("book.m4b = item:li_one"), "all"));

        Assert.Contains("--abs-map", ex.Message);
    }

    /// <summary>
    /// A mapping decides which book a file's marks reach, so a run given one is not the run that
    /// could not match that file - and must not read its checkpoint as work already done.
    /// </summary>
    [Fact]
    public void ABookMapping_ChangesTheRunFingerprint()
    {
        string[] connection = ["--abs-url", "host:9", "--abs-key", "k", "--abs-push"];
        var plain = CliOptions.Parse([.. connection, _file])!;
        var mapped = CliOptions.Parse(
            [.. connection, "--abs-map", MapFile("book.m4b = item:li_one"), _file])!;

        Assert.NotEqual(plain.RunFingerprint, mapped.RunFingerprint);
    }
}
