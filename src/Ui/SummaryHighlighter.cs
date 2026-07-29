// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using System.Text.RegularExpressions;

namespace ABChapterize.Ui;

/// <summary>
/// Colorizes a finished <c>--summary</c> line: prose in white, brackets in dark grey, and every
/// measured value in cyan together with the unit it is quoted in.
/// </summary>
/// <remarks>
/// This works on the assembled line rather than being woven into the code that builds it, which
/// keeps <see cref="ABChapterize.Processing.RunStatistics"/> free of any notion of color and means
/// a new statistic is colorized correctly the moment it is added, with nothing to remember. The
/// price is that the numbers are recognized by pattern, so the unit is matched from a deliberately
/// tiny allowlist - <c>%</c> and <c>s</c>/<c>ms</c> - rather than "whatever word follows". Anything
/// broader swallows the following word: "3 file(s)" and "0.35 seconds" both read as a number
/// followed by prose, and only the exact-unit rule keeps "file" and "seconds" out of the value.
/// </remarks>
public static partial class SummaryHighlighter
{
    /// <summary>Prose - the summary's baseline color.</summary>
    private const ConsoleColor Text = ConsoleColor.White;

    /// <summary>Brackets, matching the progress bar's treatment of them as structure.</summary>
    private const ConsoleColor Brackets = ConsoleColor.DarkGray;

    /// <summary>Measured values, matching the bar's percentage and timer.</summary>
    private const ConsoleColor Numbers = ConsoleColor.Cyan;

    /// <summary>
    /// Matches one bracket, or one number with its unit. A number is a digit run that may carry
    /// further groups after a "." or ":", so both a decimal ("0.87") and a duration ("1:23:45")
    /// come out as a single value rather than as digits split around punctuation.
    /// </summary>
    [GeneratedRegex(@"[()\[\]]|\d+(?:[.:]\d+)*(?:\s*%|\s+m?s\b)?", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();

    /// <summary>Splits a summary line into its colored spans.</summary>
    /// <param name="line">The finished summary line.</param>
    public static List<ColoredSpan> Highlight(string line)
    {
        var spans = new List<ColoredSpan>();
        var pos = 0;
        foreach (var token in TokenRegex().EnumerateMatches(line))
        {
            if (token.Index > pos)
                spans.Add(new(line[pos..token.Index], Text));
            var text = line.Substring(token.Index, token.Length);
            spans.Add(new(text, char.IsAsciiDigit(text[0]) ? Numbers : Brackets));
            pos = token.Index + token.Length;
        }
        if (pos < line.Length)
            spans.Add(new(line[pos..], Text));
        return spans;
    }
}
