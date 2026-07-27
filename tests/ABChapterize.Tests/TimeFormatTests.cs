// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Xunit;
using ABChapterize.Formatting;

namespace ABChapterize.Tests;

/// <summary>
/// Tests for <see cref="TimeFormat"/>, the shared duration/position formatter. The cases past
/// 24 hours are the reason it exists: TimeSpan's own "h" specifier prints the hours component of
/// a day and drops the rest, so a 37-hour batch used to report itself as a 13-hour one.
/// </summary>
public class TimeFormatTests
{
    [Theory]
    [InlineData(0, "0:00:00")]
    [InlineData(59, "0:00:59")]
    [InlineData(3600, "1:00:00")]
    [InlineData(86399, "23:59:59")]
    [InlineData(86400, "24:00:00")]
    [InlineData(133855, "37:10:55")]
    public void Hms_CountsHoursCumulatively_PastAFullDay(double seconds, string expected)
        => Assert.Equal(expected, TimeFormat.Hms(seconds));

    [Theory]
    [InlineData(90061.25, "25:01:01.25")]
    [InlineData(0.5, "0:00:00.50")]
    public void Hms_AppendsHundredths_WhenAskedForTwoDigits(double seconds, string expected)
        => Assert.Equal(expected, TimeFormat.Hms(seconds, 2));

    [Theory]
    [InlineData(90061.125, "25:01:01.125")]
    [InlineData(1.5, "0:00:01.500")]
    public void Hms_AppendsMilliseconds_WhenAskedForThreeDigits(double seconds, string expected)
        => Assert.Equal(expected, TimeFormat.Hms(seconds, 3));

    [Fact]
    public void Hms_TruncatesTheFraction_RatherThanRoundingItUp()
    {
        // "ff"/"fff" truncate, and the sidecar's round-trip depends on the written value never
        // landing past the mark it came from.
        Assert.Equal("0:00:01.99", TimeFormat.Hms(1.999, 2));
        Assert.Equal("0:00:01.999", TimeFormat.Hms(1.9999, 3));
        // Truncation plus binary floating point: 1.007 is really 1.006999..., so it renders .006.
        // Unchanged from the "fff" specifier this replaced, and irrelevant at a millisecond's
        // worth of chapter-mark accuracy - pinned here so the behaviour is not mistaken for a bug.
        Assert.Equal("0:00:01.006", TimeFormat.Hms(1.007, 3));
    }

    [Fact]
    public void Hms_ClampsNegativeInputToZero()
        => Assert.Equal("0:00:00.00", TimeFormat.Hms(-5, 2));

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(59, "0:59")]
    [InlineData(600, "10:00")]
    [InlineData(3599, "59:59")]
    [InlineData(3600, "1:00:00")]
    [InlineData(133855, "37:10:55")]
    public void Duration_DropsTheHoursBelowOne_AndKeepsThemCumulativeAbove(double seconds, string expected)
        => Assert.Equal(expected, TimeFormat.Duration(TimeSpan.FromSeconds(seconds)));
}
