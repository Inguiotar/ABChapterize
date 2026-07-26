// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using System.Globalization;
using System.Runtime.CompilerServices;

namespace ABChapterize.Cli;

/// <summary>
/// Fixes how this tool reads and writes numbers, independently of the machine's regional
/// settings: every number it <i>prints</i> uses "." as the decimal separator, and every number it
/// <i>accepts</i> on the command line may be written with either "." or ",".
/// <para>
/// The asymmetry is deliberate. Output is read by logs, diffs, scripts and bug reports as much as
/// by people, and a figure that silently changes shape with the machine's locale ("p=0.99" on one,
/// "p=0,99" on another) is no use in any of those; it also matches the numbers ffmpeg and the
/// sidecar format already exchange, which have always been invariant. Input, by contrast, is typed
/// by a person who will reasonably use whatever separator their keyboard and habits produce, and
/// there is no ambiguity to resolve in doing so: this tool has no notion of thousands separators
/// at all - it never groups digits on output and never reads a "," as a group separator - so "1,5"
/// can only ever mean one and a half.
/// </para>
/// <para>
/// Localizing output properly - translated messages, locale-formatted numbers throughout - is a
/// separate undertaking; until then this keeps the tool's numbers predictable rather than
/// accidentally locale-dependent.
/// </para>
/// </summary>
internal static class NumberCulture
{
    /// <summary>
    /// Switches all default number/date formatting in this process over to the invariant culture,
    /// so plain interpolation (<c>$"{seconds:0.00}"</c>) prints "." throughout without every call
    /// site having to name a culture. Runs automatically at assembly load - as a module
    /// initializer rather than from <see cref="Program.Main"/>, so the unit test assembly (which
    /// compiles these same sources) gets the identical behavior and can therefore assert on the
    /// real output format.
    /// </summary>
    [ModuleInitializer]
    internal static void UseInvariantNumberFormatting()
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CurrentThreadOverride();
    }

    /// <summary>The module initializer runs on a thread that may already have captured the
    /// machine's culture, and <see cref="CultureInfo.DefaultThreadCurrentCulture"/> only seeds
    /// threads that have not; this covers that one thread too.</summary>
    private static void CurrentThreadOverride()
        => CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

    /// <summary>
    /// Parses a command line decimal value, accepting "," as well as "." for the decimal point
    /// (see this class's remarks). Thousands separators are not accepted in either notation - a
    /// "," is always read as the decimal point, never as a group separator.
    /// </summary>
    /// <param name="value">The raw option value as typed.</param>
    /// <param name="result">The parsed value, or 0 when this returns false.</param>
    /// <returns>True when <paramref name="value"/> is a well-formed number.</returns>
    internal static bool TryParseDecimal(string value, out double result)
        => double.TryParse(
            value.Replace(',', '.'),
            NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out result);
}
