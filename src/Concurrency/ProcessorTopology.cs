// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ABChapterize.Concurrency;

/// <summary>
/// How many CPU cores this machine really has, as opposed to how many hardware threads it
/// advertises. Both thread-count options default to this figure (see
/// <see cref="ABChapterize.Cli.CliOptions.EffectiveVadThreads"/> and
/// <see cref="ABChapterize.Cli.CliOptions.EffectiveWhisperThreads"/>).
/// <para>
/// Physical rather than logical, without measuring first, because the risk is wildly asymmetric:
/// hyperthreads can add a little on a machine where they happen to help, but cost a great deal on
/// one where they do not, and nothing here can know which machine it is running on. Physical cores
/// is the choice that cannot go badly wrong anywhere - and whoever knows their own machine better
/// can say so with the options.
/// </para>
/// <para>
/// There is no BCL API for this, so every platform is read directly. The platform split is a
/// runtime branch rather than conditional compilation: this project is a single target framework
/// with no per-OS builds, and a runtime guard is also what keeps the CA1416 platform analyzer
/// happy about the OS-specific P/Invokes below - a <c>DllImport</c> for an entry point that only
/// exists on Windows is inert on Linux as long as it is never called, since native resolution is
/// lazy, and the same is what lets the libSystem import sit beside it.
/// </para>
/// </summary>
internal static class ProcessorTopology
{
    /// <summary>
    /// Number of physical CPU cores, clamped to <see cref="Environment.ProcessorCount"/> and at
    /// least 1. Queried once and cached; the answer cannot change while the process runs.
    /// </summary>
    /// <remarks>
    /// The clamp is the one real trap here, not a formality. <see cref="Environment.ProcessorCount"/>
    /// honours cgroup CPU quotas and process affinity masks, while a core count derived from the
    /// hardware topology knows about neither: a container limited to 2 CPUs on a 64-core host would
    /// otherwise report 32 physical cores and oversubscribe itself sixteenfold. It doubles as the
    /// fallback whenever neither platform query can answer.
    /// </remarks>
    public static int PhysicalCoreCount { get; } =
        Math.Clamp(QueryPhysicalCoreCount(), 1, Math.Max(1, Environment.ProcessorCount));

    /// <summary>Asks the operating system for the physical core count, falling back to the logical
    /// processor count when it cannot say.</summary>
    private static int QueryPhysicalCoreCount()
    {
        try
        {
            var count =
                OperatingSystem.IsWindows() ? WindowsCoreCount()
                : OperatingSystem.IsMacOS() ? MacCoreCount()
                : LinuxCoreCount();
            return count > 0 ? count : Environment.ProcessorCount;
        }
        catch (Exception)
        {
            // A thread-count default is not worth failing a run over, whatever went wrong down
            // there - a missing sysfs tree, a P/Invoke surprise on an exotic Windows edition, a
            // sysctl name Apple retired.
            return Environment.ProcessorCount;
        }
    }

    /// <summary>Relationship code for a physical core in a
    /// <c>SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX</c> record (<c>RelationProcessorCore</c>).</summary>
    private const int RelationProcessorCore = 0;

    /// <summary>Win32 error returned by the sizing call, which is the documented way to learn how
    /// large a buffer <c>GetLogicalProcessorInformationEx</c> needs.</summary>
    private const int ErrorInsufficientBuffer = 122;

