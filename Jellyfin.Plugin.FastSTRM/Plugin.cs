using System;
using System.Collections.Generic;
using FastSTRM.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace FastSTRM;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public const string PluginGuid = "a1b2c3d4-5678-9abc-def0-123456789abc";

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "FastSTRM";

    public override Guid Id => Guid.Parse(PluginGuid);

    public override string Description => "消除 .strm 文件播放时的 ffprobe 延迟，实现秒开起播";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        var ns = GetType().Namespace!;
        return new[]
        {
            new PluginPageInfo
            {
                Name = "FastStrmConfigurationPage",
                DisplayName = "FastSTRM",
                EnableInMainMenu = true,
                MenuIcon = "settings",
                EmbeddedResourcePath = ns + ".Configuration.configPage.html"
            },
            new PluginPageInfo
            {
                Name = "FastSTRM.js",
                EmbeddedResourcePath = ns + ".Configuration.configPage.js"
            }
        };
    }
}
