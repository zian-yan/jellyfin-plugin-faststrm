using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FastSTRM.Common;
using FastSTRM.Configuration;
using FastSTRM.Probing;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FastSTRM.Playback;

public sealed class MediaSourceInterceptor : IMediaSourceManager, IDisposable
{
    private const int MaxWarnedItems = 8192;

    private const int MaxTrackedAccelerations = 8192;

    private const int MaxTrackedPlaybackCalls = 8192;

    private static readonly TimeSpan AccelerationLogInterval = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan PlaybackCallWindow = TimeSpan.FromSeconds(30);

    private readonly IMediaSourceManager _inner;
    private readonly ILogger<MediaSourceInterceptor> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly PlaybackPolicyResolver _policyResolver;
    private readonly RemoteSizeService _remoteSize;

    private readonly ConcurrentDictionary<Guid, long> _lastAccelerationLoggedTicks = new();

    private int _accelerationLogCount;

    private readonly ConcurrentDictionary<Guid, PlaybackCall> _playbackCalls = new();

    private int _playbackCallCount;

    private readonly bool _disposeInner;

    private readonly MarkerSet<Guid> _warned = new(MaxWarnedItems);

    public MediaSourceInterceptor(
        IMediaSourceManager inner,
        ILogger<MediaSourceInterceptor> logger,
        IServiceProvider serviceProvider,
        PlaybackPolicyResolver policyResolver,
        RemoteSizeService remoteSize,
        bool disposeInner)
    {
        _inner = inner;
        _logger = logger;
        _serviceProvider = serviceProvider;
        _policyResolver = policyResolver;
        _remoteSize = remoteSize;
        _disposeInner = disposeInner;
    }

    public async Task<IReadOnlyList<MediaSourceInfo>> GetPlaybackMediaSources(
        BaseItem item,
        User user,
        bool allowMediaProbe,
        bool enablePathSubstitution,
        CancellationToken cancellationToken)
    {
        if (!StrmItem.IsStrm(item))
        {
            return await _inner.GetPlaybackMediaSources(
                item, user, allowMediaProbe, enablePathSubstitution, cancellationToken)
                .ConfigureAwait(false);
        }

        var startedAt = Stopwatch.GetTimestamp();
        var (call, gapMs) = TrackPlaybackCall(item.Id);

        var (sources, branch) = await ResolveStrmSourcesAsync(
            item, user, allowMediaProbe, enablePathSubstitution, cancellationToken)
            .ConfigureAwait(false);

        LogPlaybackCall(item, call, gapMs, branch, allowMediaProbe, sources, Stopwatch.GetElapsedTime(startedAt));

        return sources;
    }

    private async Task<(IReadOnlyList<MediaSourceInfo> Sources, string Branch)> ResolveStrmSourcesAsync(
        BaseItem item,
        User user,
        bool allowMediaProbe,
        bool enablePathSubstitution,
        CancellationToken cancellationToken)
    {
        if (IsPlaybackReady(item))
        {
            var readySources = await _inner.GetPlaybackMediaSources(
                item, user, allowMediaProbe: false, enablePathSubstitution, cancellationToken)
                .ConfigureAwait(false);

            ApplyShortcutTarget(item, readySources);

            LogAccelerationSucceeded(item);
            return (readySources, "ready");
        }

        var hasStreamInfo = StrmItem.HasStreamInfo(item);
        var policy = _policyResolver.Resolve(user?.Id);

        _policyResolver.RememberPlaybackPolicy(item.Id, user?.Id, policy);

        if (!hasStreamInfo && !policy.SkipProbe && !StrmItem.IsProbeUnsupported(item))
        {
            _logger.LogDebug(
                "FastSTRM: Unprobed .strm, falling back to the official media probe for {Name} (PolicySource={Source})",
                LogSafe.Name(item),
                policy.Source);

            var probedSources = await _inner.GetPlaybackMediaSources(
                item, user, allowMediaProbe, enablePathSubstitution, cancellationToken)
                .ConfigureAwait(false);

            return (probedSources, "official-probe");
        }

        _logger.LogDebug(
            "FastSTRM: Skipping ffprobe for {Name} (HasStreamInfo={HasStreamInfo}, User={UserId}, PolicySource={Source})",
            LogSafe.Name(item),
            hasStreamInfo,
            user?.Id,
            policy.Source);

        // 远端大小查询仍在取源的同时发起，但不再等待它：命中缓存或已补全的条目立刻下发真实大小，
        // 未命中的本次只下发已知值，结果由 RemoteSizeService 在后台写回缓存与数据库，
        // 由下一次取源带出。这样慢站与死链不再把 2 秒预算加在起播延迟上。
        //
        // 这里刻意不传请求的 cancellationToken：响应返回后 RequestAborted 可能被取消，
        // 会把这次后台查询判成失败并进入 5 分钟冷却。查询本身仍有 2 秒上限与独立闸门。
        var sizeLookup = _remoteSize.EnsureRemoteSizeAsync(item, CancellationToken.None);

        var sources = await _inner.GetPlaybackMediaSources(
            item, user, allowMediaProbe: false, enablePathSubstitution, cancellationToken)
            .ConfigureAwait(false);

        ApplyShortcutTarget(item, sources);

        int? backgroundProbeDelaySeconds = null;
        if (ProbeService.ShouldHandlePlaybackProbe(item, policy, out _))
        {
            backgroundProbeDelaySeconds = policy.BackgroundProbeDelaySeconds;
            ScheduleBackgroundProbe(item, policy, backgroundProbeDelaySeconds.Value);
        }

        WarnIfStreamInfoMissing(item, hasStreamInfo, sources, backgroundProbeDelaySeconds);

        // 只下发此刻已知的大小：进程内缓存或库里可信的值；未知就保持原样。
        _remoteSize.ApplySizeToSources(item, sources);

        _ = ForgetSizeLookupAsync(item, sizeLookup);

        return (sources, hasStreamInfo ? "skip-probe" : "instant-no-probe");
    }

