using System;
using System.Threading;
using System.Threading.Tasks;

namespace FastSTRM.Common;

internal sealed class ConcurrencyGate
{
    private readonly Func<int> _limitProvider;

    private readonly SemaphoreSlim _released = new(0);

    private int _active;

    public ConcurrencyGate(Func<int> limitProvider)
        => _limitProvider = limitProvider ?? throw new ArgumentNullException(nameof(limitProvider));

    public int Active => Volatile.Read(ref _active);

    public async Task<Lease> AcquireAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var limit = Math.Max(1, _limitProvider());
            var current = Volatile.Read(ref _active);

            if (current < limit
                && Interlocked.CompareExchange(ref _active, current + 1, current) == current)
            {
                return new Lease(this);
            }

            if (current >= limit)
            {
                await _released.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await Task.Yield();
            }
        }
    }

    public void Release()
    {
        Interlocked.Decrement(ref _active);
        _released.Release();
    }

    public readonly struct Lease : IDisposable
    {
        private readonly ConcurrencyGate _gate;

        internal Lease(ConcurrencyGate gate)
            => _gate = gate;

        public void Dispose()
            => _gate.Release();
    }
}
