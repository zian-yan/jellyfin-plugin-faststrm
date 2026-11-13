using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FastSTRM.Common;
using Xunit;

namespace FastSTRM.Test;

public class ConcurrencyGateTests
{
    [Fact]
    public async Task NeverExceedsTheConfiguredLimit()
    {
        var limit = 2;
        var gate = new ConcurrencyGate(() => limit);
        var current = 0;
        var peak = 0;

        var tasks = Enumerable.Range(0, 24).Select(async _ =>
        {
            await gate.AcquireAsync(CancellationToken.None);
            try
            {
                var now = Interlocked.Increment(ref current);
                InterlockedMax(ref peak, now);
                await Task.Delay(5);
            }
            finally
            {
                Interlocked.Decrement(ref current);
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);

        Assert.True(peak <= limit, $"并发峰值 {peak} 超过了上限 {limit}");
        Assert.Equal(0, gate.Active);
    }

    [Fact]
    public async Task RaisingTheLimitIsPickedUpByTheNextSlotRelease()
    {
        var limit = 1;
        var gate = new ConcurrencyGate(() => limit);

        await gate.AcquireAsync(CancellationToken.None);

        var waiting = gate.AcquireAsync(CancellationToken.None);
        await Task.Delay(30);
        Assert.False(waiting.IsCompleted, "上限为 1 时第二个占位者不应通过");

        limit = 2;

        await gate.AcquireAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, gate.Active);

        gate.Release();
        await waiting.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, gate.Active);
    }

    [Fact]
    public async Task LoweringTheLimitDoesNotLetAnExtraProbeIn()
    {
        var limit = 4;
        var gate = new ConcurrencyGate(() => limit);

        for (var i = 0; i < 4; i++)
        {
            await gate.AcquireAsync(CancellationToken.None);
        }

        limit = 1;

        var waiting = gate.AcquireAsync(CancellationToken.None);
        await Task.Delay(30);
        Assert.False(waiting.IsCompleted, "上限调小后，新占位者必须等待已有探测结束");

        gate.Release();
        gate.Release();
        gate.Release();
        await Task.Delay(30);
        Assert.False(waiting.IsCompleted, "名额数必须与新的上限比较，而不是与旧上限比较");

        gate.Release();
        await waiting.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, gate.Active);
    }

    [Fact]
    public async Task CancelledWaiterDoesNotLeakASlot()
    {
        var gate = new ConcurrencyGate(() => 1);
        await gate.AcquireAsync(CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => gate.AcquireAsync(cts.Token));

        gate.Release();
        Assert.Equal(0, gate.Active);

        await gate.AcquireAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, gate.Active);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var seen = Interlocked.CompareExchange(ref target, value, current);
            if (seen == current)
            {
                return;
            }

            current = seen;
        }
    }
}
