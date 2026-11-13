using System;
using System.Collections.Generic;
using System.Threading;
using FastSTRM.Common;
using FastSTRM.Configuration;
using FastSTRM.Probing;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FastSTRM.Playback;

public sealed class PlaybackStateInterceptor : IUserDataManager, IDisposable
{
    private const int MaxPreservedItems = 8192;

    private readonly IUserDataManager _inner;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<PlaybackStateInterceptor> _logger;
    private readonly PlaybackPolicyResolver _policyResolver;

    private ISessionManager? _sessionManager;

    private readonly bool _disposeInner;

    private readonly MarkerSet<Guid> _preserved = new(MaxPreservedItems);

    public PlaybackStateInterceptor(
        IUserDataManager inner,
        IServiceProvider serviceProvider,
        ILogger<PlaybackStateInterceptor> logger,
        PlaybackPolicyResolver policyResolver,
        bool disposeInner)
    {
        _inner = inner;
        _serviceProvider = serviceProvider;
        _logger = logger;
        _policyResolver = policyResolver;
        _disposeInner = disposeInner;
    }

    public event EventHandler<UserDataSaveEventArgs>? UserDataSaved
    {
        add => _inner.UserDataSaved += value;
        remove => _inner.UserDataSaved -= value;
    }

    public void SaveUserData(User user, BaseItem item, UserItemData userData, UserDataSaveReason reason, CancellationToken cancellationToken)
        => _inner.SaveUserData(user, item, userData, reason, cancellationToken);

    public void SaveUserData(User user, BaseItem item, UpdateUserItemDataDto userDataDto, UserDataSaveReason reason)
        => _inner.SaveUserData(user, item, userDataDto, reason);

    public UserItemData? GetUserData(User user, BaseItem item)
        => _inner.GetUserData(user, item);

    public UserItemDataDto? GetUserDataDto(BaseItem item, User user)
        => _inner.GetUserDataDto(item, user);

    public Dictionary<Guid, UserItemData> GetUserDataBatch(IReadOnlyList<BaseItem> items, User user)
        => _inner.GetUserDataBatch(items, user);

    public VersionResumeData? GetResumeUserData(User user, BaseItem item)
        => _inner.GetResumeUserData(user, item);

    public IReadOnlyDictionary<Guid, VersionResumeData> GetResumeUserDataBatch(IReadOnlyList<BaseItem> items, User user)
        => _inner.GetResumeUserDataBatch(items, user);

    public UserItemDataDto? GetUserDataDto(BaseItem item, BaseItemDto? itemDto, User user, DtoOptions options)
        => _inner.GetUserDataDto(item, itemDto, user, options);

    public bool UpdatePlayState(BaseItem item, UserItemData data, long? reportedPositionTicks)
    {
        var isStrm = StrmItem.IsStrm(item);
        var hasRuntime = StrmItem.HasStreamInfo(item);

        if (isStrm && hasRuntime)
        {
            _preserved.Forget(item.Id);
        }

        if (reportedPositionTicks is >= 0
            && isStrm
            && item.SupportsPositionTicksResume
            && !hasRuntime)
        {
            data.PlaybackPositionTicks = reportedPositionTicks.Value;

            var playingUserId = ResolvePlayingUserId(item);
            var policy = _policyResolver.TryGetPlaybackPolicy(item.Id, out var playbackPolicy)
                ? playbackPolicy
                : _policyResolver.Resolve(playingUserId);

            var backgroundProbeDelaySeconds = playingUserId.HasValue
                && ProbeService.ShouldHandlePlaybackProbe(item, policy, out _)
                ? policy.BackgroundProbeDelaySeconds
                : (int?)null;

            if (reportedPositionTicks.Value > 0 && _preserved.Mark(item.Id))
            {
                if (backgroundProbeDelaySeconds.HasValue)
                {
                    _logger.LogInformation(
                        "FastSTRM: Preserving playback position {Position} for {Name} because its duration is not known yet; probing in background so completion can be tracked",
                        reportedPositionTicks.Value,
                        LogSafe.Name(item));
                }
                else
                {
                    _logger.LogInformation(
                        "FastSTRM: Preserving playback position {Position} for {Name} because its duration is not known yet; no background probe is scheduled for this playback, so completion cannot be tracked until a probe runs",
                        reportedPositionTicks.Value,
                        LogSafe.Name(item));
                }
            }

            if (backgroundProbeDelaySeconds.HasValue)
            {
                ScheduleBackgroundProbe(item, policy, backgroundProbeDelaySeconds.Value);
            }

            return false;
        }

        return _inner.UpdatePlayState(item, data, reportedPositionTicks);
    }

    public void ResetPlaybackStreamSelections(User user, BaseItem item)
        => _inner.ResetPlaybackStreamSelections(user, item);

    private Guid? ResolvePlayingUserId(BaseItem item)
    {
        try
        {
            var sessionManager = _sessionManager
                ??= _serviceProvider.GetService<ISessionManager>();

            if (sessionManager is null)
            {
                return null;
            }

            foreach (var session in sessionManager.Sessions)
            {
                if (session.NowPlayingItem is not null
                    && session.NowPlayingItem.Id == item.Id
                    && session.UserId != Guid.Empty)
                {
                    return session.UserId;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "FastSTRM: Unable to resolve the playing user for {Name}", LogSafe.Name(item));
        }

        return null;
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

    public void Dispose()
    {
        if (_disposeInner && _inner is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
