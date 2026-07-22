using Wlhk.UI;
using Wlhk.WaveLink;
using Xunit;

namespace Wlhk.Tests;

public sealed class MixTargetChoicesTests
{
    [Fact]
    public void BuildsAllMixesThenMembershipInWaveLinkOrder()
    {
        var channel = new Channel
        {
            Mixes = [new ChannelMix { Id = "vc" }, new ChannelMix { Id = "stream" }]
        };
        Mix[] mixes =
        [
            new Mix { Id = "stream", Name = "Stream" },
            new Mix { Id = "unused", Name = "Unused" },
            new Mix { Id = "vc", Name = "VC" }
        ];

        var choices = MixTargetChoices.Build(channel, mixes, null);

        Assert.Collection(choices,
            choice => Assert.Null(choice.Id),
            choice => Assert.Equal("stream", choice.Id),
            choice => Assert.Equal("vc", choice.Id));
        Assert.Equal(["All Mixes", "Stream", "VC"], choices.Select(c => c.Name).ToArray());
    }

    [Fact]
    public void RetainsUnavailableSavedMixWithoutRedirecting()
    {
        var choices = MixTargetChoices.Build(new Channel(), [], "removed-mix");

        Assert.Equal("removed-mix", choices[1].Id);
        Assert.Equal("(unavailable) removed-mix", choices[1].Name);
    }
}
