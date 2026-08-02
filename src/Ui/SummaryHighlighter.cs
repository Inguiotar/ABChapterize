// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using System.Text.RegularExpressions;

namespace ABChapterize.Ui;

/// <summary>
/// One piece of a <c>--summary</c> line whose kind the caller already knows, so that
/// <see cref="SummaryHighlighter"/> need not guess it. Only book titles need this: a file name is
/// arbitrary text, and left to the pattern rules "Der Fall (Teil 2).m4b" would come out with grey
/// brackets and a cyan "2" embedded in the middle of the name.
/// </summary>
/// <param name="Text">The visible text of this piece.</param>
/// <param name="IsTitle">Whether it is a book title, drawn whole in one color.</param>
public readonly record struct SummarySegment(string Text, bool IsTitle)
{
    /// <summary>Ordinary summary prose, colorized by pattern like any other line.</summary>
    /// <param name="text">The text of the piece.</param>
    public static SummarySegment Prose(string text) => new(text, false);

    /// <summary>A book title, drawn whole in its own color.</summary>
    /// <param name="text">The title, normally an audiobook's file name.</param>
    public static SummarySegment Title(string text) => new(text, true);
}

/// <summary>
/// Colorizes a finished <c>--summary</c> line: prose in white, brackets in dark grey, every
/// measured value in cyan together with the unit it is quoted in, and a book title - the one thing
/// the caller has to point out, as a <see cref="SummarySegment"/> - in dark cyan.
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

    /// <summary>Book titles, matching the phase name the progress bar draws for the same file.</summary>
    private const ConsoleColor Titles = ConsoleColor.DarkCyan;

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
        => HighlightSegments([SummarySegment.Prose(line)]);

    /// <summary>
    /// Splits a summary line assembled from known pieces into its colored spans. Not an overload of
    /// <see cref="Highlight"/>: a <c>cref</c> to an overloaded method no longer resolves to one
    /// method, which the documentation build reports as CS0419.
    /// </summary>
    /// <param name="segments">The pieces of one finished summary line, in print order.</param>
    public static List<ColoredSpan> HighlightSegments(IReadOnlyList<SummarySegment> segments)
    {
        var spans = new List<ColoredSpan>();
        foreach (var segment in segments)
        {
            if (segment.IsTitle)
                spans.Add(new(segment.Text, Titles));
            else
                AppendProse(spans, segment.Text);
        }
        return spans;
    }

    /// <summary>Applies the pattern rules to one stretch of ordinary summary prose.</summary>
    /// <param name="spans">The line's spans so far, appended to.</param>
    /// <param name="line">The prose to colorize.</param>
    private static void AppendProse(List<ColoredSpan> spans, string line)
    {
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
    }
}