    /// <summary>
    /// 大小查询已不再阻塞起播，这里只负责观察后台任务并吞掉异常：
    /// 失败只影响下一次取源能否带上真实大小，不能变成未观察的任务异常。
    /// </summary>
    private async Task ForgetSizeLookupAsync(BaseItem item, Task<long?> lookup)
    {
        try
        {
            await lookup.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "FastSTRM: The background size lookup for {Name} failed",
                LogSafe.Name(item));
        }
    }

    private void LogPlaybackCall(
        BaseItem item,
        int call,
        long gapMs,
        string branch,
        bool allowMediaProbe,
        IReadOnlyList<MediaSourceInfo>? sources,
        TimeSpan elapsed)
    {
        var sourceCount = 0;
        var streamCount = 0;

        if (sources is not null)
        {
            sourceCount = sources.Count;
            for (var i = 0; i < sourceCount; i++)
            {
                streamCount += sources[i].MediaStreams?.Count ?? 0;
            }
        }

        _logger.LogDebug(
            "FastSTRM: 取源 #{Call} {Name} | 距上次 {Gap}ms | 分支 {Branch} | allowMediaProbe={AllowProbe} | RunTimeTicks={RuntimeTicks} | Size={Size} | strm={StrmBytes}B | 源={SourceCount} 流={StreamCount} | 耗时 {Elapsed}ms | item={ItemId}",
            call,
            LogSafe.Name(item),
            gapMs,
            branch,
            allowMediaProbe,
            item.RunTimeTicks,
            item.Size,
            ReadStrmLength(item),
            sourceCount,
            streamCount,
            (long)elapsed.TotalMilliseconds,
            ShortId(item.Id));
    }

    private (int Call, long GapMs) TrackPlaybackCall(Guid itemId)
    {
        if (itemId == Guid.Empty)
        {
            return (1, 0);
        }

        var now = DateTime.UtcNow.Ticks;
        var windowTicks = PlaybackCallWindow.Ticks;

        if (Volatile.Read(ref _playbackCallCount) >= MaxTrackedPlaybackCalls)
        {
            _playbackCalls.Clear();
            Interlocked.Exchange(ref _playbackCallCount, 0);
        }

        while (true)
        {
            if (!_playbackCalls.TryGetValue(itemId, out var previous))
            {
                if (_playbackCalls.TryAdd(itemId, new PlaybackCall(now, 1)))
                {
                    Interlocked.Increment(ref _playbackCallCount);
                    return (1, 0);
                }

                continue;
            }

            var gapTicks = now - previous.UtcTicks;
            var gapMs = gapTicks > 0 ? gapTicks / TimeSpan.TicksPerMillisecond : 0;
            var call = gapTicks >= 0 && gapTicks < windowTicks ? previous.Call + 1 : 1;

            if (_playbackCalls.TryUpdate(itemId, new PlaybackCall(now, call), previous))
            {
                return (call, gapMs);
            }
        }
    }

    private static long? ReadStrmLength(BaseItem item)
    {
        // 走 StrmItem 的长度缓存，不再为每条日志单独做一次 stat。
        if (string.IsNullOrEmpty(item.Path) || !StrmItem.TryGetLength(item.Path, out var length))
        {
            return null;
        }

        return length;
    }

    private static string ShortId(Guid itemId)
        => itemId == Guid.Empty ? "(empty)" : itemId.ToString("N")[..8];

    private void LogAccelerationSucceeded(BaseItem item)
    {
        if (ShouldLogAcceleration(item.Id))
        {
            _logger.LogDebug("FastSTRM: 加速成功");
        }
    }

    private bool ShouldLogAcceleration(Guid itemId)
    {
        if (itemId == Guid.Empty)
        {
            return true;
        }

        if (Volatile.Read(ref _accelerationLogCount) >= MaxTrackedAccelerations)
        {
            _lastAccelerationLoggedTicks.Clear();
            Interlocked.Exchange(ref _accelerationLogCount, 0);
        }

        var now = DateTime.UtcNow.Ticks;

        if (_lastAccelerationLoggedTicks.TryAdd(itemId, now))
        {
            Interlocked.Increment(ref _accelerationLogCount);
            return true;
        }

        if (!_lastAccelerationLoggedTicks.TryGetValue(itemId, out var previous)
            || now - previous < AccelerationLogInterval.Ticks)
        {
            return false;
        }

        return _lastAccelerationLoggedTicks.TryUpdate(itemId, now, previous);
    }

    private void ScheduleBackgroundProbe(BaseItem item, EffectivePlaybackPolicy policy, int delaySeconds)
    {
        try
        {
            _serviceProvider.GetService<ProbeService>()?.SchedulePlaybackProbe(item, policy, delaySeconds);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "FastSTRM: Unable to schedule a background probe for {Name}", LogSafe.Name(item));
        }
    }

    internal static bool IsPlaybackReady(BaseItem item)
        => StrmItem.HasStreamInfo(item) && !RemoteSizeService.IsRemoteSizeMissing(item);

    internal static void ApplyShortcutTarget(BaseItem item, IReadOnlyList<MediaSourceInfo> sources)
    {
        if (sources is null || sources.Count == 0)
        {
            return;
        }

        if (!StrmItem.TryReadRemoteUrl(item.Path, out var url))
        {
            return;
        }

        var container = StrmItem.GetRemoteContainer(url);

        for (var i = 0; i < sources.Count; i++)
        {
            var source = sources[i];

            if (!string.Equals(source.Path, url, StringComparison.Ordinal))
            {
                source.Path = url;
            }

            source.Protocol = MediaProtocol.Http;
            source.IsRemote = true;

            if (container is not null && !string.Equals(source.Container, container, StringComparison.Ordinal))
            {
                source.Container = container;
            }
        }
    }

    private void WarnIfStreamInfoMissing(
        BaseItem item,
        bool hasStreamInfo,
        IReadOnlyList<MediaSourceInfo> sources,
        int? backgroundProbeDelaySeconds)
    {
        if (hasStreamInfo)
        {
            _warned.Forget(item.Id);
            return;
        }

        if (StrmItem.IsProbeUnsupported(item))
        {
            if (_warned.Mark(item.Id))
            {
                _logger.LogInformation(
                    "FastSTRM: {Name} is a disc image (.iso) that ffprobe cannot read, so it is never probed and its duration and track information stay unavailable",
                    LogSafe.Name(item));
            }

            return;
        }

        var hasVideoStream = sources.Any(source =>
            source.MediaStreams is not null
            && source.MediaStreams.Any(stream => stream.Type == MediaStreamType.Video));

        if (hasVideoStream)
        {
            _warned.Forget(item.Id);
            return;
        }

        if (!_warned.Mark(item.Id))
        {
            return;
        }

        if (backgroundProbeDelaySeconds.HasValue)
        {
            _logger.LogWarning(
                "FastSTRM: {Name} has no stream info yet because probing was skipped, so progress stays in memory until a background probe runs in {Delay}s",
                LogSafe.Name(item),
                backgroundProbeDelaySeconds.Value);
            return;
        }

        _logger.LogWarning(
            "FastSTRM: {Name} has no stream info and background probing is disabled, so progress stays in memory and its duration stays unknown",
            LogSafe.Name(item));
    }

    public void AddParts(IEnumerable<IMediaSourceProvider> providers)
        => _inner.AddParts(providers);

    public IReadOnlyList<MediaStream> GetMediaStreams(Guid itemId)
        => _inner.GetMediaStreams(itemId);

    public IReadOnlyList<MediaStream> GetMediaStreams(MediaStreamQuery query)
        => _inner.GetMediaStreams(query);

    public IReadOnlyList<MediaAttachment> GetMediaAttachments(Guid itemId)
        => _inner.GetMediaAttachments(itemId);

    public IReadOnlyList<MediaAttachment> GetMediaAttachments(MediaAttachmentQuery query)
        => _inner.GetMediaAttachments(query);

    public IReadOnlyList<MediaSourceInfo> GetStaticMediaSources(BaseItem item, bool enablePathSubstitution, User? user = null)
    {
        var sources = _inner.GetStaticMediaSources(item, enablePathSubstitution, user);

        if (StrmItem.IsStrm(item))
        {
            ApplyShortcutTarget(item, sources);
            _remoteSize.ApplySizeToSources(item, sources);
        }

        return sources;
    }

    /// <summary>
    /// 这个入口刻意保持原生行为，不改写 .strm 的地址与大小。调用方都在服务端程序集里
    /// （Controller 程序集中没有任何调用点），无法确认它们是否依赖 .strm 的本地路径；
    /// 而 .strm 的实际起播走的是 GetPlaybackMediaSources，地址改写在那里完成。
    /// </summary>
    public Task<MediaSourceInfo> GetMediaSource(BaseItem item, string mediaSourceId, string liveStreamId, bool enablePathSubstitution, CancellationToken cancellationToken)
        => _inner.GetMediaSource(item, mediaSourceId, liveStreamId, enablePathSubstitution, cancellationToken);

    public Task<LiveStreamResponse> OpenLiveStream(LiveStreamRequest request, CancellationToken cancellationToken)
        => _inner.OpenLiveStream(request, cancellationToken);

    public Task<Tuple<LiveStreamResponse, IDirectStreamProvider>> OpenLiveStreamInternal(LiveStreamRequest request, CancellationToken cancellationToken)
        => _inner.OpenLiveStreamInternal(request, cancellationToken);

    public Task<MediaSourceInfo> GetLiveStream(string id, CancellationToken cancellationToken)
        => _inner.GetLiveStream(id, cancellationToken);

    public Task<Tuple<MediaSourceInfo, IDirectStreamProvider>> GetLiveStreamWithDirectStreamProvider(string id, CancellationToken cancellationToken)
        => _inner.GetLiveStreamWithDirectStreamProvider(id, cancellationToken);

    public ILiveStream GetLiveStreamInfo(string id)
        => _inner.GetLiveStreamInfo(id);

    public ILiveStream GetLiveStreamInfoByUniqueId(string uniqueId)
        => _inner.GetLiveStreamInfoByUniqueId(uniqueId);

    public Task<IReadOnlyList<MediaSourceInfo>> GetRecordingStreamMediaSources(ActiveRecordingInfo info, CancellationToken cancellationToken)
        => _inner.GetRecordingStreamMediaSources(info, cancellationToken);

    public Task CloseLiveStream(string id)
        => _inner.CloseLiveStream(id);

    public Task<MediaSourceInfo> GetLiveStreamMediaInfo(string id, CancellationToken cancellationToken)
        => _inner.GetLiveStreamMediaInfo(id, cancellationToken);

    public bool SupportsDirectStream(string path, MediaProtocol protocol)
        => _inner.SupportsDirectStream(path, protocol);

    public MediaProtocol GetPathProtocol(string path)
        => _inner.GetPathProtocol(path);

    public void SetDefaultAudioAndSubtitleStreamIndices(BaseItem item, MediaSourceInfo source, User user)
        => _inner.SetDefaultAudioAndSubtitleStreamIndices(item, source, user);

    public Task AddMediaInfoWithProbe(MediaSourceInfo mediaSource, bool isAudio, string cacheKey, bool addProbeDelay, bool isLiveStream, CancellationToken cancellationToken)
        => _inner.AddMediaInfoWithProbe(mediaSource, isAudio, cacheKey, addProbeDelay, isLiveStream, cancellationToken);

    public void Dispose()
    {
        if (_disposeInner && _inner is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private readonly record struct PlaybackCall(long UtcTicks, int Call);
}
