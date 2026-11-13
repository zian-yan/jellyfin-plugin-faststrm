using System;
using System.Threading;
using System.Threading.Tasks;

namespace FastSTRM.Common;

/// <summary>
/// 上限可在运行期变化的异步并发门。
/// </summary>
/// <remarks>
/// <para>
/// 替代「换一个新 <see cref="SemaphoreSlim"/>」的写法：那道写法在替换瞬间会让新旧两个信号量
/// 同时放行（旧信号量上已占用的名额不计入新信号量），配置调小上限时并发会短暂超标，旧实例也
/// 永远不会被释放。这里始终只有一个计数器：占位走 CAS，名额不足时等待「有人释放」的广播。
/// </para>
/// <para>
/// 上限由 <c>limitProvider</c> 每次占位时读取，因此配置保存后对下一个请求立即生效，不需要
/// 重建对象，也不会打断已经在跑的探测。已经排队等待的调用方不会被主动唤醒：提高上限在下一个
/// 名额归还（<see cref="Release"/>）时生效——排队者不轮询配置，才可能保证任何时刻都不超过上限。
/// </para>
/// </remarks>
internal sealed class ConcurrencyGate
{
    private readonly Func<int> _limitProvider;

    /// <summary>
    /// 「有名额释放」的广播：占位失败者在这里排队。许可会累积，但每次等待都会重新判定上限，
    /// 所以多出来的许可只会带来一次无害的重新检查。
    /// </summary>
    private readonly SemaphoreSlim _released = new(0);

    private int _active;

    public ConcurrencyGate(Func<int> limitProvider)
        => _limitProvider = limitProvider ?? throw new ArgumentNullException(nameof(limitProvider));

    /// <summary>Gets 当前正在执行的数量（仅用于诊断与日志）。</summary>
    public int Active => Volatile.Read(ref _active);

    /// <summary>
    /// 占一个名额，名额不足时等待。取消时抛出 <see cref="OperationCanceledException"/>，
    /// 且不会留下未释放的名额。
    /// </summary>
    public async Task AcquireAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var limit = Math.Max(1, _limitProvider());
            var current = Volatile.Read(ref _active);

            if (current < limit
                && Interlocked.CompareExchange(ref _active, current + 1, current) == current)
            {
                return;
            }

            if (current >= limit)
            {
                await _released.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // CAS 失败说明别的线程刚好抢走了名额，让出一次执行权再判定，避免空转。
                await Task.Yield();
            }
        }
    }

    /// <summary>释放一个名额，唤醒一个等待者。</summary>
    public void Release()
    {
        Interlocked.Decrement(ref _active);
        _released.Release();
    }
}
