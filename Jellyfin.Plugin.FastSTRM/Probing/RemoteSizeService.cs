using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FastSTRM.Common;
using FastSTRM.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging;

namespace FastSTRM.Probing;

/// <summary>
/// Resolves the real size of the media behind a .strm file and writes it onto the item.
/// </summary>
/// <remarks>
/// <para>
/// Jellyfin probes the .strm container, so the stored <see cref="BaseItem.Size"/> ends up being
/// the size of the local .strm text file (a few hundred bytes) instead of the remote media.
/// The remote size is not something ffprobe can reliably report either, so this service asks the
/// stream endpoint directly (HTTP <c>HEAD</c>, falling back to a one-byte ranged <c>GET</c>) and
/// persists the authoritative value.
/// </para>
/// <para>
/// The value must be written rather than only patched into the API response: Jellyfin builds
/// playlist/DTO metadata from the stored item, and <c>MediaSourceInfo.Size</c> is copied from
/// <see cref="BaseItem.Size"/> when the media sources are materialised.
/// </para>
/// </remarks>
public sealed class RemoteSizeService : IDisposable
{
    /// <summary>
    /// A .strm file is a few hundred bytes of text, so an item whose stored size is below this
    /// threshold cannot be showing the size of the real media. This is a heuristic on the
    /// <b>stored</b> value only; the acceptance threshold for a response is
    /// <see cref="MinAcceptableRemoteSize"/>.
    /// </summary>
    public const long MinimumPlausibleMediaSize = 1024L * 1024L;

    /// <summary>
    /// A response smaller than this cannot be a media file, so an HTML error page (200 + a few
    /// KB) is never persisted as a media size.
    /// </summary>
    /// <remarks>
    /// Deliberately <b>much lower</b> than <see cref="MinimumPlausibleMediaSize"/>: a short clip or
    /// a music video can legitimately be a few hundred KB. Using the 1 MiB threshold here made
    /// such items unresolvable forever — every attempt was rejected, nothing was persisted, and
    /// the item was re-queued on every browse.
    /// </remarks>
    public const long MinAcceptableRemoteSize = 4L * 1024L;

    /// <summary>
    /// Upper bounds for the caches. They only avoid repeated requests, so dropping entries when
    /// full is harmless; they must never grow with the number of items ever probed.
    /// </summary>
    private const int MaxTrackedItems = 8192;

    private const int MaxTrackedUrls = 8192;

    /// <summary>
    /// Concurrent requests against stream endpoints. The resolutions are cheap header requests,
    /// but they hit the same upstreams as playback, so the fan-out stays small.
    /// </summary>
    private const int MaxConcurrentResolutions = 2;

    /// <summary>
    /// How long a resolved size is trusted. Sizes rarely change, but a replaced file must not be
    /// stuck with a stale value for the lifetime of the server process.
    /// </summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(6);

    /// <summary>
    /// Age at which a URL cache entry stops being trusted by a forced (full-library) run. Within
    /// this window a forced run reuses the entry, so several items sharing one URL cost a single
    /// request inside the same run; past it the forced run re-resolves, which is what "full probe
    /// replaces every field with fresh data" actually means.
    /// </summary>
    private static readonly TimeSpan ForceReuseWindow = TimeSpan.FromMinutes(5);

    private static readonly HttpClient SharedHttpClient = CreateHttpClient();

    private readonly HttpClient _http;

    /// <summary>Test-only override of <see cref="AllowPrivateNetworkTargets"/>.</summary>
    private readonly bool? _fixedAllowPrivateTargets;

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<RemoteSizeService> _logger;
    private readonly SemaphoreSlim _gate = new(MaxConcurrentResolutions, MaxConcurrentResolutions);

    /// <summary>Item Id → resolved size. Read by the media-source decorator on the hot path.</summary>
    private readonly ConcurrentDictionary<Guid, CachedSize> _itemSizes = new();

    /// <summary>Stream URL → resolved size, so several items sharing one URL cost a single request.</summary>
    private readonly ConcurrentDictionary<string, CachedSize> _urlSizes = new(StringComparer.Ordinal);

