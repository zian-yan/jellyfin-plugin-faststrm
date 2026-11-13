using System.Collections.Concurrent;
using System.Threading;

namespace FastSTRM.Common;

internal sealed class MarkerSet<TKey>
    where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, byte> _keys = new();

    private readonly int _capacity;

    private int _count;

    public MarkerSet(int capacity)
        => _capacity = capacity;

    public bool Mark(TKey key)
    {
        if (Volatile.Read(ref _count) >= _capacity)
        {
            _keys.Clear();
            Interlocked.Exchange(ref _count, 0);
        }

        if (!_keys.TryAdd(key, 0))
        {
            return false;
        }

        Interlocked.Increment(ref _count);
        return true;
    }

    public void Forget(TKey key)
    {
        if (_keys.TryRemove(key, out _) && Interlocked.Decrement(ref _count) < 0)
        {
            Interlocked.Exchange(ref _count, 0);
        }
    }
}