    /// <summary>
    /// Counts <c>RelationProcessorCore</c> records returned by <c>GetLogicalProcessorInformationEx</c>,
    /// which is exactly one per physical core.
    /// </summary>
    /// <remarks>
    /// Not WMI (<c>Win32_Processor.NumberOfCores</c>), which is the answer every search result
    /// gives: it fails with "access denied" for non-administrator accounts, and an unprivileged
    /// service or SSH account is precisely the kind of machine this runs on (confirmed on this
    /// project's own remote test box). The records are variable-length and only their first two
    /// fields are needed - relationship and size - so the buffer is walked by hand rather than
    /// marshalled into a struct whose union member layout would have to be modelled.
    /// </remarks>
    [SupportedOSPlatform("windows")]
    private static int WindowsCoreCount()
    {
        var length = 0;
        if (GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref length)
            || Marshal.GetLastWin32Error() != ErrorInsufficientBuffer || length <= 0)
            return 0;

        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buffer, ref length))
                return 0;

            var cores = 0;
            var offset = 0;
            while (offset + 8 <= length)
            {
                var relationship = Marshal.ReadInt32(buffer, offset);
                var size = Marshal.ReadInt32(buffer, offset + sizeof(int));
                if (size <= 0)
                    break;
                if (relationship == RelationProcessorCore)
                    cores++;
                offset += size;
            }
            return cores;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLogicalProcessorInformationEx(
        int relationshipType, IntPtr buffer, ref int returnedLength);

    /// <summary>
    /// Asks Darwin for <c>hw.perflevel0.physicalcpu</c>: the physical core count of performance
    /// level 0, which is always the fastest core cluster on the package.
    /// </summary>
    /// <remarks>
    /// <c>hw.physicalcpu</c> is the obvious name and the wrong one, which is the whole reason this
    /// method is not one line. It counts efficiency cores in with the performance cores, and those
    /// are not interchangeable workers: an 8-core Apple Silicon part with four of each would size
    /// both thread pools for eight peers, half of which run at a fraction of the speed. That is the
    /// same oversubscription this class exists to avoid on a hyperthreaded x86 box, wearing
    /// different clothes. It stays as the fallback because a machine with one uniform cluster - an
    /// Intel Mac, or a kernel predating perflevel - publishes no perflevel keys at all, and there
    /// <c>hw.physicalcpu</c> is exactly right.
    /// <para>
    /// Untestable from this project: nobody here has a Mac and no CI runs on one, so this is
    /// written to the documented sysctl contract rather than to an observation. It is shaped so
    /// that being wrong costs nothing - every failure returns 0, and the caller reads that as
    /// "cannot say" and falls back to <see cref="Environment.ProcessorCount"/>.
    /// </para>
    /// </remarks>
    [SupportedOSPlatform("macos")]
    private static int MacCoreCount()
    {
        var performanceCores = ReadSysctlInt32("hw.perflevel0.physicalcpu");
        return performanceCores > 0 ? performanceCores : ReadSysctlInt32("hw.physicalcpu");
    }

    /// <summary>Reads an integer-valued sysctl by name; 0 when it does not exist or is not an
    /// <see cref="int"/>.</summary>
    /// <param name="name">Dotted sysctl name, e.g. <c>hw.physicalcpu</c>.</param>
    /// <remarks>The width check is not a formality: sysctl reports a value's size back through the
    /// same parameter, and several neighbouring <c>hw.*</c> keys are 64-bit (<c>hw.memsize</c>, for
    /// one). A key that silently changed width would otherwise be read as a truncated int.</remarks>
    [SupportedOSPlatform("macos")]
    private static int ReadSysctlInt32(string name)
    {
        var value = 0;
        var length = (nuint)sizeof(int);
        return SysctlByName(name, ref value, ref length, IntPtr.Zero, 0) == 0
               && length == (nuint)sizeof(int)
            ? value
            : 0;
    }

    [SupportedOSPlatform("macos")]
    // No SetLastError: the return code alone decides here, and marking it would imply errno is
    // read somewhere. The kernel32 import above does read its error, which is why it sets it.
    [DllImport("libSystem.dylib", EntryPoint = "sysctlbyname")]
    private static extern int SysctlByName(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        ref int oldValue, ref nuint oldLength, IntPtr newValue, nuint newLength);

    /// <summary>
    /// Counts distinct (package, core) pairs under
    /// <c>/sys/devices/system/cpu/cpu*/topology/</c>, which is the kernel's own view of the
    /// topology and the only place it is reliably complete.
    /// </summary>
    private static int LinuxCoreCount()
    {
        const string cpuRoot = "/sys/devices/system/cpu";
        if (!Directory.Exists(cpuRoot))
            return CpuInfoCoreCount();

        var cores = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cpuDir in Directory.EnumerateDirectories(cpuRoot, "cpu*"))
        {
            var topology = Path.Combine(cpuDir, "topology");
            var coreId = ReadFirstLine(Path.Combine(topology, "core_id"));
            if (coreId == null)
                continue;
            // A single-socket machine may omit physical_package_id; treating it as package 0 is
            // right there and harmless anywhere it is present.
            var packageId = ReadFirstLine(Path.Combine(topology, "physical_package_id")) ?? "0";
            cores.Add($"{packageId}/{coreId}");
        }
        return cores.Count > 0 ? cores.Count : CpuInfoCoreCount();
    }

    /// <summary>
    /// Fallback for kernels or containers without a topology tree: pairs of "physical id" and
    /// "core id" out of <c>/proc/cpuinfo</c>.
    /// </summary>
    /// <remarks>
    /// A fallback rather than the primary source because cpuinfo's format is architecture-specific:
    /// those two keys are an x86 convention, and ARM boards commonly print neither - which is why
    /// this returning 0 has to remain an acceptable outcome rather than an error.
    /// </remarks>
    private static int CpuInfoCoreCount()
    {
        const string path = "/proc/cpuinfo";
        if (!File.Exists(path))
            return 0;

        var cores = new HashSet<string>(StringComparer.Ordinal);
        string? package = null, core = null;
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0)
            {
                // Blank line: end of one processor's block.
                package = core = null;
                continue;
            }
            var separator = line.IndexOf(':');
            if (separator < 0)
                continue;
            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (key == "physical id")
                package = value;
            else if (key == "core id")
                core = value;
            // Only "core id" is required, for the same reason LinuxCoreCount defaults a missing
            // topology/physical_package_id to 0: a single-socket machine may name no package at
            // all, and demanding one here counted its cores as zero and silently fell back to the
            // logical count - one thread per hyperthread, on exactly the small single-socket ARM
            // boards this path exists to serve.
            if (core != null)
            {
                cores.Add($"{package ?? "0"}/{core}");
                package = core = null;
            }
        }
        return cores.Count;
    }

    /// <summary>Reads the first line of a sysfs file, or null when it is absent or unreadable.</summary>
    /// <param name="path">Full path of the file to read.</param>
    private static string? ReadFirstLine(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadLines(path).FirstOrDefault()?.Trim() : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
