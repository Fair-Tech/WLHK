using Wlhk.Core;
using Wlhk.WaveLink;
using Xunit;

namespace Wlhk.Tests;

public sealed class HotkeyActionExecutorTests
{
    [Fact]
    public void ToggleMuteChangesOnlySelectedPair()
    {
        var fake = Fake.WithChannel(new Channel
        {
            Id = "mic", Name = "Microphone",
            Mixes =
            [
                new ChannelMix { Id = "vc", IsMuted = false },
                new ChannelMix { Id = "stream", IsMuted = false }
            ]
        }, new Mix { Id = "vc", Name = "VC" }, new Mix { Id = "stream", Name = "Stream" });
        var osd = new List<(string, string?, int?)>();

        Create(fake, osd).Execute(new HotkeyAction
        {
            Type = "mute_channel", ChannelId = "mic", MixId = "vc"
        });

        Assert.Equal(("mic", "vc", (double?)null, (bool?)true),
            Assert.Single(fake.PairWrites));
        Assert.False(fake.GetChannelById("mic")!.Mixes[1].IsMuted);
        Assert.Equal(("Channel: Microphone · VC", "Muted", (int?)null),
            Assert.Single(osd));
    }

    [Theory]
    [InlineData("volume_up_channel", 10, null, 0.50)]
    [InlineData("volume_down_channel", 10, null, 0.30)]
    [InlineData("set_volume", 10, 65, 0.65)]
    public void VolumeActionsUseSelectedPair(
        string type, int step, int? value, double expected)
    {
        var fake = Fake.WithChannel(new Channel
        {
            Id = "mic", Mixes = [new ChannelMix { Id = "vc", Level = 0.40 }]
        }, new Mix { Id = "vc", Name = "VC" });

        Create(fake, []).Execute(new HotkeyAction
        {
            Type = type, ChannelId = "mic", MixId = "vc", Step = step, Value = value
        });

        Assert.Equal(expected, Assert.Single(fake.PairWrites).Level!.Value, 6);
        Assert.Empty(fake.ChannelWrites);
    }

    [Fact]
    public void NullMixIdKeepsChannelWideBehavior()
    {
        var fake = Fake.WithChannel(new Channel { Id = "music", Level = 0.60 });

        Create(fake, []).Execute(new HotkeyAction
        {
            Type = "volume_up_channel", ChannelId = "music", Step = 5
        });

        Assert.Equal(0.65, Assert.Single(fake.ChannelWrites).Level!.Value, 6);
        Assert.Empty(fake.PairWrites);
    }

    [Fact]
    public void SameChannelMixesUseIndependentLevels()
    {
        var fake = Fake.WithChannel(new Channel
        {
            Id = "music",
            Mixes =
            [
                new ChannelMix { Id = "vc", Level = 0.20 },
                new ChannelMix { Id = "stream", Level = 0.80 }
            ]
        }, new Mix { Id = "vc", Name = "VC" }, new Mix { Id = "stream", Name = "Stream" });
        var executor = Create(fake, []);

        executor.Execute(new HotkeyAction
        {
            Type = "volume_up_channel", ChannelId = "music", MixId = "vc", Step = 10
        });
        executor.Execute(new HotkeyAction
        {
            Type = "volume_down_channel", ChannelId = "music", MixId = "stream", Step = 10
        });

        Assert.Equal(0.30, fake.PairWrites[0].Level!.Value, 6);
        Assert.Equal(0.70, fake.PairWrites[1].Level!.Value, 6);
    }

    [Fact]
    public void MissingMixDoesNothing()
    {
        var fake = Fake.WithChannel(new Channel { Id = "mic" });

        Create(fake, []).Execute(new HotkeyAction
        {
            Type = "mute_channel", ChannelId = "mic", MixId = "removed"
        });

        Assert.Empty(fake.ChannelWrites);
        Assert.Empty(fake.PairWrites);
        Assert.Empty(fake.InputWrites);
    }

