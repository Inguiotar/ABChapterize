// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using System.Text;

namespace ABChapterize.Ui;

/// <summary>When the tool colorizes its console output (--color).</summary>
public enum ColorMode
{
    /// <summary>Colorize when the console looks capable of it - see <see cref="ConsoleColors.ShouldColorize"/>.</summary>
    Auto,

    /// <summary>Colorize regardless of what the detection concludes.</summary>
    Always,

    /// <summary>Never colorize.</summary>
    Never,
}

/// <summary>
/// One run of text drawn in a single foreground color, or in the terminal's default color when
/// <paramref name="Color"/> is null.
/// </summary>
/// <remarks>
/// Colors are applied at write time rather than baked into the text as escape sequences, which
/// keeps every string the renderer measures - for width truncation and for the "has this line
/// changed?" comparison - a plain run of visible characters. Escape sequences are zero-width but
/// not zero-length, so mixing them into those strings would silently corrupt both.
/// </remarks>
/// <param name="Text">The visible text of this span.</param>
/// <param name="Color">Foreground color to draw it in, or null for the terminal's default.</param>
public readonly record struct ColoredSpan(string Text, ConsoleColor? Color);

/// <summary>
/// Decides whether console output may be colorized, and provides the small span helpers the
/// renderers need to measure and cut colored text.
/// </summary>
/// <remarks>
/// <para>
/// Everything here stays within the 16 colors of <see cref="ConsoleColor"/>, which is what makes
/// the whole feature portable without a single line of platform-specific code: .NET implements
/// <see cref="Console.ForegroundColor"/> through <c>SetConsoleTextAttribute</c> on Windows and
/// through the terminfo entry for <c>$TERM</c> on Unix. In particular this needs no
/// <c>ENABLE_VIRTUAL_TERMINAL_PROCESSING</c> handshake, so it works on the older Windows consoles
/// too - the VT handshake would only become necessary for 256-color or truecolor output.
/// </para>
/// <para>
/// There is no way to *ask* a terminal whether it does color; every answer below is an inference.
/// The rules are the ones the CLI ecosystem has converged on, and each exists for a concrete
/// failure: a redirected stream would collect escape sequences in a file or a pipe someone is
/// grepping; <c>NO_COLOR</c> (no-color.org) is the user's standing "I do not want this anywhere"
/// and is honored by convention; <c>TERM=dumb</c> (or no <c>TERM</c> at all) means .NET has no
/// terminfo entry to translate a color into in the first place.
/// </para>
/// <para>
/// The last Unix rule - requiring evidence of a 16-color terminfo entry - is the one that is not
/// standard practice elsewhere, and it exists because of a measured failure rather than a
/// theoretical one. .NET clamps a color into the range the terminfo entry advertises, and the
/// stock <c>xterm</c> entry still advertises <c>colors#8</c> (verified 2026-07-29 on Debian under
/// WSL2, against <c>xterm-256color</c>'s <c>colors#0x100</c>). Under it the whole upper half of
/// <see cref="ConsoleColor"/> folds down: <see cref="ConsoleColor.DarkGray"/> - the progress bar's
/// brackets and separators - emits <c>SGR 30</c>, plain black, and disappears on the black
/// background such a terminal almost always has, while <see cref="ConsoleColor.Cyan"/> and
/// <see cref="ConsoleColor.DarkCyan"/> collapse onto each other. Drawing no color at all is the
/// better of those two outcomes. Most such terminals would in fact render <c>SGR 90</c> correctly
/// and only their terminfo entry is behind - but the entry is what .NET translates through, so
/// their capability does not help; <c>--color always</c> is what those get.
/// </para>
/// <para>
/// The known false negative is Git Bash/MSYS on Windows: a native console app's stdout there
/// often looks like a pipe, so the redirection check turns color off in a terminal that would
/// have rendered it fine. That is what <c>--color always</c> is for, and the same escape hatch
/// covers CI systems, which redirect output and render ANSI anyway.
/// </para>
/// </remarks>
public static class ConsoleColors
{
    /// <summary>Decides whether output in the given mode may be colorized.</summary>
    /// <param name="mode">The mode requested on the command line.</param>
    public static bool ShouldColorize(ColorMode mode) => mode switch
    {
        ColorMode.Always => true,
        ColorMode.Never => false,
        _ => DetectSupport(),
    };

    /// <summary>Infers whether the current console renders colors; see the class remarks for
    /// why each of these checks is here and what it costs to get wrong.</summary>
    private static bool DetectSupport()
    {
        if (Console.IsOutputRedirected)
            return false;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR")))
            return false;
        // Windows skips the terminal check: its console API carries all 16 colors unconditionally,
        // with no terminfo entry in between to understate them.
        return OperatingSystem.IsWindows() || TerminalNamesColorSupport();
    }

    /// <summary>
    /// Decides from <c>$TERM</c> whether the Unix terminal has a terminfo entry with at least 16
    /// colors in it - see the class remarks for what an 8-color entry does to the palette. Judged
    /// from the entry's name rather than by reading terminfo, since .NET exposes no way to ask it
    /// for the color count. Internal for unit testing, which cannot go through
    /// <see cref="ShouldColorize"/> for this: the test host redirects stdout, and that vetoes
    /// color before <c>$TERM</c> is ever looked at.
    /// </summary>
    /// <remarks>
    /// <c>$COLORTERM</c> is pointedly *not* consulted, though most CLIs do trust it. It describes
    /// the terminal, and the terminal is not the constraint: .NET translates a color through the
    /// terminfo entry <c>$TERM</c> names and clamps it there. Measured 2026-07-29,
    /// <c>COLORTERM=truecolor TERM=xterm</c> renders the bar's dark grey as <c>SGR 30</c>, plain
    /// black - i.e. honoring <c>$COLORTERM</c> produces exactly the unreadable output this check
    /// exists to prevent, on a terminal that is genuinely capable but described by an entry that
    /// is not.
    /// </remarks>
    internal static bool TerminalNamesColorSupport()
    {
        var term = Environment.GetEnvironmentVariable("TERM");
        if (string.IsNullOrEmpty(term) || term == "dumb")
            return false;
        return term.Contains("256color", StringComparison.Ordinal)
            || term.Contains("16color", StringComparison.Ordinal)
            || term.Contains("truecolor", StringComparison.Ordinal)
            || term.Contains("direct", StringComparison.Ordinal);
    }

    /// <summary>Joins the spans into the plain text they render as, i.e. the string whose length
    /// is the line's real width on screen.</summary>
    /// <param name="spans">The spans making up one line.</param>
    public static string PlainText(IReadOnlyList<ColoredSpan> spans)
    {
        var text = new StringBuilder();
        foreach (var span in spans)
            text.Append(span.Text);
        return text.ToString();
    }

    /// <summary>
    /// Cuts a line of spans down to at most <paramref name="width"/> visible characters, splitting
    /// the span the cut lands in rather than dropping it whole.
    /// </summary>
    /// <param name="spans">The spans making up one line.</param>
    /// <param name="width">Maximum number of visible characters to keep.</param>
    public static List<ColoredSpan> Truncate(IReadOnlyList<ColoredSpan> spans, int width)
    {
        var kept = new List<ColoredSpan>(spans.Count);
        var remaining = Math.Max(0, width);
        foreach (var span in spans)
        {
            if (remaining == 0)
                break;
            if (span.Text.Length <= remaining)
            {
                kept.Add(span);
                remaining -= span.Text.Length;
            }
            else
            {
                kept.Add(span with { Text = span.Text[..remaining] });
                remaining = 0;
            }
        }
        return kept;
    }
}
