// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Xunit;
using ABChapterize.Ui;

namespace ABChapterize.Tests;

/// <summary>
/// Tests for <see cref="SummaryHighlighter"/>. Since it recognizes values by pattern rather than
/// being told where they are, the interesting cases are all about where a value ends: the unit
/// belongs to it, the following word never does.
/// </summary>
public class SummaryHighlighterTests
{
    /// <summary>Reduces a highlighted line to (text, color) pairs for comparison.</summary>
    private static List<(string, ConsoleColor?)> Spans(string line)
        => [.. SummaryHighlighter.Highlight(line).Select(s => (s.Text, s.Color))];

    /// <summary>The single span covering <paramref name="text"/>, which must exist exactly once.</summary>
    private static ColoredSpan Span(string line, string text)
        => SummaryHighlighter.Highlight(line).Single(s => s.Text == text);

    [Fact]
    public void Highlight_LeavesTheLineItselfUnchanged()
    {
        // Colors are applied at write time, so the spans must reproduce the line exactly - a
        // highlighter that dropped or duplicated a character would corrupt the output silently.
        const string line = "Summary: 3 file(s) encountered, 2 processed, 1 skipped, 1 with warnings";
        Assert.Equal(line, ConsoleColors.PlainText(SummaryHighlighter.Highlight(line)));
    }

    [Fact]
    public void Highlight_PutsProseInWhite_BracketsInDarkGray_AndValuesInCyan()
    {
        Assert.Equal(
            [("Summary: ", ConsoleColor.White),
             ("3", ConsoleColor.Cyan),
             (" file", ConsoleColor.White),
             ("(", ConsoleColor.DarkGray),
             ("s", ConsoleColor.White),
             (")", ConsoleColor.DarkGray),
             (" encountered", ConsoleColor.White)],
            Spans("Summary: 3 file(s) encountered"));
    }

    [Theory]
    // A unit is part of the value it belongs to...
    [InlineData("Shortest silence before a chapter 1.52 s", "1.52 s")]
    [InlineData("Whisper audio processed: 12:34 of 5:43:21 run length (3.7%)", "3.7%")]
    [InlineData("transcription speed 412% of real-time", "412%")]
    [InlineData("longest jingle before a chapter 940 ms", "940 ms")]
    public void Highlight_TakesTheUnitIntoTheValue(string line, string expected)
        => Assert.Equal(ConsoleColor.Cyan, Span(line, expected).Color);

    [Theory]
    // ...but a following word never is, however much it looks like one. These two are exactly why
    // the unit is an allowlist rather than "whatever follows the number".
    [InlineData("Summary: 3 file(s) encountered", "3", " file")]
    [InlineData("marks placed 0.35 seconds before the announcement", "0.35", " seconds before the announcement")]
    public void Highlight_NeverSwallowsTheFollowingWord(string line, string value, string prose)
    {
        Assert.Equal(ConsoleColor.Cyan, Span(line, value).Color);
        Assert.Equal(ConsoleColor.White, Span(line, prose).Color);
    }

    [Theory]
    // Durations and decimals stay whole rather than splitting around their punctuation.
    [InlineData("Total time: 1:23:45", "1:23:45")]
    [InlineData("Total time: 41:52", "41:52")]
    [InlineData("Confidence of written chapter marks: min 0.87", "0.87")]
    public void Highlight_KeepsAMultiPartNumberInOneSpan(string line, string expected)
        => Assert.Equal(ConsoleColor.Cyan, Span(line, expected).Color);

    [Fact]
    public void Highlight_ColorsANestedValueWithoutItsBrackets()
    {
        // "(inter-chapter 1.84 s)": the brackets are structure, the value inside is still a value.
        var spans = Spans("Shortest silence before a chapter 1.52 s (inter-chapter 1.84 s)");
        Assert.Equal(
            [("Shortest silence before a chapter ", ConsoleColor.White),
             ("1.52 s", ConsoleColor.Cyan),
             (" ", ConsoleColor.White),
             ("(", ConsoleColor.DarkGray),
             ("inter-chapter ", ConsoleColor.White),
             ("1.84 s", ConsoleColor.Cyan),
             (")", ConsoleColor.DarkGray)],
            spans);
    }

    [Fact]
    public void Highlight_HandlesALineWithoutAnythingToHighlight()
    {
        var spans = Spans("Total time: unknown");
        Assert.Equal([("Total time: unknown", ConsoleColor.White)], spans);
    }
}
