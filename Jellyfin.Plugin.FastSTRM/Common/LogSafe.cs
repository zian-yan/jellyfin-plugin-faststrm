using System;
using MediaBrowser.Controller.Entities;

namespace FastSTRM.Common;

internal static class LogSafe
{
    public const int MaxLoggedLength = 120;

    public const int MaxDisplayedLength = 200;

    public static string Name(BaseItem item, int maxLength = MaxLoggedLength)
    {
        var name = item.Name ?? item.Path ?? string.Empty;
        return name.Length == 0 ? "(unnamed)" : Sanitize(name, maxLength);
    }

    public static string Sanitize(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var chars = (value.Length <= maxLength ? value : value[..maxLength] + "…").ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (char.IsControl(chars[i]))
            {
                chars[i] = ' ';
            }
        }

        return new string(chars);
    }
}
