using System;

namespace FastSTRM.Probing;

public enum RemoteSizeTrigger
{
    None = 0,

    ItemAdded = 1,

    PlaybackProbe = 2,

    ManualIncrementalProbe = 3,

    ManualFullProbe = 4,
}

public static class RemoteSizeTriggerPolicy
{
    public static bool AllowsSizeFill(RemoteSizeTrigger trigger)
        => trigger is RemoteSizeTrigger.ItemAdded
            or RemoteSizeTrigger.PlaybackProbe
            or RemoteSizeTrigger.ManualIncrementalProbe
            or RemoteSizeTrigger.ManualFullProbe;

    public static bool RequiresRefresh(RemoteSizeTrigger trigger)
        => trigger == RemoteSizeTrigger.ManualFullProbe;

    public static string Describe(RemoteSizeTrigger trigger) => trigger switch
    {
        RemoteSizeTrigger.ItemAdded => nameof(RemoteSizeTrigger.ItemAdded),
        RemoteSizeTrigger.PlaybackProbe => nameof(RemoteSizeTrigger.PlaybackProbe),
        RemoteSizeTrigger.ManualIncrementalProbe => nameof(RemoteSizeTrigger.ManualIncrementalProbe),
        RemoteSizeTrigger.ManualFullProbe => nameof(RemoteSizeTrigger.ManualFullProbe),
        RemoteSizeTrigger.None => nameof(RemoteSizeTrigger.None),
        _ => "Undefined(" + (int)trigger + ")"
    };
}
