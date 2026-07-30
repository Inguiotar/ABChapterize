// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using System.Runtime.InteropServices;

namespace ABChapterize.Concurrency;

/// <summary>
/// Cross-platform system load sampling used to throttle concurrent file processing
/// (see <see cref="ConcurrencyMonitor"/>). CPU usage is measured directly from the OS on
/// both supported platforms, with no extra package dependency.
/// <para>
/// CPU load only, deliberately. A GPU sampler was tried and removed again (2026-07-31): there is
/// no OS-level API common to both platforms and every vendor, so it could only ever shell out to
/// NVIDIA's <c>nvidia-smi</c>, which leaves every other machine unthrottled - and throttling on GPU
/// load is the wrong lever anyway, since the work this gate admits is whole files whose ffmpeg
/// decoding and Whisper recognition contend for the CPU either way.
/// </para>
/// </summary>
internal static class SystemLoad
{
    private static readonly Lock CpuLock = new();
    private static (ulong Idle, ulong Total)? _lastCpuSample;

    /// <summary>
    /// System-wide CPU utilization as a fraction between 0 and 1, measured as the delta
    /// since the previous call. The first call in the process has no prior sample to
    /// compare against and returns a conservative 0.5 (assume moderate load until a real
    /// reading is available).
    /// </summary>
    public static double GetCpuUsage()
    {
        var (idle, total) = ReadCpuTicks();
        lock (CpuLock)
        {
            if (_lastCpuSample is not { } last)
            {
                _lastCpuSample = (idle, total);
                return 0.5;
            }
            _lastCpuSample = (idle, total);

            var totalDelta = total - last.Total;
            if (totalDelta == 0)
                return 0.5;
            var idleDelta = idle - last.Idle;
            return Math.Clamp(1.0 - (double)idleDelta / totalDelta, 0, 1);
        }
    }

    /// <summary>Reads cumulative (idle, total) CPU tick counts since boot, OS-specific.</summary>
    private static (ulong Idle, ulong Total) ReadCpuTicks()
        => OperatingSystem.IsWindows() ? ReadWindowsCpuTicks() : ReadLinuxCpuTicks();

    /// <summary>Reads system-wide idle/kernel/user time via the Win32 GetSystemTimes API.</summary>
    private static (ulong Idle, ulong Total) ReadWindowsCpuTicks()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
            return (0, 0);
        var idleTicks = ToTicks(idle);
        // Windows counts idle time as part of kernel time, so total = kernel + user (not + idle again).
        var totalTicks = ToTicks(kernel) + ToTicks(user);
        return (idleTicks, totalTicks);
    }

    private static ulong ToTicks(FILETIME ft)
        => ((ulong)(uint)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public int dwLowDateTime;
        public int dwHighDateTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FILETIME lpIdleTime, out FILETIME lpKernelTime, out FILETIME lpUserTime);

    /// <summary>Reads the aggregate "cpu" line of /proc/stat (jiffies since boot).</summary>
    private static (ulong Idle, ulong Total) ReadLinuxCpuTicks()
    {
        try
        {
            var line = File.ReadLines("/proc/stat").FirstOrDefault(l => l.StartsWith("cpu ", StringComparison.Ordinal));
            if (line == null)
                return (0, 0);
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Skip(1)
                .Select(f => ulong.TryParse(f, out var v) ? v : 0)
                .ToArray();
            if (fields.Length < 4)
                return (0, 0);
            // user, nice, system, idle, iowait, irq, softirq, steal, guest, guest_nice
            var idle = fields[3] + (fields.Length > 4 ? fields[4] : 0);
            var total = fields.Aggregate(0UL, (acc, f) => acc + f);
            return (idle, total);
        }
        catch (IOException)
        {
            return (0, 0);
        }
    }
}
