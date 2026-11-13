using System.Net;
using FastSTRM.Common;
using Xunit;

namespace FastSTRM.Test;

public class StreamTargetPolicyTests
{
    [Fact]
    public void AlwaysBlockedRangesAreRefusedWhateverTheConfiguration()
    {
        string[] blocked =
        {
            "169.254.169.254",
            "169.254.1.1",
            "0.0.0.0",
            "224.0.0.1",
            "255.255.255.255",
            "::",
            "fe80::1",
            "ff02::1"
        };

        foreach (var address in blocked)
        {
            Assert.False(StreamTargetPolicy.IsAllowed(IPAddress.Parse(address), allowPrivateTargets: true), address);
            Assert.False(StreamTargetPolicy.IsAllowed(IPAddress.Parse(address), allowPrivateTargets: false), address);
        }
    }

    [Fact]
    public void PrivateRangesFollowConfiguration()
    {
        string[] privateAddresses =
        {
            "127.0.0.1",
            "::1",
            "10.1.2.3",
            "192.168.1.5",
            "172.16.0.1",
            "100.64.0.1",
            "fd00::1",
            "::ffff:192.168.1.10"
        };

        foreach (var address in privateAddresses)
        {
            Assert.True(StreamTargetPolicy.IsAllowed(IPAddress.Parse(address), allowPrivateTargets: true), address);
            Assert.False(StreamTargetPolicy.IsAllowed(IPAddress.Parse(address), allowPrivateTargets: false), address);
        }
    }

    [Fact]
    public void PublicAddressesAreAlwaysAllowed()
    {
        string[] publicAddresses =
        {
            "8.8.8.8",
            "172.32.0.1",
            "93.184.216.34",
            "2001:4860:4860::8888"
        };

        foreach (var address in publicAddresses)
        {
            Assert.True(StreamTargetPolicy.IsAllowed(IPAddress.Parse(address), allowPrivateTargets: true), address);
            Assert.True(StreamTargetPolicy.IsAllowed(IPAddress.Parse(address), allowPrivateTargets: false), address);
        }
    }

    [Fact]
    public void IPv4MappedPrivateAddressCannotBypassTheCheck()
    {
        Assert.False(StreamTargetPolicy.IsAllowed(IPAddress.Parse("::ffff:192.168.1.10"), allowPrivateTargets: false));
    }

    [Fact]
    public void IPv6TunnelledIPv4AddressesCannotBypassTheCheck()
    {
        Assert.False(
            StreamTargetPolicy.IsAllowed(IPAddress.Parse("64:ff9b::a00:5"), allowPrivateTargets: false),
            "NAT64 内嵌 10.0.0.5");

        Assert.False(
            StreamTargetPolicy.IsAllowed(IPAddress.Parse("64:ff9b::a9fe:a9fe"), allowPrivateTargets: true),
            "NAT64 内嵌 169.254.169.254（无论是否允许私网都必须拒绝）");

        Assert.False(
            StreamTargetPolicy.IsAllowed(IPAddress.Parse("2002:0a00:0005::"), allowPrivateTargets: false),
            "6to4 内嵌 10.0.0.5");

        Assert.False(
            StreamTargetPolicy.IsAllowed(IPAddress.Parse("2002:a9fe:a9fe::"), allowPrivateTargets: true),
            "6to4 内嵌 169.254.169.254");

        Assert.True(
            StreamTargetPolicy.IsAllowed(IPAddress.Parse("64:ff9b::808:808"), allowPrivateTargets: false),
            "NAT64 内嵌 8.8.8.8");
    }

    [Fact]
    public void LiteralHostIsEvaluatedSynchronously()
    {
        Assert.False(StreamTargetPolicy.EvaluateLiteralHost("169.254.169.254", true));
        Assert.True(StreamTargetPolicy.EvaluateLiteralHost("192.168.1.5", true));
        Assert.False(StreamTargetPolicy.EvaluateLiteralHost("192.168.1.5", false));
    }

    [Fact]
    public void HostNameNeedsAResolvedAddress()
    {
        Assert.Null(StreamTargetPolicy.EvaluateLiteralHost("media.example.com", true));
        Assert.False(StreamTargetPolicy.EvaluateLiteralHost(string.Empty, true));
    }
}
