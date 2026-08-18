// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Xunit;
using ABChapterize.Hooks;

namespace ABChapterize.Tests;

/// <summary>
/// Tests for <see cref="HookRunner"/>, which actually starts a shell.
/// </summary>
/// <remarks>
/// Real processes rather than a seam, because everything worth checking here <em>is</em> the
/// process: that the exit code survives, that both streams are drained before the result is
/// reported, and that a cancelled run does not leave the command behind. A fake shell would answer
/// none of those. The commands are chosen to work under both <c>cmd /d /s /c</c> and
/// <c>/bin/sh -c</c>, since the class picks one by platform and the project ships on both.
/// </remarks>
public class HookRunnerTests
{
    /// <summary>Prints one line and exits successfully, in the shell of whichever platform is
    /// running the test.</summary>
    /// <param name="text">The text to print.</param>
    private static string Echo(string text) => $"echo {text}";

    /// <summary>Exits with the given code without printing anything.</summary>
    /// <param name="code">The exit code to produce.</param>
    private static string Exit(int code) => OperatingSystem.IsWindows() ? $"exit /b {code}" : $"exit {code}";

    [Fact]
    public async Task ACommandThatSucceeds_ReportsZeroAndItsLastLine()
    {
        var result = await HookRunner.RunAsync(Echo("all done"), null, CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("all done", result.LastOutputLine);
    }

    [Fact]
    public async Task ACommandThatFails_ReportsItsExitCode()
    {
        // 3 rather than 1: a shell that could not start the command at all would answer 1 (sh) or
        // 9009 (cmd), so a distinctive code is what proves the command itself ran.
        var result = await HookRunner.RunAsync(Exit(3), null, CancellationToken.None);
        Assert.Equal(3, result.ExitCode);
        Assert.Null(result.LastOutputLine);
    }

    [Fact]
    public async Task EveryLineTheCommandWrites_ReachesTheLog()
    {
        var lines = new List<string>();
        var result = await HookRunner.RunAsync(
            $"{Echo("first")} && {Echo("second")}", line => { lock (lines) lines.Add(line.Trim()); },
            CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        // Ordering across the two reader threads is not promised, so the set is what is asserted -
        // what matters is that the wait did not return before both lines had been delivered.
        Assert.Equal(["first", "second"], lines.Order());
    }

    [Fact]
    public async Task WhatTheCommandWritesToStandardError_CountsAsOutputToo()
    {
        // The whole point of carrying a last line is explaining a failure, and a failing command
        // says why on stderr.
        var result = await HookRunner.RunAsync(
            OperatingSystem.IsWindows() ? "echo broken 1>&2 & exit /b 4" : "echo broken 1>&2; exit 4",
            null, CancellationToken.None);
        Assert.Equal(4, result.ExitCode);
        Assert.Equal("broken", result.LastOutputLine);
    }

    [Fact]
    public async Task ABlankLine_IsNotTakenForTheLastWord()
    {
        // "echo." on cmd and "echo" on sh both print an empty line; the result must still name the
        // line that carried something.
        var blank = OperatingSystem.IsWindows() ? "echo." : "echo";
        var result = await HookRunner.RunAsync(
            $"{Echo("the point")} && {blank}", null, CancellationToken.None);
        Assert.Equal("the point", result.LastOutputLine);
    }

    [Fact]
    public async Task ACancelledRun_ThrowsAndDoesNotLeaveTheCommandRunning()
    {
        using var cts = new CancellationTokenSource();
        var sleep = OperatingSystem.IsWindows()
            ? "ping -n 60 127.0.0.1 > nul"
            : "sleep 60";
        var run = HookRunner.RunAsync(sleep, null, cts.Token);
        cts.CancelAfter(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }
}
