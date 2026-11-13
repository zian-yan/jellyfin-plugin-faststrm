using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using FastSTRM.Common;
using FastSTRM.Configuration;
using Microsoft.Extensions.Logging;

namespace FastSTRM.Playback;

public sealed class PlaybackPolicyResolver
{
    private const int MaxWarnedEntries = 512;

    private const long DefaultFingerprint = 0;

    private static readonly TimeSpan FingerprintInterval = TimeSpan.FromSeconds(5);

    private const int MaxTrackedPlaybackPolicies = 8192;

    private readonly ILogger<PlaybackPolicyResolver> _logger;
    private readonly object _rebuildLock = new();
    private readonly MarkerSet<string> _warnedEntries = new(MaxWarnedEntries);

    private readonly ConcurrentDictionary<Guid, EffectivePlaybackPolicy> _playbackPolicies = new();

    private int _playbackPolicyCount;

    private long _lastFingerprintUtcTicks;

    private volatile Snapshot _snapshot = Snapshot.FromDefaults();

    public PlaybackPolicyResolver(ILogger<PlaybackPolicyResolver> logger)
    {
        _logger = logger;
    }

    public EffectivePlaybackPolicy GlobalPolicy => GetSnapshot().Global;

    public bool PoliciesTruncated => GetSnapshot().Truncated;

    public EffectivePlaybackPolicy Resolve(Guid? userId)
    {
        var snapshot = GetSnapshot();

        if (userId is null || userId.Value == Guid.Empty)
        {
            return snapshot.Global;
        }

        return snapshot.Policies.TryGetValue(userId.Value, out var policy)
            ? ResolveEntry(policy, snapshot.Global)
            : snapshot.Global;
    }

    public IReadOnlyList<UserPlaybackPolicy> GetConfiguredPolicies() => GetSnapshot().Configured;

    public bool TryGetConfigured(Guid userId, out UserPlaybackPolicy? policy)
    {
        policy = null;
        return userId != Guid.Empty && GetSnapshot().Policies.TryGetValue(userId, out policy);
    }

    public void RememberPlaybackPolicy(Guid itemId, EffectivePlaybackPolicy policy)
    {
        if (itemId == Guid.Empty)
        {
            return;
        }

        if (Volatile.Read(ref _playbackPolicyCount) >= MaxTrackedPlaybackPolicies)
        {
            _playbackPolicies.Clear();
            Interlocked.Exchange(ref _playbackPolicyCount, 0);
        }

        if (_playbackPolicies.TryAdd(itemId, policy))
        {
            Interlocked.Increment(ref _playbackPolicyCount);
        }
        else
        {
            _playbackPolicies[itemId] = policy;
        }
    }

    public bool TryGetPlaybackPolicy(Guid itemId, out EffectivePlaybackPolicy policy)
    {
        if (itemId != Guid.Empty && _playbackPolicies.TryGetValue(itemId, out policy))
        {
            return true;
        }

        policy = default;
        return false;
    }

    private EffectivePlaybackPolicy ResolveEntry(UserPlaybackPolicy policy, EffectivePlaybackPolicy global)
    {
        if (!PlaybackPolicyMode.TryNormalize(policy.Mode, out var mode))
        {
            if (ShouldWarn("mode:" + policy.UserId.ToString("N")))
            {
                _logger.LogWarning(
                    "FastSTRM: Invalid playback policy mode {Mode} for user {UserId}; falling back to the global playback policy",
                    LogSafeText(policy.Mode),
                    policy.UserId);
            }

            return global with
            {
                Source = PlaybackPolicySource.Fallback,
                FallbackReason = PlaybackPolicyFallbackReason.InvalidMode
            };
        }

        switch (mode)
        {
            case PlaybackPolicyMode.Inherit:
                return global with { Source = PlaybackPolicySource.Inherit };

            case PlaybackPolicyMode.OfficialProbe:
                return global with
                {
                    SkipProbe = false,
                    EnableBackgroundProbe = false,
                    Source = PlaybackPolicySource.User,
                    FallbackReason = null
                };

            case PlaybackPolicyMode.InstantNoProbe:
                return global with
                {
                    SkipProbe = true,
                    EnableBackgroundProbe = false,
                    Source = PlaybackPolicySource.User,
                    FallbackReason = null
                };

            default:
                var delay = policy.BackgroundProbeDelaySeconds is int configuredDelay
                    ? Math.Clamp(configuredDelay, 0, PluginConfiguration.MaxBackgroundProbeDelaySeconds)
                    : global.BackgroundProbeDelaySeconds;

                return global with
                {
                    SkipProbe = true,
                    EnableBackgroundProbe = true,
                    BackgroundProbeDelaySeconds = delay,
                    Source = PlaybackPolicySource.User,
                    FallbackReason = null
                };
        }
    }

