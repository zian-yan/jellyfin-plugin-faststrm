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

    /// <summary>
    /// 标记 key，返回 true 表示这是第一次标记。仅用于日志限流。
    ///
    /// 容量判断用的是计数器而不是 <c>_keys.Count</c>（后者会对整个字典加锁并且是 O(n)）。
    /// 代价是 <see cref="Mark"/> 与 <see cref="Forget"/> 并发时计数可能漂移 ±几，
    /// 导致清理提前或推迟一点点；因为只影响日志节流，这个偏差是有界且可接受的。
    /// </summary>
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
