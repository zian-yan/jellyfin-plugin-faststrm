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

/// <summary>
/// Decorator that wraps the original <see cref="IMediaSourceManager"/>.
/// Only <see cref="GetPlaybackMediaSources"/> contains interception logic;
/// all other members delegate 100% to the inner instance.
/// </summary>
public sealed class MediaSourceInterceptor : IMediaSourceManager, IDisposable
{
    /// <summary>
    /// Upper bound for the "warning already emitted" marker map. The map is only used to avoid
    /// repeating the same log line, so clearing it when full merely allows a duplicate warning
    /// later; it must never grow with the library.
    /// </summary>
    private const int MaxWarnedItems = 8192;

    private readonly IMediaSourceManager _inner;
    private readonly ILogger<MediaSourceInterceptor> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly PlaybackPolicyResolver _policyResolver;
    private readonly RemoteSizeService _remoteSize;

    /// <summary>
    /// Whether this decorator is the only owner of <see cref="_inner"/>. An instance that came
    /// from <c>ImplementationInstance</c> or an arbitrary factory may be shared, so disposing it
    /// from here could break the rest of the server.
    /// </summary>
    private readonly bool _disposeInner;

    /// <summary>
    /// Items for which the "no stream info" warning has already been emitted,
    /// so repeated PlaybackInfo requests do not flood the log.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, byte> _warnedItems = new();

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

    /// <summary>
    /// Core interception point — routing for .strm playback:
    /// 1. Non-.strm → passthrough (allowMediaProbe unchanged, zero side effects).
    /// 2. .strm with stream info (RunTimeTicks &gt; 0) → allowMediaProbe = false (skip ffprobe).
    /// 3. .strm without stream info → decided by the playing user's effective playback policy
    ///    (<see cref="PlaybackPolicyResolver"/>): the user-level policy wins over the global one,
    ///    and a user without a policy inherits the global one. The policy is read per request, so
    ///    a configuration change applies to the next playback without a restart.
    ///    With "skip probe" the item starts instantly and a background probe is scheduled only if
    ///    the policy asks for it, after the policy's delay (default: no background probe).
    /// 4. 取源本身**不触发**远端大小补全，只用进程内缓存修正展示值；真正的补全由「起播后补探测」
    ///    （<see cref="ProbeService.SchedulePlaybackProbe"/>）承担，只有「跳过探测 · 后台补探测」
    ///    策略才会调度。「官方探测」「不补探测」以及浏览详情页都不发这次请求——判定收敛在
    ///    <see cref="ProbeService.ShouldHandlePlaybackProbe"/>，不再由调用点各自拼条件。
    /// </summary>
    public async Task<IReadOnlyList<MediaSourceInfo>> GetPlaybackMediaSources(
        BaseItem item,
        User user,
        bool allowMediaProbe,
        bool enablePathSubstitution,
        CancellationToken cancellationToken)
    {
        if (!IsStrmItem(item))
        {
            return await _inner.GetPlaybackMediaSources(
                item, user, allowMediaProbe, enablePathSubstitution, cancellationToken)
                .ConfigureAwait(false);
        }

        var hasStreamInfo = item.RunTimeTicks.HasValue && item.RunTimeTicks.Value > 0;

        // 有流条目无条件跳过探测；无流条目按播放者生效策略决定。
        // user 可能为 null（部分服务端内部调用不带用户上下文），此时解析结果为全局策略。
        var policy = _policyResolver.Resolve(user?.Id);

        // 记住了这条策略，后续进度上报沿用它。进度上报发生在播放停止前后，
        // 那时很可能已经查不到「正在播放该条目的会话」，重新解析会退回全局策略，
        // 从而违背用户级「不补探测」的选择（曾经的线上 bug）。
        _policyResolver.RememberPlaybackPolicy(item.Id, policy);

        var skipProbe = hasStreamInfo || policy.SkipProbe;

        if (!skipProbe)
        {
            _logger.LogDebug(
                "FastSTRM: Unprobed .strm, falling back to the official media probe for {Name} (PolicySource={Source})",
                LogSafe.Name(item),
                policy.Source);

            // 官方探测这条路径不补远端大小：该策略的语义就是「交给 Jellyfin 自己探测」，
            // 再额外发一次流地址请求既违背该选择，也会和官方写入的大小产生两套口径。
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
            // 「起播后补探测」既补时长/轨道，也补全远端大小：已有流信息但大小仍是 .strm
            // 文本大小的条目同样要调度，否则这类条目永远补不上。
            // 调度放在取源之后，避免响应与后台刷新同时写同一个条目。
            // 「官方探测」「不探测」在 ShouldHandlePlaybackProbe 里就被拒掉，不会走到这里。
            backgroundProbeDelaySeconds = policy.BackgroundProbeDelaySeconds;
            ScheduleBackgroundProbe(item, policy, backgroundProbeDelaySeconds.Value);
        }

        WarnIfStreamInfoMissing(item, hasStreamInfo, sources, backgroundProbeDelaySeconds);

        // 只做展示层修正：取源链路不是补全的触发点，缓存未命中就保持数据库现值。
        _remoteSize.ApplyCachedSizeToSources(item, sources);

        return sources;
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

    private static bool IsStrmItem(BaseItem item)
        => item.Path is not null
           && item.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Emits a single warning per item when an unprobed .strm is played without
    /// probing, so administrators know why duration/track selection is missing.
    /// </summary>
    private void WarnIfStreamInfoMissing(
        BaseItem item,
        bool hasStreamInfo,
        IReadOnlyList<MediaSourceInfo> sources,
        int? backgroundProbeDelaySeconds)
    {
        if (hasStreamInfo)
        {
            // The item now has stream info, so its "missing stream info" marker is obsolete.
            _warnedItems.TryRemove(item.Id, out _);
            return;
        }

        var hasVideoStream = sources.Any(source =>
            source.MediaStreams is not null
            && source.MediaStreams.Any(stream => stream.Type == MediaStreamType.Video));

        if (hasVideoStream)
        {
            // Video stream information is available, so the warning marker is obsolete.
            _warnedItems.TryRemove(item.Id, out _);
            return;
        }

        if (_warnedItems.Count >= MaxWarnedItems)
        {
            // Only the log de-duplication marker is dropped; playback is unaffected.
            _warnedItems.Clear();
        }

        if (!_warnedItems.TryAdd(item.Id, 0))
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

    // ── All remaining members: 100% delegation to _inner ──

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

    /// <summary>
    /// Applies the known remote size to the static media sources Jellyfin builds for the DTOs
    /// (item details, media info page). Without this the size shown there stays the size of the
    /// local .strm text file until the value is written to the database.
    /// </summary>
    /// <remarks>
    /// 浏览详情页**只读进程内缓存，绝不发请求、绝不排队补全**：无论播放策略是什么，
    /// 打开一次媒体信息页都不该产生一次流地址请求。缓存未命中时保持数据库里的现值，
    /// 真正的补全只由探测流程（入库自动 / 后台补探测 / 增量 / 全量）承担。
    /// </remarks>
    public IReadOnlyList<MediaSourceInfo> GetStaticMediaSources(BaseItem item, bool enablePathSubstitution, User user = null)
    {
        var sources = _inner.GetStaticMediaSources(item, enablePathSubstitution, user);

        if (IsStrmItem(item))
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
