using System.Text.Json.Nodes;
using Wlhk.WaveLink;
using Xunit;

namespace Wlhk.Tests;

public sealed class WaveLinkProtocolTests
{
    [Fact]
    public void ParsesChannelMixStateAndNamedMixes()
    {
        var channels = WaveLinkProtocol.ParseChannels(JsonNode.Parse("""
            [{"id":"mic","name":"Microphone","level":0.8,"isMuted":false,
              "mixes":[{"id":"vc","level":0.4,"isMuted":true}]}]
            """));
        var mixes = WaveLinkProtocol.ParseMixes(JsonNode.Parse("""
            [{"id":"vc","name":"VC","level":1.0,"isMuted":false}]
            """));

        var channelMix = Assert.Single(Assert.Single(channels).Mixes);
        Assert.Equal(("vc", 0.4, true), (channelMix.Id, channelMix.Level, channelMix.IsMuted));
        Assert.Equal("VC", Assert.Single(mixes).Name);
    }

    [Fact]
    public void AppliesOnlyAddressedChannelMixPatch()
    {
        var channel = new Channel
        {
            Id = "mic",
            Mixes =
            [
                new ChannelMix { Id = "vc", Level = 0.4, IsMuted = false },
                new ChannelMix { Id = "stream", Level = 0.7, IsMuted = false }
            ]
        };

        WaveLinkProtocol.ApplyChannelPatch(channel, JsonNode.Parse("""
            {"id":"mic","mixes":[{"id":"vc","level":0.2,"isMuted":true}]}
            """)!);

        Assert.Equal((0.2, true), (channel.Mixes[0].Level, channel.Mixes[0].IsMuted));
        Assert.Equal((0.7, false), (channel.Mixes[1].Level, channel.Mixes[1].IsMuted));
    }

    [Theory]
    [InlineData(true, null, """{"id":"mic","mixes":[{"id":"vc","isMuted":true}]}""")]
    [InlineData(null, 0.35, """{"id":"mic","mixes":[{"id":"vc","level":0.35}]}""")]
    public void BuildsNestedPairPayload(bool? muted, double? level, string expected)
    {
        var payload = WaveLinkProtocol.BuildSetChannelMixParams("mic", "vc", level, muted);
        Assert.Equal(expected, payload.ToJsonString());
    }
}
