using Wlhk.Core;
using Wlhk.WaveLink;
using Xunit;

namespace Wlhk.Tests;

/// <summary>
/// Windows media actions (v2.6). The key sender is faked so the test run doesn't
/// pause the machine's music or move its volume.
/// </summary>
public sealed class MediaActionTests
{
    [Theory]
    [InlineData("media_play_pause", MediaKey.PlayPause, "Play / Pause")]
    [InlineData("media_next", MediaKey.NextTrack, "Next Track")]
    [InlineData("media_previous", MediaKey.PreviousTrack, "Previous Track")]
    [InlineData("media_stop", MediaKey.Stop, "Stop")]
    public void TransportActionsSendTheKeyAndShowOsd(
        string type, MediaKey expected, string osdValue)
    {
        var media = new FakeSender();
        var osd = new List<(string, string?, int?)>();

        Create(media, osd, connected: true).Execute(new HotkeyAction { Type = type });

        Assert.Equal(expected, Assert.Single(media.Sent));
        Assert.Equal(("Media", osdValue, (int?)null), Assert.Single(osd));
    }

    [Theory]
    [InlineData("media_volume_up", MediaKey.VolumeUp)]
    [InlineData("media_volume_down", MediaKey.VolumeDown)]
    public void VolumeActionsSendTheKeyWithoutOurOwnOsd(string type, MediaKey expected)
    {
        var media = new FakeSender();
        var osd = new List<(string, string?, int?)>();

        Create(media, osd, connected: true).Execute(new HotkeyAction { Type = type });

        Assert.Equal(expected, Assert.Single(media.Sent));
        // Windows draws its own volume bar; a second overlay would be noise.
        Assert.Empty(osd);
    }

    [Theory]
    [InlineData("media_play_pause")]
    [InlineData("media_volume_up")]
    public void MediaActionsWorkWhileWaveLinkIsDisconnected(string type)
    {
        var media = new FakeSender();
        var osd = new List<(string, string?, int?)>();

        Create(media, osd, connected: false).Execute(new HotkeyAction { Type = type });

        Assert.Single(media.Sent);
        Assert.DoesNotContain(osd, entry => entry.Item1 == "Wave Link Disconnected");
    }

    [Fact]
    public void WaveLinkActionsStillRequireAConnection()
    {
        var media = new FakeSender();
        var osd = new List<(string, string?, int?)>();

        Create(media, osd, connected: false).Execute(new HotkeyAction
        {
            Type = "mute_channel", ChannelId = "mic"
        });

        Assert.Empty(media.Sent);
        Assert.Equal(("Wave Link Disconnected", "Failed", (int?)null), Assert.Single(osd));
    }

    [Fact]
    public void UnknownActionTypeDoesNothing()
    {
        var media = new FakeSender();
        var osd = new List<(string, string?, int?)>();

        Create(media, osd, connected: true).Execute(new HotkeyAction { Type = "media_teleport" });

        Assert.Empty(media.Sent);
        Assert.Empty(osd);
    }

    private static HotkeyActionExecutor Create(
        IMediaKeySender media, List<(string, string?, int?)> osd, bool connected) =>
        new(() => new AppConfig(), new StubControl { IsConnected = connected },
            (a, b, c) => osd.Add((a, b, c)), media);

    private sealed class FakeSender : IMediaKeySender
    {
        public List<MediaKey> Sent { get; } = [];
        public void Send(MediaKey key) => Sent.Add(key);
    }

    private sealed class StubControl : IWaveLinkControl
    {
        public bool IsConnected { get; set; }
        public string MainOutputId => "";
        public Channel? GetChannelById(string? id) => null;
        public Mix? GetMixById(string? id) => null;
        public OutputDevice? GetOutputDeviceById(string? id) => null;
        public InputDevice? GetInputDeviceById(string? id) => null;
        public void SetChannel(string id, double? level = null, bool? isMuted = null) { }
        public void SetChannelMix(string channelId, string mixId, double? level = null, bool? isMuted = null) { }
        public void SetMainOutput(string outputDeviceId) { }
        public void SetInputMute(string deviceId, string inputId, bool isMuted) { }
    }
}
