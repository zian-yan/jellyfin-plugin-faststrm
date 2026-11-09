using System.Net;
using FastSTRM.Common;
using Xunit;

namespace FastSTRM.Test;

/// <summary>
/// 流地址目标策略：.strm 是不可信输入，服务端只应请求「合法媒体源可能存在的地址」。
/// </summary>
/// <remarks>
/// 用表驱动的单 Fact 而不是 Theory：判定矩阵是一条完整的规格，成组断言比一条条用例更容易
/// 看出「哪一档被漏掉了」。
/// </remarks>
public class StreamTargetPolicyTests
{
    [Fact]
    public void AlwaysBlockedRangesAreRefusedWhateverTheConfiguration()
    {
        // 链路本地（含 169.254.169.254 元数据端点）、未指定、组播、广播：没有合法媒体源住在这里。
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
        // 环回 / RFC1918 / CGNAT / ULA：默认放行（局域网媒体服务器是主场景），可配置关闭。
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
    public void LiteralHostIsEvaluatedSynchronously()
    {
        Assert.False(StreamTargetPolicy.EvaluateLiteralHost("169.254.169.254", true));
        Assert.True(StreamTargetPolicy.EvaluateLiteralHost("192.168.1.5", true));
        Assert.False(StreamTargetPolicy.EvaluateLiteralHost("192.168.1.5", false));
    }

    [Fact]
    public void HostNameNeedsAResolvedAddress()
    {
        // 主机名必须在建连时解析后判定（ConnectCallback），同步路径返回 null 表示「待判定」。
        Assert.Null(StreamTargetPolicy.EvaluateLiteralHost("media.example.com", true));
        Assert.False(StreamTargetPolicy.EvaluateLiteralHost(string.Empty, true));
    }
}