    public RemoteSizeService(ILibraryManager libraryManager, ILogger<RemoteSizeService> logger)
        : this(libraryManager, logger, null)
    {
    }

    /// <summary>
    /// Test seam: a replacement transport lets the whole HTTP path (HEAD → ranged GET fallback,
    /// target filtering, cache reuse) be covered by unit tests. The container uses the public
    /// constructor and therefore the process-wide shared client.
    /// </summary>
    /// <param name="allowPrivateTargets">
    /// Pins the target policy for the test; <c>null</c> reads the plugin configuration.
    /// </param>
    internal RemoteSizeService(
        ILibraryManager libraryManager,
        ILogger<RemoteSizeService> logger,
        HttpMessageHandler? handler,
        bool? allowPrivateTargets = null)
    {
        _libraryManager = libraryManager;
        _logger = logger;
        _fixedAllowPrivateTargets = allowPrivateTargets;
        _http = handler is null
            ? SharedHttpClient
            : new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>
    /// Gets a value indicating whether loopback/private stream targets are allowed. Read per
    /// connection (not cached) because the connect callback that enforces the target policy is
    /// shared by every request and a configuration change must apply without a restart.
    /// </summary>
    private bool AllowPrivateNetworkTargets
        => _fixedAllowPrivateTargets ?? ReadAllowPrivateNetworkTargets();

    private static bool ReadAllowPrivateNetworkTargets()
        => Plugin.Instance?.Configuration.AllowPrivateNetworkTargets
           ?? PluginConfiguration.DefaultAllowPrivateNetworkTargets;

    /// <summary>
    /// Returns true when the stored size cannot be the size of the real media file, i.e. the item
    /// still carries the length of its own .strm text file (or no size at all).
    /// </summary>
    public static bool NeedsRemoteSize(BaseItem item)
        => item.Size is null || item.Size.Value < MinimumPlausibleMediaSize;

    /// <summary>
    /// 统计口径：条目是否**仍未**补全远端大小。
    /// </summary>
    /// <remarks>
    /// <see cref="NeedsRemoteSize"/> 是补全动作的门槛，故意保守（&lt;1 MiB 一律当作待补全），
    /// 由 <see cref="IsAlreadyResolved"/> 在真正发请求前短路。但拿它做统计会把 4 KiB–1 MiB
    /// 的短片/音乐视频永远算成「缺少文件大小」：值明明已经写对了，控制台计数却不下降，
    /// 看起来就像补全没触发。这里用 .strm 文本文件的实际长度区分两者，且只在真正有歧义的
    /// 区间才做一次文件 stat（几百字节或 ≥1 MiB 的常见情况直接判定，无额外 I/O）。
    /// </remarks>
    public static bool IsRemoteSizeMissing(BaseItem item)
    {
        if (!NeedsRemoteSize(item))
        {
            return false;
        }

        if (item.Size is not long stored || stored <= 0)
        {
            return true;
        }

        if (stored < MinAcceptableRemoteSize)
        {
            // 比接受下限还小，只可能是 .strm 文本文件本身的长度。
            return true;
        }

        try
        {
            if (string.IsNullOrEmpty(item.Path))
            {
                return true;
            }

            var strmFileLength = new FileInfo(item.Path).Length;
            return strmFileLength <= 0 || stored == strmFileLength;
        }
        catch (Exception)
        {
            // 文件读不到时按「仍缺失」处理，宁可多补一次，也不要把真实缺失藏起来。
            return true;
        }
    }

    /// <summary>
    /// Returns true when the stored size is already a resolved remote size that happens to be
    /// below <see cref="MinimumPlausibleMediaSize"/> (a short clip or a music video), so it must
    /// not be resolved again.
    /// </summary>
    /// <remarks>
    /// Only a value that differs from the length of the .strm text file can be such a size: an
    /// item size equal to the text file length means a library scan wiped the real value and the
    /// item genuinely needs resolving again.
    /// </remarks>
    private static bool IsAlreadyResolved(BaseItem item, long strmFileLength)
        => strmFileLength > 0
           && item.Size is long stored
           && stored > 0
           && stored != strmFileLength
           && stored < MinimumPlausibleMediaSize;

    /// <summary>
    /// Returns a cached remote size for the item, or null when nothing usable is cached yet.
    /// Never performs I/O — safe to call from the PlaybackInfo/static-source hot path.
    /// </summary>
    public long? TryGetCachedSize(Guid itemId)
    {
        if (!_itemSizes.TryGetValue(itemId, out var cached))
        {
            return null;
        }

        if (cached.IsExpired())
        {
            _itemSizes.TryRemove(itemId, out _);
            return null;
        }

        return cached.Size;
    }

    /// <summary>
    /// Applies an already known size to media sources for display only.
    /// </summary>
    /// <remarks>
    /// <b>Never triggers a lookup.</b> 补全远端文件大小只允许由三条探测链路发起：入库自动探测、
    /// 起播后补探测、手动增量/全量探测。取源（PlaybackInfo）与浏览详情页都属于「其他情况」，
    /// 这里只读进程内缓存，缓存未命中就保持数据库现值，绝不排队、绝不发请求。
    /// </remarks>
    public void ApplyCachedSizeToSources(BaseItem item, IReadOnlyList<MediaSourceInfo>? sources)
    {
        if (sources is null || sources.Count == 0)
        {
            return;
        }

        var size = TryGetCachedSize(item.Id);
        if (size is null || (!NeedsRemoteSize(item) && item.Size == size))
        {
            return;
        }

        foreach (var source in sources)
        {
            if (source.Size != size)
            {
                source.Size = size;
            }
        }
    }

    /// <summary>
    /// Resolves the remote size of the item's .strm target and persists it.
    /// Returns the size on success, or null when it could not be determined.
    /// </summary>
    /// <param name="item">The .strm item.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="force">
    /// Re-resolve and overwrite even when the stored size already looks plausible. Set by the
    /// full-library probe, whose contract is to replace every field with fresh data. All other
    /// entry points pass <c>false</c>, so an item that already has a real size is never resolved
    /// twice — no .strm read and no request to the stream endpoint. A forced run still reuses a
    /// URL cache entry that is younger than <see cref="ForceReuseWindow"/>, so items sharing one
    /// URL cost a single request inside the same run.
    /// </param>
    /// <remarks>
    /// When the .strm file is replaced, the next library scan resets <see cref="BaseItem.Size"/>
    /// to the length of the new text file, so the item falls back under
    /// <see cref="NeedsRemoteSize"/> and is resolved again automatically even without
    /// <paramref name="force"/>.
    /// </remarks>
    public async Task<long?> ResolveAsync(BaseItem item, CancellationToken cancellationToken, bool force = false)
    {
        if (!IsStrmItem(item))
        {
            return null;
        }

        if (!force && !NeedsRemoteSize(item))
        {
            return null;
        }

        var url = ReadRemoteUrl(item.Path, out var strmFileLength);
        if (url is null)
        {
            _logger.LogWarning(
                "FastSTRM: No usable http(s) URL in the .strm file of {Name}; the remote size stays unchanged. "
                + "The first non-empty, non-# line must be an absolute http/https URL.",
                LogSafe.Name(item));
            return null;
        }

        // The stored size is below the plausibility threshold but is NOT the length of the .strm
        // text file: a previous run already wrote a size that is genuinely small (short clip,
        // music video). Without this check such an item is treated as "missing" forever and every
        // probe run would re-resolve an already correct value.
        if (!force && IsAlreadyResolved(item, strmFileLength))
        {
            return item.Size;
        }

        // A known URL is still the same remote object, so the cache is reused even in a forced
        // run — but only inside ForceReuseWindow, so a forced run really re-resolves stale values
        // instead of re-writing what was cached hours ago.
        if (_urlSizes.TryGetValue(url, out var knownUrlSize)
            && !knownUrlSize.IsExpired()
            && (!force || !knownUrlSize.IsOlderThan(ForceReuseWindow)))
        {
            Remember(item.Id, knownUrlSize.Size);
            await PersistAsync(item, knownUrlSize.Size, cancellationToken, force).ConfigureAwait(false);
            return knownUrlSize.Size;
        }

        long? size;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            size = await QueryRemoteSizeAsync(url, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        if (size is null)
        {
            // 补全已经触发但没拿到长度。原来只有 HTTP 层的 Debug 日志，默认日志级别下
            // 管理员完全看不到，会误判成「补全没触发」。这里给一条可见的汇总，带上
            // 脱敏后的地址和最可能的三个原因。
            _logger.LogWarning(
                "FastSTRM: Could not resolve the remote size of {Name} from {Url}; the stored size stays unchanged. "
                + "Check that the endpoint answers HEAD or a ranged GET with Content-Length/Content-Range, "
                + "raise 'Size request timeout' if it is slow, and verify the allowed-target setting.",
                LogSafe.Name(item),
                UrlForLog(url));
            return null;
        }

        Remember(item.Id, size.Value);
        RememberUrl(url, size.Value);
        await PersistAsync(item, size.Value, cancellationToken, force).ConfigureAwait(false);
        return size;
    }

    // ── HTTP ──

    /// <summary>
    /// Asks the stream endpoint for the size of the remote object.
    /// </summary>
    /// <remarks>
    /// Internal so the unit tests can drive it with a fake transport; everything it does is local
    /// to one URL (target filtering + HEAD/ranged-GET fallback).
    /// </remarks>
    internal async Task<long?> QueryRemoteSizeAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        // Fast, clear rejection for literal addresses (127.0.0.1, 169.254.169.254, 10.0.0.5, …).
        // Host names are resolved later, in the connect callback, which also covers redirects.
        if (StreamTargetPolicy.EvaluateLiteralHost(uri.DnsSafeHost, AllowPrivateNetworkTargets) == false)
        {
            _logger.LogWarning(
                "FastSTRM: Refusing to query {Url}; its address is not an allowed stream target. "
                + "Enable 'Allow requests to private network addresses' if this is a LAN media server.",
                UrlForLog(url));
            return null;
        }

        var fromHead = await TryHeadAsync(uri, url, cancellationToken).ConfigureAwait(false);
        if (fromHead is not null)
        {
            return fromHead;
        }

        return await TryRangedGetAsync(uri, url, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// HEAD is the cheapest way to ask for a length, but plenty of stream endpoints reject it
    /// (405/501) or answer without Content-Length, so the caller always falls back to a ranged GET.
    /// Each attempt gets its own deadline: a HEAD that only ran out of time must not consume the
    /// budget of the GET that follows it.
    /// </summary>
    private async Task<long?> TryHeadAsync(Uri uri, string url, CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutCts = BeginRequestTimeout(cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Head, uri);
            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode && IsAcceptableRemoteSize(response.Content.Headers.ContentLength))
            {
                return response.Content.Headers.ContentLength;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("FastSTRM: The HEAD request for {Url} timed out, trying a ranged GET", UrlForLog(url));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "FastSTRM: The HEAD request for {Url} failed", UrlForLog(url));
        }

        return null;
    }

    private async Task<long?> TryRangedGetAsync(Uri uri, string url, CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutCts = BeginRequestTimeout(cancellationToken);
            var token = timeoutCts.Token;

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Range = new RangeHeaderValue(0, 0);

            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);

            // 206 answers carry the total length in Content-Range; a 200 answer means the server
            // ignored the range, so only its Content-Length is usable (and the body is discarded
            // right away without being read).
            var totalLength = response.Content.Headers.ContentRange?.Length;
            if (IsAcceptableRemoteSize(totalLength))
            {
                // Drain the single byte the range asked for. Without this the connection is closed
                // on dispose instead of returning to the pool, and every lookup pays a full
                // TCP+TLS handshake.
                await DrainTinyBodyAsync(response, token).ConfigureAwait(false);
                return totalLength;
            }

            if (response.StatusCode == HttpStatusCode.OK && IsAcceptableRemoteSize(response.Content.Headers.ContentLength))
            {
                return response.Content.Headers.ContentLength;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("FastSTRM: The size request for {Url} timed out", UrlForLog(url));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "FastSTRM: The size request for {Url} failed", UrlForLog(url));
        }

