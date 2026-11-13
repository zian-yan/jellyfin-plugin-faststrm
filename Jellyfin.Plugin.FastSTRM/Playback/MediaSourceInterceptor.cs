#nullable disable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

    private static readonly TimeSpan AccelerationLogInterval = TimeSpan.FromSeconds(5);

    private readonly IMediaSourceManager _inner;
    private readonly ILogger<MediaSourceInterceptor> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly PlaybackPolicyResolver _policyResolver;
    private readonly RemoteSizeService _remoteSize;

    private readonly ConcurrentDictionary<Guid, long> _lastAccelerationLoggedTicks = new();

    private int _accelerationLogCount;

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

        if (IsPlaybackReady(item))
        {
            var readySources = await _inner.GetPlaybackMediaSources(
                item, user, allowMediaProbe: false, enablePathSubstitution, cancellationToken)
                .ConfigureAwait(false);

            LogAccelerationSucceeded(item);
            return readySources;
        }

        var hasStreamInfo = StrmItem.HasStreamInfo(item);
        var policy = _policyResolver.Resolve(user?.Id);

        _policyResolver.RememberPlaybackPolicy(item.Id, policy);

        var skipProbe = hasStreamInfo || policy.SkipProbe;

        if (!skipProbe)
        {
            _logger.LogDebug(
                "FastSTRM: Unprobed .strm, falling back to the official media probe for {Name} (PolicySource={Source})",
                LogSafe.Name(item),
                policy.Source);

            return await _inner.GetPlaybackMediaSources(
                item, user, allowMediaProbe, enablePathSubstitution, cancellationToken)
                .ConfigureAwait(false);
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

        return sources;
    }

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
}
