using System;
using System.IO;
using FastSTRM.Configuration;
using FastSTRM.Probing;
using MediaBrowser.Controller.Entities;
using Xunit;

namespace FastSTRM.Test;

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
        Assert.False(ProbeService.TryParseMode("SizeOnly", out var normalized));
        Assert.Equal(ProbeService.IncrementalMode, normalized);
    }

    [Fact]
    public void NewItem_IsSkippedWhenAutoProbeIsDisabled()
    {
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

    [Fact]
    public void NewItem_NonStrmItemIsNeverHandled()
    {
        var item = new Video { Path = "/media/movie.mkv", Size = null };

        Assert.False(ProbeService.ShouldHandleNewItem(item, autoProbeEnabled: true, out var fullProbe));
        Assert.False(fullProbe);
    }

    [Fact]
    public void PlaybackProbe_FillsSizeForAlreadyProbedItem()
    {
        var item = new Video { Path = "/media/movie.strm", Size = 180, RunTimeTicks = 12_000_000 };

        Assert.True(ProbeService.ShouldHandlePlaybackProbe(item, BackgroundProbe, out var fullProbe));
        Assert.False(fullProbe);
    }

    [Fact]
    public void PlaybackProbe_RunsFullProbeForUnprobedItem()
    {
        var item = new Video { Path = "/media/movie.strm", Size = 180 };

        Assert.True(ProbeService.ShouldHandlePlaybackProbe(item, BackgroundProbe, out var fullProbe));
        Assert.True(fullProbe);
    }

    [Fact]
    public void PlaybackProbe_SkipsItemWithResolvedSize()
    {
        var item = new Video { Path = "/media/movie.strm", Size = 8L * 1024 * 1024, RunTimeTicks = 12_000_000 };

        Assert.False(ProbeService.ShouldHandlePlaybackProbe(item, BackgroundProbe, out var fullProbe));
        Assert.False(fullProbe);
    }

    [Fact]
    public void PlaybackProbe_IsRejectedByOfficialProbePolicy()
    {
        var probed = new Video { Path = "/media/movie.strm", Size = 180, RunTimeTicks = 12_000_000 };
        var unprobed = new Video { Path = "/media/movie.strm", Size = 180 };

        Assert.False(ProbeService.ShouldHandlePlaybackProbe(probed, OfficialProbe, out var probedFull));
        Assert.False(probedFull);

        Assert.False(ProbeService.ShouldHandlePlaybackProbe(unprobed, OfficialProbe, out var unprobedFull));
        Assert.False(unprobedFull);
    }

    [Fact]
    public void PlaybackProbe_IsRejectedByNoProbePolicy()
    {
        var probed = new Video { Path = "/media/movie.strm", Size = 180, RunTimeTicks = 12_000_000 };
        var unprobed = new Video { Path = "/media/movie.strm", Size = 180 };

        Assert.False(ProbeService.ShouldHandlePlaybackProbe(probed, NoProbe, out _));
        Assert.False(ProbeService.ShouldHandlePlaybackProbe(unprobed, NoProbe, out _));
    }

    [Fact]
    public void PlaybackProbe_RejectsNonStrmItem()
    {
        var item = new Video { Path = "/media/movie.mkv", Size = 180 };

        Assert.False(ProbeService.ShouldHandlePlaybackProbe(item, BackgroundProbe, out var fullProbe));
        Assert.False(fullProbe);
    }

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

    [Theory]
    [InlineData(RemoteSizeTrigger.ItemAdded, true)]
    [InlineData(RemoteSizeTrigger.PlaybackProbe, true)]
    [InlineData(RemoteSizeTrigger.ManualIncrementalProbe, true)]
    [InlineData(RemoteSizeTrigger.ManualFullProbe, true)]
    [InlineData(RemoteSizeTrigger.None, false)]
    public void TriggerPolicy_AllowsOnlyTheFourProbeScenarios(RemoteSizeTrigger trigger, bool expected)
        => Assert.Equal(expected, RemoteSizeTriggerPolicy.AllowsSizeFill(trigger));

    [Theory]
    [InlineData(5)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void TriggerPolicy_RejectsUndefinedTriggerValues(int raw)
    {
        var trigger = (RemoteSizeTrigger)raw;

        Assert.False(RemoteSizeTriggerPolicy.AllowsSizeFill(trigger));
        Assert.False(RemoteSizeTriggerPolicy.RequiresRefresh(trigger));
        Assert.False(string.IsNullOrWhiteSpace(RemoteSizeTriggerPolicy.Describe(trigger)));
    }

    [Fact]
    public void TriggerPolicy_ForcesRefreshOnlyForFullProbe()
    {
        Assert.True(RemoteSizeTriggerPolicy.RequiresRefresh(RemoteSizeTrigger.ManualFullProbe));

        Assert.False(RemoteSizeTriggerPolicy.RequiresRefresh(RemoteSizeTrigger.ManualIncrementalProbe));
        Assert.False(RemoteSizeTriggerPolicy.RequiresRefresh(RemoteSizeTrigger.ItemAdded));
        Assert.False(RemoteSizeTriggerPolicy.RequiresRefresh(RemoteSizeTrigger.PlaybackProbe));
        Assert.False(RemoteSizeTriggerPolicy.RequiresRefresh(RemoteSizeTrigger.None));
    }

    [Theory]
    [InlineData("Full", RemoteSizeTrigger.ManualFullProbe)]
    [InlineData("full", RemoteSizeTrigger.ManualFullProbe)]
    [InlineData("Incremental", RemoteSizeTrigger.ManualIncrementalProbe)]
    [InlineData("incremental", RemoteSizeTrigger.ManualIncrementalProbe)]
    [InlineData(null, RemoteSizeTrigger.ManualIncrementalProbe)]
    [InlineData("", RemoteSizeTrigger.ManualIncrementalProbe)]
    [InlineData("SizeOnly", RemoteSizeTrigger.ManualIncrementalProbe)]
    public void ProbeMode_MapsToManualTrigger(string? mode, RemoteSizeTrigger expected)
        => Assert.Equal(expected, ProbeService.GetRemoteSizeTrigger(mode));

    [Theory]
    [InlineData("Full")]
    [InlineData("Incremental")]
    public void ManualProbeModes_AreAllowedToFillSize(string mode)
    {
        Assert.True(RemoteSizeTriggerPolicy.AllowsSizeFill(ProbeService.GetRemoteSizeTrigger(mode)));
    }

    [Fact]
    public void IsRemoteSizeMissing_BoundaryAtAcceptableMinimum()
    {
        var path = WriteStrm("http://example.invalid/movie.mkv\n");
        try
        {
            var fileLength = new FileInfo(path).Length;

            var atMinimum = new Video { Path = path, Size = RemoteSizeService.MinAcceptableRemoteSize };
            Assert.True(atMinimum.Size != fileLength);
            Assert.False(RemoteSizeService.IsRemoteSizeMissing(atMinimum));

            var belowMinimum = new Video { Path = path, Size = RemoteSizeService.MinAcceptableRemoteSize - 1 };
            Assert.True(RemoteSizeService.IsRemoteSizeMissing(belowMinimum));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void IsRemoteSizeMissing_TrueWhenStrmFileIsUnreadable()
    {
        var item = new Video { Path = Path.Combine(Path.GetTempPath(), "faststrm-missing-" + Guid.NewGuid().ToString("N") + ".strm"), Size = 500L * 1024L };

        Assert.True(RemoteSizeService.IsRemoteSizeMissing(item));
    }

    private static EffectivePlaybackPolicy BackgroundProbe { get; } = Policy(skipProbe: true, backgroundProbe: true);

    private static EffectivePlaybackPolicy NoProbe { get; } = Policy(skipProbe: true, backgroundProbe: false);

    private static EffectivePlaybackPolicy OfficialProbe { get; } = Policy(skipProbe: false, backgroundProbe: false);

    private static EffectivePlaybackPolicy Policy(bool skipProbe, bool backgroundProbe)
        => new(
            skipProbe,
            skipProbe && backgroundProbe,
            PluginConfiguration.DefaultBackgroundProbeDelaySeconds,
            PluginConfiguration.DefaultCancelBackgroundProbeOnPlaybackStop,
            PlaybackPolicySource.Global,
            null);

    private static string WriteStrm(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), "faststrm-" + Guid.NewGuid().ToString("N") + ".strm");
        File.WriteAllText(path, content);
        return path;
    }
}
