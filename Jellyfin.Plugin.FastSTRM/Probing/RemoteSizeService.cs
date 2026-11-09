using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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
    /// threshold cannot be showing the size of the real media. Also used as the minimum accepted
    /// answer from the stream endpoint, so an HTML error page is never persisted as a media size.
    /// </summary>
    public const long MinimumPlausibleMediaSize = 1024L * 1024L;

    /// <summary>
    /// Upper bounds for the caches. They only avoid repeated requests, so dropping entries when
    /// full is harmless; they must never grow with the number of items ever probed.
    /// </summary>
    private const int MaxTrackedItems = 8192;

    private const int MaxTrackedUrls = 8192;

    /// <summary>
    /// Upper bound for the scheduled (non-batch) resolutions, so browsing a large library cannot
    /// turn into an unbounded queue of HTTP requests against the stream endpoint.
    /// </summary>
    private const int MaxQueuedResolutions = 256;

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
    /// Minimum interval between two attempts for the same item, so a dead link is not retried on
    /// every playback and every library page view.
    /// </summary>
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(10);

    private static readonly HttpClient Http = CreateHttpClient();

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<RemoteSizeService> _logger;
    private readonly SemaphoreSlim _gate = new(MaxConcurrentResolutions, MaxConcurrentResolutions);
    private readonly CancellationTokenSource _shutdownCts = new();

    /// <summary>Item Id → resolved size. Read by the media-source decorator on the hot path.</summary>
    private readonly ConcurrentDictionary<Guid, CachedSize> _itemSizes = new();

    /// <summary>Stream URL → resolved size, so several items sharing one URL cost a single request.</summary>
    private readonly ConcurrentDictionary<string, CachedSize> _urlSizes = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<Guid, byte> _inFlight = new();
    private readonly ConcurrentDictionary<Guid, long> _lastAttemptUtcTicks = new();
    private readonly ConcurrentDictionary<Guid, Task> _tasks = new();

    private volatile bool _disposed;

    public RemoteSizeService(ILibraryManager libraryManager, ILogger<RemoteSizeService> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>
    /// Gets a value indicating whether remote size resolution is enabled in the configuration.
    /// </summary>
    public bool Enabled => Plugin.Instance?.Configuration.EnableRemoteSize ?? true;

    /// <summary>
    /// Returns true when the stored size cannot be the size of the real media file, i.e. the item
    /// still carries the length of its own .strm text file (or no size at all).
    /// </summary>
    public static bool NeedsRemoteSize(BaseItem item)
        => item.Size is null || item.Size.Value < MinimumPlausibleMediaSize;

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
    /// Applies the size known for this item to the given media sources and schedules a background
    /// resolution when nothing is known yet. Purely additive: never throws, never blocks.
    /// </summary>
    public void ApplyToSources(BaseItem item, IReadOnlyList<MediaSourceInfo>? sources)
    {
        if (sources is null || sources.Count == 0 || !Enabled)
        {
            return;
        }

        var size = TryGetCachedSize(item.Id);
        if (size is null)
        {
            Schedule(item);
            return;
        }

        if (!NeedsRemoteSize(item) && item.Size == size)
        {
            // Already correct in the database; nothing to patch and nothing to schedule.
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
    /// Queues a background resolution for an item whose size is still the .strm file size.
    /// </summary>
    public void Schedule(BaseItem item)
    {
        if (_disposed
            || !Enabled
            || !IsStrmItem(item)
            || !NeedsRemoteSize(item))
        {
            return;
        }

        var now = DateTime.UtcNow.Ticks;
        if (_lastAttemptUtcTicks.TryGetValue(item.Id, out var lastAttempt)
            && now - lastAttempt < RetryInterval.Ticks)
        {
            return;
        }

        if (_tasks.Count >= MaxQueuedResolutions)
        {
            return;
        }

        if (!_inFlight.TryAdd(item.Id, 0))
        {
            return;
        }

        try
        {
            PruneAttempts(now);
            _lastAttemptUtcTicks[item.Id] = now;
            var token = _shutdownCts.Token;

            var task = Task.Run(async () =>
            {
                try
                {
                    await ResolveAsync(item, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Shutdown or a cancelled probe; the next playback retries.
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "FastSTRM: Unable to resolve the remote size of {Name}", LogSafe.Name(item));
                }
                finally
                {
                    _inFlight.TryRemove(item.Id, out _);
                    _tasks.TryRemove(item.Id, out _);
                }
            });

            _tasks[item.Id] = task;
            if (task.IsCompleted)
            {
                _tasks.TryRemove(item.Id, out _);
            }
        }
        catch (Exception ex)
        {
            _inFlight.TryRemove(item.Id, out _);
            _logger.LogDebug(ex, "FastSTRM: Unable to schedule a remote size lookup for {Name}", LogSafe.Name(item));
        }
    }

    /// <summary>
    /// Resolves the remote size of the item's .strm target and persists it.
    /// Returns the size on success, or null when it could not be determined.
    /// </summary>
    /// <remarks>
    /// An item that already carries a plausible remote size is not resolved again: no .strm read
    /// and no request to the stream endpoint. This matters for full-library probes, which visit
    /// every item. If the .strm file is replaced, the next library scan resets
    /// <see cref="BaseItem.Size"/> to the length of the new text file, so the item falls back
    /// under <see cref="NeedsRemoteSize"/> and is resolved again automatically.
    /// </remarks>
    public async Task<long?> ResolveAsync(BaseItem item, CancellationToken cancellationToken)
    {
        if (!Enabled || !IsStrmItem(item))
        {
            return null;
        }

        if (!NeedsRemoteSize(item))
        {
            return null;
        }

        var url = ReadRemoteUrl(item.Path);
        if (url is null)
        {
            _logger.LogDebug(
                "FastSTRM: No usable stream URL inside the .strm file of {Name}, skipping the remote size lookup",
                LogSafe.Name(item));
            return null;
        }

        if (_urlSizes.TryGetValue(url, out var knownUrlSize) && !knownUrlSize.IsExpired())
        {
            Remember(item.Id, knownUrlSize.Size);
            await PersistAsync(item, knownUrlSize.Size, cancellationToken).ConfigureAwait(false);
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
            return null;
        }

        Remember(item.Id, size.Value);
        RememberUrl(url, size.Value);
        await PersistAsync(item, size.Value, cancellationToken).ConfigureAwait(false);
        return size;
    }

    // ── HTTP ──

    private async Task<long?> QueryRemoteSizeAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(GetTimeoutSeconds()));
        var token = timeoutCts.Token;

        // HEAD is the cheapest way to ask for a length, but plenty of stream endpoints reject it
        // (405/501) or answer without Content-Length, so a one-byte ranged GET is always tried.
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, uri);
            using var response = await Http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode && IsPlausible(response.Content.Headers.ContentLength))
            {
                return response.Content.Headers.ContentLength;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("FastSTRM: The HEAD request for {Url} timed out", UrlForLog(url));
            return null;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "FastSTRM: The HEAD request for {Url} failed", UrlForLog(url));
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Range = new RangeHeaderValue(0, 0);

            using var response = await Http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);

            // 206 answers carry the total length in Content-Range; a 200 answer means the server
            // ignored the range, so only its Content-Length is usable (and the body is discarded
            // right away without being read).
            if (IsPlausible(response.Content.Headers.ContentRange?.Length))
            {
                return response.Content.Headers.ContentRange!.Length;
            }

            if (response.StatusCode == HttpStatusCode.OK && IsPlausible(response.Content.Headers.ContentLength))
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

    private static bool IsPlausible(long? size)
        => size.HasValue && size.Value >= MinimumPlausibleMediaSize;

    private static int GetTimeoutSeconds()
        => Plugin.Instance?.Configuration.GetRemoteSizeTimeoutSeconds()
           ?? PluginConfiguration.DefaultRemoteSizeTimeoutSeconds;

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
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

    // ── Persistence ──

    private async Task PersistAsync(BaseItem item, long size, CancellationToken cancellationToken)
    {
        if (!NeedsRemoteSize(item) || item.Size == size)
        {
            // Never overwrite a size that is already plausible: it may come from a real probe.
            return;
        }

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
            // Shutdown; the size stays in memory and is written by the next run.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FastSTRM: Unable to persist the remote size of {Name}", LogSafe.Name(item));
        }
    }

    // ── .strm parsing ──

    /// <summary>
    /// Returns the stream URL stored in the .strm file, or null when the file holds no absolute
    /// HTTP(S) URL. The file is untrusted input: only the first non-empty, non-comment line is
    /// considered and it must parse as an absolute http/https URI.
    /// </summary>
    private string? ReadRemoteUrl(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
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
    /// Keeps the retry-throttle map bounded. Entries only matter inside
    /// <see cref="RetryInterval"/>, so anything older is dead; a map still full afterwards is
    /// cleared, which at worst allows one extra attempt per item.
    /// </summary>
    private void PruneAttempts(long nowTicks)
    {
        if (_lastAttemptUtcTicks.Count < MaxTrackedItems)
        {
            return;
        }

        foreach (var pair in _lastAttemptUtcTicks)
        {
            if (nowTicks - pair.Value >= RetryInterval.Ticks)
            {
                _lastAttemptUtcTicks.TryRemove(pair.Key, out _);
            }
        }

        if (_lastAttemptUtcTicks.Count >= MaxTrackedItems)
        {
            _lastAttemptUtcTicks.Clear();
        }
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

    public void Dispose()
    {
        _disposed = true;

        // Cancel only: a scheduled task may still be reading the token, and disposing the source
        // underneath it would throw instead of unwinding cleanly.
        try
        {
            _shutdownCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (AggregateException)
        {
        }
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
    }
}
