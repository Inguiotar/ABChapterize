// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using System.Diagnostics;
using ABChapterize.Errors;

namespace ABChapterize.Hooks;

/// <summary>
/// Runs one finished <c>--run-before</c> / <c>--run-after</c> command line through the platform's
/// shell and reports how it went.
/// </summary>
/// <remarks>
/// A shell rather than the executable directly, because the option exists to let a user write the
/// line they would have typed: <c>move</c> and <c>copy</c> are cmd built-ins with no executable to
/// start, and <c>~</c>, <c>&amp;&amp;</c> and redirection are the shell's doing too. Windows gets
/// <c>cmd /d /s /c</c> - <c>/d</c> so a machine with an AutoRun command in its registry does not
/// silently run that first, <c>/s</c> so the command reaches it verbatim instead of going through
/// cmd's quote-stripping rules. Elsewhere it is <c>/bin/sh -c</c>, the one shell a POSIX system is
/// guaranteed to have; <c>SHELL</c> is deliberately not consulted, so that a command line behaves
/// the same for everyone running it.
/// </remarks>
public static class HookRunner
{
    /// <summary>
    /// What one hook run came to.
    /// </summary>
    /// <param name="ExitCode">The command's exit code; 0 means success, as everywhere else.</param>
    /// <param name="LastOutputLine">The last non-blank line the command wrote to either stream, or
    /// null if it wrote nothing. Carried so a failure can be explained on the result line without
    /// the user having to re-run under --verbose to see "command not found".</param>
    public readonly record struct HookResult(int ExitCode, string? LastOutputLine);

    /// <summary>
    /// Runs a command and waits for it to finish.
    /// </summary>
    /// <param name="command">The finished command line, placeholders already resolved.</param>
    /// <param name="log">Sink for the command's own output, one line at a time, or null when
    /// nothing is listening. Called from the reader threads, so it must be safe to call from
    /// anywhere.</param>
    /// <param name="ct">Cancellation token bound to Ctrl+C. Kills the command rather than leaving
    /// it running behind an abandoned run.</param>
    /// <exception cref="AppError">Thrown when the shell itself could not be started, which is a
    /// broken installation rather than a failing command and so is fatal to the run.</exception>
    public static async Task<HookResult> RunAsync(string command, Action<string>? log, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (OperatingSystem.IsWindows())
        {
            startInfo.FileName = "cmd.exe";
            startInfo.Arguments = $"/d /s /c \"{command}\"";
        }
        else
        {
            startInfo.FileName = "/bin/sh";
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(command);
        }

        using var process = new Process { StartInfo = startInfo };
        string? lastLine = null;
        void Receive(object? sender, DataReceivedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e.Data))
                return;
            // Racy between the two reader threads, and deliberately so: which of two lines written
            // at the same moment is "the last" is not a question worth a lock over a hint.
            lastLine = e.Data.Trim();
            log?.Invoke(e.Data);
        }
        process.OutputDataReceived += Receive;
        process.ErrorDataReceived += Receive;

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new AppError($"Could not start {startInfo.FileName} to run \"{command}\": {ex.Message}");
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            TryKill(process, log);
            throw;
        }
        return new HookResult(process.ExitCode, lastLine);
    }

    /// <summary>
    /// Kills an abandoned command and everything it started, so a Ctrl+C does not leave a
    /// half-finished conversion running behind a run that is already gone.
    /// </summary>
    /// <param name="process">The shell process to kill.</param>
    /// <param name="log">Sink for the one line a failure is worth, or null.</param>
    private static void TryKill(Process process, Action<string>? log)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            // Never fatal: by the time this runs the user has pressed Ctrl+C, and a command that
            // exited on its own in between must not turn that into an error of its own. Worth a
            // line though - a survivor of a killed run is exactly the kind of thing someone later
            // finds still holding a file open.
            log?.Invoke($"could not stop the command: {ex.Message}");
        }
    }
}
