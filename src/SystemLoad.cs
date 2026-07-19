// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ABChapterize;

/// <summary>
/// Cross-platform system load sampling used to throttle concurrent file processing
/// (see <see cref="ConcurrencyMonitor"/>). CPU usage is measured directly from the OS on
/// both supported platforms, with no extra package dependency. GPU usage is best-effort:
/// there is no OS-level API common to both platforms and to every GPU vendor, so it is
/// only available when NVIDIA's `nvidia-smi` is on PATH (the common case for CUDA users);
/// otherwise it is simply unavailable and does not factor into any decision.
/// </summary>
internal static class SystemLoad
{
    private static readonly Lock CpuLock = new();
    private static (ulong Idle, ulong Total)? _lastCpuSample;

    private static readonly Lock GpuLock = new();
    private static bool _gpuProbed;
    private static string? _nvidiaSmiPath;

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

    /// <summary>
    /// Current GPU utilization percent (0-100) via `nvidia-smi`, or null when it is not
    /// available (no NVIDIA driver, or a non-CUDA GPU backend). The absence check is
    /// cached for the process lifetime so unavailable GPUs are not re-probed every poll.
    /// </summary>
    public static int? GetGpuUsagePercent()
    {
        lock (GpuLock)
        {
            if (!_gpuProbed)
            {
                _gpuProbed = true;
                _nvidiaSmiPath = FindNvidiaSmi();
            }
        }
        if (_nvidiaSmiPath == null)
            return null;

        try
        {
            using var proc = Process.Start(new ProcessStartInfo(_nvidiaSmiPath,
                "--query-gpu=utilization.gpu --format=csv,noheader,nounits")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (proc == null)
                return null;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(2000);
            var firstLine = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            return firstLine != null && int.TryParse(firstLine, out var pct) ? Math.Clamp(pct, 0, 100) : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>Locates nvidia-smi on PATH (and common install directories on Windows), if present.</summary>
    private static string? FindNvidiaSmi()
    {
        var exe = OperatingSystem.IsWindows() ? "nvidia-smi.exe" : "nvidia-smi";
        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        var candidates = pathDirs.Select(d => Path.Combine(d, exe));
        if (OperatingSystem.IsWindows())
        {
            candidates = candidates.Append(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), exe));
            candidates = candidates.Append(@"C:\Program Files\NVIDIA Corporation\NVSMI\" + exe);
        }
        return candidates.FirstOrDefault(File.Exists);
    }
}
