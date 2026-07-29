// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Xunit;
using ABChapterize.Ui;

namespace ABChapterize.Tests;

/// <summary>
/// Tests for <see cref="ConsoleColors"/>. The detection itself is deliberately not asserted for
/// <see cref="ColorMode.Auto"/> - it reads the ambient console and environment, so its answer is
/// a property of whatever runs the test, not of the code. What is asserted is that the two
/// explicit modes override that detection unconditionally, which is the whole point of offering
/// them, and that the span helpers measure and cut text the way the renderer relies on.
/// </summary>
public class ConsoleColorsTests
{
    [Fact]
    public void ExplicitModes_OverrideDetection()
    {
        Assert.True(ConsoleColors.ShouldColorize(ColorMode.Always));
        Assert.False(ConsoleColors.ShouldColorize(ColorMode.Never));
    }

    [Theory]
    [InlineData("xterm-256color", true)]
    [InlineData("screen-256color", true)]
    [InlineData("xterm-16color", true)]
    [InlineData("xterm-direct", true)]
    // Plain "xterm" and friends advertise eight colors in terminfo, which folds the palette's
    // dark grey down to black; see ConsoleColors' remarks for the measurement behind this.
    [InlineData("xterm", false)]
    [InlineData("screen", false)]
    [InlineData("linux", false)]
    [InlineData("dumb", false)]
    [InlineData("", false)]
    public void AutoMode_OnUnix_NeedsEvidenceOfASixteenColorTerminal(string term, bool expected)
    {
        var old = Environment.GetEnvironmentVariable("TERM");
        try
        {
            Environment.SetEnvironmentVariable("TERM", term);
            Assert.Equal(expected, ConsoleColors.TerminalNamesColorSupport());
        }
        finally
        {
            Environment.SetEnvironmentVariable("TERM", old);
        }
    }

    [Fact]
    public void AutoMode_IgnoresColorTerm_BecauseTerminfoIsWhatClampsThePalette()
    {
        // A capable terminal behind an 8-color terminfo entry still renders the palette wrong,
        // so its COLORTERM boast must not talk the detection into colorizing anyway.
        var oldTerm = Environment.GetEnvironmentVariable("TERM");
        var oldColorTerm = Environment.GetEnvironmentVariable("COLORTERM");
        try
        {
            Environment.SetEnvironmentVariable("TERM", "xterm");
            Environment.SetEnvironmentVariable("COLORTERM", "truecolor");
            Assert.False(ConsoleColors.TerminalNamesColorSupport());
        }
        finally
        {
            Environment.SetEnvironmentVariable("TERM", oldTerm);
            Environment.SetEnvironmentVariable("COLORTERM", oldColorTerm);
        }
    }

    [Fact]
    public void PlainText_IsTheVisibleTextOfAllSpans()
    {
        List<ColoredSpan> spans = [new("[", ConsoleColor.DarkGray), new("##", null), new("]", ConsoleColor.DarkGray)];
        Assert.Equal("[##]", ConsoleColors.PlainText(spans));
    }

    [Fact]
    public void Truncate_CutsInsideTheSpanTheLimitFallsIn()
    {
        // The cut lands in the middle of the second span: it must be shortened rather than
        // dropped, or the line would lose visible width and stop matching its own plain text.
        List<ColoredSpan> spans = [new("abc", ConsoleColor.Red), new("defgh", ConsoleColor.Blue)];
        var cut = ConsoleColors.Truncate(spans, 5);

        Assert.Equal("abcde", ConsoleColors.PlainText(cut));
        Assert.Equal(2, cut.Count);
        Assert.Equal(ConsoleColor.Blue, cut[1].Color);
    }

    [Fact]
    public void Truncate_DropsSpansPastTheLimitEntirely()
    {
        List<ColoredSpan> spans = [new("abc", null), new("def", null), new("ghi", null)];
        Assert.Equal("abc", ConsoleColors.PlainText(ConsoleColors.Truncate(spans, 3)));
        Assert.Empty(ConsoleColors.Truncate(spans, 0));
    }

    [Fact]
    public void Truncate_LeavesAShortLineAlone()
    {
        List<ColoredSpan> spans = [new("abc", ConsoleColor.Red), new("de", null)];
        Assert.Equal(spans, ConsoleColors.Truncate(spans, 80));
    }
}
