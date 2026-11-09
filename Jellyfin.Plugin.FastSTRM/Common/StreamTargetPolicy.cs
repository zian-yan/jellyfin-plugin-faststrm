using System;
using System.Net;
using System.Net.Sockets;

namespace FastSTRM.Common;

/// <summary>
/// 判定 .strm 里的流地址是否允许服务端直接请求。
/// </summary>
/// <remarks>
/// <para>
/// .strm 是库目录里的普通文本文件，任何能写该目录的人（共享目录、下载来的 strm 包、第三方
/// 脚本）都能决定插件去请求哪个地址。若不加限制，一次「打开详情页」就等于让服务器对内网
/// 发起一次 HTTP 请求：内网端口探测、命中云厂商元数据端点（169.254.169.254），而拿到的
/// Content-Length 会被写入 <c>BaseItem.Size</c>——该字段对所有已登录用户可见，盲 SSRF
/// 由此变成「内网资源长度侧信道」。
/// </para>
/// <para>
/// 分级策略：
/// </para>
/// <list type="bullet">
/// <item><description>
/// 链路本地 / 组播 / 未指定 / 保留段：<b>永远拒绝</b>。没有任何合法的媒体源住在这里，
/// 拒绝它也就切断了元数据端点这条最现实的攻击路径。
/// </description></item>
/// <item><description>
/// 环回 / RFC1918 / CGNAT / IPv6 ULA：默认允许（局域网 Emby、Alist、OpenList 是本插件的
/// 主场景），可由 <c>AllowPrivateNetworkTargets</c> 关闭。
/// </description></item>
/// <item><description>公网地址：允许。</description></item>
/// </list>
/// </remarks>
internal static class StreamTargetPolicy
{
    /// <summary>
    /// 返回该地址是否允许作为流地址的目标。
    /// </summary>
    /// <param name="address">即将连接的地址（已解析，不是主机名）。</param>
    /// <param name="allowPrivateTargets">是否允许环回/私网/CGNAT/ULA 目标。</param>
    public static bool IsAllowed(IPAddress address, bool allowPrivateTargets)
    {
        if (address is null)
        {
            return false;
        }

        // ::ffff:192.168.1.10 这类映射地址按 IPv4 规则判定，否则会绕过私网检查。
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

            return address.IsIPv6UniqueLocal ? allowPrivateTargets : true;
        }

        var bytes = address.GetAddressBytes();
        if (bytes.Length != 4)
        {
            return false;
        }

        // 0.0.0.0/8 未指定、169.254.0.0/16 链路本地（含 169.254.169.254 元数据端点）、
        // 224.0.0.0/4 组播、240.0.0.0/4 保留与广播：一律拒绝。
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

    /// <summary>
    /// 对字面量地址做同步判定。主机名无法在同步路径上判定（需要 DNS），返回 <c>null</c>，
    /// 由建连时的 <c>ConnectCallback</c> 在解析之后校验——那也是防 DNS rebinding 的位置。
    /// </summary>
    /// <returns><c>true</c>/<c>false</c> 为确定结论，<c>null</c> 表示需要在建连时再判定。</returns>
    public static bool? EvaluateLiteralHost(string? host, bool allowPrivateTargets)
    {
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }

        var trimmed = host.Trim('[', ']');
        if (!IPAddress.TryParse(trimmed, out var address))
        {
            return null;
        }

        return IsAllowed(address, allowPrivateTargets);
    }
}
