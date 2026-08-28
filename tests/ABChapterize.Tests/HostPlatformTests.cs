// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using System.Runtime.InteropServices;
using Xunit;
using ABChapterize.Cli;

namespace ABChapterize.Tests;

/// <summary>
/// Tests for <see cref="HostPlatform"/>, the line <c>--version</c> prints and every log file opens
/// with. What can be asserted here is its shape and the half that is knowable from the test process
/// itself; the system half is whatever machine the suite happens to run on, and pinning it would
/// only pin this machine.
/// </summary>
public sealed class HostPlatformTests
{
    /// <summary>The build half is the runtime identifier, so a reader can tell a win-x64 binary
    /// from an osx-arm64 one without being told which folder it came out of.</summary>
    [Fact]
    public void Description_OpensWithTheRuntimeIdentifier()
        => Assert.StartsWith(
            RuntimeInformation.RuntimeIdentifier + " on ", HostPlatform.Description, StringComparison.Ordinal);

    /// <summary>The system half is present and is the runtime's own description of it.</summary>
    [Fact]
    public void Description_NamesTheOperatingSystem()
        => Assert.Contains(RuntimeInformation.OSDescription.Trim(), HostPlatform.Description, StringComparison.Ordinal);

    /// <summary>
    /// One line, because it goes into a log header framed by <c>===</c> and onto the console under
    /// <c>--version</c>. A stray newline would break both, and the operating-system half is a string
    /// this code does not control - on a Linux without <c>/etc/os-release</c> it is the
    /// <c>uname</c> output.
    /// </summary>
    [Fact]
    public void Description_IsASingleNonEmptyLine()
    {
        Assert.NotEmpty(HostPlatform.Description);
        Assert.DoesNotContain('\n', HostPlatform.Description);
        Assert.DoesNotContain('\r', HostPlatform.Description);
    }

    /// <summary>
    /// The emulation clause appears only when the process and the machine really disagree, which on
    /// an ordinary build machine they do not - so the common case is the plain two-part line, with
    /// no parenthetical inviting the reader to wonder what it means.
    /// </summary>
    [Fact]
    public void Description_MentionsEmulationOnlyWhenThereIsAny()
    {
        var emulated = RuntimeInformation.ProcessArchitecture != RuntimeInformation.OSArchitecture;
        Assert.Equal(emulated, HostPlatform.Description.Contains(" hardware)", StringComparison.Ordinal));
    }
}
