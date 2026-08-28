// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using System.Runtime.InteropServices;

namespace ABChapterize.Cli;

/// <summary>
/// One line naming the machine a run is happening on: which of this project's platform builds is
/// executing, and what system it found itself on.
/// </summary>
/// <remarks>
/// <para>
/// Printed by <c>--version</c> and written into the header of every <c>--log-file</c> and
/// <c>.debug.log</c>, which is what it exists for. A log arrives detached from the machine that
/// produced it, and "which platform" is the first thing a report of a crash, a missing native
/// library or a GPU that was never used runs into. It is not a question a maintainer can answer by
/// assumption either: two of the four targets in the csproj are built and documented but have never
/// been run by anyone here (see <c>doc/building-on-macos.md</c>), so a report from one of them is
/// exactly the report that most needs to say so.
/// </para>
/// <para>
/// It also makes one expensive mistake visible on sight. Whisper is bit-reproducible on one machine
/// and is not across machines, so diffing two debug logs - the standard way of proving a change
/// moved no mark - is only meaningful when both came from the same box. Two logs whose platform
/// lines differ are not comparable at all, and now say so on the second line instead of through a
/// divergence hundreds of lines further down that looks like a regression.
/// </para>
/// </remarks>
internal static class HostPlatform
{
    /// <summary>
    /// The platform line: <c>win-x64 on Microsoft Windows 10.0.26200</c>,
    /// <c>linux-x64 on Debian GNU/Linux 13 (trixie)</c>.
    /// </summary>
    /// <remarks>
    /// Built once, since a batch opens one debug log per book and the answer cannot change inside a
    /// run.
    /// <para>
    /// The system half is worth knowing what it costs: measured 2026-08-28, Windows reports
    /// <c>Microsoft Windows 10.0.26200</c> and Linux reports its distribution's own pretty name,
    /// <c>Debian GNU/Linux 13 (trixie)</c>. The Linux answer is the readable one only because .NET 8
    /// began reading <c>/etc/os-release</c> for it; older runtimes returned the <c>uname</c> string,
    /// and a system without that file still does. So this line has no length worth relying on, which
    /// is why nothing here truncates it - a log is read by a person looking for exactly this.
    /// </para>
    /// </remarks>
    internal static readonly string Description = Describe();

    /// <summary>Assembles <see cref="Description"/>.</summary>
    private static string Describe()
        // The runtime identifier rather than a chain of OperatingSystem.IsX tests: every release is
        // a self-contained per-RID publish, so this names the very folder the binary came out of
        // (bin\publish\win-x64\ and its siblings) instead of a guess reassembled from two enums.
        => $"{RuntimeInformation.RuntimeIdentifier} on {RuntimeInformation.OSDescription.Trim()}"
           + Emulation();

    /// <summary>
    /// The clause naming a process running as one architecture on a machine that is another - an
    /// x64 build under Windows-on-ARM or macOS Rosetta - and empty in the ordinary case where the
    /// two agree.
    /// </summary>
    /// <remarks>
    /// Worth its own clause rather than left to the reader of the runtime identifier, because it is
    /// the shape behind a whole family of reports that otherwise look like defects: emulation costs
    /// several times the transcription speed, and the Vulkan device list is routinely empty under
    /// it, so a run that is merely slow and on the CPU has a cause here rather than in detection.
    /// </remarks>
    private static string Emulation()
        => RuntimeInformation.ProcessArchitecture == RuntimeInformation.OSArchitecture
            ? ""
            : $" ({Name(RuntimeInformation.ProcessArchitecture)} process on " +
              $"{Name(RuntimeInformation.OSArchitecture)} hardware)";

    /// <summary>An architecture as this line spells it - lower case, the way a runtime identifier
    /// writes it, so "x64" reads the same in both halves of the sentence.</summary>
    /// <param name="architecture">The architecture to name.</param>
    private static string Name(Architecture architecture)
        => architecture.ToString().ToLowerInvariant();
}
