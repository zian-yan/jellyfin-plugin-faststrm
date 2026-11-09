using System;
using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace FastSTRM.Configuration;

/// <summary>
/// Plugin configuration model for FastSTRM.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Upper bound for the background probe delay, so a typo cannot postpone the probe for days.
    /// </summary>
    public const int MaxBackgroundProbeDelaySeconds = 3600;

    /// <summary>
    /// Lower bound for the configurable probe concurrency.
    /// </summary>
    public const int MinConcurrentProbes = 1;

    /// <summary>
    /// Upper bound for the configurable probe concurrency, so a typo cannot flood the
    /// stream endpoints with parallel probes.
    /// </summary>
    public const int MaxConcurrentProbesLimit = 16;

    /// <summary>
    /// Upper bound for the number of per-user playback policies. One entry per distinct user is
    /// expected; the bound only stops a hand-edited config file from growing without limit.
    /// </summary>
    public const int MaxUserPolicies = 1000;

    /// <summary>
    /// Lower bound for the remote size request timeout, in seconds.
    /// </summary>
    public const int MinRemoteSizeTimeoutSeconds = 1;

    /// <summary>
    /// Upper bound for the remote size request timeout, so a typo cannot stall a whole probe run.
    /// </summary>
    public const int MaxRemoteSizeTimeoutSeconds = 60;

    /// <summary>Default for <see cref="RemoteSizeTimeoutSeconds"/>.</summary>
    public const int DefaultRemoteSizeTimeoutSeconds = 10;

    // ── 全局播放策略的默认值（唯一来源）──
    // 属性初始值、EffectivePlaybackPolicy.Defaults 与配置页的兜底取值全部引用这三个常量，
    // 因此「新增安装」「旧配置文件缺字段」「配置对象不可用」三条路径得到同一套默认策略。

    /// <summary>Default for <see cref="SkipProbeForUnprobedStrm"/>.</summary>
    public const bool DefaultSkipProbeForUnprobedStrm = true;

    /// <summary>Default for <see cref="EnableBackgroundProbeForUnprobedStrm"/>.</summary>
    public const bool DefaultEnableBackgroundProbeForUnprobedStrm = false;

    /// <summary>Default for <see cref="BackgroundProbeDelaySeconds"/>.</summary>
    public const int DefaultBackgroundProbeDelaySeconds = 5;

    /// <summary>Default for <see cref="CancelBackgroundProbeOnPlaybackStop"/>.</summary>
    public const bool DefaultCancelBackgroundProbeOnPlaybackStop = true;

    /// <summary>
    /// Gets or sets a value indicating whether to automatically probe
    /// newly added .strm items that lack stream info.
    /// </summary>
    public bool EnableAutoProbe { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether .strm items without stream info
    /// should also skip the official media probe on playback
    /// (allowMediaProbe = false) for instant start.
    /// When disabled, such items fall back to the official ffprobe
    /// and the probe result is written to the database.
    /// </summary>
    public bool SkipProbeForUnprobedStrm { get; set; } = DefaultSkipProbeForUnprobedStrm;

    /// <summary>
    /// Gets or sets a value indicating whether an unprobed .strm item that already started
    /// playing should additionally be probed in the background.
    /// Default <c>false</c>: playback starts and no probe is scheduled, so the stream endpoint
    /// is not requested a second time.
    /// Only used when <see cref="SkipProbeForUnprobedStrm"/> is enabled.
    /// </summary>
    public bool EnableBackgroundProbeForUnprobedStrm { get; set; } = DefaultEnableBackgroundProbeForUnprobedStrm;

    /// <summary>
    /// Gets or sets the delay, in seconds, between playback start and the background probe.
    /// Default 5 seconds. Only used when <see cref="EnableBackgroundProbeForUnprobedStrm"/> is enabled.
    /// </summary>
    public int BackgroundProbeDelaySeconds { get; set; } = DefaultBackgroundProbeDelaySeconds;

    /// <summary>
    /// Gets or sets a value indicating whether stopping playback should cancel a pending or
    /// running background probe. Default <c>true</c>, so a probe does not keep running after
    /// the client has stopped playback. Only used when background probing is enabled.
    /// </summary>
    public bool CancelBackgroundProbeOnPlaybackStop { get; set; } = DefaultCancelBackgroundProbeOnPlaybackStop;

    /// <summary>
    /// Gets or sets the maximum number of concurrent probes. Default <c>1</c>
    /// keeps the original one-at-a-time behaviour.
    /// </summary>
    public int MaxConcurrentProbes { get; set; } = 1;

    /// <summary>
    /// Gets or sets a value indicating whether the real size of the remote file should be
    /// resolved and stored on the .strm item. Default <c>true</c>.
    /// </summary>
    /// <remarks>
    /// Jellyfin probes the .strm container, so the stored size is the size of the local .strm text
    /// file (a few hundred bytes). When enabled, the plugin asks the stream endpoint for the real
    /// length (HTTP HEAD, falling back to a one-byte ranged GET) and writes it onto the item, so
    /// the media info shows the actual file size.
    /// </remarks>
    public bool EnableRemoteSize { get; set; } = DefaultEnableRemoteSize;

    /// <summary>Default for <see cref="EnableRemoteSize"/>.</summary>
    public const bool DefaultEnableRemoteSize = true;

    /// <summary>
    /// Gets or sets the timeout, in seconds, of a single remote size request.
    /// Default 10 seconds, clamped to 1–60.
    /// </summary>
    public int RemoteSizeTimeoutSeconds { get; set; } = DefaultRemoteSizeTimeoutSeconds;

    /// <summary>
    /// Gets or sets a value indicating whether the size lookup may connect to loopback, private
    /// (RFC1918 / CGNAT) or IPv6 ULA addresses.
    /// </summary>
    /// <remarks>
    /// Default <c>true</c>, because a LAN Emby / Alist / OpenList origin is the normal deployment.
    /// Link-local addresses (including the cloud metadata endpoint 169.254.169.254), multicast,
    /// unspecified and reserved ranges are always refused regardless of this switch: no legitimate
    /// media origin lives there, and a <c>.strm</c> file is untrusted input that anyone with write
    /// access to the library folder can craft. Set this to <c>false</c> to also block private and
    /// loopback targets — recommended when the library folder is writable by non-administrators.
    /// </remarks>
    public bool AllowPrivateNetworkTargets { get; set; } = DefaultAllowPrivateNetworkTargets;

    /// <summary>Default for <see cref="AllowPrivateNetworkTargets"/>.</summary>
    public const bool DefaultAllowPrivateNetworkTargets = true;

    /// <summary>
    /// Gets or sets the per-user playback policies. A user without an entry here inherits the
    /// global policy; a user entry always wins over the global one. An entry whose
    /// <see cref="UserPlaybackPolicy.Mode"/> cannot be parsed is ignored (the user falls back to
    /// the global policy) instead of failing playback.
    /// </summary>
    public List<UserPlaybackPolicy> UserPolicies { get; set; } = new();

    /// <summary>
    /// Gets the configured background probe delay clamped to a sane range (0–3600 seconds).
    /// </summary>
    public int GetBackgroundProbeDelaySeconds()
        => Math.Clamp(BackgroundProbeDelaySeconds, 0, MaxBackgroundProbeDelaySeconds);

    /// <summary>
    /// Gets the configured probe concurrency clamped to
    /// [<see cref="MinConcurrentProbes"/>, <see cref="MaxConcurrentProbesLimit"/>].
    /// </summary>
    public int GetMaxConcurrentProbes()
        => Math.Clamp(MaxConcurrentProbes, MinConcurrentProbes, MaxConcurrentProbesLimit);

    /// <summary>
    /// Gets the configured remote size request timeout clamped to
    /// [<see cref="MinRemoteSizeTimeoutSeconds"/>, <see cref="MaxRemoteSizeTimeoutSeconds"/>].
    /// </summary>
    public int GetRemoteSizeTimeoutSeconds()
        => Math.Clamp(RemoteSizeTimeoutSeconds, MinRemoteSizeTimeoutSeconds, MaxRemoteSizeTimeoutSeconds);

    /// <summary>
    /// Builds the global policy from the flat configuration fields.
    /// A per-user policy that does not override a value inherits exactly this object, so the
    /// global policy is the single baseline for every playback decision.
    /// </summary>
    public EffectivePlaybackPolicy GetGlobalPlaybackPolicy()
    {
        var skipProbe = SkipProbeForUnprobedStrm;

        return new EffectivePlaybackPolicy(
            skipProbe,
            // Background probing is meaningless when the item goes through the official probe,
            // so the two flags are combined here instead of at every call site.
            skipProbe && EnableBackgroundProbeForUnprobedStrm,
            GetBackgroundProbeDelaySeconds(),
            CancelBackgroundProbeOnPlaybackStop,
            PlaybackPolicySource.Global,
            null);
    }
}
