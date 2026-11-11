using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FastSTRM.Common;
using FastSTRM.Configuration;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FastSTRM.Probing;

/// <summary>
/// Background service that provides library-wide .strm pre-probing
/// and automatic probing of newly added items.
/// </summary>
public sealed class ProbeService : IHostedService, IDisposable
{
    /// <summary>
    /// Upper bound for the per-item playback-probe attempt map. Entries older than
    /// <see cref="PlaybackProbeRetryInterval"/> are pruned first; the oldest entry is dropped
    /// when a very large library still exceeds the bound inside the retry window.
    /// </summary>
    private const int MaxTrackedPlaybackAttempts = 4096;

    /// <summary>
    /// How long shutdown waits for background probes to finish before giving up.
    /// </summary>
    private static readonly TimeSpan ShutdownTaskTimeout = TimeSpan.FromSeconds(10);

    private const int MaxQueuedAutoProbes = 2048;

    /// <summary>
    /// Length caps for item names written to the log / rendered in the UI. See <see cref="LogSafe"/>:
    /// names come from file names on disk, so they must be sanitized before they reach a log sink.
    /// </summary>
    private const int MaxLoggedNameLength = LogSafe.MaxLoggedLength;
    private const int MaxDisplayedNameLength = LogSafe.MaxDisplayedLength;

    /// <summary>
    /// How long a summary (total/probed/unprobed) stays cached. The config page asks for it on
    /// every visit and after every probe, while computing it scans the whole video library.
    /// </summary>
    private static readonly TimeSpan SummaryCacheTtl = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Minimum interval between two progress log lines during a full library probe.
    /// </summary>
    private static readonly TimeSpan ProgressLogInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Minimum interval between two prune passes over the playback-attempt map. The prune is a
    /// full scan of up to <see cref="MaxTrackedPlaybackAttempts"/> entries and is reached from
    /// every playback progress report, so it must not run more often than this.
    /// </summary>
    private static readonly TimeSpan PruneInterval = TimeSpan.FromSeconds(60);

    private readonly ILibraryManager _libraryManager;
    private readonly IDirectoryService _directoryService;
    private readonly ISessionManager _sessionManager;
    private readonly RemoteSizeService _remoteSize;
    private readonly ILogger<ProbeService> _logger;

    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _shutdownCts;
    private Task? _probeTask;
    private int _probeGeneration;
    private volatile bool _stopping;
    private readonly object _lock = new();

    /// <summary>
    /// Serializes replacement of the probe gate so a configuration change cannot swap it out
    /// while probes are still waiting on the previous instance.
    /// </summary>
    private readonly object _probeGateLock = new();

    /// <summary>
    /// Gate that keeps the number of concurrently running probes bounded by the configured
    /// <see cref="PluginConfiguration.MaxConcurrentProbes"/> (1 by default).
    /// </summary>
    private SemaphoreSlim _probeGate = new(1, 1);
    private int _probeGateLimit = 1;
    private int _activeProbes;
    private long _lastProgressLogTicks;

    /// <summary>
    /// Guards <see cref="_summaryCache"/> so the whole-library scan behind a summary request
    /// runs once per <see cref="SummaryCacheTtl"/> instead of once per page view.
    /// </summary>
    private readonly object _summaryLock = new();
    private (int Total, int Probed, int Unprobed, int SizeMissing) _summaryCache;
    private long _summaryCacheUtcTicks;
    private long _lastPruneUtcTicks;

    /// <summary>
    /// Items whose background playback probe is currently running, so one item is never probed twice at once.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, byte> _playbackProbeInFlight = new();

    /// <summary>
    /// Cancellation source of the background playback probe per item, so stopping playback can
    /// abort a probe that is waiting for its delay or running an ffprobe.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _playbackProbeCancellation = new();

    /// <summary>
    /// Background playback probe task per item, so shutdown can wait for them instead of
    /// abandoning work that is still touching library services.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, Task> _playbackProbeTasks = new();

    /// <summary>
    /// Last time a background playback probe was attempted per item. Prevents a failing remote
    /// source from being re-probed on every playback progress report. The map is pruned so it
    /// cannot grow with the number of distinct items ever played.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, DateTime> _lastPlaybackProbeAttemptUtc = new();

    /// <summary>
    /// Automatic (item added) probe tasks per item, so shutdown can cancel and wait for them
    /// and duplicate ItemAdded events for the same item do not probe it concurrently.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, Task> _autoProbeTasks = new();

    /// <summary>
    /// Items whose automatic (item added) probe is currently running.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, byte> _autoProbeInFlight = new();

