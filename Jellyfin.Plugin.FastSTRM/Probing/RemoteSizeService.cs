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

    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(6);

    private static readonly TimeSpan ForceReuseWindow = TimeSpan.FromMinutes(5);

    private static readonly HttpClient SharedHttpClient = CreateHttpClient();

    private static readonly string[] ProxyEnvironmentVariables =
    {
        "HTTPS_PROXY", "https_proxy", "HTTP_PROXY", "http_proxy", "ALL_PROXY", "all_proxy"
    };

    private readonly HttpClient _http;

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
            return true;
        }
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

        var url = ReadRemoteUrl(item.Path, out var strmFileLength);
        if (url is null)
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
        using (await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false))
        {
            (size, diagnostic) = await QueryRemoteSizeWithDiagnosticsAsync(url, cancellationToken).ConfigureAwait(false);
        }

        if (size is null)
        {
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

    internal async Task<long?> QueryRemoteSizeAsync(string url, CancellationToken cancellationToken)
        => (await QueryRemoteSizeWithDiagnosticsAsync(url, cancellationToken).ConfigureAwait(false)).Size;

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

        var literal = StreamTargetPolicy.EvaluateLiteralHost(uri.DnsSafeHost, allowPrivate);
        if (literal == false)
        {
            _logger.LogWarning(
                "FastSTRM: Refusing to query {Url}; its address is not an allowed stream target. "
                + "Enable 'Allow requests to private network addresses' if this is a LAN media server.",
                UrlForLog(url));
            return (null, "the target address is not allowed by the allowed-target setting");
        }

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

            if (!response.IsSuccessStatusCode)
            {
                notes.Add($"HEAD returned {DescribeStatus(response)}");
                return null;
            }

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

            if (IsHtmlResponse(response))
            {
                notes.Add($"{DescribeStatus(response)} answered as {DescribeContentType(response)}, which is not a media file");
                return null;
            }

            var totalLength = response.Content.Headers.ContentRange?.Length;
            if (IsAcceptableRemoteSize(totalLength))
            {
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

    private static string DescribeStatus(HttpResponseMessage response)
        => $"HTTP {(int)response.StatusCode} {Short(response.ReasonPhrase ?? string.Empty, 40)}".TrimEnd();

    private static string DescribeLength(long? length)
        => length is long value ? value.ToString(CultureInfo.InvariantCulture) : "none";

    private static bool IsHtmlResponse(HttpResponseMessage response)
        => response.Content.Headers.ContentType?.MediaType is string mediaType
           && mediaType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase);

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
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 3,
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
        if (item.Size == size)
        {
            return;
        }

        if (!force && !NeedsRemoteSize(item))
        {
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
            item.Size = previousSize;
        }
        catch (Exception ex)
        {
            item.Size = previousSize;
            _logger.LogWarning(ex, "FastSTRM: Unable to persist the remote size of {Name}", LogSafe.Name(item));
        }
    }

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

    private void Remember(Guid itemId, long size)
    {
        PruneIfNeeded(_itemSizes);
        _itemSizes[itemId] = new CachedSize(size, DateTime.UtcNow.Ticks);
    }

    private void RememberUrl(string url, long size)
    {
        PruneIfNeeded(_urlSizes);
        _urlSizes[url] = new CachedSize(size, DateTime.UtcNow.Ticks);
    }

    private static void PruneIfNeeded<TKey>(ConcurrentDictionary<TKey, CachedSize> cache)
        where TKey : notnull
    {
        if (cache.Count < MaxCacheEntries)
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

        if (cache.Count >= MaxCacheEntries)
        {
            cache.Clear();
        }
    }

    private static string UrlForLog(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath}"
            : "(invalid url)";

    public void Dispose()
    {
    }

    private readonly record struct CachedSize(long Size, long UtcTicks)
    {
        public bool IsExpired() => DateTime.UtcNow.Ticks - UtcTicks > CacheTtl.Ticks;

        public bool IsOlderThan(TimeSpan age) => DateTime.UtcNow.Ticks - UtcTicks > age.Ticks;
    }
}
