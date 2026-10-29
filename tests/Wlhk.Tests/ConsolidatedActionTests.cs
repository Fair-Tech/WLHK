using System.Text.Json;
using Wlhk.Core;
using Xunit;

namespace Wlhk.Tests;

/// <summary>Consolidated action kinds, legacy upgrade, Mute/Volume modes and per-action OSD (v2.6).</summary>
public sealed class ConsolidatedActionTests
{
    // ─── Upgrade ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("mute_channel", "mute", "toggle")]
    [InlineData("volume_up_channel", "volume", "up")]
    [InlineData("volume_down_channel", "volume", "down")]
    [InlineData("set_volume", "volume", "set")]
    [InlineData("media_play_pause", "media", "play_pause")]
    [InlineData("media_next", "media", "next")]
    [InlineData("media_previous", "media", "previous")]
    [InlineData("media_stop", "media", "stop")]
    [InlineData("media_volume_up", "media", "volume_up")]
    [InlineData("media_volume_down", "media", "volume_down")]
    public void LegacyTypesUpgradeToKindAndMode(string legacy, string kind, string mode)
    {
        var action = new HotkeyAction { Type = legacy, ChannelId = "mic", Step = 7 };

        Assert.True(ActionCatalog.Upgrade(action));
        Assert.Equal(kind, action.Type);
        Assert.Equal(mode, action.Mode);
        Assert.Equal("mic", action.ChannelId); // targets and settings survive
        Assert.Equal(7, action.Step);
    }

    [Fact]
    public void UpgradeIsIdempotentAndOutputActionsHaveNoMode()
    {
        var mute = new HotkeyAction { Type = "mute", Mode = "unmute" };
        var output = new HotkeyAction { Type = "switch_output", DeviceId = "spk" };

        Assert.False(ActionCatalog.Upgrade(mute));
        Assert.Equal("unmute", mute.Mode);
        Assert.False(ActionCatalog.Upgrade(output));
        Assert.Null(output.Mode);
    }

    [Fact]
    public void ResolveReadsLegacyTypesWithoutChangingThem()
    {
        var action = new HotkeyAction { Type = "set_volume" };
        Assert.Equal(("volume", "set"), ActionCatalog.Resolve(action));
        Assert.Equal("set_volume", action.Type);
    }

