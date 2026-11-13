using System;

namespace FastSTRM.Configuration;

public static class PlaybackPolicyMode
{
    public const string Inherit = "Inherit";

    public const string InstantNoProbe = "InstantNoProbe";

    public const string InstantBackgroundProbe = "InstantBackgroundProbe";

    public const string OfficialProbe = "OfficialProbe";

    public static readonly string[] All =
    {
        Inherit,
        InstantNoProbe,
        InstantBackgroundProbe,
        OfficialProbe
    };

    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = Inherit;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        foreach (var mode in All)
        {
            if (string.Equals(mode, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                normalized = mode;
                return true;
            }
        }

        return false;
    }

    public static string Describe(bool skipProbe, bool enableBackgroundProbe)
        => !skipProbe
            ? OfficialProbe
            : enableBackgroundProbe
                ? InstantBackgroundProbe
                : InstantNoProbe;
}

public sealed class UserPlaybackPolicy
{
    public Guid UserId { get; set; }

    public string Mode { get; set; } = PlaybackPolicyMode.Inherit;

    public int? BackgroundProbeDelaySeconds { get; set; }
}

public static class PlaybackPolicySource
{
    public const string Global = "Global";

    public const string User = "User";

    public const string Inherit = "Inherit";

    public const string Fallback = "Fallback";
}

public static class PlaybackPolicyFallbackReason
{
    public const string InvalidMode = "InvalidMode";
}

public readonly record struct EffectivePlaybackPolicy(
    bool SkipProbe,
    bool EnableBackgroundProbe,
    int BackgroundProbeDelaySeconds,
    bool CancelBackgroundProbeOnPlaybackStop,
    string Source,
    string? FallbackReason)
{
    public static EffectivePlaybackPolicy Defaults { get; } = new(
        PluginConfiguration.DefaultSkipProbeForUnprobedStrm,
        PluginConfiguration.DefaultSkipProbeForUnprobedStrm
            && PluginConfiguration.DefaultEnableBackgroundProbeForUnprobedStrm,
        PluginConfiguration.DefaultBackgroundProbeDelaySeconds,
        PluginConfiguration.DefaultCancelBackgroundProbeOnPlaybackStop,
        PlaybackPolicySource.Global,
        null);

    public bool ShouldProbeInBackground => SkipProbe && EnableBackgroundProbe;

    public string Mode => PlaybackPolicyMode.Describe(SkipProbe, EnableBackgroundProbe);
}
