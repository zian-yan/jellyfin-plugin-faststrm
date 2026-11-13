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
    /// <remarks>
    /// 下限保持 2，使「探测并发数 = 1」时大小补全仍能有一点流水线；上限 8 防止一次全量探测
    /// 把上游打满。实际取值跟随 <c>MaxConcurrentProbes</c>，配置保存后立即生效。
    /// </remarks>
    private const int MinConcurrentResolutions = 2;

    private const int MaxConcurrentResolutions = 8;

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

    /// <summary>
    /// 限制同时在途的大小请求数。上限跟随配置，且每次占位时重新读取，因此不需要重建对象。
    /// </summary>
    private readonly ConcurrencyGate _gate = new(ReadResolutionLimit);

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
    /// 大小请求的并发上限：跟随「探测并发数」配置，夹在 2–8 之间。
    /// </summary>
    private static int ReadResolutionLimit()
    {
        var configured = Plugin.Instance?.Configuration.GetMaxConcurrentProbes()
                         ?? PluginConfiguration.MinConcurrentProbes;

        return Math.Clamp(configured, MinConcurrentResolutions, MaxConcurrentResolutions);
    }

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
    /// <b>Never triggers a lookup.</b> 补全远端文件大小只允许由 <see cref="RemoteSizeTrigger"/> 里
    /// 登记的场景发起：入库自动探测、起播后补探测、手动增量探测、手动全量探测。取源（PlaybackInfo）
    /// 与浏览详情页都属于「其他情况」，这里只读进程内缓存，缓存未命中就保持数据库现值，
    /// 绝不排队、绝不发请求。
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
    /// <param name="trigger">
    /// 触发来源，见 <see cref="RemoteSizeTrigger"/>。这是补全的**准入凭证**：
    /// 只有入库自动探测、起播后补探测、手动增量探测、手动全量探测会补全，
    /// 其余来源（含 <see cref="RemoteSizeTrigger.None"/> 与未定义值）直接返回
    /// <c>null</c>，不读 .strm、不发请求、不写库。判定集中在
    /// <see cref="RemoteSizeTriggerPolicy.AllowsSizeFill"/>，调用点无法绕过。
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// <para>
    /// 「是否重新解析」由来源推导（见 <see cref="RemoteSizeTriggerPolicy.RequiresRefresh"/>），
    /// 不再是独立参数：全量探测重新解析并覆盖，其余三个来源只补仍然缺失的大小，
    /// 因此一个已有真实大小的条目不会被重复解析。
    /// </para>
    /// <para>
    /// When the .strm file is replaced, the next library scan resets <see cref="BaseItem.Size"/>
    /// to the length of the new text file, so the item falls back under
    /// <see cref="NeedsRemoteSize"/> and is resolved again automatically by the next probe run.
    /// </para>
    /// </remarks>
    public async Task<long?> ResolveAsync(
        BaseItem item,
        RemoteSizeTrigger trigger,
        CancellationToken cancellationToken)
    {
        // 准入判定放在最前面：来源不合法时连 .strm 都不读，杜绝任何「顺手补一次」的路径。
        if (!RemoteSizeTriggerPolicy.AllowsSizeFill(trigger))
        {
            _logger.LogDebug(
                "FastSTRM: Not resolving the remote size of {Name}; {Trigger} is not a probe trigger",
                LogSafe.Name(item),
                RemoteSizeTriggerPolicy.Describe(trigger));
            return null;
        }

        if (!IsStrmItem(item))
        {
            return null;
        }

        var force = RemoteSizeTriggerPolicy.RequiresRefresh(trigger);

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
        string diagnostic;
        await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            (size, diagnostic) = await QueryRemoteSizeWithDiagnosticsAsync(url, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        if (size is null)
        {
            // 补全已经触发但没拿到长度。原来只有 HTTP 层的 Debug 日志，默认日志级别下
            // 管理员完全看不到，会误判成「补全没触发」。这里给一条可见的汇总，
            // 直接带上每一跳的结论（状态码 / 超时 / 异常），并点出传输方式，
            // 这样「有时能补、有时不能」不必开 Debug 也能定位到是源站还是代理。
            _logger.LogWarning(
                "FastSTRM: Could not resolve the remote size of {Name} from {Url}; the stored size stays unchanged. "
                + "Reason: {Reason}. Requests go {Transport}. "
                + "Check that the endpoint answers HEAD or a ranged GET with Content-Length/Content-Range, "
                + "raise 'Size request timeout' if it is slow, and verify the allowed-target setting.",
                LogSafe.Name(item),
                UrlForLog(url),
                diagnostic,
                DescribeTransport());
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
        => (await QueryRemoteSizeWithDiagnosticsAsync(url, cancellationToken).ConfigureAwait(false)).Size;

    /// <summary>
    /// Same as <see cref="QueryRemoteSizeAsync"/>, but also returns a short, log-safe explanation of
    /// what the endpoint actually answered.
    /// </summary>
    /// <remarks>
    /// 一条「拿不到长度」的日志如果只说拿不到，管理员无从区分是「HEAD 被拒」「源站不回长度」
    /// 还是「网关/代理拦了请求」，只能靠开 Debug 复现。这里把每一跳的结论累积下来，
    /// 由调用方一次性写进 Warning，失败原因当场可见。
    /// </remarks>
    internal async Task<(long? Size, string Diagnostic)> QueryRemoteSizeWithDiagnosticsAsync(
        string url,
        CancellationToken cancellationToken)
    {
        var notes = new LookupNotes();

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return (null, "the url is not an absolute uri");
        }

        var allowPrivate = AllowPrivateNetworkTargets;

        // Fast, clear rejection for literal addresses (127.0.0.1, 169.254.169.254, 10.0.0.5, …).
        // Host names are resolved later, in the connect callback, which also covers redirects.
        var literal = StreamTargetPolicy.EvaluateLiteralHost(uri.DnsSafeHost, allowPrivate);
        if (literal == false)
        {
            _logger.LogWarning(
                "FastSTRM: Refusing to query {Url}; its address is not an allowed stream target. "
                + "Enable 'Allow requests to private network addresses' if this is a LAN media server.",
                UrlForLog(url));
            return (null, "the target address is not allowed by the allowed-target setting");
        }

        // 走代理时 connect callback 看到的是代理地址，不是目标地址，主机名这一层的判定会整段失效
        // （含云厂商元数据端点 169.254.169.254 这类「永远拒绝」的地址）。因此只在请求会经过代理时
        // 先自行解析一次并判定；直连场景仍由 connect callback 兜底，不多付一次 DNS。
        if (literal is null
            && IsProxied(uri)
            && !await AreResolvedTargetsAllowedAsync(uri.DnsSafeHost, allowPrivate, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogWarning(
                "FastSTRM: Refusing to query {Url} through the configured proxy; its resolved address is not an allowed stream target "
                + "(the connect callback only sees the proxy address, so a private or link-local target would not be detected there)",
                UrlForLog(url));
            return (null, "the resolved address is not allowed by the allowed-target setting");
        }

        var fromHead = await TryHeadAsync(uri, url, notes, cancellationToken).ConfigureAwait(false);
        if (fromHead is not null)
        {
            return (fromHead, "ok (HEAD)");
        }

        var fromGet = await TryRangedGetAsync(uri, url, notes, cancellationToken).ConfigureAwait(false);
        return fromGet is not null
            ? (fromGet, "ok (ranged GET)")
            : (null, notes.Summarize());
    }

    /// <summary>
    /// 判断该请求是否会被 <see cref="HttpClient"/> 交给代理转发。
    /// </summary>
    /// <remarks>
    /// 无代理时 <see cref="HttpClient.DefaultProxy"/> 返回的就是目标地址本身；返回别的地址
    /// （或解析失败）才说明这一跳不由本进程直连。判定失败时按「不走代理」处理，此时
    /// connect callback 的校验仍然有效，不会因此放宽安全检查。
    /// </remarks>
    private static bool IsProxied(Uri uri)
    {
        try
        {
            var proxy = HttpClient.DefaultProxy?.GetProxy(uri);
            return proxy is not null && !proxy.Equals(uri);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 解析主机名并判定结果是否全部落在允许范围内。
    /// </summary>
    /// <remarks>
    /// 与 <c>ConnectCallback</c> 保持同一语义：只要存在一个允许的地址就放行，由建连阶段挑用
    /// 允许的那个。解析失败时放行——那种情况下这次请求本来也连不上目标，而把一次 DNS 抖动
    /// 判成「目标非法」会让条目的远端大小永久补不上。
    /// </remarks>
    private static async Task<bool> AreResolvedTargetsAllowedAsync(
        string host,
        bool allowPrivateTargets,
        CancellationToken cancellationToken)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
            return addresses.Length == 0
                   || addresses.Any(address => StreamTargetPolicy.IsAllowed(address, allowPrivateTargets));
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>
    /// HEAD is the cheapest way to ask for a length, but plenty of stream endpoints reject it
    /// (405/501) or answer without Content-Length, so the caller always falls back to a ranged GET.
    /// Each attempt gets its own deadline: a HEAD that only ran out of time must not consume the
    /// budget of the GET that follows it.
    /// </summary>
    private async Task<long?> TryHeadAsync(
        Uri uri,
        string url,
        LookupNotes notes,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutCts = BeginRequestTimeout(cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Head, uri);
            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                // 一个 >= 4 KiB 的 HTML 错误页同样带 Content-Length，写进 Size 就是错的数据。
                // 流地址永远不该返回 text/html，直接判为无效。
                if (IsHtmlResponse(response))
                {
                    notes.Add($"HEAD returned {DescribeStatus(response)} as {DescribeContentType(response)}, which is not a media file");
                    return null;
                }

                var contentLength = response.Content.Headers.ContentLength;
                if (IsAcceptableRemoteSize(contentLength))
                {
                    return contentLength;
                }

                notes.Add($"HEAD returned {DescribeStatus(response)} without a usable Content-Length ({DescribeLength(contentLength)})");
                return null;
            }

            notes.Add($"HEAD returned {DescribeStatus(response)}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            notes.Add($"HEAD timed out after {GetTimeoutSeconds()}s");
            _logger.LogDebug("FastSTRM: The HEAD request for {Url} timed out, trying a ranged GET", UrlForLog(url));
        }
        catch (HttpRequestException ex)
        {
            notes.Add("HEAD failed: " + Short(ex.Message));
            _logger.LogDebug(ex, "FastSTRM: The HEAD request for {Url} failed", UrlForLog(url));
        }

        return null;
    }

    /// <summary>
    /// Fallback for endpoints that reject or mishandle <c>HEAD</c>: asks for a two-byte range and
    /// reads the total length out of <c>Content-Range</c>.
    /// </summary>
    /// <remarks>
    /// <b>Why two bytes and not one:</b> <c>Range: bytes=0-0</c> is a legal request, but some CDNs
    /// (observed on 光雅 / guangyacdn smartstrm links) special-case a single-byte range and answer
    /// <c>200</c> with <c>Content-Length: 1</c> and <b>no</b> <c>Content-Range</c> — the total length
    /// is then unobtainable and every probe of that item fails with "could not resolve the remote
    /// size". The same host answers <c>bytes=0-1</c> with a proper <c>206</c> +
    /// <c>Content-Range: bytes 0-1/&lt;total&gt;</c>, so the request asks for two bytes. The extra byte
    /// costs nothing: it is drained below to keep the connection pooled.
    /// </remarks>
    private async Task<long?> TryRangedGetAsync(
        Uri uri,
        string url,
        LookupNotes notes,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutCts = BeginRequestTimeout(cancellationToken);
            var token = timeoutCts.Token;

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Range = new RangeHeaderValue(0, 1);

            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);

            // 206 answers carry the total length in Content-Range; a 200 answer means the server
            // ignored the range, so only its Content-Length is usable (and the body is discarded
            // right away without being read).
            if (IsHtmlResponse(response))
            {
                notes.Add($"{DescribeStatus(response)} answered as {DescribeContentType(response)}, which is not a media file");
                return null;
            }

            var totalLength = response.Content.Headers.ContentRange?.Length;
            if (IsAcceptableRemoteSize(totalLength))
            {
                // Drain the two bytes the range asked for. Without this the connection is closed
                // on dispose instead of returning to the pool, and every lookup pays a full
                // TCP+TLS handshake.
                await DrainTinyBodyAsync(response, token).ConfigureAwait(false);
                return totalLength;
            }

            var contentLength = response.Content.Headers.ContentLength;
            if (response.StatusCode == HttpStatusCode.OK && IsAcceptableRemoteSize(contentLength))
            {
                return contentLength;
            }

            notes.Add(
                $"{DescribeStatus(response)} without a usable Content-Range ({DescribeLength(totalLength)}), "
                + $"Content-Length {DescribeLength(contentLength)}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            notes.Add($"ranged GET timed out after {GetTimeoutSeconds()}s");
            _logger.LogDebug("FastSTRM: The size request for {Url} timed out", UrlForLog(url));
        }
        catch (HttpRequestException ex)
        {
            notes.Add("ranged GET failed: " + Short(ex.Message));
            _logger.LogDebug(ex, "FastSTRM: The size request for {Url} failed", UrlForLog(url));
        }

        return null;
    }

    /// <summary>
    /// Renders the response status (and its reason phrase) for a diagnostic note. The reason phrase
    /// comes from the remote server, so it is sanitized and shortened before it reaches the log.
    /// </summary>
    private static string DescribeStatus(HttpResponseMessage response)
        => $"HTTP {(int)response.StatusCode} {Short(response.ReasonPhrase ?? string.Empty, 40)}".TrimEnd();

    private static string DescribeLength(long? length)
        => length is long value ? value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "none";

    /// <summary>
    /// 判断响应是不是一个 HTML 页面（网关错误页、登录页、分享链接失效页都会这样返回）。
    /// </summary>
    private static bool IsHtmlResponse(HttpResponseMessage response)
        => response.Content.Headers.ContentType?.MediaType is string mediaType
           && mediaType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase);

    private static string DescribeContentType(HttpResponseMessage response)
        => Short(response.Content.Headers.ContentType?.MediaType ?? "an unknown content type", 40);

    /// <summary>
    /// Sanitizes a value that originated from the remote server (exception text, reason phrase):
    /// control characters are neutralized so a hostile endpoint cannot forge log lines, and the
    /// length is capped because the value is repeated for every failed item.
    /// </summary>
    private static string Short(string value, int maxLength = 160)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var chars = (value.Length <= maxLength ? value : value[..maxLength] + "…").ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (char.IsControl(chars[i]))
            {
                chars[i] = ' ';
            }
        }

        return new string(chars).Trim();
    }

    /// <summary>
    /// Describes how size lookups leave the process, so "sometimes it works" is diagnosable from the
    /// startup log alone: <c>.NET</c> lets <see cref="HttpClient"/> pick the proxy from the
    /// environment, and a proxy that drops <c>CONNECT</c>/<c>HEAD</c> looks exactly like a flaky
    /// origin.
    /// </summary>
    public static string DescribeTransport()
    {
        foreach (var name in ProxyEnvironmentVariables)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return "through proxy " + RedactProxy(value);
            }
        }

        return "directly (no proxy configured)";
    }

    private static readonly string[] ProxyEnvironmentVariables =
    {
        "HTTPS_PROXY", "https_proxy", "HTTP_PROXY", "http_proxy", "ALL_PROXY", "all_proxy"
    };

    /// <summary>
    /// Strips credentials from a proxy URL: the value is an environment variable and may carry
    /// <c>user:password@</c>, which must not be written to the server log.
    /// </summary>
    private static string RedactProxy(string value)
    {
        var trimmed = value.Trim();

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            var authority = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
            return $"{uri.Scheme}://{authority}";
        }

        // Not a parseable URL: keep the shape but never echo possible credentials.
        return trimmed.Contains('@', StringComparison.Ordinal)
            ? Short(trimmed[(trimmed.LastIndexOf('@') + 1)..], 80)
            : Short(trimmed, 80);
    }

    /// <summary>
    /// Accumulates one short note per failed hop, so the final warning explains what the endpoint
    /// actually answered instead of only that the size is unknown.
    /// </summary>
    private sealed class LookupNotes
    {
        private const int MaxNotes = 4;

        private readonly List<string> _notes = new();

        public void Add(string note)
        {
            if (_notes.Count < MaxNotes && !_notes.Contains(note, StringComparer.Ordinal))
            {
                _notes.Add(note);
            }
        }

        public string Summarize()
            => _notes.Count == 0 ? "the endpoint returned no usable answer" : string.Join("; ", _notes);
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
