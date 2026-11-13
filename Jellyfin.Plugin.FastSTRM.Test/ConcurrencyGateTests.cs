using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FastSTRM.Common;
using Xunit;

namespace FastSTRM.Test;

/// <summary>
/// 动态并发门：上限跟随配置变化，且任何时刻都不放行超过上限的并发。
/// </summary>
/// <remarks>
/// 旧实现用一个可被整体替换的 <see cref="SemaphoreSlim"/>：上限调小时，旧信号量上已经占用的
/// 名额不计入新信号量，并发会短暂超标。这里的断言就是盯住这条回归。
/// </remarks>
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

        // 上限被改成 2：不需要重建实例。新来的占位者立刻按新上限通过，排队者在下一次
        // 释放广播时通过（排队者不轮询配置，才可能保证任何时刻都不超过上限）。
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

        // 释放到只剩 1 个在跑：仍然等于新的上限，不能让第 5 个进来。
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

        // 被取消的等待者没有占位，所以下一个占位者应当立刻通过。
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
