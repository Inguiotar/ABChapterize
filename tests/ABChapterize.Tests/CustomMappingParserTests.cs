// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Xunit;
using ABChapterize.Cli;
using ABChapterize.Errors;
using ABChapterize.Language;

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
    public void ParseSpec_ReadsALanguageTagAsItAlwaysDid()
    {
        var mapping = Assert.Single(CustomMappingParser.ParseSpec("[de]vorwort:Vorwort"));

        Assert.Equal("de", mapping.Language);
        Assert.Equal("vorwort", mapping.Phrase);
        Assert.False(mapping.Tag!.Value.HasHints);
    }

    [Fact]
    public void ParseSpec_ReadsHintsBesideTheLanguage()
    {
        var mapping = Assert.Single(
            CustomMappingParser.ParseSpec("[de,before-first-chapter,once]/^vorwort/:Vorwort"));
        var tag = mapping.Tag!.Value;

        Assert.Equal("de", tag.Language);
        Assert.Equal(NamedPhraseScope.BeforeFirstChapter, tag.Scope);
        Assert.True(tag.Once);
        // The pause in front is not a hint: it rides in the phrase, untouched by the tag parser.
        Assert.Equal("/^vorwort/", mapping.Phrase);
    }

    /// <summary>The short aliases mean the same as the long forms, which are what the docs use.</summary>
    /// <param name="keyword">The keyword to write in the tag.</param>
    /// <param name="expected">The scope it must resolve to.</param>
    [Theory]
    [InlineData("before-first-chapter", NamedPhraseScope.BeforeFirstChapter)]
    [InlineData("before-first", NamedPhraseScope.BeforeFirstChapter)]
    [InlineData("after-first-chapter", NamedPhraseScope.AfterFirstChapter)]
    [InlineData("after-first", NamedPhraseScope.AfterFirstChapter)]
    [InlineData("after-last-chapter", NamedPhraseScope.AfterLastChapter)]
    [InlineData("after-last", NamedPhraseScope.AfterLastChapter)]
    public void ParseSpec_AcceptsBothSpellingsOfEveryPosition(string keyword, NamedPhraseScope expected)
        => Assert.Equal(
            expected,
            Assert.Single(CustomMappingParser.ParseSpec($"[{keyword}]interlude:Interlude"))
                .Tag!.Value.Scope);

    /// <summary>
    /// The rule that keeps Whisper's own bracketed non-speech tags usable as phrases: a bracket run
    /// with nothing recognizable in it is phrase text, not a tag. Measured over one 16-book corpus,
    /// [Musik] alone appears 259 times in the transcripts, so this is a mapping somebody will write.
    /// </summary>
    /// <param name="spec">The mapping to parse.</param>
    /// <param name="phrase">The phrase it must come out with, brackets included.</param>
    [Theory]
    [InlineData("[Musik]:Zwischenmusik", "[Musik]")]
    [InlineData("[BLANK_AUDIO]:Stille", "[BLANK_AUDIO]")]
    [InlineData("[Aufregende Musik]:Musik", "[Aufregende Musik]")]
    public void ParseSpec_LeavesAnUnrecognizedBracketRunInThePhrase(string spec, string phrase)
    {
        var mapping = Assert.Single(CustomMappingParser.ParseSpec(spec));

        Assert.Equal(phrase, mapping.Phrase);
        Assert.Null(mapping.Language);
        Assert.Null(mapping.Tag);
    }

    /// <summary>
    /// The other side of that line: one good token makes the run a tag, so a typo beside it is an
    /// error rather than silently becoming part of the phrase.
    /// </summary>
    /// <param name="spec">The malformed mapping.</param>
    [Theory]
    [InlineData("[once,headnig]interlude:Interlude")]
    [InlineData("[de,en]interlude:Interlude")]
    [InlineData("[before-first,after-last]interlude:Interlude")]
    [InlineData("[max=abc]interlude:Interlude")]
    [InlineData("[max=0]interlude:Interlude")]
    [InlineData("[max=1]interlude:Interlude")]
    [InlineData("[max=2,max=3]interlude:Interlude")]
    public void ParseSpec_RejectsAMalformedTag(string spec)
        => Assert.Throws<CliError>(() => CustomMappingParser.ParseSpec(spec));

    [Fact]
    public void ParseSpec_ReadsAPerMappingMarkCap()
        => Assert.Equal(
            3,
            Assert.Single(CustomMappingParser.ParseSpec("[max=3]interlude:Interlude"))
                .Tag!.Value.MaxMarks);

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
