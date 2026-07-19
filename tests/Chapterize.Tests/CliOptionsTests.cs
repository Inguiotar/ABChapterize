// Chapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Xunit;

namespace Chapterize.Tests;

/// <summary>
/// Tests for <see cref="CliOptions.Parse"/>: option syntax (long, short, collapsed),
/// per-language defaults, --filter handling, the chapter phrase regex, and all
/// semantic validation rules. Target paths point into a per-test temp directory.
/// </summary>
public sealed class CliOptionsTests : IDisposable
{
    private readonly string _dir;
    private readonly string _file;

    /// <summary>Creates a temp directory with one supported audio file to parse against.</summary>
    public CliOptionsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"chapterize-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "book.m4b");
        File.WriteAllText(_file, "x");
    }

    /// <summary>Removes the temp directory.</summary>
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>Parses the given options followed by the temp audio file as target.</summary>
    private CliOptions? ParseFile(params string[] options)
        => CliOptions.Parse([.. options, _file]);

    /// <summary>Parses the given options followed by the temp directory as target.</summary>
    private CliOptions? ParseDir(params string[] options)
        => CliOptions.Parse([.. options, _dir]);

    [Fact]
    public void Defaults_AreEnglish()
    {
        var o = ParseFile()!;
        Assert.Equal("en", o.Language);
        Assert.Equal("chapter", o.ChapterPhrase);
        Assert.Equal("Chapter", o.Title);
        Assert.Equal("Intro", o.IntroTitle);
        Assert.Equal("turbo", o.Model);
        Assert.Equal(1.5, o.MinSilenceSeconds);
        Assert.False(o.TargetIsDirectory);
        Assert.False(o.Recurse | o.Backup | o.Revert | o.Force | o.Jingle | o.Quiet | o.Verbose | o.NoBar | o.Summary);
    }

    [Fact]
    public void Lang_LocalizesPhraseTitleAndIntro()
    {
        var o = ParseFile("--lang", "tr")!;
        Assert.Equal("bölüm", o.ChapterPhrase);
        Assert.Equal("Bölüm", o.Title);
        Assert.Equal("Giriş", o.IntroTitle);
    }

    [Theory]
    [InlineData("en", "Intro")]
    [InlineData("de", "Intro")]
    [InlineData("fr", "Introduction")]
    [InlineData("es", "Introducción")]
    [InlineData("it", "Introduzione")]
    [InlineData("nl", "Intro")]
    [InlineData("tr", "Giriş")]
    [InlineData("pt", "Introdução")]
    [InlineData("pl", "Wstęp")]
    [InlineData("sv", "Introduktion")]
    [InlineData("da", "Introduktion")]
    [InlineData("cs", "Intro")] // no dedicated language support: English-ish defaults
    public void IntroTitle_Default_IsLocalized(string lang, string expected)
    {
        Assert.Equal(expected, ParseFile("--lang", lang)!.IntroTitle);
    }

    [Fact]
    public void ExplicitPhraseAndTitle_WinOverLocalization()
    {
        var o = ParseFile("-l", "de", "-c", "Teil", "-t", "Teil", "-i", "Anfang")!;
        Assert.Equal("Teil", o.ChapterPhrase);
        Assert.Equal("Teil", o.Title);
        Assert.Equal("Anfang", o.IntroTitle);
    }

    [Fact]
    public void LanguageAndModel_AreCaseNormalized()
    {
        var o = ParseFile("--lang", "DE", "--model", "TURBO")!;
        Assert.Equal("de", o.Language);
        Assert.Equal("turbo", o.Model);
    }

    [Fact]
    public void CollapsedShortFlags_AllApply()
    {
        var o = ParseDir("-rbfjqvs")!;
        Assert.True(o.Recurse && o.Backup && o.Force && o.Jingle && o.Quiet && o.Verbose && o.Summary);
    }

    [Fact]
    public void ShortValueOption_AsLastCollapsedLetter_TakesParameter()
    {
        var o = ParseFile("-bl", "fr")!;
        Assert.True(o.Backup);
        Assert.Equal("fr", o.Language);
    }

    [Fact]
    public void ShortValueOption_NotLastInCollapsedGroup_IsAnError()
    {
        var ex = Assert.Throws<CliError>(() => ParseFile("-lb", "fr"));
        Assert.Contains("cannot be collapsed", ex.Message);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-?")]
    [InlineData("/?")]
    public void HelpRequests_ReturnNull(string arg)
    {
        Assert.Null(CliOptions.Parse([arg]));
    }

    [Theory]
    [InlineData("--bogus")]
    [InlineData("-z")]
    public void UnknownOptions_AreRejected(string arg)
    {
        var ex = Assert.Throws<CliError>(() => ParseFile(arg));
        Assert.Contains("Unknown option", ex.Message);
    }

    [Fact]
    public void MissingParameter_IsAnError()
    {
        var ex = Assert.Throws<CliError>(() => CliOptions.Parse(["--lang"]));
        Assert.Contains("requires a parameter", ex.Message);
    }

    [Fact]
    public void TargetMustBeLastArgument()
    {
        Assert.Throws<CliError>(() => CliOptions.Parse([_file, "--backup"]));
    }

    [Fact]
    public void MissingTarget_IsAnError()
    {
        Assert.Throws<CliError>(() => CliOptions.Parse(["--backup"]));
    }

    [Fact]
    public void NonexistentTarget_IsAnError()
    {
        Assert.Throws<CliError>(() => CliOptions.Parse([Path.Combine(_dir, "missing.m4b")]));
    }

    [Fact]
    public void UnsupportedFileExtension_IsAnError()
    {
        var txt = Path.Combine(_dir, "notes.txt");
        File.WriteAllText(txt, "x");
        var ex = Assert.Throws<CliError>(() => CliOptions.Parse([txt]));
        Assert.Contains("Unsupported file type", ex.Message);
    }

    [Fact]
    public void Recurse_OnSingleFile_IsAnError()
    {
        Assert.Throws<CliError>(() => ParseFile("--recurse"));
    }

    [Theory]
    [InlineData("xx1")]
    [InlineData("e")]
    [InlineData("deu")]
    public void InvalidLanguageCodes_AreRejected(string lang)
    {
        Assert.Throws<CliError>(() => ParseFile("--lang", lang));
    }

    [Fact]
    public void InvalidModel_IsRejected()
    {
        Assert.Throws<CliError>(() => ParseFile("--model", "gigantic"));
    }

    [Fact]
    public void FilterExtensionList_IsNormalized()
    {
        var o = ParseDir("--filter", "MP3, .m4b,mp3")!;
        Assert.Equal([".mp3", ".m4b"], o.FilterExtensions!);
        Assert.Equal([".mp3", ".m4b"], o.EffectiveExtensions);
    }

    [Fact]
    public void FilterExtensionList_UnsupportedExtension_IsAnError()
    {
        var ex = Assert.Throws<CliError>(() => ParseDir("--filter", "mp3,wav"));
        Assert.Contains(".wav", ex.Message);
    }

    [Fact]
    public void FilterRegexAndExtensionList_CanBeCombined()
    {
        var o = ParseDir("-F", "/part\\d+/", "-F", "mp3")!;
        Assert.NotNull(o.FilterRegex);
        Assert.Matches(o.FilterRegex!, @"C:\audio\PART7.mp3");
        Assert.Equal([".mp3"], o.FilterExtensions!);
    }

    [Fact]
    public void SecondFilterOfSameKind_IsAnError()
    {
        Assert.Throws<CliError>(() => ParseDir("-F", "mp3", "-F", "m4b"));
        Assert.Throws<CliError>(() => ParseDir("-F", "/a/", "-F", "/b/"));
    }

    [Fact]
    public void InvalidFilterRegex_IsAnError()
    {
        Assert.Throws<CliError>(() => ParseDir("--filter", "/(unclosed/"));
    }

    [Fact]
    public void WithoutFilter_AllSupportedExtensions_AreEffective()
    {
        var o = ParseDir()!;
        Assert.Null(o.FilterExtensions);
        Assert.Equal(CliOptions.SupportedExtensions, o.EffectiveExtensions);
    }

    [Fact]
    public void Revert_WithIncompatibleOptions_IsAnError()
    {
        Assert.Throws<CliError>(() => ParseDir("--revert", "--force"));
        Assert.Throws<CliError>(() => ParseDir("--revert", "--lang", "de"));
        Assert.Throws<CliError>(() => ParseDir("--revert", "--backup"));
    }

    [Fact]
    public void Revert_WithRecurseAndFilter_IsAllowed()
    {
        var o = ParseDir("--revert", "--recurse", "--filter", "m4b")!;
        Assert.True(o.Revert && o.Recurse);
    }

    [Fact]
    public void Revert_WithOutputOptions_IsAllowed()
    {
        var o = ParseDir("--revert", "--quiet", "--summary", "--verbose", "--no-bar")!;
        Assert.True(o.Revert && o.Quiet && o.Summary);
    }

    [Fact]
    public void MaxJingleLength_WithoutJingle_IsAnError()
    {
        Assert.Throws<CliError>(() => ParseFile("--max-jingle-length", "30"));
    }

    [Fact]
    public void JingleParameters_AreParsed()
    {
        var o = ParseFile("--jingle", "--max-jingle-length", "30.5")!;
        Assert.True(o.Jingle);
        Assert.Equal(30.5, o.MaxJingleSeconds);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0.5")]
    [InlineData("-3")]
    [InlineData("601")]
    [InlineData("abc")]
    public void InvalidJingleLengths_AreRejected(string value)
    {
        Assert.Throws<CliError>(() => ParseFile("--jingle", "--max-jingle-length", value));
    }

    [Theory]
    [InlineData("0.05")]
    [InlineData("61")]
    [InlineData("abc")]
    public void InvalidMinSilenceLengths_AreRejected(string value)
    {
        Assert.Throws<CliError>(() => ParseFile("--min-silence-length", value));
    }

    [Fact]
    public void InvalidMaxChapters_IsRejected()
    {
        Assert.Throws<CliError>(() => ParseFile("--max-chapters", "-1"));
        Assert.Throws<CliError>(() => ParseFile("--max-chapters", "many"));
    }

    [Fact]
    public void LiteralChapterPhrase_IsEscapedAndCaseInsensitive()
    {
        var o = ParseFile("-c", "part (a)")!;
        Assert.False(o.PhraseHasNumberGroup);
        Assert.Matches(o.PhraseRegex, "PART (A) two");
        Assert.DoesNotMatch(o.PhraseRegex, "part a");
    }

    [Fact]
    public void RegexChapterPhrase_WithCaptureGroup_IsDetected()
    {
        var o = ParseFile("-c", @"/chapter (\d+)/")!;
        Assert.True(o.PhraseHasNumberGroup);
        var m = o.PhraseRegex.Match("Chapter 12 begins");
        Assert.True(m.Success);
        Assert.Equal("12", m.Groups[1].Value);
    }

    [Fact]
    public void RegexChapterPhrase_WithoutGroup_HasNoNumberGroup()
    {
        var o = ParseFile("-c", @"/chapter/")!;
        Assert.False(o.PhraseHasNumberGroup);
    }

    [Fact]
    public void InvalidChapterPhraseRegex_IsAnError()
    {
        Assert.Throws<CliError>(() => ParseFile("-c", "/(unclosed/"));
    }

    [Fact]
    public void EmptyChapterPhrase_IsAnError()
    {
        Assert.Throws<CliError>(() => ParseFile("-c", ""));
    }
}
