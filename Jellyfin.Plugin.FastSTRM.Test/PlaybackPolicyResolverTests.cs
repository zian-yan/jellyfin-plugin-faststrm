using System;
using FastSTRM.Configuration;
using FastSTRM.Playback;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FastSTRM.Test;

public class PlaybackPolicyResolverTests
{
    [Fact]
    public void PlaybackPolicy_IsRememberedPerUser()
    {
        var resolver = new PlaybackPolicyResolver(NullLogger<PlaybackPolicyResolver>.Instance);
        var itemId = Guid.NewGuid();
        var firstUser = Guid.NewGuid();
        var secondUser = Guid.NewGuid();

        var backgroundProbe = EffectivePlaybackPolicy.Defaults with
        {
            SkipProbe = true,
            EnableBackgroundProbe = true,
            BackgroundProbeDelaySeconds = 3
        };

        var noProbe = EffectivePlaybackPolicy.Defaults with
        {
            SkipProbe = true,
            EnableBackgroundProbe = false
        };

        resolver.RememberPlaybackPolicy(itemId, firstUser, backgroundProbe);
        resolver.RememberPlaybackPolicy(itemId, secondUser, noProbe);

        Assert.True(resolver.TryGetPlaybackPolicy(itemId, firstUser, out var first));
        Assert.True(first.EnableBackgroundProbe);
        Assert.Equal(3, first.BackgroundProbeDelaySeconds);

        Assert.True(resolver.TryGetPlaybackPolicy(itemId, secondUser, out var second));
        Assert.False(second.EnableBackgroundProbe);
    }

    [Fact]
    public void PlaybackPolicy_IsNotSharedWithAnotherUser()
    {
        var resolver = new PlaybackPolicyResolver(NullLogger<PlaybackPolicyResolver>.Instance);
        var itemId = Guid.NewGuid();

        resolver.RememberPlaybackPolicy(itemId, Guid.NewGuid(), EffectivePlaybackPolicy.Defaults);

        Assert.False(resolver.TryGetPlaybackPolicy(itemId, Guid.NewGuid(), out _));
        Assert.False(resolver.TryGetPlaybackPolicy(Guid.Empty, Guid.NewGuid(), out _));
    }
}
