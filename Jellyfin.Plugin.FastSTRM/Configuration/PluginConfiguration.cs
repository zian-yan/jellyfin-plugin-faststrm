using System;
using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace FastSTRM.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public const int MaxBackgroundProbeDelaySeconds = 3600;

    public const int MinConcurrentProbes = 1;

    public const int MaxConcurrentProbesLimit = 16;

    public const int MaxUserPolicies = 1000;

    public const int MinRemoteSizeTimeoutSeconds = 1;

    public const int MaxRemoteSizeTimeoutSeconds = 60;

    public const int DefaultRemoteSizeTimeoutSeconds = 10;

    public const bool DefaultSkipProbeForUnprobedStrm = true;

    public const bool DefaultEnableBackgroundProbeForUnprobedStrm = false;

    public const int DefaultBackgroundProbeDelaySeconds = 5;

    public const bool DefaultCancelBackgroundProbeOnPlaybackStop = true;

    /// <summary>
    /// 默认不允许请求内网地址。.strm 内容完全由外部提供，默认开启会把插件变成
    /// 内网探测跳板；局域网媒体服务器需要用户显式打开该开关。
    /// </summary>
    public const bool DefaultAllowPrivateNetworkTargets = false;

    public bool EnableAutoProbe { get; set; }

    public bool SkipProbeForUnprobedStrm { get; set; } = DefaultSkipProbeForUnprobedStrm;

    public bool EnableBackgroundProbeForUnprobedStrm { get; set; } = DefaultEnableBackgroundProbeForUnprobedStrm;

    public int BackgroundProbeDelaySeconds { get; set; } = DefaultBackgroundProbeDelaySeconds;

    public bool CancelBackgroundProbeOnPlaybackStop { get; set; } = DefaultCancelBackgroundProbeOnPlaybackStop;

    public int MaxConcurrentProbes { get; set; } = 1;

    public int RemoteSizeTimeoutSeconds { get; set; } = DefaultRemoteSizeTimeoutSeconds;

    public bool AllowPrivateNetworkTargets { get; set; } = DefaultAllowPrivateNetworkTargets;

    public List<UserPlaybackPolicy> UserPolicies { get; set; } = new();

    public int GetBackgroundProbeDelaySeconds()
        => Math.Clamp(BackgroundProbeDelaySeconds, 0, MaxBackgroundProbeDelaySeconds);

    public int GetMaxConcurrentProbes()
        => Math.Clamp(MaxConcurrentProbes, MinConcurrentProbes, MaxConcurrentProbesLimit);

    public int GetRemoteSizeTimeoutSeconds()
        => Math.Clamp(RemoteSizeTimeoutSeconds, MinRemoteSizeTimeoutSeconds, MaxRemoteSizeTimeoutSeconds);

    public EffectivePlaybackPolicy GetGlobalPlaybackPolicy()
    {
        var skipProbe = SkipProbeForUnprobedStrm;

        return new EffectivePlaybackPolicy(
            skipProbe,
            skipProbe && EnableBackgroundProbeForUnprobedStrm,
            GetBackgroundProbeDelaySeconds(),
            CancelBackgroundProbeOnPlaybackStop,
            PlaybackPolicySource.Global,
            null);
    }
}
