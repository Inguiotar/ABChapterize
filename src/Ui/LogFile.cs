// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using System.Text;
using ABChapterize.Cli;
using ABChapterize.Errors;

namespace ABChapterize.Ui;

/// <summary>
/// The <c>--log-file</c> destination: the same log stream <see cref="ProgressRenderer"/> would
/// print with <c>--verbose</c>, written to a file instead. Also the I/O behind
/// <see cref="DebugLog"/>, which opens it with <c>append: false</c>.
/// <para>
/// A <c>--log-file</c> is opened for appending. It is a path the user named for a whole run, often
/// the same one across many runs, and a second run silently erasing the record of the first is the
/// one failure a logging feature must not have; the run header line makes the appended runs easy
/// to tell apart. A debug log is the opposite case - see <see cref="DebugLog"/> for why. Either
/// way every line is flushed as it is written: a log that only survives an orderly shutdown would
/// be missing exactly the run one wants to read afterwards.
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
    /// <param name="path">Path given to <c>--log-file</c>, or <see cref="DebugLog"/>'s own.</param>
    /// <param name="append">Whether to keep what the file already holds. True for a
    /// <c>--log-file</c>, false for a debug log, which starts every run empty.</param>
    /// <exception cref="AppError">Thrown when the file cannot be opened, so a run that was asked
    /// to keep a record fails at once rather than discovering it kept none.</exception>
    public static LogFile Open(string path, bool append = true)
    {
        try
        {
            var writer = new StreamWriter(path, append, new UTF8Encoding(false)) { AutoFlush = true };
            var log = new LogFile(writer);
            // The build number, not just the version: a log outlives the build that wrote it, and
            // several builds share one version during development. Without it, a log that
            // contradicts the current sources cannot be told from one that merely predates a fix.
            var build = CliOptions.BuildNumber is { } n ? $" (build {n})" : "";
            log.WriteRaw($"=== abchapterize {CliOptions.Version}{build} run started {Timestamp()} ===");
            // The machine, on a line of its own between the build and the command: those three are
            // what a log needs to say about its own provenance, and this is the one of them that
            // cannot be reconstructed from anything else in the file. See HostPlatform, which also
            // explains why it earns a line in a --log-file and not only in a .debug.log.
            log.WriteRaw($"=== {HostPlatform.Description} ===");
            // Redacted, because this line is the one place a key or a password typed on the command
            // line would reach a file meant to be kept - and, for a --debug log, attached to a bug
            // report. See CommandLineRedaction.
            log.WriteRaw($"=== {CommandLineRedaction.Redact(Environment.CommandLine)} ===");
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

    /// <summary>Appends a line verbatim. Locked rather than relying on its caller: every line from
    /// inside a run arrives under <see cref="ProgressRenderer"/>'s own lock, but the header and
    /// footer written here do not, and a log is the last thing that should interleave badly when
    /// something else eventually writes to one.</summary>
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
