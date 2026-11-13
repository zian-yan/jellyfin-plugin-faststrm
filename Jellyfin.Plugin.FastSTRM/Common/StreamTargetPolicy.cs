using System;
using System.Net;
using System.Net.Sockets;

namespace FastSTRM.Common;

internal static class StreamTargetPolicy
{
    public static bool IsAllowed(IPAddress address, bool allowPrivateTargets)
    {
        if (address is null)
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return allowPrivateTargets;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.Equals(IPAddress.IPv6Any)
                || address.Equals(IPAddress.IPv6None)
                || address.IsIPv6LinkLocal
                || address.IsIPv6SiteLocal
                || address.IsIPv6Multicast)
            {
                return false;
            }

            if (TryExtractEmbeddedIPv4(address, out var embedded))
            {
                return IsAllowed(embedded, allowPrivateTargets);
            }

            return address.IsIPv6UniqueLocal ? allowPrivateTargets : true;
        }

        var bytes = address.GetAddressBytes();
        if (bytes.Length != 4)
        {
            return false;
        }

        if (bytes[0] == 0
            || bytes[0] >= 224
            || (bytes[0] == 169 && bytes[1] == 254))
        {
            return false;
        }

        var isPrivate = bytes[0] == 10
            || bytes[0] == 127
            || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            || (bytes[0] == 192 && bytes[1] == 168)
            || (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127);

        return !isPrivate || allowPrivateTargets;
    }

    public static bool? EvaluateLiteralHost(string? host, bool allowPrivateTargets)
    {
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }

        return IPAddress.TryParse(host.Trim('[', ']'), out var address)
            ? IsAllowed(address, allowPrivateTargets)
            : null;
    }

    private static bool TryExtractEmbeddedIPv4(IPAddress address, out IPAddress embedded)
    {
        embedded = IPAddress.None;

        var bytes = address.GetAddressBytes();
        if (bytes.Length != 16)
        {
            return false;
        }

        if (bytes[0] == 0x20 && bytes[1] == 0x02)
        {
            embedded = new IPAddress(new[] { bytes[2], bytes[3], bytes[4], bytes[5] });
            return true;
        }

        if (bytes[0] == 0x00 && bytes[1] == 0x64 && bytes[2] == 0xFF && bytes[3] == 0x9B)
        {
            embedded = new IPAddress(new[] { bytes[12], bytes[13], bytes[14], bytes[15] });
            return true;
        }

        return false;
    }
}
