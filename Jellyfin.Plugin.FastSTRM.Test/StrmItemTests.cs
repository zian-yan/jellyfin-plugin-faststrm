using System;
using System.IO;
using FastSTRM.Common;
using MediaBrowser.Controller.Entities;
using Xunit;

namespace FastSTRM.Test;

public class StrmItemTests
{
    private static string WriteStrm(string content)
    {
        var directory = Path.Combine(Path.GetTempPath(), "faststrm-iso", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, "movie.strm");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void ProbeUnsupported_TrueForIsoNamedStrm()
    {
        var item = new Video { Path = "/media/Hotel.Transylvania.2012.iso.strm" };

        Assert.True(StrmItem.IsProbeUnsupported(item));
    }

    [Fact]
    public void ProbeUnsupported_TrueForIsoNamedStrmInUpperCase()
    {
        var item = new Video { Path = "/media/Hotel.Transylvania.2012.ISO.STRM" };

        Assert.True(StrmItem.IsProbeUnsupported(item));
    }

    [Fact]
    public void ProbeUnsupported_TrueForIsoUrlInsideAPlainStrm()
    {
        var path = WriteStrm("http://192.168.10.228:5244/d/media/Hotel.Transylvania.2012.iso\n");
        try
        {
            var item = new Video { Path = path };

            Assert.True(StrmItem.IsProbeUnsupported(item));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ProbeUnsupported_TrueForIsoUrlWithQueryString()
    {
        var path = WriteStrm("# comment\n\nhttp://192.168.10.228:5244/d/media/movie.iso?token=abc\n");
        try
        {
            var item = new Video { Path = path };

            Assert.True(StrmItem.IsProbeUnsupported(item));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ProbeUnsupported_FalseForRegularVideo()
    {
        var path = WriteStrm("http://192.168.10.228:5244/d/media/movie.mkv\n");
        try
        {
            var item = new Video { Path = path };

            Assert.False(StrmItem.IsProbeUnsupported(item));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ProbeUnsupported_FalseForMkvNamedStrm()
    {
        var item = new Video { Path = "/media/movie.mkv.strm" };

        Assert.False(StrmItem.IsProbeUnsupported(item));
    }

    [Fact]
    public void ProbeUnsupported_FalseForNonStrmItem()
    {
        var item = new Video { Path = "/media/movie.iso" };

        Assert.False(StrmItem.IsProbeUnsupported(item));
    }

    [Fact]
    public void ProbeUnsupported_FalseWhenTheStrmFileIsMissing()
    {
        var item = new Video { Path = "/media/does-not-exist/movie.strm" };

        Assert.False(StrmItem.IsProbeUnsupported(item));
    }
}