        return null;
    }

    private static CancellationTokenSource BeginRequestTimeout(CancellationToken cancellationToken)
    {
        var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(GetTimeoutSeconds()));
        return timeoutCts;
    }

    /// <summary>
    /// Reads (and discards) a body that is known to be a few bytes, so the connection goes back to
    /// the pool. Anything larger is left unread on purpose: a server that ignores the range could
    /// otherwise make us download the whole file.
    /// </summary>
    private static async Task DrainTinyBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        const long MaxDrainedBytes = 64L * 1024L;

        var length = response.Content.Headers.ContentLength;
        if (length is null || length > MaxDrainedBytes)
        {
            return;
        }

        try
        {
            await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Draining is an optimisation only; failure to read it changes nothing but the pool.
        }
    }

    private static bool IsAcceptableRemoteSize(long? size)
        => size.HasValue && size.Value >= MinAcceptableRemoteSize;

    private static int GetTimeoutSeconds()
        => Plugin.Instance?.Configuration.GetRemoteSizeTimeoutSeconds()
           ?? PluginConfiguration.DefaultRemoteSizeTimeoutSeconds;

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            // Redirects stay enabled for legitimate CDN hops, but every hop is re-validated by the
            // connect callback, so a redirect cannot smuggle the request into a blocked range.
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 3,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectCallback = ConnectCheckedAsync
        };

        var client = new HttpClient(handler)
        {
            // Per-request cancellation (see QueryRemoteSizeAsync) controls the deadline, so the
            // client-wide timeout must not cut a slow-but-progressing request short.
            Timeout = Timeout.InfiniteTimeSpan
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("FastSTRM/3.4 (+jellyfin)");
        return client;
    }

    /// <summary>
    /// Validates the address that is about to be connected to. This is the only interception point
    /// that covers both the resolved address of a host name and the target of an HTTP redirect,
    /// and it validates the actual IP, which also defeats DNS rebinding.
    /// </summary>
    private static async ValueTask<Stream> ConnectCheckedAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        var port = context.DnsEndPoint.Port;
        var allowPrivate = ReadAllowPrivateNetworkTargets();

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectCts.CancelAfter(TimeSpan.FromSeconds(10));

        IPAddress[] addresses;
        if (IPAddress.TryParse(host.Trim('[', ']'), out var literal))
        {
            addresses = new[] { literal };
        }
        else
        {
            addresses = await Dns.GetHostAddressesAsync(host, connectCts.Token).ConfigureAwait(false);
        }

        // IPv4 first: a dual-stack name whose AAAA record is unreachable should not fail the
        // request just because the resolver returned IPv6 first.
        var candidates = addresses
            .Where(address => StreamTargetPolicy.IsAllowed(address, allowPrivate))
            .OrderBy(address => address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .ToArray();

        if (candidates.Length == 0)
        {
            // Surfaces as an HttpRequestException at the call site, which is logged and treated as
            // "size unknown" — never as a playback or probe failure.
            throw new HttpRequestException(
                $"FastSTRM: refusing to connect to {host}:{port}; its address is not an allowed stream target");
        }

        Exception? lastError = null;
        foreach (var candidate in candidates)
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(candidate, port), connectCts.Token).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex)
            {
                socket.Dispose();
                lastError = ex;

                if (connectCts.IsCancellationRequested)
                {
                    throw;
                }
            }
        }

        throw new HttpRequestException($"FastSTRM: unable to connect to {host}:{port}", lastError);
    }

    // ── Persistence ──

    private async Task PersistAsync(BaseItem item, long size, CancellationToken cancellationToken, bool force)
    {
        if (item.Size == size)
        {
            return;
        }

        if (!force && !NeedsRemoteSize(item))
        {
            // Outside a forced run, never overwrite a size that is already plausible: it may come
            // from a real probe.
            return;
        }

        var previousSize = item.Size;
        item.Size = size;

        try
        {
            await _libraryManager
                .UpdateItemAsync(item, item.GetParent(), ItemUpdateType.MetadataEdit, cancellationToken)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "FastSTRM: Wrote the remote size of {Name} ({Size} bytes) in place of the .strm file size",
                LogSafe.Name(item),
                size);
        }
        catch (OperationCanceledException)
        {
            // Shutdown. Roll the in-memory value back too: otherwise this object reports a
            // plausible size, NeedsRemoteSize goes false and no later run retries the DB write.
            item.Size = previousSize;
        }
        catch (Exception ex)
        {
            item.Size = previousSize;
            _logger.LogWarning(ex, "FastSTRM: Unable to persist the remote size of {Name}", LogSafe.Name(item));
        }
    }

    // ── .strm parsing ──

    /// <summary>
    /// Returns the stream URL stored in the .strm file, or null when the file holds no absolute
    /// HTTP(S) URL. The file is untrusted input: only the first non-empty, non-comment line is
    /// considered and it must parse as an absolute http/https URI.
    /// </summary>
    /// <param name="path">Path of the .strm file.</param>
    /// <param name="fileLength">
    /// Length of the .strm text file itself, or 0 when it could not be read. A stored item size
    /// equal to this value is the size of the text file, not of the media behind it.
    /// </param>
    private string? ReadRemoteUrl(string? path, out long fileLength)
    {
        fileLength = 0;

        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fileLength = stream.Length;

            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            // A .strm file is one URL, occasionally preceded by blank lines or a comment.
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
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "FastSTRM: Unable to read {Path}", path);
        }

        return null;
    }

    // ── Cache ──

    private void Remember(Guid itemId, long size)
    {
        PruneIfNeeded(_itemSizes, MaxTrackedItems);
        _itemSizes[itemId] = new CachedSize(size, DateTime.UtcNow.Ticks);
    }

    private void RememberUrl(string url, long size)
    {
        PruneIfNeeded(_urlSizes, MaxTrackedUrls);
        _urlSizes[url] = new CachedSize(size, DateTime.UtcNow.Ticks);
    }

    /// <summary>
    /// Keeps a cache bounded: expired entries go first, and a cache still full afterwards is
    /// cleared, which only costs repeated requests.
    /// </summary>
    private static void PruneIfNeeded<TKey>(ConcurrentDictionary<TKey, CachedSize> cache, int capacity)
        where TKey : notnull
    {
        if (cache.Count < capacity)
        {
            return;
        }

        foreach (var pair in cache)
        {
            if (pair.Value.IsExpired())
            {
                cache.TryRemove(pair.Key, out _);
            }
        }

        if (cache.Count >= capacity)
        {
            cache.Clear();
        }
    }

    private static bool IsStrmItem(BaseItem item)
        => item.Path is not null
           && item.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Renders a stream URL for the log without leaking signed query parameters or control
    /// characters: only scheme/host/path are kept.
    /// </summary>
    private static string UrlForLog(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath}"
            : "(invalid url)";

    /// <summary>
    /// Nothing to release: the shared <see cref="HttpClient"/> is process-wide and the test-only
    /// transport is owned by the test. Every request is owned and cancelled by the calling probe
    /// flow, so this service no longer owns background tasks.
    /// </summary>
    public void Dispose()
    {
    }

    private readonly struct CachedSize
    {
        public CachedSize(long size, long utcTicks)
        {
            Size = size;
            UtcTicks = utcTicks;
        }

        public long Size { get; }

        private long UtcTicks { get; }

        public bool IsExpired() => DateTime.UtcNow.Ticks - UtcTicks > CacheTtl.Ticks;

        public bool IsOlderThan(TimeSpan age) => DateTime.UtcNow.Ticks - UtcTicks > age.Ticks;
    }
}
