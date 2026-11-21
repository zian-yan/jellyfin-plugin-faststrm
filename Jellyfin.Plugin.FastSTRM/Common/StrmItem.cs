using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using MediaBrowser.Controller.Entities;

namespace FastSTRM.Common;

internal static class StrmItem
{
    private const string StrmSuffix = ".strm";

    private const string ProbeUnsupportedExtension = ".iso";

    private const int MaxCachedEntries = 8192;

    /// <summary>
    /// 解析结果的最长复用时间。库列表映射（GetStaticMediaSources）会逐条命中这里，
    /// 稳态下必须零 I/O；5 秒足够短，用户改完 .strm 内容后最多 5 秒生效。
    /// </summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 按 path 缓存解析结果。命中且未过期时不产生任何文件系统调用；过期后先 stat，
    /// 只有当长度或写入时间变化时才重新打开文件读取首行。
    /// </summary>
    private static readonly ConcurrentDictionary<string, StrmFileEntry> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    public static bool IsStrm(BaseItem item)
        => item.Path is not null
           && item.Path.EndsWith(StrmSuffix, StringComparison.OrdinalIgnoreCase);

    public static bool HasStreamInfo(BaseItem item)
        => item.RunTimeTicks is > 0;

    public static bool IsProbeUnsupported(BaseItem item)
    {
        if (!IsStrm(item))
        {
            return false;
        }

        var path = item.Path!;

        // 纯路径判断，不碰文件系统：Hotel.Transylvania.iso.strm 这类即使文件不在也应判定。
        if (HasProbeUnsupportedExtension(path))
        {
            return true;
        }

        return TryGetEntry(path, out var entry) && entry.ProbeUnsupported;
    }

    public static bool TryReadRemoteUrl(string? path, out string url)
        => TryReadRemoteUrl(path, out url, out _);

    /// <summary>
    /// 读取 .strm 的首个 http(s) URL，同时返回文件长度（避免调用方再补一次 stat）。
    /// </summary>
    public static bool TryReadRemoteUrl(string? path, out string url, out long length)
    {
        url = string.Empty;
        length = 0;

        if (!TryGetEntry(path, out var entry))
        {
            return false;
        }

        length = entry.Length;

        if (entry.Url is null)
        {
            return false;
        }

        url = entry.Url;
        return true;
    }

    /// <summary>
    /// 只取 .strm 的文件长度。命中缓存时零 I/O，供 IsRemoteSizeMissing 这类热路径复用。
    /// </summary>
    public static bool TryGetLength(string? path, out long length)
    {
        length = 0;

        if (!TryGetEntry(path, out var entry))
        {
            return false;
        }

        length = entry.Length;
        return true;
    }

    public static string? GetRemoteContainer(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var extension = Path.GetExtension(uri.AbsolutePath);
        return extension.Length > 1 ? extension[1..].ToLowerInvariant() : null;
    }

    private static bool TryGetEntry(string? path, out StrmFileEntry entry)
    {
        entry = default;

        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var now = DateTime.UtcNow.Ticks;
        if (Cache.TryGetValue(path, out var cached) && now - cached.CachedAtTicks < CacheTtl.Ticks)
        {
            entry = cached;
            return true;
        }

        return TryRefresh(path, cached, out entry);
    }

    private static bool TryRefresh(string path, StrmFileEntry? cached, out StrmFileEntry entry)
    {
        entry = default;

        long length;
        long lastWriteTicks;

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return false;
            }

            length = info.Length;
            lastWriteTicks = info.LastWriteTimeUtc.Ticks;
        }
        catch (Exception)
        {
            return false;
        }

        // 长度与写入时间都没变，说明内容没动过，直接延长缓存寿命，省掉一次 open。
        if (cached is not null && cached.Value.Length == length && cached.Value.LastWriteTicks == lastWriteTicks)
        {
            entry = cached.Value with { CachedAtTicks = DateTime.UtcNow.Ticks };
            Store(path, entry);
            return true;
        }

        var url = ReadFirstHttpUrl(path);
        entry = new StrmFileEntry(
            url,
            length,
            lastWriteTicks,
            DateTime.UtcNow.Ticks,
            IsProbeUnsupportedUrl(url));

        Store(path, entry);
        return true;
    }

    private static void Store(string path, StrmFileEntry entry)
    {
        // 只有新路径才需要判断容量，避免每次写入都读 Count（那是全字典加锁的 O(n) 操作）。
        if (!Cache.ContainsKey(path) && Cache.Count >= MaxCachedEntries)
        {
            Cache.Clear();
        }

        Cache[path] = entry;
    }

    private static string? ReadFirstHttpUrl(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            for (var i = 0; i < 10; i++)
            {
                var line = reader.ReadLine();
                if (line is null)
                {
                    break;
                }

                line = line.Trim();
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }

                return Uri.TryCreate(line, UriKind.Absolute, out var uri)
                       && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                    ? line
                    : null;
            }
        }
        catch (Exception)
        {
        }

        return null;
    }

    private static bool IsProbeUnsupportedUrl(string? url)
        => url is not null
           && Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && uri.AbsolutePath.EndsWith(ProbeUnsupportedExtension, StringComparison.OrdinalIgnoreCase);

    private static bool HasProbeUnsupportedExtension(string path)
    {
        var name = path[..^StrmSuffix.Length];
        return name.EndsWith(ProbeUnsupportedExtension, StringComparison.OrdinalIgnoreCase);
    }

    private readonly record struct StrmFileEntry(
        string? Url,
        long Length,
        long LastWriteTicks,
        long CachedAtTicks,
        bool ProbeUnsupported);
}
