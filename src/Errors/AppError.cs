// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

namespace ABChapterize.Errors;

/// <summary>
/// Exception for fatal application errors that abort the whole run: the message is printed to
/// stderr without a stack trace and the process exits 1 (see
/// <see cref="ABChapterize.Cli.Program"/>'s top-level handler). Throwing this rather than a
/// framework exception is the assertion that the message is fit for a user to read - an ffmpeg
/// that cannot be found, a model that will not download, a log file that cannot be written -
/// where anything else reaching that handler is reported as a bug, prefixed with its type name.
/// <para>
/// Lives in a namespace of its own because every layer throws it: audio, transcription, VAD,
/// detection, processing and the UI all do, and belonging to any one of them would make the
/// other five depend on that one for no reason. It used to sit at the bottom of
/// <c>FfmpegLocator.cs</c>, which made <c>using ABChapterize.Audio;</c> a line that files with
/// nothing to do with audio had to carry.
/// </para>
/// </summary>
public sealed class AppError : Exception
{
    /// <summary>Creates a new fatal application error.</summary>
    /// <param name="message">Human readable description of the error.</param>
    public AppError(string message) : base(message) { }
}
