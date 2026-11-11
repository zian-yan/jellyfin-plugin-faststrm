using System;
using System.IO;
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
    public void RemoteSize_UsesDefaultTimeout()
    {
        // 补全远端文件大小已无开关（3.6.1 起固定启用），只剩请求超时可配。
        var configuration = new PluginConfiguration();

        Assert.Equal(PluginConfiguration.DefaultRemoteSizeTimeoutSeconds, configuration.GetRemoteSizeTimeoutSeconds());
        Assert.Null(typeof(PluginConfiguration).GetProperty("EnableRemoteSize"));
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

    // ── 触发点 1：入库自动探测 ──

    [Fact]
    public void NewItem_IsSkippedWhenAutoProbeIsDisabled()
    {
        // 其他情况不触发：入库自动探测关闭时，ItemAdded 不产生任何补全动作。
        var item = new Video { Path = "/media/movie.strm", Size = 180 };

        Assert.False(ProbeService.ShouldHandleNewItem(item, autoProbeEnabled: false, out var fullProbe));
        Assert.False(fullProbe);
    }

    [Fact]
    public void NewItem_RunsFullProbeWhenAutoProbeIsEnabledAndStreamInfoIsMissing()
    {
        var item = new Video { Path = "/media/movie.strm", Size = 180 };

        Assert.True(ProbeService.ShouldHandleNewItem(item, autoProbeEnabled: true, out var fullProbe));
        Assert.True(fullProbe);
    }

    [Fact]
    public void NewItem_WithStreamInfo_StillFillsSize()
    {
        // 库扫描已写入流信息、但大小还是 .strm 文本大小的条目必须补大小。
        var item = new Video { Path = "/media/movie.strm", Size = 180, RunTimeTicks = 12_000_000 };

        Assert.True(ProbeService.ShouldHandleNewItem(item, autoProbeEnabled: true, out var fullProbe));
        Assert.False(fullProbe);
    }

    [Fact]
    public void NewItem_WithResolvedSize_IsSkipped()
    {
        var item = new Video { Path = "/media/movie.strm", Size = 8L * 1024 * 1024, RunTimeTicks = 12_000_000 };

        Assert.False(ProbeService.ShouldHandleNewItem(item, autoProbeEnabled: true, out var fullProbe));
        Assert.False(fullProbe);
    }

    // ── 触发点 2：起播后补探测 ──

    [Fact]
    public void PlaybackProbe_FillsSizeForAlreadyProbedItem()
    {
        // 回归：旧实现遇到已有流信息直接 return，这类条目永远补不上大小。
        var item = new Video { Path = "/media/movie.strm", Size = 180, RunTimeTicks = 12_000_000 };

        Assert.True(ProbeService.ShouldHandlePlaybackProbe(item, out var fullProbe));
        Assert.False(fullProbe);
    }

    [Fact]
    public void PlaybackProbe_RunsFullProbeForUnprobedItem()
    {
        var item = new Video { Path = "/media/movie.strm", Size = 180 };

        Assert.True(ProbeService.ShouldHandlePlaybackProbe(item, out var fullProbe));
        Assert.True(fullProbe);
    }

    [Fact]
    public void PlaybackProbe_SkipsItemWithResolvedSize()
    {
        var item = new Video { Path = "/media/movie.strm", Size = 8L * 1024 * 1024, RunTimeTicks = 12_000_000 };

        Assert.False(ProbeService.ShouldHandlePlaybackProbe(item, out var fullProbe));
        Assert.False(fullProbe);
    }

    // ── 统计口径：短片补上后不能再被算作「缺少文件大小」──

    [Fact]
    public void IsRemoteSizeMissing_TrueForNullSize()
    {
        var item = new Video { Path = "/media/movie.strm", Size = null };

        Assert.True(RemoteSizeService.IsRemoteSizeMissing(item));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(180L)]
    [InlineData(4095L)]
    public void IsRemoteSizeMissing_TrueForUnresolvedSizes(long size)
    {
        var item = new Video { Path = "/media/movie.strm", Size = size };

        Assert.True(RemoteSizeService.IsRemoteSizeMissing(item));
    }

    [Fact]
    public void IsRemoteSizeMissing_FalseForPlausibleMediaSize()
    {
        var item = new Video { Path = "/media/movie.strm", Size = 8L * 1024 * 1024 };

        Assert.False(RemoteSizeService.IsRemoteSizeMissing(item));
    }

    [Fact]
    public void IsRemoteSizeMissing_FalseForResolvedSmallClip()
    {
        var path = WriteStrm("http://example.invalid/clip.mkv\n");
        try
        {
            // 500 KB 的短片：低于 1 MiB 但确实是远端真实大小，统计上必须算已补全。
            var item = new Video { Path = path, Size = 500L * 1024L };

            Assert.True(RemoteSizeService.NeedsRemoteSize(item));
            Assert.False(RemoteSizeService.IsRemoteSizeMissing(item));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void IsRemoteSizeMissing_TrueWhenStoredSizeEqualsStrmFileLength()
    {
        // 一个异常大的 .strm 文本文件（>4 KiB）被当作媒体大小写进库：仍应算缺失。
        var path = Path.Combine(Path.GetTempPath(), "faststrm-" + Guid.NewGuid().ToString("N") + ".strm");
        try
        {
            File.WriteAllText(path, new string('x', 5000));
            var item = new Video { Path = path, Size = new FileInfo(path).Length };

            Assert.True(RemoteSizeService.IsRemoteSizeMissing(item));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string WriteStrm(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), "faststrm-" + Guid.NewGuid().ToString("N") + ".strm");
        File.WriteAllText(path, content);
        return path;
    }
}
