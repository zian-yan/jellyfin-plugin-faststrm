using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
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

public sealed class RemoteSizeService : IDisposable
{
    public const long MinimumPlausibleMediaSize = 1024L * 1024L;

    public const long MinAcceptableRemoteSize = 4L * 1024L;

    private const int MaxCacheEntries = 8192;

    private const int MinConcurrentResolutions = 2;

    private const int MaxConcurrentResolutions = 8;

    private const int MaxRedirects = 3;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(6);

    private static readonly TimeSpan ForceReuseWindow = TimeSpan.FromMinutes(5);

    private static readonly HttpClient SharedHttpClient = CreateHttpClient();

    private static readonly string[] ProxyEnvironmentVariables =
    {
        "HTTPS_PROXY", "https_proxy", "HTTP_PROXY", "http_proxy", "ALL_PROXY", "all_proxy"
    };

    private readonly HttpClient _http;

    private readonly bool _ownsHttpClient;

    private readonly bool? _fixedAllowPrivateTargets;

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<RemoteSizeService> _logger;

    private readonly ConcurrencyGate _gate = new(ReadResolutionLimit);

    private readonly ConcurrentDictionary<Guid, CachedSize> _itemSizes = new();

    private readonly ConcurrentDictionary<string, CachedSize> _urlSizes = new(StringComparer.Ordinal);

    public RemoteSizeService(ILibraryManager libraryManager, ILogger<RemoteSizeService> logger)
        : this(libraryManager, logger, null)
    {
    }

    internal RemoteSizeService(
        ILibraryManager libraryManager,
        ILogger<RemoteSizeService> logger,
        HttpMessageHandler? handler,
        bool? allowPrivateTargets = null)
    {
        _libraryManager = libraryManager;
        _logger = logger;
        _fixedAllowPrivateTargets = allowPrivateTargets;
        _ownsHttpClient = handler is not null;
        _http = handler is null
            ? SharedHttpClient
            : new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private bool AllowPrivateNetworkTargets
        => _fixedAllowPrivateTargets ?? ReadAllowPrivateNetworkTargets();

    private static bool ReadAllowPrivateNetworkTargets()
        => Plugin.Instance?.Configuration.AllowPrivateNetworkTargets
           ?? PluginConfiguration.DefaultAllowPrivateNetworkTargets;

    private static int ReadResolutionLimit()
    {
        var configured = Plugin.Instance?.Configuration.GetMaxConcurrentProbes()
                         ?? PluginConfiguration.MinConcurrentProbes;

        return Math.Clamp(configured, MinConcurrentResolutions, MaxConcurrentResolutions);
    }

    public static bool NeedsRemoteSize(BaseItem item)
        => item.Size is null || item.Size.Value < MinimumPlausibleMediaSize;

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
            return true;
        }

        if (string.IsNullOrEmpty(item.Path))
        {
            return true;
        }

        // 走 StrmItem 的长度缓存：这条判断在播放取源和条目映射上都是热路径，
        // 每次 new FileInfo 都是一次多余的系统调用。
        if (!StrmItem.TryGetLength(item.Path, out var strmFileLength))
        {
            return true;
        }

