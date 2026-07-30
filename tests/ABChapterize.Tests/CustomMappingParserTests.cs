// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Xunit;
using ABChapterize.Cli;
using ABChapterize.Errors;

namespace ABChapterize.Tests;

/// <summary>Tests for the <c>--custom</c> mapping syntax: where the delimiters are, which of them
/// a regexp may contain without being cut in half, and what a malformed mapping reports.</summary>
public sealed class CustomMappingParserTests
{
    [Fact]
    public void ParseSpec_SplitsSeveralMappingsOnSemicolons()
    {
        var mappings = CustomMappingParser.ParseSpec(
            "zwischenspiel:Zwischenspiel;/zeit[- ]?tafel/:Zeittafel");

        Assert.Equal(
            [new CustomMapping("zwischenspiel", "Zwischenspiel"),
             new CustomMapping("/zeit[- ]?tafel/", "Zeittafel")],
            mappings);
    }

    [Fact]
    public void ParseSpec_IgnoresEmptyEntries()
    {
        // A trailing or doubled separator is a typo, not a reason to fail the whole command line.
        Assert.Single(CustomMappingParser.ParseSpec("prelude:Prelude;;"));
    }

    [Fact]
    public void ParseSpec_TreatsOnlyTheFirstColonAsDelimiter()
    {
        var mapping = Assert.Single(CustomMappingParser.ParseSpec("time:Time: an interlude"));

        Assert.Equal(new CustomMapping("time", "Time: an interlude"), mapping);
    }

    [Fact]
    public void ParseSpec_LetsARegexpKeepItsOwnColons()
    {
        // The phrase ends at its closing slash, so this colon separates nothing.
        var mapping = Assert.Single(CustomMappingParser.ParseSpec("/act:scene/:Scene"));

        Assert.Equal(new CustomMapping("/act:scene/", "Scene"), mapping);
    }

    [Fact]
    public void ParseSpec_LetsAnEscapedSemicolonStayInsideARegexp()
    {
        var mapping = Assert.Single(CustomMappingParser.ParseSpec(@"/a\;b/:Both"));

        Assert.Equal(new CustomMapping(@"/a;b/", "Both"), mapping);
    }

    [Fact]
    public void ParseSpec_LeavesOrdinaryBackslashEscapesAlone()
    {
        // Only "\;" is consumed by the splitter; a regexp's own "\d" must survive it untouched.
        var mapping = Assert.Single(CustomMappingParser.ParseSpec(@"/part \d+/:Part"));

        Assert.Equal(@"/part \d+/", mapping.Phrase);
    }

    [Fact]
    public void ParseSpec_TrimsSurroundingWhitespace()
    {
        var mapping = Assert.Single(CustomMappingParser.ParseSpec("  prelude : Prelude  "));

        Assert.Equal(new CustomMapping("prelude", "Prelude"), mapping);
    }

    [Theory]
    [InlineData("prelude")]
    [InlineData(":Prelude")]
    [InlineData("prelude:")]
    [InlineData("/unterminated:Prelude")]
    [InlineData("/regexp/ :Prelude")]
    public void ParseSpec_RejectsAMalformedMapping(string spec)
        => Assert.Throws<CliError>(() => CustomMappingParser.ParseSpec(spec));

    [Fact]
    public void ParseFile_ReadsOneMappingPerLine_SkippingBlanksAndComments()
    {
        var path = Path.Combine(Path.GetTempPath(), $"abchapterize-custom-{Guid.NewGuid():N}.txt");
        File.WriteAllLines(path, [
            "# structural elements",
            "",
            "zwischenspiel:Zwischenspiel",
            "   ",
            "/zeit[- ]?tafel/:Zeittafel",
        ]);
        try
        {
            Assert.Equal(
                [new CustomMapping("zwischenspiel", "Zwischenspiel"),
                 new CustomMapping("/zeit[- ]?tafel/", "Zeittafel")],
                CustomMappingParser.ParseFile(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ParseFile_LeavesSemicolonsAlone()
    {
        // Line breaks separate the mappings here, so a regexp needs no escape for its semicolon.
        var path = Path.Combine(Path.GetTempPath(), $"abchapterize-custom-{Guid.NewGuid():N}.txt");
        File.WriteAllLines(path, ["/a;b/:Both"]);
        try
        {
            Assert.Equal("/a;b/", Assert.Single(CustomMappingParser.ParseFile(path)).Phrase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ParseFile_RejectsAMissingFile()
        => Assert.Throws<CliError>(() => CustomMappingParser.ParseFile(
            Path.Combine(Path.GetTempPath(), $"abchapterize-absent-{Guid.NewGuid():N}.txt")));

    [Fact]
    public void ParseFile_RejectsAFileWithoutASingleMapping()
    {
        var path = Path.Combine(Path.GetTempPath(), $"abchapterize-custom-{Guid.NewGuid():N}.txt");
        File.WriteAllLines(path, ["# nothing but a comment"]);
        try
        {
            Assert.Throws<CliError>(() => CustomMappingParser.ParseFile(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
