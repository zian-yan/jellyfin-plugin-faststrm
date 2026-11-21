using System;
using System.Collections.Generic;
using System.IO;
using FastSTRM.Playback;
using FastSTRM.Probing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using Xunit;

namespace FastSTRM.Test;

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

    private static string CreateStrmFileWithContent(string content)
    {
        var directory = Path.Combine(Path.GetTempPath(), "faststrm-routing", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, "movie.strm");
        File.WriteAllText(path, content);
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
        var strmPath = CreateStrmFile(180);
        var item = CreateStrmItem(size: 180, runTimeTicks: TimeSpan.FromMinutes(90).Ticks, path: strmPath);

        Assert.True(RemoteSizeService.IsRemoteSizeMissing(item));
        Assert.False(MediaSourceInterceptor.IsPlaybackReady(item));
    }

    [Fact]
    public void IsPlaybackReady_TrueForResolvedShortClip()
    {
        var strmPath = CreateStrmFile(60);
        var item = CreateStrmItem(
            size: 300L * 1024,
            runTimeTicks: TimeSpan.FromSeconds(45).Ticks,
            path: strmPath);

        Assert.False(RemoteSizeService.IsRemoteSizeMissing(item));
        Assert.True(MediaSourceInterceptor.IsPlaybackReady(item));
    }

    [Fact]
    public void ApplyShortcutTarget_UsesTheFreshSignedUrlFromTheStrmFile()
    {
        var url = "http://192.168.10.228:5244/d/media/movie.mkv?sign=abc=:0";
        var strmPath = CreateStrmFileWithContent(url + "\n");
        var item = CreateStrmItem(size: 296, runTimeTicks: null, path: strmPath);
        var sources = new List<MediaSourceInfo>
        {
            new()
            {
                Path = "/data/op_strm/movie.mkv.strm",
                Protocol = MediaProtocol.File,
                Container = "strm"
            }
        };

        MediaSourceInterceptor.ApplyShortcutTarget(item, sources);

        Assert.Equal(url, sources[0].Path);
        Assert.Equal(MediaProtocol.Http, sources[0].Protocol);
        Assert.True(sources[0].IsRemote);
        Assert.Equal("mkv", sources[0].Container);
    }

    [Fact]
    public void ApplyShortcutTarget_LeavesSourcesAloneWhenTheStrmFileHasNoRemoteUrl()
    {
        var strmPath = CreateStrmFileWithContent("/local/path/movie.mkv\n");
        var item = CreateStrmItem(size: 296, runTimeTicks: null, path: strmPath);
        var sources = new List<MediaSourceInfo>
        {
            new()
            {
                Path = "/data/op_strm/movie.mkv.strm",
                Protocol = MediaProtocol.File,
                Container = "strm"
            }
        };

        MediaSourceInterceptor.ApplyShortcutTarget(item, sources);

        Assert.Equal("/data/op_strm/movie.mkv.strm", sources[0].Path);
        Assert.Equal(MediaProtocol.File, sources[0].Protocol);
        Assert.Equal("strm", sources[0].Container);
    }
}
