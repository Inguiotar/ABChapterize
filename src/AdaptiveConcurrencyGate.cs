// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

namespace ABChapterize;

/// <summary>
/// Bounded concurrency gate whose effective limit can be lowered or raised at any time
/// while callers are waiting or holding a slot - unlike <see cref="SemaphoreSlim"/>, whose
/// count cannot be shrunk below the number of outstanding releases. Used to let
/// <see cref="ConcurrencyMonitor"/> throttle the number of files processed at once based on
/// live system load, without tearing down and recreating the admission primitive.
/// </summary>
public sealed class AdaptiveConcurrencyGate
{
    private readonly int _hardCap;
    private int _softLimit;
    private int _active;
    private readonly Lock _lock = new();
    private readonly List<TaskCompletionSource> _waiters = [];

    /// <summary>
    /// Creates a gate with the given absolute ceiling and starting limit.
    /// </summary>
    /// <param name="hardCap">Maximum number of slots ever handed out, regardless of <see cref="SetSoftLimit"/>.</param>
    /// <param name="initialSoftLimit">Starting effective limit, clamped to [1, hardCap].</param>
    public AdaptiveConcurrencyGate(int hardCap, int initialSoftLimit)
    {
        _hardCap = Math.Max(1, hardCap);
        _softLimit = Math.Clamp(initialSoftLimit, 1, _hardCap);
    }

    /// <summary>The absolute ceiling this gate was constructed with.</summary>
    public int HardCap => _hardCap;

    /// <summary>The current effective limit (read for --verbose diagnostics).</summary>
    public int SoftLimit
    {
        get { lock (_lock) return _softLimit; }
    }

    /// <summary>
    /// Adjusts the effective concurrency limit, clamped to [1, hardCap]. Raising it may
    /// immediately release waiting callers; lowering it only prevents new admissions until
    /// enough active slots are released to fall back under the new limit.
    /// </summary>
    public void SetSoftLimit(int limit)
    {
        List<TaskCompletionSource>? toRelease = null;
        lock (_lock)
        {
            _softLimit = Math.Clamp(limit, 1, _hardCap);
            while (_waiters.Count > 0 && _active < _softLimit)
            {
                _active++;
                (toRelease ??= []).Add(_waiters[0]);
                _waiters.RemoveAt(0);
            }
        }
        if (toRelease != null)
            foreach (var tcs in toRelease)
                tcs.SetResult();
    }

    /// <summary>
    /// Waits for a free slot and returns a handle that releases it on <see cref="IDisposable.Dispose"/>.
    /// </summary>
    public async Task<IDisposable> AcquireAsync(CancellationToken ct)
    {
        TaskCompletionSource? tcs = null;
        lock (_lock)
        {
            if (_active < _softLimit)
            {
                _active++;
                return new Releaser(this);
            }
            tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add(tcs);
        }
        await using (ct.Register(() =>
        {
            lock (_lock) _waiters.Remove(tcs);
            tcs.TrySetCanceled(ct);
        }))
        {
            await tcs.Task;
        }
        return new Releaser(this);
    }

    /// <summary>Frees one slot, admitting the next waiter if the current limit allows it.</summary>
    private void Release()
    {
        TaskCompletionSource? next = null;
        lock (_lock)
        {
            _active--;
            if (_waiters.Count > 0 && _active < _softLimit)
            {
                _active++;
                next = _waiters[0];
                _waiters.RemoveAt(0);
            }
        }
        next?.SetResult();
    }

    private sealed class Releaser(AdaptiveConcurrencyGate gate) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                gate.Release();
        }
    }
}