    private static readonly TimeSpan PlaybackProbeRetryInterval = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Delays between consecutive background playback-probe attempts. A probe often fails for
    /// transient reasons — the upstream .strm endpoint returning 5xx while the client is opening
    /// the very same file, a cloud-drive rate limit, a slow redirect — so a short retry usually
    /// succeeds without waiting for the next playback.
    /// Tune these values to trade retry aggressiveness against load on the stream endpoint.
    /// </summary>
    private static readonly TimeSpan[] PlaybackProbeRetryDelays =
    {
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30)
    };

    // The probe state below is written by the probe task and read by the API thread. Writes to
    // State always happen under _lock, but readers (the status endpoint) do not take the lock,
    // so the field is volatile to keep a status reader from observing a stale value forever.
    private volatile string _state = "Idle";

    /// <summary>Total number of items in the current probe batch.</summary>
    private int _totalItems;

    /// <summary>Number of items already processed in the current batch.</summary>
    private int _processedItems;

    /// <summary>Name of the item currently being probed, shown on the status endpoint.</summary>
    private volatile string _currentItemName = string.Empty;

    /// <summary>Gets the current task state: Idle, Running, or Stopping.</summary>
    public string State
    {
        get => _state;
        private set => _state = value;
    }

    /// <summary>Gets the total number of items in the current probe batch.</summary>
    /// <summary>Gets the number of items already processed.</summary>
    /// <summary>Gets the name of the item currently being probed.</summary>
    public int TotalItems => Volatile.Read(ref _totalItems);

    public int ProcessedItems => Volatile.Read(ref _processedItems);

    public string CurrentItemName
    {
        get => _currentItemName;
        private set => _currentItemName = value ?? string.Empty;
    }

    public ProbeService(
        ILibraryManager libraryManager,
        IDirectoryService directoryService,
        ISessionManager sessionManager,
        RemoteSizeService remoteSize,
        ILogger<ProbeService> logger)
    {
        _libraryManager = libraryManager;
        _directoryService = directoryService;
        _sessionManager = sessionManager;
        _remoteSize = remoteSize;
        _logger = logger;
    }

    // ── IHostedService lifecycle ──

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stopping = false;

        // Replace a source left over from a previous start so it does not stay undisposed.
        _shutdownCts?.Dispose();
        _shutdownCts = new CancellationTokenSource();

        // Unsubscribe first: a second StartAsync must not register the handlers twice.
        _libraryManager.ItemAdded -= OnItemAdded;
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        _libraryManager.ItemAdded += OnItemAdded;
        _sessionManager.PlaybackStopped += OnPlaybackStopped;
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping = true;

        _libraryManager.ItemAdded -= OnItemAdded;
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;

        try
        {
            _shutdownCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already shut down.
        }
        catch (AggregateException ex)
        {
            _logger.LogDebug(ex, "FastSTRM: Error while cancelling pending background probes");
        }

        StopProbe();

        var pending = new List<Task>();
        Task? batchTask;
        lock (_lock)
        {
            batchTask = _probeTask;
        }

        if (batchTask is not null)
        {
            pending.Add(batchTask);
        }

        pending.AddRange(_playbackProbeTasks.Values);
        pending.AddRange(_autoProbeTasks.Values);

        var running = pending.Where(task => !task.IsCompleted).ToArray();
        if (running.Length == 0)
        {
            return;
        }

        // Do not let a stuck ffprobe hold server shutdown forever; the host has its own timeout too.
        var all = Task.WhenAll(running);
        var completed = await Task.WhenAny(all, Task.Delay(ShutdownTaskTimeout, cancellationToken))
            .ConfigureAwait(false);
        if (!ReferenceEquals(completed, all))
        {
            _logger.LogWarning(
                "FastSTRM: {Count} background probe task(s) did not finish within {Timeout}s of shutdown",
                running.Length,
                (int)ShutdownTaskTimeout.TotalSeconds);
            return;
        }

        try
        {
            await all.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "FastSTRM: Background probe task failed during shutdown");
        }
    }

    // ── Auto-probe on item added ──

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        if (_stopping)
        {
            return;
        }

        var item = e.Item;
        if (!IsStrmItem(item))
        {
            return;
        }

        // 「入库自动探测」是补全的三个触发点之一，关闭时整条入库链路不动作。
        // 开启后，无论条目是否已有流信息，只要大小缺失就补大小。
        var autoProbeEnabled = Plugin.Instance?.Configuration is { EnableAutoProbe: true };
        if (!ShouldHandleNewItem(item, autoProbeEnabled, out var runFullProbe))
        {
            return;
        }

        if (!_autoProbeInFlight.TryAdd(item.Id, 0))
        {
            // The same item can raise ItemAdded more than once during a scan.
            return;
        }

        var pendingCount = _autoProbeTasks.Count;
        if (pendingCount >= MaxQueuedAutoProbes)
        {
            // A scan of a very large library must not turn into an unbounded task queue. The
            // item still gets probed by the next incremental run, so dropping it here is safe.
            _autoProbeInFlight.TryRemove(item.Id, out _);
            _logger.LogDebug(
                "FastSTRM: Skipping auto-probe for {Name}; {Pending} probes are already pending",
                LogSafe.Name(item, MaxLoggedNameLength),
                pendingCount);
            return;
        }

        try
        {
            // Reading _shutdownCts.Token throws ObjectDisposedException once Dispose() ran, so it
            // must stay inside the try: an exception escaping here would propagate into Jellyfin's
            // ItemAdded invocation during shutdown.
            var token = _shutdownCts?.Token ?? CancellationToken.None;
            var task = Task.Run(() => RunAutoProbeAsync(item, runFullProbe, token));
            _autoProbeTasks[item.Id] = task;
            if (task.IsCompleted)
            {
                _autoProbeTasks.TryRemove(item.Id, out _);
            }
        }
        catch (Exception ex)
        {
            // Never let a scheduling failure both escape the event handler and leave the item
            // permanently marked as "already being probed".
            _autoProbeInFlight.TryRemove(item.Id, out _);
            _logger.LogDebug(ex, "FastSTRM: Unable to schedule the auto-probe for {Name}", LogSafe.Name(item, MaxLoggedNameLength));
        }
    }

    /// <summary>
    /// 判断新入库的 .strm 是否需要处理，以及是「只补大小」还是「完整 ffprobe」。
    /// </summary>
    /// <remarks>
    /// 库扫描往往已经给新条目写入了流信息，但大小仍是那个几百字节的 .strm 文本文件，
    /// 这类条目不能因为「已有流信息」被整条丢掉。抽成纯函数便于覆盖「入库自动探测开启时
    /// 补全是否触发、关闭时其他情况是否一律不触发」这一回归点。
    /// 「入库自动探测」关闭 = 入库链路整体不动作，此时连大小也不补。
    /// </remarks>
    internal static bool ShouldHandleNewItem(BaseItem item, bool autoProbeEnabled, out bool runFullProbe)
    {
        runFullProbe = false;

        if (!autoProbeEnabled || !IsStrmItem(item))
        {
            // 非 .strm 条目永远不进入这条链路（补全只对 .strm 有意义），
            // 因此调用点即使漏掉类型过滤也不会产生误触发。
            return false;
        }

        runFullProbe = !HasStreamInfo(item);
        return runFullProbe || RemoteSizeService.IsRemoteSizeMissing(item);
    }

    /// <summary>
    /// 「起播后补探测」是否需要处理该条目，以及是否要跑 ffprobe。
    /// 取源链路、进度上报与 <see cref="SchedulePlaybackProbe"/> 共用这一判定，避免三处条件漂移。
    /// </summary>
    /// <param name="item">正在播放的条目。</param>
    /// <param name="policy">本次播放生效的策略。</param>
    /// <param name="runFullProbe">是否需要完整 ffprobe（无流信息时）。</param>
    /// <returns>
    /// 只有「跳过探测 · 后台补探测」（<see cref="EffectivePlaybackPolicy.ShouldProbeInBackground"/>）
    /// 才可能返回 <c>true</c>。「官方探测」交给 Jellyfin 自己 ffprobe、「不探测」连探测都跳过，
    /// 两者都不是补全远端大小的触发点，返回 <c>false</c> 且 <paramref name="runFullProbe"/> 为 <c>false</c>。
    /// </returns>
    internal static bool ShouldHandlePlaybackProbe(
        BaseItem item,
        EffectivePlaybackPolicy policy,
        out bool runFullProbe)
    {
        runFullProbe = false;

        if (!policy.ShouldProbeInBackground || !IsStrmItem(item))
        {
            return false;
        }

        runFullProbe = !HasStreamInfo(item);
        return runFullProbe || RemoteSizeService.IsRemoteSizeMissing(item);
    }

    /// <summary>
    /// 把探测模式映射为补全远端大小的触发来源。
    /// </summary>
    /// <remarks>
    /// <see cref="StartProbe"/> 已用 <see cref="TryParseMode"/> 归一化过模式，
    /// 这里只做一次等价映射：<see cref="FullMode"/> → 手动全量，其余（含空值）→ 手动增量。
    /// 「是否重新解析已有大小」随之由来源推导，避免模式与 force 两个真值来源打架。
    /// </remarks>
    internal static RemoteSizeTrigger GetRemoteSizeTrigger(string? mode)
        => string.Equals(mode, FullMode, StringComparison.OrdinalIgnoreCase)
            ? RemoteSizeTrigger.ManualFullProbe
            : RemoteSizeTrigger.ManualIncrementalProbe;

    private async Task RunAutoProbeAsync(BaseItem item, bool runFullProbe, CancellationToken cancellationToken)
    {
        SemaphoreSlim? gate = null;
        try
        {
            // Bounded fan-out: at most GetMaxConcurrentProbes() probes run at the same time, the
            // rest wait here (and are cancellable on shutdown).
            gate = await AcquireProbeSlotAsync(cancellationToken).ConfigureAwait(false);

            if (runFullProbe)
            {
                // ProbeItemAsync 在官方探测之后顺带补写远端大小（来源：入库自动探测）。
                _logger.LogInformation("FastSTRM: Auto-probing new item {Name}", LogSafe.Name(item, MaxLoggedNameLength));
                await ProbeItemAsync(item, RemoteSizeTrigger.ItemAdded, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // 条目已有流信息：只补大小，不重跑 ffprobe。
                _logger.LogDebug(
                    "FastSTRM: Filling in the remote size of new item {Name}",
                    LogSafe.Name(item, MaxLoggedNameLength));
                await FillRemoteSizeAsync(item, RemoteSizeTrigger.ItemAdded, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("FastSTRM: Auto-probe of {Name} was cancelled", LogSafe.Name(item, MaxLoggedNameLength));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FastSTRM: Auto-probe failed for {Name}", LogSafe.Name(item, MaxLoggedNameLength));
        }
        finally
        {
            if (gate is not null)
            {
                ReleaseProbeSlot(gate);
            }

            _autoProbeInFlight.TryRemove(item.Id, out _);
            _autoProbeTasks.TryRemove(item.Id, out _);
        }
    }

    // ── Playback stop handling ──

    /// <summary>
    /// Cancels the background probe of an item as soon as its playback stops, unless the
    /// administrator opted into letting the probe finish. A probe that is still waiting for its
    /// initial delay is cancelled immediately; a running ffprobe receives the cancellation token.
    /// </summary>
    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        if (Plugin.Instance?.Configuration.CancelBackgroundProbeOnPlaybackStop != true)
        {
            return;
        }

        var item = e.Item;
        if (item is null)
        {
            return;
        }

        // Another client may still be playing the same item. Stopping one session must not
        // abort the probe that the remaining session still needs.
        if (IsAnotherSessionPlaying(item, e.Session))
        {
            _logger.LogDebug(
                "FastSTRM: Playback of {Name} stopped in one session but another session is still playing it; keeping its background probe",
                LogSafe.Name(item, MaxLoggedNameLength));
            return;
        }

        if (CancelPlaybackProbe(item.Id))
        {
            _logger.LogInformation(
                "FastSTRM: Playback of {Name} stopped, cancelling its background probe",
                LogSafe.Name(item, MaxLoggedNameLength));
        }
    }

    private bool CancelPlaybackProbe(Guid itemId)
    {
        if (!_playbackProbeCancellation.TryGetValue(itemId, out var cts))
        {
            return false;
        }

        // Allow a probe on the next playback even if this one was cancelled.
        _lastPlaybackProbeAttemptUtc.TryRemove(itemId, out _);

        try
        {
            cts.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (AggregateException ex)
        {
            _logger.LogDebug(ex, "FastSTRM: Error while cancelling the background probe of {ItemId}", itemId);
            return false;
        }
    }

    /// <summary>
    /// Returns true when a session other than <paramref name="stoppingSession"/> is still playing
    /// the item, so stopping one client does not cancel a probe another client still needs.
    /// </summary>
    private bool IsAnotherSessionPlaying(BaseItem item, SessionInfo? stoppingSession)
    {
        try
        {
            var stoppingSessionId = stoppingSession?.Id;
            return _sessionManager.Sessions.Any(session =>
                (stoppingSessionId is null
                    || !string.Equals(session.Id, stoppingSessionId, StringComparison.OrdinalIgnoreCase))
                && session.NowPlayingItem is not null
                && session.NowPlayingItem.Id == item.Id);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "FastSTRM: Unable to inspect playback sessions for {Name}", LogSafe.Name(item, MaxLoggedNameLength));
            return false;
        }
    }

    /// <summary>
    /// Returns true when no active session is playing the item anymore. Used right before each
    /// probe attempt so a probe that was scheduled or re-scheduled around the stop event cannot
    /// outlive playback when cancellation on stop is enabled.
    /// </summary>
    private bool ShouldSkipProbeForStoppedPlayback(BaseItem item)
    {
        if (Plugin.Instance?.Configuration.CancelBackgroundProbeOnPlaybackStop != true)
        {
            return false;
        }

        try
        {
            var sessions = _sessionManager.Sessions.ToList();
            if (sessions.Count == 0)
            {
                // Without session information there is nothing to prove that playback stopped.
                return false;
            }

            return !sessions.Any(session =>
                session.NowPlayingItem is not null
                && session.NowPlayingItem.Id == item.Id);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "FastSTRM: Unable to inspect playback sessions for {Name}", LogSafe.Name(item, MaxLoggedNameLength));
            return false;
        }
    }

    // ── Public API ──

    /// <summary>
    /// Incremental probe mode: items without stream info are fully probed, and items that already
    /// have stream info but still carry the size of the .strm text file only get their remote size
    /// filled in (no ffprobe).
    /// </summary>
    public const string IncrementalMode = "Incremental";

    /// <summary>
    /// Full probe mode: every .strm item, every field refreshed — including the remote size, which
    /// is re-resolved even when a plausible one is already stored.
    /// </summary>
    public const string FullMode = "Full";

    /// <summary>
    /// Normalizes a probe mode. Unknown values are rejected instead of silently falling back to
    /// a full-library probe, so a typo cannot trigger an expensive rescan.
    /// </summary>
    public static bool TryParseMode(string? mode, out string normalized)
    {
        normalized = FullMode;

        if (string.Equals(mode, FullMode, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(mode)
            || string.Equals(mode, IncrementalMode, StringComparison.OrdinalIgnoreCase))
        {
            normalized = IncrementalMode;
            return true;
        }

        // Leave the caller with the cheapest mode so an invalid value never triggers a rescan.
        normalized = IncrementalMode;
        return false;
    }

    /// <summary>
    /// Returns a summary of .strm items in the library. The result is cached for
    /// <see cref="SummaryCacheTtl"/> because computing it scans the whole video library.
    /// </summary>
    /// <param name="forceRefresh">Bypasses the cache, e.g. right after a probe finished.</param>
    /// <returns>
    /// Total, probed and unprobed item counts plus the number of items whose stored size is still
    /// the size of the .strm text file instead of the remote media.
    /// </returns>
    public (int Total, int Probed, int Unprobed, int SizeMissing) GetSummary(bool forceRefresh = false)
    {
        lock (_summaryLock)
        {
            var now = DateTime.UtcNow;
            var ageTicks = now.Ticks - _summaryCacheUtcTicks;
            if (!forceRefresh
                && _summaryCacheUtcTicks != 0
                && ageTicks >= 0
                && ageTicks < SummaryCacheTtl.Ticks)
            {
                return _summaryCache;
            }

            var items = GetStrmItems();
            var probed = items.Count(HasStreamInfo);
            // 用统计口径而不是补全门槛：4 KiB–1 MiB 的短片写对后不该继续被算成「缺少大小」。
            var sizeMissing = items.Count(RemoteSizeService.IsRemoteSizeMissing);
            _summaryCache = (items.Count, probed, items.Count - probed, sizeMissing);
            _summaryCacheUtcTicks = now.Ticks;
            return _summaryCache;
        }
    }

    /// <summary>
    /// Starts a probe task. Returns false if one is already running or still stopping.
    /// </summary>
    /// <param name="mode">
    /// <see cref="IncrementalMode"/> (unprobed items + items missing the remote size) or
    /// <see cref="FullMode"/> (every item, every field).
    /// </param>
    public bool StartProbe(string mode)
    {
        if (!TryParseMode(mode, out var normalizedMode))
        {
            return false;
        }

        CancellationTokenSource cts;
        int generation;
        lock (_lock)
        {
            // "Stopping" must also block a new start: the previous task is still winding down and
            // its finally block is the current owner of State/_cts.
            if (State != "Idle")
            {
                return false;
            }

            // Reserve the slot before the (potentially slow) library scan. Holding _lock while
            // enumerating the library used to block StopProbe and the task's own finally block
            // for seconds on a large library.
            State = "Running";
            Volatile.Write(ref _totalItems, 0);
            Volatile.Write(ref _processedItems, 0);
            CurrentItemName = string.Empty;

            cts = new CancellationTokenSource();
            _cts = cts;
            generation = ++_probeGeneration;
        }

        // The scan runs inside the probe task, outside the lock; a cancellation (StopProbe) or a
        // scan failure is handled there, and RunProbeAsync's finally block releases the slot.
        Task probeTask;
        try
        {
            probeTask = Task.Run(() => RunProbeAsync(normalizedMode, cts, generation));
        }
        catch (Exception ex)
        {
            // Nothing will ever run, so release the slot we reserved instead of leaving the
            // service stuck in "Running" with no task behind it.
            lock (_lock)
            {
                if (generation == _probeGeneration)
                {
                    State = "Idle";
                    _cts = null;
                    _probeTask = null;
                }
            }

            cts.Dispose();
            _logger.LogError(ex, "FastSTRM: Unable to start the probe task");
            return false;
        }

        lock (_lock)
        {
            if (generation == _probeGeneration)
            {
                // Do not keep a completed task: the run may have finished before it was stored.
                _probeTask = probeTask.IsCompleted ? null : probeTask;
            }
        }

        return true;
    }

    /// <summary>
    /// Requests cancellation of the running probe task.
    /// </summary>
    public void StopProbe()
    {
        lock (_lock)
        {
            if (State != "Running")
            {
                return;
            }

            State = "Stopping";
            try
            {
                _cts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The probe finished between the state check and the cancel.
            }
            catch (AggregateException ex)
            {
                _logger.LogDebug(ex, "FastSTRM: Error while cancelling the probe task");
            }
        }
    }

    /// <summary>
    /// 「起播后补探测」：无流条目在后台补 ffprobe；同时**所有**仍缺远端大小的 .strm 条目在此
    /// 补全大小（已有流信息的条目只发一次头部请求，不重跑 ffprobe）。
    /// Playback itself is never blocked: the client keeps its instant direct-play start, and the
    /// probe waits <paramref name="delaySeconds"/> (the administrator-configured delay, default
    /// 5 seconds) so it does not compete with the player opening the stream. A full probe that
    /// yields no runtime (typically a transient 5xx from the stream endpoint) is retried a few
    /// times at short intervals before giving up until the next playback.
    /// </summary>
    /// <param name="item">The item that is being played.</param>
    /// <param name="policy">
    /// 本次播放生效的策略。「官方探测」「不探测」在这里就被拒掉：
    /// 它们不是补全远端大小的触发点，<see cref="ShouldHandlePlaybackProbe"/> 会返回 <c>false</c>。
    /// </param>
    /// <param name="delaySeconds">Seconds to wait after playback started before probing.</param>
    /// <returns><c>true</c> when a background probe was scheduled.</returns>
    public bool SchedulePlaybackProbe(BaseItem item, EffectivePlaybackPolicy policy, int delaySeconds)
    {
        if (_stopping)
        {
            return false;
        }

        // A running batch probe already covers these items; do not touch the same item twice.
        if (State != "Idle")
        {
            return false;
        }

        // 旧实现遇到「已有流信息」直接 return，导致这类条目永远补不上大小：
        // 它们没有 ffprobe 需求，但大小仍是 .strm 文本文件的几百字节。
        if (!ShouldHandlePlaybackProbe(item, policy, out var runFullProbe))
        {
            return false;
        }

        var now = DateTime.UtcNow;
        PrunePlaybackProbeAttempts(now);
        if (_lastPlaybackProbeAttemptUtc.TryGetValue(item.Id, out var lastAttempt)
            && now - lastAttempt < PlaybackProbeRetryInterval)
        {
            return false;
        }

        if (!_playbackProbeInFlight.TryAdd(item.Id, 0))
        {
            return false;
        }

        CancellationTokenSource? probeCts = null;
        try
        {
            _lastPlaybackProbeAttemptUtc[item.Id] = now;

            var initialDelay = TimeSpan.FromSeconds(
                Math.Clamp(delaySeconds, 0, PluginConfiguration.MaxBackgroundProbeDelaySeconds));

            probeCts = CancellationTokenSource.CreateLinkedTokenSource(
                _shutdownCts?.Token ?? CancellationToken.None);

            if (runFullProbe)
            {
                // ffprobe 依赖播放会话，停播时按配置取消。
                _playbackProbeCancellation[item.Id] = probeCts;
            }
            // 只补大小不登记取消源：一次 HEAD 是与播放会话无关的元数据回填，
            // 用户很快停止播放不该让它永远补不上。关闭服务时仍由 _shutdownCts 取消。

            var task = Task.Run(() => RunPlaybackProbeAsync(item, runFullProbe, initialDelay, probeCts));
            _playbackProbeTasks[item.Id] = task;
            if (task.IsCompleted)
            {
                // The probe can finish before the assignment above; do not leave a completed
                // task in the map (shutdown would otherwise keep awaiting stale entries).
                _playbackProbeTasks.TryRemove(item.Id, out _);
            }

            return true;
        }
        catch (Exception ex)
        {
            // Roll back every marker so a transient failure cannot permanently disable the
            // background probe of this item.
            _playbackProbeInFlight.TryRemove(item.Id, out _);
            _lastPlaybackProbeAttemptUtc.TryRemove(item.Id, out _);
            if (_playbackProbeCancellation.TryRemove(item.Id, out var cts))
            {
                cts.Dispose();
            }
            else
            {
                probeCts?.Dispose();
            }

            _logger.LogDebug(ex, "FastSTRM: Unable to schedule a background probe for {Name}", LogSafe.Name(item, MaxLoggedNameLength));
            return false;
        }
    }

    private async Task RunPlaybackProbeAsync(
        BaseItem item,
        bool runFullProbe,
        TimeSpan initialDelay,
        CancellationTokenSource probeCts)
    {
        try
        {
            var token = probeCts.Token;

            if (!runFullProbe)
            {
                _logger.LogInformation(
                    "FastSTRM: Filling in the remote size of {Name} in the background after playback started",
                    LogSafe.Name(item, MaxLoggedNameLength));

                await Task.Delay(initialDelay, token).ConfigureAwait(false);

                if (State != "Idle")
                {
                    _logger.LogDebug(
                        "FastSTRM: Dropping the remote size lookup of {Name} because a batch probe is running",
                        LogSafe.Name(item, MaxLoggedNameLength));
                    return;
                }

                var sizeGate = await AcquireProbeSlotAsync(token).ConfigureAwait(false);
                long? size;
                try
                {
                    size = await FillRemoteSizeAsync(item, RemoteSizeTrigger.PlaybackProbe, token).ConfigureAwait(false);
                }
                finally
                {
                    ReleaseProbeSlot(sizeGate);
                }

                if (size is not null)
                {
                    // 成功即清掉节流标记，避免 10 分钟内该条目再被别的链路排一次。
                    _lastPlaybackProbeAttemptUtc.TryRemove(item.Id, out _);
                }

                return;
            }

            _logger.LogInformation(
                "FastSTRM: Playback started before {Name} was probed, filling in duration and remote size in the background",
                LogSafe.Name(item, MaxLoggedNameLength));

            _logger.LogDebug(
                "FastSTRM: Waiting {Delay}s before probing {Name} so the probe does not compete with the player opening the same stream",
                (int)initialDelay.TotalSeconds,
                LogSafe.Name(item, MaxLoggedNameLength));

            await Task.Delay(initialDelay, token).ConfigureAwait(false);

            for (var attempt = 0; ; attempt++)
            {
                if (State != "Idle")
                {
                    // A batch probe is running and already covers every unprobed item.
                    _logger.LogDebug(
                        "FastSTRM: Dropping background probe of {Name} because a batch probe is running",
                        LogSafe.Name(item, MaxLoggedNameLength));
                    return;
                }

                // Playback may have stopped while the probe was waiting, and a stop that races
                // with scheduling may not have reached the cancellation source in time.
                if (ShouldSkipProbeForStoppedPlayback(item))
                {
                    _lastPlaybackProbeAttemptUtc.TryRemove(item.Id, out _);
                    _logger.LogInformation(
                        "FastSTRM: Playback of {Name} has stopped, skipping the background probe",
                        LogSafe.Name(item, MaxLoggedNameLength));
                    return;
                }

                // A failed probe does not throw: Jellyfin logs the provider error and lets the
                // refresh finish, so the runtime is the only reliable success signal.
                var gate = await AcquireProbeSlotAsync(token).ConfigureAwait(false);
                try
                {
                    await ProbeItemAsync(item, RemoteSizeTrigger.PlaybackProbe, token).ConfigureAwait(false);
                }
                finally
                {
                    ReleaseProbeSlot(gate);
                }

                var probedRuntimeTicks = GetProbedRuntimeTicks(item);
                if (probedRuntimeTicks is > 0)
                {
                    // The item now has stream info, so the "next playback may retry" marker is
                    // dead weight; drop it instead of keeping one entry per probed library item.
                    _lastPlaybackProbeAttemptUtc.TryRemove(item.Id, out _);

                    _logger.LogInformation(
                        "FastSTRM: Background probe of {Name} finished (RunTimeTicks={RunTimeTicks})",
                        LogSafe.Name(item, MaxLoggedNameLength),
                        probedRuntimeTicks);
                    return;
                }

                if (attempt >= PlaybackProbeRetryDelays.Length)
                {
                    _logger.LogWarning(
                        "FastSTRM: Background probe of {Name} produced no stream info after {Attempts} attempt(s) "
                        + "(RunTimeTicks=null). Playback and progress tracking are unaffected; the next playback "
                        + "retries it, at most once every {Interval}",
                        LogSafe.Name(item, MaxLoggedNameLength),
                        attempt + 1,
                        PlaybackProbeRetryInterval);
                    return;
                }

                var retryDelay = PlaybackProbeRetryDelays[attempt];
                _logger.LogWarning(
                    "FastSTRM: Background probe of {Name} produced no stream info (RunTimeTicks=null); "
                    + "retrying in {Delay}s (retry {Retry}/{MaxRetries})",
                    LogSafe.Name(item, MaxLoggedNameLength),
                    (int)retryDelay.TotalSeconds,
                    attempt + 1,
                    PlaybackProbeRetryDelays.Length);

                await Task.Delay(retryDelay, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug(
                "FastSTRM: Background probe of {Name} was cancelled because playback stopped or the server is shutting down",
                LogSafe.Name(item, MaxLoggedNameLength));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FastSTRM: Background playback probe failed for {Name}", LogSafe.Name(item, MaxLoggedNameLength));
        }
        finally
        {
            _playbackProbeInFlight.TryRemove(item.Id, out _);
            _playbackProbeTasks.TryRemove(item.Id, out _);
            // 已登记的 CTS 就是 probeCts 本身，这里只从表里摘掉，统一在最后释放一次。
            _playbackProbeCancellation.TryRemove(item.Id, out _);
            probeCts.Dispose();
        }
    }

    // ── Internal ──

    private async Task RunProbeAsync(string mode, CancellationTokenSource cts, int generation)
    {
        var ct = cts.Token;

        try
        {
            // 全量探测的契约是「每个字段都用新探测的数据覆盖」，所以它重新解析远端大小；
            // 增量探测只补仍然缺失的大小。这个差异由触发来源推导，不再另传一个 force 参数，
            // 因此不会出现「模式是增量、却按全量覆盖」这类漂移。
            var trigger = GetRemoteSizeTrigger(mode);
            var incremental = trigger == RemoteSizeTrigger.ManualIncrementalProbe;

            // The library scan stays off the caller's lock: on a large library it takes seconds
            // and would otherwise block StopProbe and this task's own finally block.
            var items = GetStrmItems();
            if (incremental)
            {
                // 远端大小补全已并入增量探测，不再有单独的模式：
                // 没有流信息的条目要跑 ffprobe，已有流信息但大小仍是 .strm 文本大小的条目
                // 只补大小。否则这类条目在「官方探测 / 不补探测」策略下永远补不上。
                items = items
                    .Where(item => !HasStreamInfo(item) || RemoteSizeService.IsRemoteSizeMissing(item))
                    .ToList();
            }

            lock (_lock)
            {
                // Only the run that still owns the state may publish its item count.
                if (generation == _probeGeneration)
                {
                    Volatile.Write(ref _totalItems, items.Count);
                }
            }

            var total = items.Count;
            var limit = GetConfiguredProbeLimit();
            var probeIndex = 0;
            Volatile.Write(ref _lastProgressLogTicks, 0);

            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = limit,
                CancellationToken = ct
            };

            await Parallel.ForEachAsync(items, options, async (item, token) =>
            {
                var gate = await AcquireProbeSlotAsync(token).ConfigureAwait(false);
                var name = LogSafe.Name(item, MaxDisplayedNameLength);

                try
                {
                    var index = Interlocked.Increment(ref probeIndex);
                    CurrentItemName = name;
                    LogProgress(index, total, name);

                    if (incremental && HasStreamInfo(item))
                    {
                        // 已有流信息，只是大小还停在 .strm 文本文件：只补大小，不重跑 ffprobe。
                        await FillRemoteSizeAsync(item, trigger, token).ConfigureAwait(false);
                    }
                    else
                    {
                        await ProbeItemAsync(item, trigger, token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "FastSTRM: Probe failed for {Name}, skipping", name);
                }
                finally
                {
                    ReleaseProbeSlot(gate);
                }

                Interlocked.Increment(ref _processedItems);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // Keep the task from faulting with an exception nobody observes; the per-item
            // failures above already logged themselves.
            _logger.LogError(ex, "FastSTRM: Probe task failed unexpectedly");
        }
        finally
        {
            lock (_lock)
            {
                // A newer StartProbe may already own State/_cts; only the owning run may reset them.
                if (generation == _probeGeneration)
                {
                    State = "Idle";
                    CurrentItemName = string.Empty;
                    _cts = null;
                    _probeTask = null;
                }
            }

            cts.Dispose();
        }
    }

    /// <summary>
    /// Triggers the official Jellyfin metadata refresh with remote content probing
    /// for a single item. This writes real probe data into the database.
    /// </summary>
    private void LogProgress(int index, int total, string name)
    {
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastProgressLogTicks);
        if (index != 1 && index != total && now - last < ProgressLogInterval.Ticks)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _lastProgressLogTicks, now, last) != last)
        {
            return;
        }

        _logger.LogInformation("FastSTRM: Probing [{Index}/{Total}] {Name}", index, total, name);
    }

    private static int GetConfiguredProbeLimit()
        => Plugin.Instance?.Configuration.GetMaxConcurrentProbes() ?? PluginConfiguration.MinConcurrentProbes;

    private async Task<SemaphoreSlim> AcquireProbeSlotAsync(CancellationToken cancellationToken)
    {
        SemaphoreSlim gate;
        lock (_probeGateLock)
        {
            var limit = GetConfiguredProbeLimit();
            if (limit != _probeGateLimit && Volatile.Read(ref _activeProbes) == 0)
            {
                _probeGate = new SemaphoreSlim(limit, limit);
                _probeGateLimit = limit;
            }

            gate = _probeGate;
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _activeProbes);
        return gate;
    }

    private void ReleaseProbeSlot(SemaphoreSlim gate)
    {
        Interlocked.Decrement(ref _activeProbes);
        gate.Release();
    }

    private async Task ProbeItemAsync(BaseItem item, RemoteSizeTrigger trigger, CancellationToken ct)
    {
        try
        {
            await item.RefreshMetadata(
                new MetadataRefreshOptions(_directoryService)
                {
                    EnableRemoteContentProbe = true,
                    MetadataRefreshMode = MetadataRefreshMode.FullRefresh
                },
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // ffprobe 失败不能把同一条链路里的大小补全一起丢掉：两者互不依赖。
            _logger.LogWarning(ex, "FastSTRM: Official probe failed for {Name}; still resolving its remote size", LogSafe.Name(item, MaxLoggedNameLength));
        }

        // The official probe measures the .strm container, so the size it stores is the size of
        // the local text file. Replace it with the real remote size after the refresh.
        await FillRemoteSizeAsync(item, trigger, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves and stores the real (remote) size of the item's .strm target.
    /// Returns the size on success, or <c>null</c> when it could not be determined.
    /// Never throws (except cancellation): a failing size lookup must not fail the surrounding probe.
    /// </summary>
    /// <param name="item">The .strm item.</param>
    /// <param name="trigger">
    /// 触发来源。补全的准入判定在这里先做一次，再由
    /// <see cref="RemoteSizeService.ResolveAsync"/> 复核：未登记的来源不会读 .strm、不发请求。
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// 这是补全远端文件大小的**唯一执行入口**：入库自动探测、起播后补探测、手动增量探测、
    /// 手动全量探测四条链路都汇到这里，取源与浏览详情页不调用它，因此不会产生额外的流地址请求。
    /// </remarks>
    private async Task<long?> FillRemoteSizeAsync(
        BaseItem item,
        RemoteSizeTrigger trigger,
        CancellationToken ct)
    {
        if (!RemoteSizeTriggerPolicy.AllowsSizeFill(trigger))
        {
            return null;
        }

        try
        {
            return await _remoteSize.ResolveAsync(item, trigger, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FastSTRM: Remote size lookup failed for {Name}", LogSafe.Name(item, MaxLoggedNameLength));
            return null;
        }
    }

    private List<BaseItem> GetStrmItems()
    {
        return _libraryManager
            .GetItemList(new InternalItemsQuery
            {
                MediaTypes = new[] { MediaType.Video },
                Recursive = true
            })
            .Where(IsStrmItem)
            .ToList();
    }

    private static bool IsStrmItem(BaseItem item)
        => item.Path is not null
           && item.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase);

    private static bool HasStreamInfo(BaseItem item)
        => item.RunTimeTicks.HasValue && item.RunTimeTicks.Value > 0;

    /// <summary>
    /// Keeps the playback-probe attempt map bounded. Entries are only needed for the retry
    /// throttle window, so anything older is dead. If a very large library still exceeds the
    /// bound inside the window, the oldest record is dropped to guarantee the bound.
    /// The whole pass is a full scan of up to <see cref="MaxTrackedPlaybackAttempts"/> entries
    /// and is reached from every playback progress report, so it runs at most once per
    /// <see cref="PruneInterval"/>.
    /// </summary>
    private void PrunePlaybackProbeAttempts(DateTime now)
    {
        if (_lastPlaybackProbeAttemptUtc.Count < MaxTrackedPlaybackAttempts)
        {
            return;
        }

        var lastPruneTicks = Interlocked.Read(ref _lastPruneUtcTicks);
        if (now.Ticks - lastPruneTicks < PruneInterval.Ticks)
        {
            return;
        }

        // Only one reporter per interval does the scan; the others return immediately.
        if (Interlocked.CompareExchange(ref _lastPruneUtcTicks, now.Ticks, lastPruneTicks) != lastPruneTicks)
        {
            return;
        }

        var cutoff = now - PlaybackProbeRetryInterval;
        foreach (var pair in _lastPlaybackProbeAttemptUtc)
        {
            if (pair.Value < cutoff)
            {
                _lastPlaybackProbeAttemptUtc.TryRemove(pair.Key, out _);
            }
        }

        if (_lastPlaybackProbeAttemptUtc.Count < MaxTrackedPlaybackAttempts)
        {
            return;
        }

        var oldestKey = Guid.Empty;
        var oldestValue = DateTime.MaxValue;
        foreach (var pair in _lastPlaybackProbeAttemptUtc)
        {
            if (pair.Value < oldestValue)
            {
                oldestValue = pair.Value;
                oldestKey = pair.Key;
            }
        }

        if (oldestKey != Guid.Empty)
        {
            _lastPlaybackProbeAttemptUtc.TryRemove(oldestKey, out _);
        }
    }

    /// <summary>
    /// Returns the runtime a probe actually stored, or null when the probe produced nothing.
    /// The instance passed to the interceptor is not necessarily the instance Jellyfin keeps in
    /// its cache, so re-read the item from the library before declaring the probe failed.
    /// </summary>
    private long? GetProbedRuntimeTicks(BaseItem item)
    {
        if (HasStreamInfo(item))
        {
            return item.RunTimeTicks;
        }

        try
        {
            var current = _libraryManager.GetItemById(item.Id);
            return current is not null && HasStreamInfo(current) ? current.RunTimeTicks : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "FastSTRM: Unable to re-read {Name} after probing", LogSafe.Name(item, MaxLoggedNameLength));
            return null;
        }
    }

    public void Dispose()
    {
        _stopping = true;

        try
        {
            _shutdownCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (AggregateException)
        {
        }

        StopProbe();

        // Cancel the remaining per-item probes so a pending delay or ffprobe stops promptly.
        // A source whose task is still alive is released by that task's own finally block;
        // disposing it here would make the task's next Token/Register call throw.
        foreach (var pair in _playbackProbeCancellation)
        {
            if (_playbackProbeTasks.ContainsKey(pair.Key))
            {
                try
                {
                    pair.Value.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // The owning task already finished and disposed it.
                }
                catch (AggregateException)
                {
                    // One of the registered callbacks failed; nothing left to do at shutdown.
                }
            }
            else if (_playbackProbeCancellation.TryRemove(pair.Key, out var orphan))
            {
                // No task owns it anymore, so nobody else will dispose it.
                orphan.Dispose();
            }
        }

        // The batch probe task owns _cts and disposes it in RunProbeAsync's finally block.
        // Disposing it here could race with a running RefreshMetadata call.
        _shutdownCts?.Dispose();

        // _probeGate is intentionally not disposed: a queued probe may still be waiting on it,
        // and disposing it underneath that waiter would throw instead of cancelling cleanly
        // (the shutdown token above already unblocks them).
    }
}
