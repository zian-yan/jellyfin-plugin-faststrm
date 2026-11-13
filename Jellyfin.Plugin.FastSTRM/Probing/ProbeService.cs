using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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

public sealed class ProbeService : IHostedService, IDisposable
{
    private const int MaxTrackedPlaybackAttempts = 4096;

    private static readonly TimeSpan ShutdownTaskTimeout = TimeSpan.FromSeconds(10);

    private const int MaxQueuedAutoProbes = 2048;

    private static readonly string PluginVersion =
        typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "unknown";

    private static readonly string BuildStamp = ReadBuildStamp();

    private static readonly TimeSpan SummaryCacheTtl = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan StrmListCacheTtl = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan ProgressLogInterval = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan PruneInterval = TimeSpan.FromSeconds(60);

    public const string IncrementalMode = "Incremental";

    public const string FullMode = "Full";

    private static readonly TimeSpan PlaybackProbeRetryInterval = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan[] PlaybackProbeRetryDelays =
    {
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30)
    };

    private readonly ILibraryManager _libraryManager;
    private readonly IDirectoryService _directoryService;
    private readonly ISessionManager _sessionManager;
    private readonly RemoteSizeService _remoteSize;
    private readonly ILogger<ProbeService> _logger;

    private readonly ConcurrencyGate _probeGate = new(GetConfiguredProbeLimit);

    private readonly object _lock = new();
    private readonly object _summaryLock = new();

    private readonly ConcurrentDictionary<Guid, byte> _playbackProbeInFlight = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _playbackProbeCancellation = new();
    private readonly ConcurrentDictionary<Guid, Task> _playbackProbeTasks = new();
    private readonly ConcurrentDictionary<Guid, DateTime> _lastPlaybackProbeAttemptUtc = new();
    private readonly ConcurrentDictionary<Guid, Task> _autoProbeTasks = new();
    private readonly ConcurrentDictionary<Guid, byte> _autoProbeInFlight = new();

    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _shutdownCts;
    private Task? _probeTask;
    private int _probeGeneration;
    private volatile bool _stopping;

    private long _lastProgressLogTicks;

    private volatile SummarySnapshot? _summaryCache;

    private volatile IReadOnlyList<BaseItem>? _strmItems;

    private long _strmItemsUtcTicks;

    private int _summaryRefreshRunning;

    private long _lastPruneUtcTicks;

    private int _queuedAutoProbes;

    private volatile string _state = "Idle";

    private int _totalItems;

    private int _processedItems;

    private volatile string _currentItemName = string.Empty;

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

    public string State
    {
        get => _state;
        private set => _state = value;
    }

    public int TotalItems => Volatile.Read(ref _totalItems);

    public int ProcessedItems => Volatile.Read(ref _processedItems);

    public string CurrentItemName
    {
        get => _currentItemName;
        private set => _currentItemName = value ?? string.Empty;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stopping = false;

        _logger.LogInformation(
            "FastSTRM {Version} loaded (FastSTRM.dll built {BuiltAt}); remote size requests go {Transport}, timeout {TimeoutSeconds}s",
            PluginVersion,
            BuildStamp,
            RemoteSizeService.DescribeTransport(),
            Plugin.Instance?.Configuration.GetRemoteSizeTimeoutSeconds()
                ?? PluginConfiguration.DefaultRemoteSizeTimeoutSeconds);

        _shutdownCts?.Dispose();
        _shutdownCts = new CancellationTokenSource();

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

        CancelQuietly(_shutdownCts, "FastSTRM: Error while cancelling pending background probes");

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

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        if (_stopping)
        {
            return;
        }

        var item = e.Item;
        if (!StrmItem.IsStrm(item))
        {
            return;
        }

        if (Plugin.Instance?.Configuration is not { EnableAutoProbe: true })
        {
            return;
        }

        if (StrmItem.HasStreamInfo(item) && !RemoteSizeService.NeedsRemoteSize(item))
        {
            return;
        }

        if (!_autoProbeInFlight.TryAdd(item.Id, 0))
        {
            return;
        }

        var pendingCount = Interlocked.Increment(ref _queuedAutoProbes);
        if (pendingCount > MaxQueuedAutoProbes)
        {
            Interlocked.Decrement(ref _queuedAutoProbes);
            _autoProbeInFlight.TryRemove(item.Id, out _);
            _logger.LogDebug(
                "FastSTRM: Skipping auto-probe for {Name}; {Pending} probes are already pending",
                LogSafe.Name(item, LogSafe.MaxLoggedLength),
                pendingCount - 1);
            return;
        }

        try
        {
            var token = _shutdownCts?.Token ?? CancellationToken.None;
            var task = Task.Run(() => RunAutoProbeAsync(item, token));
            _autoProbeTasks[item.Id] = task;

            _ = task.ContinueWith(
                completed => _autoProbeTasks.TryRemove(new KeyValuePair<Guid, Task>(item.Id, completed)),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            Interlocked.Decrement(ref _queuedAutoProbes);
            _autoProbeInFlight.TryRemove(item.Id, out _);
            _logger.LogDebug(ex, "FastSTRM: Unable to schedule the auto-probe for {Name}", LogSafe.Name(item, LogSafe.MaxLoggedLength));
        }
    }

    internal static bool ShouldHandleNewItem(BaseItem item, bool autoProbeEnabled, out bool runFullProbe)
    {
        runFullProbe = false;

        if (!autoProbeEnabled || !StrmItem.IsStrm(item))
        {
            return false;
        }

        runFullProbe = !StrmItem.HasStreamInfo(item);
        return runFullProbe || RemoteSizeService.IsRemoteSizeMissing(item);
    }

    internal static bool ShouldHandlePlaybackProbe(
        BaseItem item,
        EffectivePlaybackPolicy policy,
        out bool runFullProbe)
    {
        runFullProbe = false;

        if (!policy.ShouldProbeInBackground || !StrmItem.IsStrm(item))
        {
            return false;
        }

        runFullProbe = !StrmItem.HasStreamInfo(item);
        return runFullProbe || RemoteSizeService.IsRemoteSizeMissing(item);
    }

    internal static RemoteSizeTrigger GetRemoteSizeTrigger(string? mode)
        => string.Equals(mode, FullMode, StringComparison.OrdinalIgnoreCase)
            ? RemoteSizeTrigger.ManualFullProbe
            : RemoteSizeTrigger.ManualIncrementalProbe;

    private async Task RunAutoProbeAsync(BaseItem item, CancellationToken cancellationToken)
    {
        try
        {
            if (!ShouldHandleNewItem(item, autoProbeEnabled: true, out var runFullProbe))
            {
                return;
            }

            using var slot = await AcquireProbeSlotAsync(cancellationToken).ConfigureAwait(false);

            if (runFullProbe)
            {
                _logger.LogInformation("FastSTRM: Auto-probing new item {Name}", LogSafe.Name(item, LogSafe.MaxLoggedLength));
                await ProbeItemAsync(item, RemoteSizeTrigger.ItemAdded, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _logger.LogDebug(
                    "FastSTRM: Filling in the remote size of new item {Name}",
                    LogSafe.Name(item, LogSafe.MaxLoggedLength));
                await FillRemoteSizeAsync(item, RemoteSizeTrigger.ItemAdded, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("FastSTRM: Auto-probe of {Name} was cancelled", LogSafe.Name(item, LogSafe.MaxLoggedLength));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FastSTRM: Auto-probe failed for {Name}", LogSafe.Name(item, LogSafe.MaxLoggedLength));
        }
        finally
        {
            _autoProbeInFlight.TryRemove(item.Id, out _);
            Interlocked.Decrement(ref _queuedAutoProbes);
        }
    }

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

        if (IsAnotherSessionPlaying(item, e.Session))
        {
            _logger.LogDebug(
                "FastSTRM: Playback of {Name} stopped in one session but another session is still playing it; keeping its background probe",
                LogSafe.Name(item, LogSafe.MaxLoggedLength));
            return;
        }

        if (CancelPlaybackProbe(item.Id))
        {
            _logger.LogInformation(
                "FastSTRM: Playback of {Name} stopped, cancelling its background probe",
                LogSafe.Name(item, LogSafe.MaxLoggedLength));
        }
    }

    private bool CancelPlaybackProbe(Guid itemId)
    {
        if (!_playbackProbeCancellation.TryGetValue(itemId, out var cts))
        {
            return false;
        }

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
            _logger.LogDebug(ex, "FastSTRM: Unable to inspect playback sessions for {Name}", LogSafe.Name(item, LogSafe.MaxLoggedLength));
            return false;
        }
    }

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
                return false;
            }

            return !sessions.Any(session =>
                session.NowPlayingItem is not null
                && session.NowPlayingItem.Id == item.Id);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "FastSTRM: Unable to inspect playback sessions for {Name}", LogSafe.Name(item, LogSafe.MaxLoggedLength));
            return false;
        }
    }

    public static bool TryParseMode(string? mode, out string normalized)
    {
        normalized = FullMode;

        if (string.Equals(mode, FullMode, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        normalized = IncrementalMode;
        return string.IsNullOrWhiteSpace(mode)
               || string.Equals(mode, IncrementalMode, StringComparison.OrdinalIgnoreCase);
    }

    public (int Total, int Probed, int Unprobed, int SizeMissing) GetSummary(bool forceRefresh = false)
    {
        var cached = _summaryCache;

        if (cached is not null && !forceRefresh)
        {
            if (!IsFresh(cached.UtcTicks, DateTime.UtcNow.Ticks, SummaryCacheTtl))
            {
                QueueSummaryRefresh();
            }

            return cached.ToTuple();
        }

        return ComputeSummary().ToTuple();
    }

    private SummarySnapshot ComputeSummary()
    {
        lock (_summaryLock)
        {
            var items = GetStrmItems();
            var probed = items.Count(StrmItem.HasStreamInfo);

            var snapshot = new SummarySnapshot(
                items.Count,
                probed,
                items.Count - probed,
                items.Count(RemoteSizeService.IsRemoteSizeMissing),
                DateTime.UtcNow.Ticks);

            _summaryCache = snapshot;
            return snapshot;
        }
    }

    private void QueueSummaryRefresh()
    {
        if (Interlocked.CompareExchange(ref _summaryRefreshRunning, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Yield();
                ComputeSummary();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "FastSTRM: Unable to refresh the .strm summary in the background");
            }
            finally
            {
                Volatile.Write(ref _summaryRefreshRunning, 0);
            }
        });
    }

    private static bool IsFresh(long utcTicks, long nowTicks, TimeSpan ttl)
    {
        var age = nowTicks - utcTicks;
        return age >= 0 && age < ttl.Ticks;
    }

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
            if (State != "Idle")
            {
                return false;
            }

            State = "Running";
            Volatile.Write(ref _totalItems, 0);
            Volatile.Write(ref _processedItems, 0);
            CurrentItemName = string.Empty;

            cts = new CancellationTokenSource();
            _cts = cts;
            generation = ++_probeGeneration;
        }

        Task probeTask;
        try
        {
            probeTask = Task.Run(() => RunProbeAsync(normalizedMode, cts, generation));
        }
        catch (Exception ex)
        {
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
                _probeTask = probeTask.IsCompleted ? null : probeTask;
            }
        }

        return true;
    }

    public void StopProbe()
    {
        lock (_lock)
        {
            if (State != "Running")
            {
                return;
            }

            State = "Stopping";
            CancelQuietly(_cts, "FastSTRM: Error while cancelling the probe task");
        }
    }

    public bool SchedulePlaybackProbe(BaseItem item, EffectivePlaybackPolicy policy, int delaySeconds)
    {
        if (_stopping)
        {
            return false;
        }

        if (State != "Idle")
        {
            return false;
        }

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
                _playbackProbeCancellation[item.Id] = probeCts;
            }

            var task = Task.Run(() => RunPlaybackProbeAsync(item, runFullProbe, initialDelay, probeCts));
            _playbackProbeTasks[item.Id] = task;

            _ = task.ContinueWith(
                completed => _playbackProbeTasks.TryRemove(new KeyValuePair<Guid, Task>(item.Id, completed)),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            return true;
        }
        catch (Exception ex)
        {
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

            _logger.LogDebug(ex, "FastSTRM: Unable to schedule a background probe for {Name}", LogSafe.Name(item, LogSafe.MaxLoggedLength));
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
                    LogSafe.Name(item, LogSafe.MaxLoggedLength));

                await Task.Delay(initialDelay, token).ConfigureAwait(false);

                if (State != "Idle")
                {
                    _logger.LogDebug(
                        "FastSTRM: Dropping the remote size lookup of {Name} because a batch probe is running",
                        LogSafe.Name(item, LogSafe.MaxLoggedLength));
                    return;
                }

                var size = await ResolveRemoteSizeInSlotAsync(item, RemoteSizeTrigger.PlaybackProbe, token)
                    .ConfigureAwait(false);

                if (size is not null)
                {
                    _lastPlaybackProbeAttemptUtc.TryRemove(item.Id, out _);
                }

                return;
            }

            _logger.LogInformation(
                "FastSTRM: Playback started before {Name} was probed, filling in duration and remote size in the background",
                LogSafe.Name(item, LogSafe.MaxLoggedLength));

            _logger.LogDebug(
                "FastSTRM: Waiting {Delay}s before probing {Name} so the probe does not compete with the player opening the same stream",
                (int)initialDelay.TotalSeconds,
                LogSafe.Name(item, LogSafe.MaxLoggedLength));

            await Task.Delay(initialDelay, token).ConfigureAwait(false);

            for (var attempt = 0; ; attempt++)
            {
                if (State != "Idle")
                {
                    _logger.LogDebug(
                        "FastSTRM: Dropping background probe of {Name} because a batch probe is running",
                        LogSafe.Name(item, LogSafe.MaxLoggedLength));
                    return;
                }

                if (ShouldSkipProbeForStoppedPlayback(item))
                {
                    _lastPlaybackProbeAttemptUtc.TryRemove(item.Id, out _);
                    _logger.LogInformation(
                        "FastSTRM: Playback of {Name} has stopped, skipping the background probe",
                        LogSafe.Name(item, LogSafe.MaxLoggedLength));
                    return;
                }

                await ProbeItemInSlotAsync(item, RemoteSizeTrigger.PlaybackProbe, token).ConfigureAwait(false);

                var probedRuntimeTicks = GetProbedRuntimeTicks(item);
                if (probedRuntimeTicks is > 0)
                {
                    _lastPlaybackProbeAttemptUtc.TryRemove(item.Id, out _);

                    _logger.LogInformation(
                        "FastSTRM: Background probe of {Name} finished (RunTimeTicks={RunTimeTicks})",
                        LogSafe.Name(item, LogSafe.MaxLoggedLength),
                        probedRuntimeTicks);
                    return;
                }

                if (attempt >= PlaybackProbeRetryDelays.Length)
                {
                    _logger.LogWarning(
                        "FastSTRM: Background probe of {Name} produced no stream info after {Attempts} attempt(s) "
                        + "(RunTimeTicks=null). Playback and progress tracking are unaffected; the next playback "
                        + "retries it, at most once every {Interval}",
                        LogSafe.Name(item, LogSafe.MaxLoggedLength),
                        attempt + 1,
                        PlaybackProbeRetryInterval);
                    return;
                }

                var retryDelay = PlaybackProbeRetryDelays[attempt];
                _logger.LogWarning(
                    "FastSTRM: Background probe of {Name} produced no stream info (RunTimeTicks=null); "
                    + "retrying in {Delay}s (retry {Retry}/{MaxRetries})",
                    LogSafe.Name(item, LogSafe.MaxLoggedLength),
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
                LogSafe.Name(item, LogSafe.MaxLoggedLength));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FastSTRM: Background playback probe failed for {Name}", LogSafe.Name(item, LogSafe.MaxLoggedLength));
        }
        finally
        {
            _playbackProbeInFlight.TryRemove(item.Id, out _);
            _playbackProbeCancellation.TryRemove(
                new KeyValuePair<Guid, CancellationTokenSource>(item.Id, probeCts));
            probeCts.Dispose();
        }
    }

    private async Task RunProbeAsync(string mode, CancellationTokenSource cts, int generation)
    {
        var ct = cts.Token;

        try
        {
            var trigger = GetRemoteSizeTrigger(mode);
            var incremental = trigger == RemoteSizeTrigger.ManualIncrementalProbe;

            var items = GetStrmItems(forceRefresh: true);
            if (incremental)
            {
                items = items
                    .Where(item => !StrmItem.HasStreamInfo(item) || RemoteSizeService.IsRemoteSizeMissing(item))
                    .ToList();
            }

            lock (_lock)
            {
                if (generation == _probeGeneration)
                {
                    Volatile.Write(ref _totalItems, items.Count);
                }
            }

            var total = items.Count;
            var probeIndex = 0;
            Volatile.Write(ref _lastProgressLogTicks, 0);

            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = PluginConfiguration.MaxConcurrentProbesLimit,
                CancellationToken = ct
            };

            await Parallel.ForEachAsync(items, options, async (item, token) =>
            {
                var name = LogSafe.Name(item, LogSafe.MaxDisplayedLength);

                try
                {
                    using var slot = await AcquireProbeSlotAsync(token).ConfigureAwait(false);

                    var index = Interlocked.Increment(ref probeIndex);
                    CurrentItemName = name;
                    LogProgress(index, total, name);

                    if (incremental && StrmItem.HasStreamInfo(item))
                    {
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

                Interlocked.Increment(ref _processedItems);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FastSTRM: Probe task failed unexpectedly");
        }
        finally
        {
            lock (_lock)
            {
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

    private Task<ConcurrencyGate.Lease> AcquireProbeSlotAsync(CancellationToken cancellationToken)
        => _probeGate.AcquireAsync(cancellationToken);

    private async Task<long?> ResolveRemoteSizeInSlotAsync(BaseItem item, RemoteSizeTrigger trigger, CancellationToken ct)
    {
        using var slot = await AcquireProbeSlotAsync(ct).ConfigureAwait(false);
        return await FillRemoteSizeAsync(item, trigger, ct).ConfigureAwait(false);
    }

    private async Task ProbeItemInSlotAsync(BaseItem item, RemoteSizeTrigger trigger, CancellationToken ct)
    {
        using var slot = await AcquireProbeSlotAsync(ct).ConfigureAwait(false);
        await ProbeItemAsync(item, trigger, ct).ConfigureAwait(false);
    }

    private void CancelQuietly(CancellationTokenSource? cts, string logMessage)
    {
        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (AggregateException ex)
        {
            _logger.LogDebug(ex, logMessage);
        }
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
            _logger.LogWarning(ex, "FastSTRM: Official probe failed for {Name}; still resolving its remote size", LogSafe.Name(item, LogSafe.MaxLoggedLength));
        }

        await FillRemoteSizeAsync(item, trigger, ct).ConfigureAwait(false);
    }

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
            _logger.LogWarning(ex, "FastSTRM: Remote size lookup failed for {Name}", LogSafe.Name(item, LogSafe.MaxLoggedLength));
            return null;
        }
    }

    private IReadOnlyList<BaseItem> GetStrmItems(bool forceRefresh = false)
    {
        if (!forceRefresh)
        {
            var cached = _strmItems;
            if (cached is not null
                && IsFresh(Volatile.Read(ref _strmItemsUtcTicks), DateTime.UtcNow.Ticks, StrmListCacheTtl))
            {
                return cached;
            }
        }

        var items = _libraryManager
            .GetItemList(new InternalItemsQuery
            {
                MediaTypes = new[] { MediaType.Video },
                Recursive = true
            })
            .Where(StrmItem.IsStrm)
            .ToList();

        _strmItems = items;
        Volatile.Write(ref _strmItemsUtcTicks, DateTime.UtcNow.Ticks);
        return items;
    }

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

    private long? GetProbedRuntimeTicks(BaseItem item)
    {
        if (StrmItem.HasStreamInfo(item))
        {
            return item.RunTimeTicks;
        }

        try
        {
            var current = _libraryManager.GetItemById(item.Id);
            return current is not null && StrmItem.HasStreamInfo(current) ? current.RunTimeTicks : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "FastSTRM: Unable to re-read {Name} after probing", LogSafe.Name(item, LogSafe.MaxLoggedLength));
            return null;
        }
    }

    private static string ReadBuildStamp()
    {
        try
        {
            var location = typeof(Plugin).Assembly.Location;
            return string.IsNullOrEmpty(location)
                ? "unknown"
                : File.GetLastWriteTimeUtc(location).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "Z";
        }
        catch (Exception)
        {
            return "unknown";
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
                }
                catch (AggregateException)
                {
                }
            }
            else if (_playbackProbeCancellation.TryRemove(pair.Key, out var orphan))
            {
                orphan.Dispose();
            }
        }

        _shutdownCts?.Dispose();
    }

    private sealed record SummarySnapshot(int Total, int Probed, int Unprobed, int SizeMissing, long UtcTicks)
    {
        public (int Total, int Probed, int Unprobed, int SizeMissing) ToTuple()
            => (Total, Probed, Unprobed, SizeMissing);
    }
}