    [Fact]
    public void InputFallbackRunsOnlyWithoutMixId()
    {
        var fake = new Fake();
        fake.Inputs.Add(new InputDevice
        {
            Id = "legacy", Name = "Legacy",
            Inputs = [new InputPort { Id = "port", IsMuted = false }]
        });
        var executor = Create(fake, []);

        executor.Execute(new HotkeyAction { Type = "mute_channel", ChannelId = "legacy" });
        executor.Execute(new HotkeyAction
        {
            Type = "mute_channel", ChannelId = "legacy", MixId = "vc"
        });

        Assert.Equal(("legacy", "port", true), Assert.Single(fake.InputWrites));
        Assert.Empty(fake.PairWrites);
    }

    [Fact]
    public void DisconnectedExecutionOnlyShowsFailureOsd()
    {
        var fake = new Fake { IsConnected = false };
        var osd = new List<(string, string?, int?)>();

        Create(fake, osd).Execute(new HotkeyAction
        {
            Type = "mute_channel", ChannelId = "mic", MixId = "vc"
        });

        Assert.Equal(("Wave Link Disconnected", "Failed", (int?)null), Assert.Single(osd));
        Assert.Empty(fake.PairWrites);
    }

    private static HotkeyActionExecutor Create(
        Fake fake, List<(string, string?, int?)> osd) =>
        new(() => new AppConfig(), fake, (a, b, c) => osd.Add((a, b, c)));

    private sealed class Fake : IWaveLinkControl
    {
        public bool IsConnected { get; set; } = true;
        public string MainOutputId { get; set; } = "";
        public List<Channel> Channels { get; } = [];
        public List<Mix> Mixes { get; } = [];
        public List<OutputDevice> Outputs { get; } = [];
        public List<InputDevice> Inputs { get; } = [];
        public List<(string Id, double? Level, bool? Muted)> ChannelWrites { get; } = [];
        public List<(string ChannelId, string MixId, double? Level, bool? Muted)> PairWrites { get; } = [];
        public List<(string DeviceId, string InputId, bool Muted)> InputWrites { get; } = [];

        public static Fake WithChannel(Channel channel, params Mix[] mixes)
        {
            var fake = new Fake();
            fake.Channels.Add(channel);
            fake.Mixes.AddRange(mixes);
            return fake;
        }

        public Channel? GetChannelById(string? id) => Channels.FirstOrDefault(c => c.Id == id);
        public Mix? GetMixById(string? id) => Mixes.FirstOrDefault(m => m.Id == id);
        public OutputDevice? GetOutputDeviceById(string? id) => Outputs.FirstOrDefault(d => d.Id == id);
        public InputDevice? GetInputDeviceById(string? id) => Inputs.FirstOrDefault(d => d.Id == id);

        public void SetChannel(string id, double? level = null, bool? isMuted = null)
        {
            ChannelWrites.Add((id, level, isMuted));
            var channel = GetChannelById(id);
            if (channel is null) return;
            if (level is double l) channel.Level = l;
            if (isMuted is bool m) channel.IsMuted = m;
        }

        public void SetChannelMix(
            string channelId, string mixId, double? level = null, bool? isMuted = null)
        {
            PairWrites.Add((channelId, mixId, level, isMuted));
            var mix = GetChannelById(channelId)?.Mixes.FirstOrDefault(m => m.Id == mixId);
            if (mix is null) return;
            if (level is double l) mix.Level = l;
            if (isMuted is bool m) mix.IsMuted = m;
        }

        public void SetMainOutput(string outputDeviceId) => MainOutputId = outputDeviceId;

        public void SetInputMute(string deviceId, string inputId, bool isMuted)
        {
            InputWrites.Add((deviceId, inputId, isMuted));
            var input = GetInputDeviceById(deviceId)?.Inputs.FirstOrDefault(i => i.Id == inputId);
            if (input is not null) input.IsMuted = isMuted;
        }
    }
}
