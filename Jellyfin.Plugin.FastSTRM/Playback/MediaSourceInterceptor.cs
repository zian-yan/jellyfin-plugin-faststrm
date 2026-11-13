#nullable disable

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

            LogAccelerationSucceeded(item);
            return (readySources, "ready");
        }

        var hasStreamInfo = StrmItem.HasStreamInfo(item);
        var policy = _policyResolver.Resolve(user?.Id);

        _policyResolver.RememberPlaybackPolicy(item.Id, policy);

        if (!hasStreamInfo && !policy.SkipProbe)
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

        var sources = await _inner.GetPlaybackMediaSources(
            item, user, allowMediaProbe: false, enablePathSubstitution, cancellationToken)
            .ConfigureAwait(false);

        int? backgroundProbeDelaySeconds = null;
        if (ProbeService.ShouldHandlePlaybackProbe(item, policy, out _))
        {
            backgroundProbeDelaySeconds = policy.BackgroundProbeDelaySeconds;
            ScheduleBackgroundProbe(item, policy, backgroundProbeDelaySeconds.Value);
        }

        WarnIfStreamInfoMissing(item, hasStreamInfo, sources, backgroundProbeDelaySeconds);

        _remoteSize.ApplyCachedSizeToSources(item, sources);

        return (sources, hasStreamInfo ? "skip-probe" : "instant-no-probe");
    }

    private void LogPlaybackCall(
        BaseItem item,
        int call,
        long gapMs,
        string branch,
        bool allowMediaProbe,
        IReadOnlyList<MediaSourceInfo> sources,
        TimeSpan elapsed)
    {
        var sourceCount = sources?.Count ?? 0;
        var streamCount = 0;

        for (var i = 0; i < sourceCount; i++)
        {
            streamCount += sources[i].MediaStreams?.Count ?? 0;
        }

        _logger.LogInformation(
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
        try
        {
            return string.IsNullOrEmpty(item.Path) ? null : new FileInfo(item.Path).Length;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string ShortId(Guid itemId)
        => itemId == Guid.Empty ? "(empty)" : itemId.ToString("N")[..8];

    private void LogAccelerationSucceeded(BaseItem item)
    {
        if (ShouldLogAcceleration(item.Id))
        {
            _logger.LogInformation("FastSTRM: 加速成功");
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
                "FastSTRM: {Name} has no stream info and probing was skipped for instant playback. "
                + "Playback progress is kept; a background probe in {Delay}s fills in the duration/track information.",
                LogSafe.Name(item),
                backgroundProbeDelaySeconds.Value);
            return;
        }

        _logger.LogWarning(
            "FastSTRM: {Name} has no stream info and probing was skipped for instant playback. "
            + "Playback progress is kept, but background probing is disabled, so the duration/track "
            + "information stays unavailable until a probe runs (enable background probing or run an incremental probe).",
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

    public IReadOnlyList<MediaSourceInfo> GetStaticMediaSources(BaseItem item, bool enablePathSubstitution, User user = null)
    {
        var sources = _inner.GetStaticMediaSources(item, enablePathSubstitution, user);

        if (StrmItem.IsStrm(item))
        {
            _remoteSize.ApplyCachedSizeToSources(item, sources);
        }

        return sources;
    }

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
