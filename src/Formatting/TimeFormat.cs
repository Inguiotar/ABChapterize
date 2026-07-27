// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

namespace ABChapterize.Formatting;

/// <summary>
/// The one place durations and positions-in-a-file are turned into text.
/// <para>
/// Everything here counts hours cumulatively, and that is the entire point of the class
/// existing. <see cref="TimeSpan"/>'s own <c>"h"</c> format specifier is the hours
/// <em>component</em> of a day, so <c>TimeSpan.FromHours(37).ToString(@"h\:mm\:ss")</c> is
/// "13:00:00" - the day is not rounded, not truncated, not reported anywhere, just gone.
/// Nothing this tool prints wants that: a batch's total run length routinely passes 24 hours,
/// and an omnibus audiobook can too, so a mark at 25:00:00 must never be written as 1:00:00.
/// </para>
/// </summary>
public static class TimeFormat
{
    /// <summary>
    /// Formats a position in a file as <c>H:MM:SS</c>, optionally with a fractional part, with
    /// hours counted from the start rather than wrapping at 24. Negative input is clamped to
    /// zero: a mark before the file start is not a thing, and "-0:00:01" would only ever be a
    /// rounding artifact rendered as noise.
    /// </summary>
    /// <param name="seconds">The position in seconds.</param>
    /// <param name="fractionDigits">Digits of a second to append (0 for none). Truncated, not
    /// rounded, matching what the <c>"ff"</c>/<c>"fff"</c> specifiers this replaced did.</param>
    public static string Hms(double seconds, int fractionDigits = 0)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        var text = $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";
        return fractionDigits switch
        {
            2 => $"{text}.{t.Milliseconds / 10:00}",
            3 => $"{text}.{t.Milliseconds:000}",
            _ => text,
        };
    }

    /// <summary>
    /// Formats a length of time as <c>H:MM:SS</c>, or as <c>M:SS</c> when it is under an hour -
    /// the summary's own house style, where most values are short and a leading "0:" would only
    /// be noise. Hours are cumulative, as everywhere in this class.
    /// </summary>
    /// <param name="t">The duration.</param>
    public static string Duration(TimeSpan t)
        => t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes}:{t.Seconds:00}";
}
