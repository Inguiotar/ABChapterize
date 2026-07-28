// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using System.Text;
using ABChapterize.Audio;
using ABChapterize.Cli;

namespace ABChapterize.Ui;

/// <summary>
/// The <c>--log-file</c> destination: the same log stream <see cref="ProgressRenderer"/> would
/// print with <c>--verbose</c>, written to a file instead.
/// <para>
/// Opened for appending rather than truncating. A log is written precisely because runs are long
/// and can end badly, and a second run silently erasing the record of the first is the one failure
/// a logging feature must not have; the run header line makes the appended runs easy to tell
/// apart. For the same reason every line is flushed as it is written - a log that only survives an
/// orderly shutdown would be missing exactly the run one wants to read afterwards.
/// </para>
/// <para>
/// Lines carry the full date, unlike the console's <c>HH:mm:ss</c>: the console log is read while
/// it scrolls past, this one is read days later, next to entries from other runs.
/// </para>
/// </summary>
public sealed class LogFile : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly Lock _lock = new();

    /// <summary>Takes ownership of an opened writer; use <see cref="Open"/>.</summary>
    /// <param name="writer">The opened, appending writer.</param>
    private LogFile(StreamWriter writer) => _writer = writer;

    /// <summary>
    /// Opens (or creates) the log file and writes the run's header line.
    /// </summary>
    /// <param name="path">Path given to <c>--log-file</c>.</param>
    /// <exception cref="AppError">Thrown when the file cannot be opened, so a run that was asked
    /// to keep a record fails at once rather than discovering it kept none.</exception>
    public static LogFile Open(string path)
    {
        try
        {
            var writer = new StreamWriter(path, append: true, new UTF8Encoding(false)) { AutoFlush = true };
            var log = new LogFile(writer);
            log.WriteRaw($"=== abchapterize {CliOptions.Version} run started {Timestamp()} ===");
            log.WriteRaw($"=== {Environment.CommandLine} ===");
            return log;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                       or ArgumentException)
        {
            throw new AppError($"Cannot write the log file \"{path}\": {ex.Message}");
        }
    }

    /// <summary>Appends one timestamped message.</summary>
    /// <param name="message">The message, without timestamp.</param>
    public void Write(string message) => WriteRaw($"[{Timestamp()}] {message}");

    /// <summary>Appends a line verbatim. Thread-safe: several files' workers log concurrently.</summary>
    /// <param name="line">The complete line to append.</param>
    private void WriteRaw(string line)
    {
        lock (_lock)
            _writer.WriteLine(line);
    }

    /// <summary>The log's timestamp format, local time like the console's.</summary>
    private static string Timestamp() => $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}";

    /// <summary>Writes the closing line and releases the file.</summary>
    public void Dispose()
    {
        WriteRaw($"=== run finished {Timestamp()} ===");
        _writer.Dispose();
    }
}