        return strmFileLength <= 0 || stored == strmFileLength;
    }

    private static bool IsAlreadyResolved(BaseItem item, long strmFileLength)
        => strmFileLength > 0
           && item.Size is long stored
           && stored > 0
           && stored != strmFileLength
           && stored < MinimumPlausibleMediaSize;

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

    public void Forget(Guid itemId)
    {
        if (itemId == Guid.Empty)
        {
            return;
        }

        _itemSizes.TryRemove(itemId, out _);
    }

    public void ApplySizeToSources(BaseItem item, IReadOnlyList<MediaSourceInfo>? sources)
    {
        if (sources is null || sources.Count == 0)
        {
            return;
        }

        var size = ResolveSizeToPublish(item);
        if (size is null)
        {
            return;
        }

        for (var i = 0; i < sources.Count; i++)
        {
            var source = sources[i];
            if (source.Size != size)
            {
                source.Size = size;
            }
        }
    }

    private long? ResolveSizeToPublish(BaseItem item)
    {
        var cached = TryGetCachedSize(item.Id);
        if (cached is not null)
        {
            return cached;
        }

        return IsRemoteSizeMissing(item) ? null : item.Size;
    }

    public async Task<long?> ResolveAsync(
        BaseItem item,
        RemoteSizeTrigger trigger,
        CancellationToken cancellationToken)
    {
        if (!RemoteSizeTriggerPolicy.AllowsSizeFill(trigger))
        {
            _logger.LogDebug(
                "FastSTRM: Not resolving the remote size of {Name}; {Trigger} is not a probe trigger",
                LogSafe.Name(item),
                RemoteSizeTriggerPolicy.Describe(trigger));
            return null;
        }

        if (!StrmItem.IsStrm(item))
        {
            return null;
        }

        var force = RemoteSizeTriggerPolicy.RequiresRefresh(trigger);

        if (!force && !NeedsRemoteSize(item))
        {
            return null;
        }

        if (!StrmItem.TryReadRemoteUrl(item.Path, out var url, out var strmFileLength))
        {
            _logger.LogWarning(
                "FastSTRM: No usable http(s) URL in the .strm file of {Name}; the remote size stays unchanged. "
                + "The first non-empty, non-# line must be an absolute http/https URL.",
                LogSafe.Name(item));
            return null;
        }

        if (!force && IsAlreadyResolved(item, strmFileLength))
        {
            return item.Size;
        }

        var urlKey = SizeCacheKey(url);
        if (_urlSizes.TryGetValue(urlKey, out var knownUrlSize)
            && !knownUrlSize.IsExpired()
            && (!force || !knownUrlSize.IsOlderThan(ForceReuseWindow)))
        {
            Remember(item.Id, knownUrlSize.Size);
            await PersistAsync(item, knownUrlSize.Size, cancellationToken, force).ConfigureAwait(false);
            return knownUrlSize.Size;
        }

        RemoteSizeLookup result;
        using (await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false))
        {
            result = await LookupAsync(url, cancellationToken).ConfigureAwait(false);
        }

        if (result.Size is not long resolved)
        {
            // 逐跳细节只进 Debug；管理员可见的 Warning 只给失败分类。
            // 否则这条日志会变成内网探测的响应指纹（能区分端口开闭、服务类型）。
            _logger.LogDebug(
                "FastSTRM: Remote size lookup for {Name} from {Url} failed. Reason: {Reason}. Requests go {Transport}",
                LogSafe.Name(item),
                UrlForLog(url),
                result.Detail,
                DescribeTransport());

            _logger.LogWarning(
                "FastSTRM: Could not resolve the remote size of {Name}; the stored size stays unchanged. "
                + "Reason: {Reason}. Requests go {Transport}. "
                + "Enable debug logging for the per-hop detail, and check that the endpoint answers HEAD "
                + "or a ranged GET with Content-Length/Content-Range.",
                LogSafe.Name(item),
                result.Categories,
                DescribeTransport());
            return null;
        }

        Remember(item.Id, resolved);
        RememberUrl(url, resolved);
        await PersistAsync(item, resolved, cancellationToken, force).ConfigureAwait(false);
        return resolved;
    }

    internal async Task<long?> QueryRemoteSizeAsync(string url, CancellationToken cancellationToken)
        => (await LookupAsync(url, cancellationToken).ConfigureAwait(false)).Size;

    internal async Task<(long? Size, string Diagnostic)> QueryRemoteSizeWithDiagnosticsAsync(
        string url,
        CancellationToken cancellationToken)
    {
        var result = await LookupAsync(url, cancellationToken).ConfigureAwait(false);
        return (result.Size, result.Detail);
    }

    private async Task<RemoteSizeLookup> LookupAsync(string url, CancellationToken cancellationToken)
    {
        var notes = new LookupNotes();

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return new RemoteSizeLookup(null, "the url is not an absolute uri", "invalid-url");
        }

        if (!await IsTargetAllowedAsync(uri, cancellationToken).ConfigureAwait(false))
        {
            notes.Add("blocked-target", "the target address is not allowed by the allowed-target setting");
            return new RemoteSizeLookup(null, notes.Summarize(), notes.SummarizeCategories());
        }

        var (fromHead, finalUri, stopped) = await TryHeadAsync(uri, notes, cancellationToken).ConfigureAwait(false);
        if (fromHead is not null)
        {
            return new RemoteSizeLookup(fromHead, "ok (HEAD)", "ok");
        }

        // stopped 表示重定向链上已经出现被拒目标或跳数超限。
        // 这种情况下不能再拿 finalUri 去发 ranged GET，否则等于绕回被拦截的地址。
        if (stopped)
        {
            return new RemoteSizeLookup(null, notes.Summarize(), notes.SummarizeCategories());
        }

        var fromGet = await TryRangedGetAsync(finalUri, notes, cancellationToken).ConfigureAwait(false);
        return fromGet is not null
            ? new RemoteSizeLookup(fromGet, "ok (ranged GET)", "ok")
            : new RemoteSizeLookup(null, notes.Summarize(), notes.SummarizeCategories());
    }

    /// <summary>
    /// 目标校验。字面 IP 直接判定；域名必须解析后所有地址都落在允许范围内。
    ///
    /// 两点与旧实现不同：
    /// 1. 解析失败按「不允许」处理（fail-closed）。旧实现 catch 后 return true，
    ///    在配置了代理时（此时连接回调只看到代理地址）等于完全放行。
    /// 2. 用 All 而不是 Any。旧实现只要有一条 A 记录合法就放行，而代理侧可能
    ///    自行挑中那条非法的地址。
    /// </summary>
    private async Task<bool> IsTargetAllowedAsync(Uri uri, CancellationToken cancellationToken)
    {
        var allowPrivate = AllowPrivateNetworkTargets;
        var literal = StreamTargetPolicy.EvaluateLiteralHost(uri.DnsSafeHost, allowPrivate);

        if (literal is false)
        {
            _logger.LogWarning(
                "FastSTRM: Refusing to query {Url}; its address is not an allowed stream target. "
                + "Enable 'Allow requests to private network addresses' if this is a LAN media server.",
                UrlForLog(uri.ToString()));
            return false;
        }

        if (literal is true)
        {
            return true;
        }

        if (await AreResolvedTargetsAllowedAsync(uri.DnsSafeHost, allowPrivate, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        _logger.LogWarning(
            "FastSTRM: Refusing to query {Url}; its resolved address is not an allowed stream target. "
            + "Enable 'Allow requests to private network addresses' if this is a LAN media server.",
            UrlForLog(uri.ToString()));
        return false;
    }

    private static async Task<bool> AreResolvedTargetsAllowedAsync(
        string host,
        bool allowPrivateTargets,
        CancellationToken cancellationToken)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
            return addresses.Length > 0
                   && addresses.All(address => StreamTargetPolicy.IsAllowed(address, allowPrivateTargets));
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 手动跟随重定向，每一跳都重新做目标校验。旧实现依赖 AllowAutoRedirect，
    /// 直连时由连接回调兜底，但走代理时回调只看到代理地址，302 到内网就直接穿透了。
    /// </summary>
    private async Task<(HttpResponseMessage? Response, Uri FinalUri, bool Stopped)> SendFollowingRedirectsAsync(
        Func<Uri, HttpRequestMessage> createRequest,
        Uri startUri,
        LookupNotes notes,
        CancellationToken cancellationToken)
    {
        var current = startUri;

        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            if (hop > 0 && !await IsTargetAllowedAsync(current, cancellationToken).ConfigureAwait(false))
            {
                notes.Add("redirect", $"redirect #{hop} pointed at a target that is not allowed by the allowed-target setting");
                return (null, current, true);
            }

            using var request = createRequest(current);
            var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!IsRedirect(response.StatusCode))
            {
                return (response, current, false);
            }

            var location = response.Headers.Location;
            var status = (int)response.StatusCode;
            response.Dispose();

            if (location is null)
            {
                notes.Add("redirect", $"HTTP {status} redirect without a Location header");
                return (null, current, true);
            }

            current = location.IsAbsoluteUri ? location : new Uri(current, location);
        }

        notes.Add("redirect", $"more than {MaxRedirects} redirects");
        return (null, current, true);
    }

    private static bool IsRedirect(HttpStatusCode status)
        => status is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Redirect
            or HttpStatusCode.RedirectMethod
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private async Task<(long? Size, Uri FinalUri, bool Stopped)> TryHeadAsync(
        Uri uri,
        LookupNotes notes,
        CancellationToken cancellationToken)
    {
        var finalUri = uri;

        try
        {
            using var timeoutCts = BeginRequestTimeout(cancellationToken);
            var token = timeoutCts.Token;

            var (response, nextUri, stopped) = await SendFollowingRedirectsAsync(
                target => new HttpRequestMessage(HttpMethod.Head, target),
                uri,
                notes,
                token).ConfigureAwait(false);

            finalUri = nextUri;

            using (response)
            {
                if (stopped || response is null)
                {
                    return (null, finalUri, stopped);
                }

                if (!response.IsSuccessStatusCode)
                {
                    notes.Add("http-error", $"HEAD returned {DescribeStatus(response)}");
                    return (null, finalUri, false);
                }

                if (IsHtmlResponse(response))
                {
                    notes.Add(
                        "not-a-media-file",
                        $"HEAD returned {DescribeStatus(response)} as {DescribeContentType(response)}, which is not a media file");
                    return (null, finalUri, false);
                }

                var contentLength = response.Content.Headers.ContentLength;
                if (IsAcceptableRemoteSize(contentLength))
                {
                    return (contentLength, finalUri, false);
                }

                notes.Add(
                    "no-length",
                    $"HEAD returned {DescribeStatus(response)} without a usable Content-Length ({DescribeLength(contentLength)})");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            notes.Add("timeout", $"HEAD timed out after {GetTimeoutSeconds()}s");
            _logger.LogDebug("FastSTRM: The HEAD request for {Url} timed out, trying a ranged GET", UrlForLog(uri.ToString()));
        }
        catch (HttpRequestException ex)
        {
            notes.Add("request-failed", "HEAD failed: " + Short(ex.Message));
            _logger.LogDebug(ex, "FastSTRM: The HEAD request for {Url} failed", UrlForLog(uri.ToString()));
        }

        return (null, finalUri, false);
    }

    private async Task<long?> TryRangedGetAsync(
        Uri uri,
        LookupNotes notes,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutCts = BeginRequestTimeout(cancellationToken);
            var token = timeoutCts.Token;

            var (response, _, _) = await SendFollowingRedirectsAsync(
                target =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, target);
                    request.Headers.Range = new RangeHeaderValue(0, 1);
                    return request;
                },
                uri,
                notes,
                token).ConfigureAwait(false);

            using (response)
            {
                if (response is null)
                {
                    return null;
                }

                if (IsHtmlResponse(response))
                {
                    notes.Add(
                        "not-a-media-file",
                        $"{DescribeStatus(response)} answered as {DescribeContentType(response)}, which is not a media file");
                    return null;
                }

                var totalLength = response.Content.Headers.ContentRange?.Length;
                if (IsAcceptableRemoteSize(totalLength))
                {
                    await DrainTinyBodyAsync(response, token).ConfigureAwait(false);
                    return totalLength;
                }

                var contentLength = response.Content.Headers.ContentLength;
                if (response.StatusCode == HttpStatusCode.OK
                    && IsAcceptableRemoteSize(contentLength)
                    && !IsNonMediaContentType(response))
                {
                    return contentLength;
                }

                notes.Add(
                    "no-length",
                    $"{DescribeStatus(response)} without a usable Content-Range ({DescribeLength(totalLength)}), "
                    + $"Content-Length {DescribeLength(contentLength)}");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            notes.Add("timeout", $"ranged GET timed out after {GetTimeoutSeconds()}s");
            _logger.LogDebug("FastSTRM: The size request for {Url} timed out", UrlForLog(uri.ToString()));
        }
        catch (HttpRequestException ex)
        {
            notes.Add("request-failed", "ranged GET failed: " + Short(ex.Message));
            _logger.LogDebug(ex, "FastSTRM: The size request for {Url} failed", UrlForLog(uri.ToString()));
        }

        return null;
    }

    private static string DescribeStatus(HttpResponseMessage response)
        => $"HTTP {(int)response.StatusCode} {Short(response.ReasonPhrase ?? string.Empty, 40)}".TrimEnd();

    private static string DescribeLength(long? length)
        => length is long value ? value.ToString(CultureInfo.InvariantCulture) : "none";

    private static bool IsHtmlResponse(HttpResponseMessage response)
        => response.Content.Headers.ContentType?.MediaType is string mediaType
           && mediaType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 服务器忽略 Range 直接回 200 时，Content-Length 可能是错误页而不是媒体。
    /// 除了 text/html，再排除 text/* 与 application/json。
    /// </summary>
    private static bool IsNonMediaContentType(HttpResponseMessage response)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is null)
        {
            return false;
        }

        return mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
               || mediaType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase);
    }

    private static string DescribeContentType(HttpResponseMessage response)
        => Short(response.Content.Headers.ContentType?.MediaType ?? "an unknown content type", 40);

    private static string Short(string? value, int maxLength = 160)
        => LogSafe.Sanitize(value, maxLength).Trim();

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

    private static string RedactProxy(string value)
    {
        var trimmed = value.Trim();

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            var authority = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
            return $"{uri.Scheme}://{authority}";
        }

        return trimmed.Contains('@', StringComparison.Ordinal)
            ? Short(trimmed[(trimmed.LastIndexOf('@') + 1)..], 80)
            : Short(trimmed, 80);
    }

    private sealed class LookupNotes
    {
        private const int MaxNotes = 4;

        private readonly List<string> _notes = new();

        private readonly HashSet<string> _categories = new(StringComparer.Ordinal);

        public void Add(string category, string detail)
        {
            _categories.Add(category);

            if (_notes.Count < MaxNotes && !_notes.Contains(detail, StringComparer.Ordinal))
            {
                _notes.Add(detail);
            }
        }

        public string Summarize()
            => _notes.Count == 0 ? "the endpoint returned no usable answer" : string.Join("; ", _notes);

        /// <summary>只给分类，不含具体状态码 —— 用于管理员可见的 Warning。</summary>
        public string SummarizeCategories()
            => _categories.Count == 0 ? "no usable answer" : string.Join(", ", _categories);
    }

    private static CancellationTokenSource BeginRequestTimeout(CancellationToken cancellationToken)
    {
        var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(GetTimeoutSeconds()));
        return timeoutCts;
    }

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
            // 重定向由我们自己跟随，每一跳都重新校验目标地址（见 SendFollowingRedirectsAsync）。
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectCallback = ConnectCheckedAsync
        };

        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FastSTRM/3.4 (+jellyfin)");
        return client;
    }

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

        var candidates = addresses
            .Where(address => StreamTargetPolicy.IsAllowed(address, allowPrivate))
            .OrderBy(address => address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .ToArray();

        if (candidates.Length == 0)
        {
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

    private async Task PersistAsync(BaseItem item, long size, CancellationToken cancellationToken, bool force)
    {
        if (!ShouldPersist(item, size, force))
        {
            return;
        }

        var previousSize = item.Size;
        item.Size = size;

        try
        {
            await WriteSizeAsync(item, size, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            item.Size = previousSize;
        }
        catch (Exception ex)
        {
            item.Size = previousSize;
            _logger.LogWarning(ex, "FastSTRM: Unable to persist the remote size of {Name}", LogSafe.Name(item));
        }
    }

    private bool ShouldPersist(BaseItem item, long size, bool force)
    {
        if (item.Size == size)
        {
            return false;
        }

        return force || NeedsRemoteSize(item);
    }

    private async Task WriteSizeAsync(BaseItem item, long size, CancellationToken cancellationToken)
    {
        await _libraryManager
            .UpdateItemAsync(item, item.GetParent(), ItemUpdateType.MetadataEdit, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "FastSTRM: Wrote the remote size of {Name} ({Size} bytes) in place of the .strm file size",
            LogSafe.Name(item),
            size);
    }

    /// <summary>
    /// 缓存 key 去掉 query：签名参数每次播放都会变，但指向的是同一个文件。
    /// 直接用完整 URL 当 key 会让签名凭证在内存里长期驻留，也几乎命中不了缓存。
    /// </summary>
    private static string SizeCacheKey(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath}"
            : url;

    private void Remember(Guid itemId, long size)
    {
        PruneIfNeeded(_itemSizes);
        _itemSizes[itemId] = new CachedSize(size, DateTime.UtcNow.Ticks);
    }

    private void RememberUrl(string url, long size)
    {
        PruneIfNeeded(_urlSizes);
        _urlSizes[SizeCacheKey(url)] = new CachedSize(size, DateTime.UtcNow.Ticks);
    }

    private static void PruneIfNeeded<TKey>(ConcurrentDictionary<TKey, CachedSize> cache)
        where TKey : notnull
    {
        if (cache.Count < MaxCacheEntries)
        {
            return;
        }

        var cutoff = DateTime.UtcNow.Ticks - CacheTtl.Ticks;
        foreach (var pair in cache)
        {
            if (pair.Value.UtcTicks < cutoff)
            {
                cache.TryRemove(pair.Key, out _);
            }
        }

        var target = MaxCacheEntries * 7 / 8;
        var overflow = cache.Count - target;
        if (overflow <= 0)
        {
            return;
        }

        // 全部未过期时按最旧时间淘汰一批，而不是 Clear()：一次清空会让后续探测
        // 把所有远端 HEAD 请求重新打满，等于自造缓存雪崩。
        var victims = cache
            .OrderBy(pair => pair.Value.UtcTicks)
            .Take(overflow)
            .Select(pair => pair.Key)
            .ToArray();

        foreach (var victim in victims)
        {
            cache.TryRemove(victim, out _);
        }
    }

    private static string UrlForLog(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath}"
            : "(invalid url)";

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }

    private readonly record struct RemoteSizeLookup(long? Size, string Detail, string Categories);

    private readonly record struct CachedSize(long Size, long UtcTicks)
    {
        public bool IsExpired() => DateTime.UtcNow.Ticks - UtcTicks > CacheTtl.Ticks;

        public bool IsOlderThan(TimeSpan age) => DateTime.UtcNow.Ticks - UtcTicks > age.Ticks;
    }
}