    private Snapshot GetSnapshot()
    {
        PluginConfiguration? config;
        try
        {
            config = Plugin.Instance?.Configuration;
        }
        catch (Exception ex)
        {
            if (ShouldWarn("config-read"))
            {
                _logger.LogWarning(
                    ex,
                    "FastSTRM: Unable to read the plugin configuration; using the built-in default playback policy");
            }

            return Snapshot.FromDefaults();
        }

        var snapshot = _snapshot;

        try
        {
            var now = DateTime.UtcNow.Ticks;

            if (ReferenceEquals(snapshot.Config, config)
                && now - Interlocked.Read(ref _lastFingerprintUtcTicks) < FingerprintInterval.Ticks)
            {
                return snapshot;
            }

            var fingerprint = ComputeFingerprint(config);
            Interlocked.Exchange(ref _lastFingerprintUtcTicks, now);

            return ReferenceEquals(snapshot.Config, config) && snapshot.Fingerprint == fingerprint
                ? snapshot
                : Rebuild(config, fingerprint);
        }
        catch (Exception ex)
        {
            if (ShouldWarn("snapshot-race"))
            {
                _logger.LogWarning(
                    ex,
                    "FastSTRM: Failed to refresh the playback policy snapshot; keeping the previous one");
            }

            return snapshot;
        }
    }

    private Snapshot Rebuild(PluginConfiguration? config, long fingerprint)
    {
        lock (_rebuildLock)
        {
            var snapshot = _snapshot;
            if (ReferenceEquals(snapshot.Config, config) && snapshot.Fingerprint == fingerprint)
            {
                return snapshot;
            }

            var rebuilt = Build(config, fingerprint);
            _snapshot = rebuilt;
            return rebuilt;
        }
    }

    private Snapshot Build(PluginConfiguration? config, long fingerprint)
    {
        if (config is null)
        {
            return Snapshot.FromDefaults();
        }

        var global = config.GetGlobalPlaybackPolicy();
        var policies = new Dictionary<Guid, UserPlaybackPolicy>();
        var configured = new List<UserPlaybackPolicy>();
        var truncated = false;

        var source = config.UserPolicies;
        if (source is not null)
        {
            foreach (var policy in source)
            {
                if (policy is null)
                {
                    continue;
                }

                if (configured.Count >= PluginConfiguration.MaxUserPolicies)
                {
                    truncated = true;
                    break;
                }

                if (policy.UserId == Guid.Empty)
                {
                    if (ShouldWarn("empty-user"))
                    {
                        _logger.LogWarning("FastSTRM: Ignoring a playback policy entry with an empty user id");
                    }

                    continue;
                }

                if (!policies.TryAdd(policy.UserId, policy))
                {
                    if (ShouldWarn("duplicate:" + policy.UserId.ToString("N")))
                    {
                        _logger.LogWarning(
                            "FastSTRM: Duplicate playback policy for user {UserId}; the first entry wins",
                            policy.UserId);
                    }

                    continue;
                }

                configured.Add(policy);
            }
        }

        if (truncated && ShouldWarn("truncated"))
        {
            _logger.LogWarning(
                "FastSTRM: More than {Max} per-user playback policies are configured; the extra entries are ignored",
                PluginConfiguration.MaxUserPolicies);
        }

        return new Snapshot(global, policies, configured, config, fingerprint, truncated);
    }

    private static long ComputeFingerprint(PluginConfiguration? config)
    {
        if (config is null)
        {
            return DefaultFingerprint;
        }

        unchecked
        {
            var hash = 14695981039346656037UL;

            Mix(ref hash, config.SkipProbeForUnprobedStrm ? 1 : 0);
            Mix(ref hash, config.EnableBackgroundProbeForUnprobedStrm ? 1 : 0);
            Mix(ref hash, config.BackgroundProbeDelaySeconds);
            Mix(ref hash, config.CancelBackgroundProbeOnPlaybackStop ? 1 : 0);

            var policies = config.UserPolicies;
            if (policies is null)
            {
                Mix(ref hash, -1);
                return (long)hash;
            }

            Mix(ref hash, policies.Count);
            foreach (var policy in policies)
            {
                if (policy is null)
                {
                    Mix(ref hash, -7);
                    continue;
                }

                Mix(ref hash, policy.UserId.GetHashCode());
                Mix(ref hash, policy.Mode is null ? 0 : StringComparer.Ordinal.GetHashCode(policy.Mode));
                Mix(ref hash, policy.BackgroundProbeDelaySeconds ?? int.MinValue);
            }

            return (long)hash;
        }
    }

    private static void Mix(ref ulong hash, long value)
    {
        unchecked
        {
            hash ^= (ulong)value;
            hash *= 1099511628211UL;
        }
    }

    private static string LogSafeText(string? value)
        => string.IsNullOrEmpty(value) ? "(empty)" : LogSafe.Sanitize(value, 64);

    private bool ShouldWarn(string key) => _warnedEntries.Mark(key);

    private sealed record Snapshot(
        EffectivePlaybackPolicy Global,
        IReadOnlyDictionary<Guid, UserPlaybackPolicy> Policies,
        IReadOnlyList<UserPlaybackPolicy> Configured,
        PluginConfiguration? Config,
        long Fingerprint,
        bool Truncated)
    {
        public static Snapshot FromDefaults()
            => new(
                EffectivePlaybackPolicy.Defaults,
                new Dictionary<Guid, UserPlaybackPolicy>(),
                Array.Empty<UserPlaybackPolicy>(),
                null,
                DefaultFingerprint,
                false);
    }
}
