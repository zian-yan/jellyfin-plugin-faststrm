using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FastSTRM.Common;

/// <summary>
/// 按动态上限限流的异步闸门。
///
/// 换掉了原先「SemaphoreSlim 当自动重置事件用」的写法：那个写法在无人等待时也会
/// 累加许可，之后的并发进入会把一大批等待者瞬间唤醒再全部折回，形成惊群，
/// 再叠加 Task.Yield 自旋就是纯 CPU 空转。这里改为等待队列 + 单次唤醒，
/// 被唤醒者重新拿锁竞争名额，不做许可累积。
/// </summary>
internal sealed class ConcurrencyGate
{
    private readonly Func<int> _limitProvider;

    private readonly object _lock = new();

    private readonly Queue<TaskCompletionSource<bool>> _waiters = new();

    private int _active;

    public ConcurrencyGate(Func<int> limitProvider)
        => _limitProvider = limitProvider ?? throw new ArgumentNullException(nameof(limitProvider));

    public int Active => Volatile.Read(ref _active);

    public async Task<Lease> AcquireAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            TaskCompletionSource<bool> waiter;

            lock (_lock)
            {
                var limit = Math.Max(1, _limitProvider());
                if (_active < limit)
                {
                    _active++;
                    return new Lease(this);
                }

                waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Enqueue(waiter);
            }

            try
            {
                // 被唤醒后必须重新竞争名额：限额可能在等待期间被改大或改小。
                await waiter.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 把等待者标记完成，否则后续 Release 的唤醒循环会把它当成活等待者，
                // 白白消费掉一次唤醒机会，排在后面的等待者就被饿住。
                waiter.TrySetCanceled();
                throw;
            }
        }
    }

    public void Release()
    {
        lock (_lock)
        {
            if (_active > 0)
            {
                _active--;
            }

            // 只唤醒一个等待者；同时唤醒多个会全部折回队列重新排队，等于惊群。
            while (_waiters.Count > 0)
            {
                if (_waiters.Dequeue().TrySetResult(true))
                {
                    break;
                }
            }
        }
    }

    public readonly struct Lease : IDisposable
    {
        private readonly ConcurrencyGate _gate;

        internal Lease(ConcurrencyGate gate) => _gate = gate;

        public void Dispose() => _gate.Release();
    }
}
