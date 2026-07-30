// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

namespace ABChapterize.Errors;

/// <summary>
/// Exception thrown for any command line syntax or validation error: an unknown option, a value
/// that will not parse, or a combination of options that contradict each other. The message
/// describes the problem on its own terms; <see cref="ABChapterize.Cli.Program"/> prints it,
/// adds the pointer to <c>--help</c>, and exits 2.
/// <para>
/// Kept beside <see cref="AppError"/> rather than with the parser that throws it, because the
/// two are defined against each other: both stop the run with a message and no stack trace, and
/// the only difference - usage hint and exit 2, or no hint and exit 1 - is a statement about
/// whether the user's <em>command</em> was wrong or their <em>run</em> failed. That distinction
/// is easiest to keep straight when both are in one place.
/// </para>
/// </summary>
public sealed class CliError : Exception
{
    /// <summary>Creates a new command line error.</summary>
    /// <param name="message">Human readable description of the error.</param>
    public CliError(string message) : base(message) { }
}
