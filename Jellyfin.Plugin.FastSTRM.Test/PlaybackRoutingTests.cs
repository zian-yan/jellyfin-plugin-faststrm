using System;
using System.IO;
using FastSTRM.Playback;
using FastSTRM.Probing;
using MediaBrowser.Controller.Entities;
using Xunit;

namespace FastSTRM.Test;

/// <summary>
/// 播放分流判定：哪些条目可以直接走「零探测 · 直连播放」的快速路径。
/// </summary>
/// <remarks>
/// 快速路径一旦判错，代价是双向的：
/// 把未就绪的条目判成就绪 → 大小/时长永远补不上；
/// 把已就绪的条目判成未就绪 → 每次起播都多一次后台探测或取源等待，秒开失效。
/// </remarks>
public class PlaybackRoutingTests
{
    private static Video CreateStrmItem(long? size, long? runTimeTicks, string? path = null)
        => new()
        {
            Path = path ?? Path.Combine(Path.GetTempPath(), "faststrm-routing", "movie.strm"),
            Size = size,
            RunTimeTicks = runTimeTicks
        };

    private static string CreateStrmFile(int length)
    {
        var directory = Path.Combine(Path.GetTempPath(), "faststrm-routing", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, "movie.strm");
        File.WriteAllText(path, new string('a', length));
        return path;
    }

    [Fact]
    public void IsPlaybackReady_TrueWhenStreamInfoAndRemoteSizeArePresent()
    {
        var item = CreateStrmItem(
            size: 8L * 1024 * 1024,
            runTimeTicks: TimeSpan.FromMinutes(90).Ticks);

        Assert.True(MediaSourceInterceptor.IsPlaybackReady(item));
    }

    [Fact]
    public void IsPlaybackReady_FalseWhenRuntimeIsMissing()
    {
        // 无流：即使大小已补全，缺时长仍要按策略决定探测，不能走进快速路径。
        var item = CreateStrmItem(size: 8L * 1024 * 1024, runTimeTicks: null);

        Assert.False(MediaSourceInterceptor.IsPlaybackReady(item));
    }

    [Fact]
    public void IsPlaybackReady_FalseWhenSizeIsMissing()
    {
        var item = CreateStrmItem(size: null, runTimeTicks: TimeSpan.FromMinutes(90).Ticks);

        Assert.False(MediaSourceInterceptor.IsPlaybackReady(item));
    }

    [Fact]
    public void IsPlaybackReady_FalseWhenSizeIsStillTheStrmFileLength()
    {
        // 库扫描把大小写回了 .strm 文本文件长度：这类条目仍要补大小，不属于就绪。
        var strmPath = CreateStrmFile(180);
        var item = CreateStrmItem(size: 180, runTimeTicks: TimeSpan.FromMinutes(90).Ticks, path: strmPath);

        Assert.True(RemoteSizeService.IsRemoteSizeMissing(item));
        Assert.False(MediaSourceInterceptor.IsPlaybackReady(item));
    }

    [Fact]
    public void IsPlaybackReady_TrueForResolvedShortClip()
    {
        // 4 KiB–1 MiB 的短片：大小写对了就不该再被当成缺失，否则每次起播都要重新探测。
        var strmPath = CreateStrmFile(60);
        var item = CreateStrmItem(
            size: 300L * 1024,
            runTimeTicks: TimeSpan.FromSeconds(45).Ticks,
            path: strmPath);

        Assert.False(RemoteSizeService.IsRemoteSizeMissing(item));
        Assert.True(MediaSourceInterceptor.IsPlaybackReady(item));
    }
}
