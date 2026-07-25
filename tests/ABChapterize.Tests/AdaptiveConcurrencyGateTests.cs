// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Xunit;
using ABChapterize.Concurrency;

namespace ABChapterize.Tests;

/// <summary>
/// Tests for <see cref="AdaptiveConcurrencyGate"/>: the resizable admission gate that lets
/// --jobs auto-tune the number of files processed concurrently.
/// </summary>
public class AdaptiveConcurrencyGateTests
{
    [Fact]
    public async Task NeverAdmitsMoreThanSoftLimit()
    {
        var gate = new AdaptiveConcurrencyGate(hardCap: 4, initialSoftLimit: 2);
        var active = 0;
        var maxObserved = 0;
        var lockObj = new object();

        async Task Work()
        {
            using var slot = await gate.AcquireAsync(CancellationToken.None);
            int current;
            lock (lockObj) { active++; current = active; maxObserved = Math.Max(maxObserved, current); }
            await Task.Delay(30);
            lock (lockObj) active--;
        }

        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Work()));
        Assert.True(maxObserved <= 2, $"expected at most 2 concurrent, observed {maxObserved}");
    }

    [Fact]
    public async Task RaisingSoftLimit_AdmitsWaitingCallersImmediately()
    {
        var gate = new AdaptiveConcurrencyGate(hardCap: 3, initialSoftLimit: 1);
        var slot1 = await gate.AcquireAsync(CancellationToken.None);

        var acquireTask = gate.AcquireAsync(CancellationToken.None);
        await Task.Delay(20);
        Assert.False(acquireTask.IsCompleted); // still blocked at limit 1 with slot1 held

        gate.SetSoftLimit(2);
        var slot2 = await acquireTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(slot2);

        slot1.Dispose();
        slot2.Dispose();
    }

    [Fact]
    public async Task LoweringSoftLimit_BlocksNewAcquisitionsUntilActiveSlotsFreeUp()
    {
        var gate = new AdaptiveConcurrencyGate(hardCap: 3, initialSoftLimit: 3);
        var slot1 = await gate.AcquireAsync(CancellationToken.None);
        var slot2 = await gate.AcquireAsync(CancellationToken.None);

        gate.SetSoftLimit(1); // already 2 active, above the new limit

        var acquireTask = gate.AcquireAsync(CancellationToken.None);
        await Task.Delay(20);
        Assert.False(acquireTask.IsCompleted);

        slot1.Dispose();
        await Task.Delay(20);
        Assert.False(acquireTask.IsCompleted); // still 1 active == limit, no room yet

        slot2.Dispose();
        var slot3 = await acquireTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(slot3);
        slot3.Dispose();
    }

    [Fact]
    public void SoftLimit_IsClampedToHardCap()
    {
        var gate = new AdaptiveConcurrencyGate(hardCap: 2, initialSoftLimit: 10);
        Assert.Equal(2, gate.SoftLimit);
        gate.SetSoftLimit(100);
        Assert.Equal(2, gate.SoftLimit);
        gate.SetSoftLimit(0);
        Assert.Equal(1, gate.SoftLimit);
    }

    [Fact]
    public async Task CancellationBeforeAdmission_ThrowsAndDoesNotConsumeASlot()
    {
        var gate = new AdaptiveConcurrencyGate(hardCap: 1, initialSoftLimit: 1);
        var slot1 = await gate.AcquireAsync(CancellationToken.None);

        using var cts = new CancellationTokenSource();
        var waitingTask = gate.AcquireAsync(cts.Token);
        await Task.Delay(20);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waitingTask);

        slot1.Dispose();
        // The gate must still work correctly afterward: no phantom slot was left occupied.
        var slot2 = await gate.AcquireAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(slot2);
        slot2.Dispose();
    }
}
