using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using MediaBrowser.Controller.Entities;

namespace FastSTRM.Common;

internal static class StrmItem
{
    private const string StrmSuffix = ".strm";

    private const string ProbeUnsupportedExtension = ".iso";

    private const int MaxProbeSupportEntries = 8192;

    private static readonly ConcurrentDictionary<string, bool> ProbeSupport =
        new(StringComparer.OrdinalIgnoreCase);

    public static bool IsStrm(BaseItem item)
        => item.Path is not null
           && item.Path.EndsWith(StrmSuffix, StringComparison.OrdinalIgnoreCase);

    public static bool HasStreamInfo(BaseItem item)
        => item.RunTimeTicks is > 0;

    public static bool IsProbeUnsupported(BaseItem item)
    {
        if (!IsStrm(item))
        {
            return false;
        }

        var path = item.Path!;
        if (ProbeSupport.TryGetValue(path, out var cached))
        {
            return cached;
        }

        var unsupported = HasProbeUnsupportedExtension(path) || HasProbeUnsupportedRemoteTarget(path);

        if (ProbeSupport.Count >= MaxProbeSupportEntries)
        {
            ProbeSupport.Clear();
        }

        ProbeSupport[path] = unsupported;
        return unsupported;
    }

    private static bool HasProbeUnsupportedExtension(string path)
    {
        var name = path[..^StrmSuffix.Length];
        return name.EndsWith(ProbeUnsupportedExtension, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasProbeUnsupportedRemoteTarget(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            for (var i = 0; i < 10; i++)
            {
                var line = reader.ReadLine();
                if (line is null)
                {
                    break;
                }

                line = line.Trim();
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }

                return Uri.TryCreate(line, UriKind.Absolute, out var uri)
                       && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                       && uri.AbsolutePath.EndsWith(ProbeUnsupportedExtension, StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (Exception)
        {
        }

        return false;
    }
}
