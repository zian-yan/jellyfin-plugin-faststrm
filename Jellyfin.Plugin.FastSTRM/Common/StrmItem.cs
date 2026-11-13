using System;
using MediaBrowser.Controller.Entities;

namespace FastSTRM.Common;

internal static class StrmItem
{
    public static bool IsStrm(BaseItem item)
        => item.Path is not null
           && item.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase);

    public static bool HasStreamInfo(BaseItem item)
        => item.RunTimeTicks is > 0;
}
