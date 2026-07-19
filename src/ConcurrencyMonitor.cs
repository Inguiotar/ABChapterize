// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

namespace ABChapterize;

/// <summary>
/// Periodically samples live CPU load and adjusts an <see cref="AdaptiveConcurrencyGate"/>'s
/// soft limit between 1 and its hard cap: backs off when the system is already busy (so
/// abchapterize does not pile onto a machine doing other work), ramps up when there is
/// headroom. A hysteresis band between the two thresholds avoids flapping the limit up and
/// down every poll on borderline load.
/// </summary>
public sealed class ConcurrencyMonitor : IDisposable
{
    /// <summary>Below this CPU usage fraction there is headroom to raise concurrency by one step.</summary>
    internal const double LowWatermark = 0.55;

    /// <summary>Above this CPU usage fraction concurrency is lowered by one step.</summary>
    internal const double HighWatermark = 0.85;

    private readonly AdaptiveConcurrencyGate _gate;
    private readonly Action<string>? _log;
    private readonly Timer _timer;

    /// <summary>
    /// Starts monitoring immediately; the gate's soft limit is adjusted on a fixed poll
    /// interval for as long as this instance is not disposed. No-op (never adjusts) when
    /// <paramref name="gate"/>'s hard cap is 1, since there is nothing to adjust.
    /// </summary>
    /// <param name="gate">The gate whose soft limit is adjusted.</param>
    /// <param name="pollInterval">How often to sample CPU load and reconsider the limit.</param>
    /// <param name="log">Optional --verbose sink for limit changes.</param>
    public ConcurrencyMonitor(AdaptiveConcurrencyGate gate, TimeSpan pollInterval, Action<string>? log = null)
    {
        _gate = gate;
        _log = log;
        _timer = gate.HardCap > 1
            ? new Timer(_ => Tick(), null, pollInterval, pollInterval)
            : new Timer(_ => { }, null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Samples CPU usage once and adjusts the gate's soft limit by at most one step.</summary>
    private void Tick()
    {
        var cpu = SystemLoad.GetCpuUsage();
        var current = _gate.SoftLimit;
        int next;
        if (cpu < LowWatermark && current < _gate.HardCap)
            next = current + 1;
        else if (cpu > HighWatermark && current > 1)
            next = current - 1;
        else
            next = current;

        if (next != current)
        {
            _gate.SetSoftLimit(next);
            _log?.Invoke($"concurrency: CPU {cpu * 100:0}%, adjusted parallel file limit {current} -> {next}");
        }
    }

    /// <summary>Stops the polling timer.</summary>
    public void Dispose() => _timer.Dispose();
}
