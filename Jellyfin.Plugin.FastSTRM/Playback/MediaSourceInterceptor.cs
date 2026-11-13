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

    /// <summary>
    /// Upper bound for the "acceleration logged" marker map. Same rationale as
    /// <see cref="MaxWarnedItems"/>: it only suppresses duplicate log lines.
    /// </summary>
    private const int MaxTrackedAccelerations = 8192;

    /// <summary>
    /// 同一条目在这个窗口内只输出一行「加速成功」。一次起播往往会连发两次 PlaybackInfo
    /// （客户端先试探直连、再按结果取源），不去重会出现两条成功日志，看起来像重复计数。
    /// </summary>
    private static readonly TimeSpan AccelerationLogInterval = TimeSpan.FromSeconds(5);

    private readonly IMediaSourceManager _inner;
    private readonly ILogger<MediaSourceInterceptor> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly PlaybackPolicyResolver _policyResolver;
    private readonly RemoteSizeService _remoteSize;

    /// <summary>
    /// Item id → UTC ticks of the last "acceleration succeeded" line, used only to collapse the
    /// burst of PlaybackInfo calls that belongs to a single playback start.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, long> _lastAccelerationLoggedTicks = new();

    /// <summary>
    /// Number of entries in <see cref="_lastAccelerationLoggedTicks"/>.
    /// </summary>
    /// <remarks>
    /// <c>ConcurrentDictionary.Count</c> takes every bucket lock and walks all entries, and this
    /// check runs on every PlaybackInfo — i.e. on the playback hot path. A counter incremented only
    /// when an entry is actually added keeps the bound check free.
    /// </remarks>
    private int _accelerationLogCount;

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

    /// <summary>
    /// Number of entries in <see cref="_warnedItems"/>. Same rationale as
    /// <see cref="_accelerationLogCount"/>: the bound check must not pay for
    /// <c>ConcurrentDictionary.Count</c> on the request path.
    /// </summary>
    private int _warnedItemCount;

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
    /// 0. .strm that is already "ready" (stream info <b>and</b> a resolved remote size) → the
    ///    zero-probe fast path below: no policy resolution, no background probe, no size stat
    ///    beyond the readiness check, one inner call with allowMediaProbe = false.
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

        // 就绪快速路径：有流且远端大小已补全的条目没有任何待补信息。
        // 这条路径上不做策略解析、不查补探测条件、不碰警告去重表，只有一次取源调用，
        // 客户端拿到媒体源后直连播放 —— 起播即秒开，且流地址不会被多请求一次。
        // 不写「本次播放的策略」记录是有意的：PlaybackStateInterceptor 只在缺时长时才查策略，
        // 而这条路径的前提就是时长已入库。
        if (IsPlaybackReady(item))
        {
            var readySources = await _inner.GetPlaybackMediaSources(
                item, user, allowMediaProbe: false, enablePathSubstitution, cancellationToken)
                .ConfigureAwait(false);

            // 只在取源成功返回后记录：抛异常说明这次没能加速，不能报成功。
            LogAccelerationSucceeded(item);
            return readySources;
        }

        var hasStreamInfo = HasStreamInfo(item);

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

    /// <summary>
    /// 输出一行「加速成功」，说明该条目被拦截后直接进入了播放，没有任何探测。
    /// </summary>
    /// <remarks>
    /// 只输出这一句，不带片名、耗时等附加信息：默认日志级别下就可见，管理员扫一眼即可确认
    /// 加速生效，不需要按行解析。同一条目在 <see cref="AccelerationLogInterval"/> 内只记一次，
    /// 避免一次起播的多次 PlaybackInfo 刷出多条。
    /// </remarks>
    private void LogAccelerationSucceeded(BaseItem item)
    {
        if (!ShouldLogAcceleration(item.Id))
        {
            return;
        }

        _logger.LogInformation("FastSTRM: 加速成功");
    }

    private bool ShouldLogAcceleration(Guid itemId)
    {
        if (itemId == Guid.Empty)
        {
            return true;
        }

        if (Volatile.Read(ref _accelerationLogCount) >= MaxTrackedAccelerations)
        {
            // Only the log de-duplication marker is dropped; playback is unaffected.
            ClearAccelerationLog();
        }

        var now = DateTime.UtcNow.Ticks;

        // TryAdd/TryUpdate instead of AddOrUpdate: the count only changes when an entry is really
        // inserted, so the bound stays accurate without reading the whole dictionary.
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

    private void ClearAccelerationLog()
    {
        _lastAccelerationLoggedTicks.Clear();
        Interlocked.Exchange(ref _accelerationLogCount, 0);
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
    /// 「有流」判定：库中已有时长，即该条目探测过并写入了流信息。
    /// </summary>
    private static bool HasStreamInfo(BaseItem item)
        => item.RunTimeTicks.HasValue && item.RunTimeTicks.Value > 0;

    /// <summary>
    /// 「已就绪」判定：既有流信息，远端文件大小也已补全 —— 该条目在播放链路上没有任何待补内容。
    /// </summary>
    /// <remarks>
    /// 「有大小」用 <see cref="RemoteSizeService.IsRemoteSizeMissing"/> 而不是直接比较阈值：
    /// 它会被写入过真实大小的短片（4 KiB–1 MiB）正确判为已补全，只有大小仍等于 .strm
    /// 文本文件长度时才算缺失。判定为就绪后播放不再触发任何探测，因此这里的口径必须与
    /// 补全链路一致，否则会出现「补了又被判缺失 → 每次起播重新探测」的抖动。
    /// </remarks>
    internal static bool IsPlaybackReady(BaseItem item)
        => HasStreamInfo(item) && !RemoteSizeService.IsRemoteSizeMissing(item);

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
            ForgetWarned(item.Id);
            return;
        }

        var hasVideoStream = sources.Any(source =>
            source.MediaStreams is not null
            && source.MediaStreams.Any(stream => stream.Type == MediaStreamType.Video));

        if (hasVideoStream)
        {
            // Video stream information is available, so the warning marker is obsolete.
            ForgetWarned(item.Id);
            return;
        }

        if (!MarkWarned(item.Id))
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

    /// <summary>
    /// 记录「该条目的告警已经输出过」。返回 <c>false</c> 表示之前已经记过，本次不必再输出。
    /// </summary>
    /// <remarks>
    /// 用自增计数器判定上限，避免在 PlaybackInfo 热路径上调用
    /// <c>ConcurrentDictionary.Count</c>（它会锁住全部桶并遍历）。
    /// </remarks>
    private bool MarkWarned(Guid itemId)
    {
        if (Volatile.Read(ref _warnedItemCount) >= MaxWarnedItems)
        {
            // Only the log de-duplication marker is dropped; playback is unaffected.
            _warnedItems.Clear();
            Interlocked.Exchange(ref _warnedItemCount, 0);
        }

        if (!_warnedItems.TryAdd(itemId, 0))
        {
            return false;
        }

        Interlocked.Increment(ref _warnedItemCount);
        return true;
    }

    /// <summary>条目已经有流信息，丢弃它的告警标记（让计数跟着一起减）。</summary>
    private void ForgetWarned(Guid itemId)
    {
        if (_warnedItems.TryRemove(itemId, out _) && Interlocked.Decrement(ref _warnedItemCount) < 0)
        {
            // 上限清理会整体清空字典，此时计数已经被重置；夹到 0 以免长期偏移。
            Interlocked.Exchange(ref _warnedItemCount, 0);
        }
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
