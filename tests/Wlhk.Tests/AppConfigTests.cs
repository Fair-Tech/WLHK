using System.Text.Json;
using Wlhk.Core;
using Xunit;

namespace Wlhk.Tests;

public sealed class AppConfigTests
{
    [Fact]
    public void LegacyActionWithoutMixIdRemainsChannelWide()
    {
        const string json = """
            {"hotkeys":{"CTRL+M":{"normalAction":{"type":"mute_channel","channelId":"mic"}}}}
            """;
        var config = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.AppConfig)!;
        var action = config.Hotkeys["CTRL+M"].NormalAction!;

        Assert.Equal("mic", action.ChannelId);
        Assert.Null(action.MixId);
        Assert.DoesNotContain("\"mixId\"", JsonSerializer.Serialize(config, ConfigJsonContext.Default.AppConfig));
    }

    [Fact]
    public void PairTargetRoundTripsInCamelCase()
    {
        var config = new AppConfig
        {
            Hotkeys = new()
            {
                ["CTRL+M"] = new HotkeyBinding
                {
                    NormalAction = new HotkeyAction
                    {
                        Type = "mute_channel", ChannelId = "mic", MixId = "vc"
                    }
                }
            }
        };

        string json = JsonSerializer.Serialize(config, ConfigJsonContext.Default.AppConfig);
        var clone = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.AppConfig)!;

        Assert.Contains("\"mixId\": \"vc\"", json);
        Assert.Equal("vc", clone.Hotkeys["CTRL+M"].NormalAction!.MixId);
    }
}
