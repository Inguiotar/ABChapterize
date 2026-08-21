// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Xunit;
using ABChapterize.Cli;
using ABChapterize.Errors;

namespace ABChapterize.Tests;

/// <summary>
/// Tests for <c>--config</c>: how a line becomes argument tokens, and how a file's options are
/// spliced into the command line. The end-to-end cases go through <see cref="CliOptions.Parse"/>
/// rather than <c>ConfigFile.Expand</c> alone, because what the feature promises is that an option
/// behaves identically whichever side it came from - which only the real parser can answer.
/// </summary>
public sealed class ConfigFileTests : IDisposable
{
    private readonly string _dir;
    private readonly string _file;

    /// <summary>Creates a temp directory with one supported audio file to parse against.</summary>
    public ConfigFileTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"abchapterize-cfg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "book.m4b");
        File.WriteAllText(_file, "x");
    }

    /// <summary>Removes the temp directory.</summary>
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>Writes a config file into the temp directory and returns its path.</summary>
    private string Config(string name, params string[] lines)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllLines(path, lines);
        return path;
    }

    /// <summary>Parses the given options followed by the temp audio file as target.</summary>
    private CliOptions? ParseFile(params string[] options)
        => CliOptions.Parse([.. options, _file]);

    [Fact]
    public void ALine_SplitsOnceSoAnArgumentKeepsItsSpaces()
    {
        // The whole reason there is no quoting grammar: one option per line leaves nothing for a
        // quote to disambiguate, so a phrase regexp can be written with the spaces it needs.
        Assert.Equal(["--chapter-phrase", "/^kapitel ()/"],
                     ConfigFile.LineTokens("--chapter-phrase /^kapitel ()/", "test"));
        Assert.Equal(["--force"], ConfigFile.LineTokens("  --force  ", "test"));
        Assert.Equal(["-fs"], ConfigFile.LineTokens("-fs", "test"));
        Assert.Equal(["--mark-lead", "0.5"], ConfigFile.LineTokens("--mark-lead\t0.5", "test"));
    }

    [Fact]
    public void ALine_DropsOneLayerOfTheShellQuotesPeoplePasteWithIt()
    {
        Assert.Equal(["--custom", "[de]/^vorwort/:Vorwort"],
                     ConfigFile.LineTokens("--custom \"[de]/^vorwort/:Vorwort\"", "test"));
        // The only way to write an argument that is meant to be empty.
        Assert.Equal(["--chapter-title", ""], ConfigFile.LineTokens("--chapter-title \"\"", "test"));
        // One layer only, and only when it wraps the whole argument.
        Assert.Equal(["--custom", "\"a\" and \"b\""],
                     ConfigFile.LineTokens("--custom \"\"a\" and \"b\"\"", "test"));
        Assert.Equal(["--custom", "say \"hi\""], ConfigFile.LineTokens("--custom say \"hi\"", "test"));
    }

    [Fact]
    public void ALine_ThatCarriesNothing_IsSkipped_AndOneThatIsNotAnOption_IsAnError()
    {
        Assert.Null(ConfigFile.LineTokens("", "test"));
        Assert.Null(ConfigFile.LineTokens("   ", "test"));
        Assert.Null(ConfigFile.LineTokens("# a comment", "test"));
        Assert.Null(ConfigFile.LineTokens("   # indented comment", "test"));
        var ex = Assert.Throws<CliError>(() => ConfigFile.LineTokens("book.m4b", "line 3"));
        Assert.Contains("line 3", ex.Message);
        Assert.Contains("belong on the command line", ex.Message);
    }

    [Fact]
    public void OptionsFromAFile_AreApplied()
    {
        var cfg = Config("a.cfg",
            "# settings for this shelf",
            "--verbose",
            "--mark-lead 0.5",
            "",
            "--chapter-phrase \"[de]/^kapitel ()/\"");
        var o = ParseFile("--config", cfg)!;
        Assert.True(o.Verbose);
        Assert.Equal(0.5, o.MarkLeadSeconds);
        Assert.Contains("kapitel", o.ChapterPhrase);
    }

    [Fact]
    public void AnOptionOnTheCommandLine_BeatsTheSameOptionInAFile_WhicheverSideCameFirst()
    {
        // The point of moving a file's options to the front rather than expanding in place: the
        // answer must not depend on where --config happened to sit.
        var cfg = Config("a.cfg", "--mark-lead 0.5");
        Assert.Equal(0.9, ParseFile("--config", cfg, "--mark-lead", "0.9")!.MarkLeadSeconds);
        Assert.Equal(0.9, ParseFile("--mark-lead", "0.9", "--config", cfg)!.MarkLeadSeconds);
    }

    [Fact]
    public void ARepeatableOption_AccumulatesAcrossTheFileAndTheCommandLine()
    {
        var cfg = Config("a.cfg", "--custom /^vorwort/:Vorwort");
        var o = ParseFile("--config", cfg, "--custom", "/^nachwort/:Nachwort")!;
        Assert.Equal(2, o.CustomMappings.Count);
        Assert.Contains(o.CustomMappings, m => m.Title == "Vorwort");
        Assert.Contains(o.CustomMappings, m => m.Title == "Nachwort");
    }

    [Fact]
    public void TwoFiles_AreAppliedInTheOrderTheyWereNamed()
    {
        var first = Config("first.cfg", "--mark-lead 0.5");
        var second = Config("second.cfg", "--mark-lead 0.7");
        Assert.Equal(0.7, ParseFile("--config", first, "--config", second)!.MarkLeadSeconds);
    }

    [Fact]
    public void AFile_MayNameAnother_RelativeToItself()
    {
        var nested = Path.Combine(_dir, "sub");
        Directory.CreateDirectory(nested);
        File.WriteAllLines(Path.Combine(nested, "inner.cfg"), ["--mark-lead 0.42"]);
        var outer = Config("outer.cfg", "--config sub/inner.cfg", "--verbose");
        var o = ParseFile("--config", outer)!;
        Assert.Equal(0.42, o.MarkLeadSeconds);
        Assert.True(o.Verbose);
    }

    /// <summary>
    /// Two config files pulling in a common base is the ordinary way to write a set of them, and
    /// until build 375 it was refused with "includes itself" - which was not what had happened.
    /// The base is taken once, so its repeatable options are not doubled either.
    /// </summary>
    [Fact]
    public void TwoFiles_SharingABase_TakeItOnce_RatherThanReportingACycle()
    {
        File.WriteAllLines(Path.Combine(_dir, "base.cfg"), ["--custom /^zeittafel/:Zeittafel"]);
        var first = Config("first.cfg", "--config base.cfg", "--mark-lead 0.5");
        var second = Config("second.cfg", "--config base.cfg", "--verbose");
        var o = ParseFile("--config", first, "--config", second)!;
        Assert.Equal(0.5, o.MarkLeadSeconds);
        Assert.True(o.Verbose);
        Assert.Single(o.CustomMappings);
    }

    /// <summary>A cycle two files long is still a cycle, which the chain check has to see even
    /// though neither file names itself directly.</summary>
    [Fact]
    public void TwoFiles_IncludingEachOther_AreReported()
    {
        File.WriteAllLines(Path.Combine(_dir, "ping.cfg"), ["--config pong.cfg"]);
        File.WriteAllLines(Path.Combine(_dir, "pong.cfg"), ["--config ping.cfg"]);
        var ex = Assert.Throws<CliError>(
            () => ParseFile("--config", Path.Combine(_dir, "ping.cfg")));
        Assert.Contains("includes itself", ex.Message);
    }

    [Fact]
    public void AFile_ThatIncludesItself_IsReported_RatherThanFollowed()
    {
        var cfg = Path.Combine(_dir, "loop.cfg");
        File.WriteAllLines(cfg, ["--config loop.cfg"]);
        var ex = Assert.Throws<CliError>(() => ParseFile("--config", cfg));
        Assert.Contains("includes itself", ex.Message);
    }

    [Fact]
    public void AMissingFile_IsACommandLineError()
    {
        var ex = Assert.Throws<CliError>(
            () => ParseFile("--config", Path.Combine(_dir, "nope.cfg")));
        Assert.Contains("nope.cfg", ex.Message);
    }

    [Fact]
    public void TheOptionObeysTheSameRuleAsEveryOther_AndMustPrecedeTheTargets()
    {
        var cfg = Config("a.cfg", "--verbose");
        var ex = Assert.Throws<CliError>(() => CliOptions.Parse([_file, "--config", cfg]));
        Assert.Contains("must precede", ex.Message);
    }

    [Fact]
    public void HelpIsStillAnswered_WhenTheConfigFileIsBroken()
    {
        // Someone reaching for --help because their config is not working should get the help,
        // not the error they already know about.
        Assert.Null(CliOptions.Parse(["--config", Path.Combine(_dir, "nope.cfg"), "--help"]));
    }
}
