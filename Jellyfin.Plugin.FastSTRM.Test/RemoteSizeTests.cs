using FastSTRM.Configuration;
using FastSTRM.Probing;
using MediaBrowser.Controller.Entities;
using Xunit;

namespace FastSTRM.Test;

/// <summary>
/// 远端文件大小的判定与阈值守卫。
/// </summary>
/// <remarks>
/// 「是否是 .strm 文件自身的大小」这一判断是整条补全链路的前提：
/// 判错要么把真实大小当成缺失反复请求，要么把 .strm 的几百字节当成真实值不再补全。
/// </remarks>
public class RemoteSizeTests
{
    [Fact]
    public void NeedsRemoteSize_TrueForMissingSize()
    {
        var item = new Video { Path = "/media/movie.strm", Size = null };

        Assert.True(RemoteSizeService.NeedsRemoteSize(item));
    }

    [Fact]
    public void NeedsRemoteSize_TrueForStrmFileSize()
    {
        // 一个正常 .strm 文本文件是几十到几百字节。
        var item = new Video { Path = "/media/movie.strm", Size = 180 };

        Assert.True(RemoteSizeService.NeedsRemoteSize(item));
    }

    [Fact]
    public void NeedsRemoteSize_FalseForPlausibleMediaSize()
    {
        var item = new Video { Path = "/media/movie.strm", Size = 8L * 1024 * 1024 };

        Assert.False(RemoteSizeService.NeedsRemoteSize(item));
    }

    [Fact]
    public void NeedsRemoteSize_BoundaryIsInclusive()
    {
        var atBoundary = new Video
        {
            Path = "/media/movie.strm",
            Size = RemoteSizeService.MinimumPlausibleMediaSize
        };

        var belowBoundary = new Video
        {
            Path = "/media/movie.strm",
            Size = RemoteSizeService.MinimumPlausibleMediaSize - 1
        };

        Assert.False(RemoteSizeService.NeedsRemoteSize(atBoundary));
        Assert.True(RemoteSizeService.NeedsRemoteSize(belowBoundary));
    }

    [Fact]
    public void RemoteSize_IsEnabledByDefault()
    {
        var configuration = new PluginConfiguration();

        Assert.True(configuration.EnableRemoteSize);
        Assert.Equal(PluginConfiguration.DefaultRemoteSizeTimeoutSeconds, configuration.GetRemoteSizeTimeoutSeconds());
    }

    [Theory]
    [InlineData(0, PluginConfiguration.MinRemoteSizeTimeoutSeconds)]
    [InlineData(-5, PluginConfiguration.MinRemoteSizeTimeoutSeconds)]
    [InlineData(120, PluginConfiguration.MaxRemoteSizeTimeoutSeconds)]
    [InlineData(15, 15)]
    public void RemoteSizeTimeout_IsClamped(int configured, int expected)
    {
        var configuration = new PluginConfiguration { RemoteSizeTimeoutSeconds = configured };

        Assert.Equal(expected, configuration.GetRemoteSizeTimeoutSeconds());
    }

    [Theory]
    [InlineData("Full", ProbeService.FullMode)]
    [InlineData("full", ProbeService.FullMode)]
    [InlineData("Incremental", ProbeService.IncrementalMode)]
    [InlineData("incremental", ProbeService.IncrementalMode)]
    [InlineData(null, ProbeService.IncrementalMode)]
    public void TryParseMode_AcceptsKnownModes(string? mode, string expected)
    {
        Assert.True(ProbeService.TryParseMode(mode, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void TryParseMode_RejectsUnknownMode()
    {
        Assert.False(ProbeService.TryParseMode("SizeOnlyy", out var normalized));
        Assert.Equal(ProbeService.IncrementalMode, normalized);
    }

    [Fact]
    public void TryParseMode_RejectsRemovedSizeOnlyMode()
    {
        // 「仅补全文件大小」已并入增量探测，旧脚本再传该值时必须被拒绝，
        // 而不是静默落到某个模式上。
        Assert.False(ProbeService.TryParseMode("SizeOnly", out var normalized));
        Assert.Equal(ProbeService.IncrementalMode, normalized);
    }
}