    [Fact]
    public void LoadUpgradesLegacyConfigAndKeepsABackup()
    {
        string dir = Path.Combine(Path.GetTempPath(), "WLHK.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "config.json");
            const string legacy = """
                {"hotkeys":{"VOLUME_MUTE":{"holdAction":{"type":"cycle_output","deviceIds":["a","b"]},
                 "normalAction":{"type":"volume_down_channel","channelId":"mic","step":3}}}}
                """;
            File.WriteAllText(path, legacy);

            var store = new ConfigStore(path);
            store.Load();

            var normal = store.Current.Hotkeys["VOLUME_MUTE"].NormalAction!;
            Assert.Equal(("volume", "down"), (normal.Type, normal.Mode));
            Assert.Equal(3, normal.Step);
            Assert.Equal(legacy, File.ReadAllText(path + ".pre-2.6.bak"));
            Assert.Contains("\"type\": \"volume\"", File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void NewFieldsStayOutOfConfigUntilUsed()
    {
        var config = new AppConfig();
        config.Hotkeys["F13"] = new HotkeyBinding { NormalAction = new HotkeyAction { Type = "mute", Mode = "toggle" } };

        string json = JsonSerializer.Serialize(config, ConfigJsonContext.Default.AppConfig);

        foreach (string field in new[] { "useActiveTarget", "\"scope\"", "effectId", "effectName", "targetId", "\"osd\"", "repeat", "autoRepeat", "globalFeatures", "activeTargetId" })
            Assert.DoesNotContain(field, json);
    }

    [Fact]
    public void PreReleaseActiveTargetFlagUpgradesToScope()
    {
        var action = new HotkeyAction { Type = "volume", Mode = "up", UseActiveTarget = true };

        Assert.True(ActionCatalog.Upgrade(action));
        Assert.Equal("active", action.Scope);
        Assert.Null(action.UseActiveTarget);
    }

    [Fact]
    public void PreReleaseGlobalFeaturesVolumeKeysCarryOver()
    {
        string dir = Path.Combine(Path.GetTempPath(), "WLHK.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "config.json");
            File.WriteAllText(path, """{"globalFeatures":{"volumeKeys":"wavelink","trackKeys":"target"}}""");

            var store = new ConfigStore(path);
            store.Load();

            Assert.Equal("wavelink", store.Current.VolumeKeys);
            Assert.Null(store.Current.LegacyGlobalFeatures);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ─── Mute modes ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("toggle", false, true)]
    [InlineData("toggle", true, false)]
    [InlineData("mute", false, true)]
    [InlineData("mute", true, true)]    // already muted: still sent, so watchers get an on/off signal
    [InlineData("unmute", true, false)]
    [InlineData("unmute", false, false)]
    public void MuteModesSetTheExpectedState(string mode, bool startMuted, bool expected)
    {
        var wl = new FakeWaveLink().WithChannel("mic", "Microphone", muted: startMuted);

        Executor(wl).Execute(new HotkeyAction { Type = "mute", Mode = mode, ChannelId = "mic" });

        Assert.Equal(expected, Assert.Single(wl.ChannelWrites).Muted);
    }

    [Fact]
    public void ExplicitMuteWorksOnASingleMix()
    {
        var wl = new FakeWaveLink().WithChannel("mic", "Microphone", mixes: new Wlhk.WaveLink.ChannelMix { Id = "vc", IsMuted = true });

        Executor(wl).Execute(new HotkeyAction { Type = "mute", Mode = "mute", ChannelId = "mic", MixId = "vc" });

        Assert.Equal(("mic", "vc", (double?)null, (bool?)true), Assert.Single(wl.PairWrites));
    }

    // ─── Volume modes ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("up", 0.60)]
    [InlineData("down", 0.40)]
    [InlineData("set", 0.25)]
    public void VolumeModes(string mode, double expected)
    {
        var wl = new FakeWaveLink().WithChannel("music", "Music", level: 0.50);

        Executor(wl).Execute(new HotkeyAction { Type = "volume", Mode = mode, ChannelId = "music", Step = 10, Value = 25 });

        Assert.Equal(expected, Assert.Single(wl.ChannelWrites).Level!.Value, 6);
    }

    // ─── Per-action OSD ─────────────────────────────────────────────────────────

    [Fact]
    public void OsdOffSilencesTheAction()
    {
        var wl = new FakeWaveLink().WithChannel("mic", "Microphone");
        var osd = new List<(string, string?, int?)>();

        Executor(wl, osd).Execute(new HotkeyAction { Type = "mute", Mode = "toggle", ChannelId = "mic", Osd = false });

        Assert.Single(wl.ChannelWrites); // still acts
        Assert.Empty(osd);
    }

    [Fact]
    public void OsdOffAlsoSilencesTheDisconnectedNotice()
    {
        var wl = new FakeWaveLink { IsConnected = false };
        var osd = new List<(string, string?, int?)>();

        Executor(wl, osd).Execute(new HotkeyAction { Type = "mute", Mode = "toggle", ChannelId = "mic", Osd = false });

        Assert.Empty(osd);
    }

    [Fact]
    public void OsdIsOnByDefault()
    {
        var wl = new FakeWaveLink().WithChannel("mic", "Microphone");
        var osd = new List<(string, string?, int?)>();

        Executor(wl, osd).Execute(new HotkeyAction { Type = "mute", Mode = "toggle", ChannelId = "mic" });

        Assert.Single(osd);
    }

    [Fact]
    public void SystemMuteIsAMediaKey()
    {
        var media = new RecordingMediaSender();
        var exec = new HotkeyActionExecutor(() => new AppConfig(), new FakeWaveLink { IsConnected = false },
            (_, _, _) => { }, media);

        exec.Execute(new HotkeyAction { Type = "media", Mode = "volume_mute" });

        Assert.Equal(MediaKey.VolumeMute, Assert.Single(media.Sent));
    }

    private static HotkeyActionExecutor Executor(FakeWaveLink wl, List<(string, string?, int?)>? osd = null) =>
        new(() => new AppConfig(), wl, (a, b, c) => osd?.Add((a, b, c)), new RecordingMediaSender());
}
