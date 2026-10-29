using System.Text.Json.Nodes;
using Wlhk.Core;
using Wlhk.WaveLink;
using Xunit;
using static Wlhk.Tests.Targets;

namespace Wlhk.Tests;

/// <summary>
/// The Wave Link capabilities added in 2.6: mix masters, output devices, mic gain /
/// Mic-PC mix / gain lock, channel effects, the focused app, and app routing.
/// Payload shapes asserted here are the ones verified against Wave Link 3.2.9.
/// </summary>
public sealed class WaveLinkCapabilityTests
{
    // ─── Parsing ────────────────────────────────────────────────────────────────

    private static readonly string WaveGainTable =
        string.Join(",", Enumerable.Range(0, 41).Select(i => $"[{i * 0.025},{i}]"));

    [Fact]
    public void ParsesMicGainMonitorMixAndGainLock()
    {
        var devices = WaveLinkProtocol.ParseInputDevices(JsonNode.Parse($$$"""
            [{"id":"wave","name":"Wave Mic","inputs":[{"id":"in","name":"Wave Mic","isMuted":true,
              "gain":{"value":0.25,"min":0,"max":40,"lookUpTable":[{{{WaveGainTable}}}]},
              "isGainLockOn":false,"micPcMix":{"value":0.8,"isInverted":false}}]}]
            """));

        var port = Assert.Single(Assert.Single(devices).Inputs);
        Assert.True(port.IsMuted);
        Assert.Equal(0.25, port.Gain);
        Assert.Equal((0.0, 40.0), (port.GainMinDb, port.GainMaxDb));
        Assert.Equal(41, port.GainTable.Count);
        Assert.Equal(0.8, port.MicPcMix);
        Assert.False(port.IsGainLockOn);
    }

    [Fact]
    public void MicWithoutWaveFeaturesReportsThemMissing()
    {
        var port = Assert.Single(Assert.Single(WaveLinkProtocol.ParseInputDevices(JsonNode.Parse(
            """[{"id":"usb","name":"USB Mic","inputs":[{"id":"in","isMuted":false}]}]"""))).Inputs);

        Assert.Null(port.Gain);
        Assert.Null(port.MicPcMix);
        Assert.Null(port.IsGainLockOn);
    }

    [Fact]
    public void ParsesOutputPortsEffectsAppsAndFocusedApp()
    {
        var output = Assert.Single(WaveLinkProtocol.ParseOutputDevices(JsonNode.Parse(
            """[{"id":"wave","name":"Wave Mic","outputs":[{"id":"hp","name":"Wave Mic","isMuted":false,"level":0.6565,"mixId":"personal"}]}]""")));
        Assert.Equal(0.6565, Assert.Single(output.Outputs).Level);

        var channel = Assert.Single(WaveLinkProtocol.ParseChannels(JsonNode.Parse(
            """[{"id":"mic","name":"Wave Mic","effects":[{"id":"g","name":"Basic Noise Gate","isEnabled":true}],"apps":[{"id":"Spotify","name":"Spotify"}]}]""")));
        Assert.Equal("Basic Noise Gate", Assert.Single(channel.Effects).Name);
        Assert.Equal("Spotify", Assert.Single(channel.Apps).Id);

        var app = WaveLinkProtocol.ParseFocusedApp(JsonNode.Parse(
            """{"id":"Spotify","name":"Spotify","channel":{"id":"music","name":"Music"}}"""));
        Assert.Equal(("Spotify", "music"), (app!.Id, app.ChannelId));
    }

    [Fact]
    public void PatchesUpdateOnlyWhatTheyCarry()
    {
        var channel = new Channel { Id = "mic", Effects = [new ChannelEffect { Id = "g", Name = "Gate", IsEnabled = true }] };
        WaveLinkProtocol.ApplyChannelPatch(channel, JsonNode.Parse("""{"id":"mic","effects":[{"id":"g","isEnabled":false}]}""")!);
        Assert.False(channel.Effects[0].IsEnabled);
        Assert.Equal("Gate", channel.Effects[0].Name);

        var port = new InputPort { Id = "in", Gain = 0.25, MicPcMix = 0.5, IsGainLockOn = false };
        WaveLinkProtocol.ApplyInputPatch(port, JsonNode.Parse("""{"id":"in","gain":{"value":0.5}}""")!);
        Assert.Equal((0.5, 0.5, false), (port.Gain, port.MicPcMix, port.IsGainLockOn));
    }

    // ─── Gain dB conversion ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(0.25, 10.0)]
    [InlineData(0.2625, 10.5)]
    [InlineData(1.0, 40.0)]
    public void GainConvertsThroughTheLookupTable(double value, double db)
    {
        var port = WavePort(value);
        Assert.Equal(db, WaveLinkProtocol.GainToDb(port, value), 6);
        Assert.Equal(value, WaveLinkProtocol.DbToGain(port, db), 6);
    }

    [Fact]
    public void NonLinearTablesAreFollowed()
    {
        var port = new InputPort { GainMinDb = 0, GainMaxDb = 30, GainTable = [(0, 0), (0.5, 24), (1, 30)] };
        Assert.Equal(12, WaveLinkProtocol.GainToDb(port, 0.25), 6);
        Assert.Equal(0.75, WaveLinkProtocol.DbToGain(port, 27), 6);
    }

    [Fact]
    public void WithoutATableGainMapsLinearlyOverTheRange()
    {
        var port = new InputPort { GainMinDb = 6, GainMaxDb = 46 };
        Assert.Equal(26, WaveLinkProtocol.GainToDb(port, 0.5), 6);
        Assert.Equal(0.5, WaveLinkProtocol.DbToGain(port, 26), 6);
    }

    // ─── Client payloads (shapes verified against Wave Link 3.2.9) ─────────────

    [Fact]
    public async Task SettersSendTheVerifiedPayloadShapes()
    {
        var sent = new List<string>();
        using var client = new WaveLinkClient((method, p) =>
        {
            lock (sent) sent.Add($"{method} {p.ToJsonString()}");
            return Task.CompletedTask;
        });
        client.HandleMessage("""{"method":"channelsChanged","params":{"channels":[{"id":"mic","name":"Mic","effects":[{"id":"g","name":"Gate","isEnabled":true}]}]}}""");
        client.HandleMessage("""{"method":"mixesChanged","params":{"mixes":[{"id":"stream","name":"Stream Mix","level":1}]}}""");

        client.SetMix("stream", level: 0.5);
        client.SetOutput("wave", "hp", isMuted: true);
        client.SetInput("wave", "in", gain: 0.3);
        client.SetInput("wave", "in", micPcMix: 0.6, gainLock: true);
        client.SetChannelEffect("mic", "g", false);
        client.AddToChannel("Spotify", "music");
        await client.WaitForMutationQueueAsync();

        Assert.Equal(
        [
            """setMix {"id":"stream","level":0.5}""",
            """setOutputDevice {"id":"wave","outputs":[{"id":"hp","isMuted":true}]}""",
            """setInputDevice {"id":"wave","inputs":[{"id":"in","gain":{"value":0.3}}]}""",
            """setInputDevice {"id":"wave","inputs":[{"id":"in","micPcMix":{"value":0.6},"isGainLockOn":true}]}""",
            """setChannel {"id":"mic","effects":[{"id":"g","isEnabled":false}]}""",
            """addToChannel {"appId":"Spotify","channelId":"music"}"""
        ], sent);
        Assert.False(client.GetChannelById("mic")!.Effects[0].IsEnabled); // optimistic update
        Assert.Equal(0.5, client.GetMixById("stream")!.Level);
    }

    [Fact]
    public void FocusedAppFollowsNotifications()
    {
        using var client = new WaveLinkClient((_, _) => Task.CompletedTask);
        client.HandleMessage("""{"method":"focusedAppChanged","params":{"id":"Discord","name":"Discord","channel":{"id":"voice"}}}""");
        Assert.Equal("voice", client.FocusedApp!.ChannelId);
    }

    // ─── Executor: scopes ───────────────────────────────────────────────────────

    [Fact]
    public void MixScopeControlsTheMixMaster()
    {
        var wl = Fake();
        var exec = Executor(wl);

        exec.Execute(new HotkeyAction { Type = "mute", Mode = "mute", Scope = "mix", MixId = "stream" });
        exec.Execute(new HotkeyAction { Type = "volume", Mode = "down", Scope = "mix", MixId = "stream", Step = 25 });

        Assert.Equal(["setMix stream level= muted=True", "setMix stream level=0.75 muted="], wl.Writes);
    }

    [Fact]
    public void OutputScopeControlsTheOutputPort()
    {
        var wl = Fake();
        Executor(wl).Execute(new HotkeyAction { Type = "volume", Mode = "set", Scope = "output", DeviceId = "wave", Value = 40 });
        Assert.Equal("setOutput wave/hp level=0.4 muted=", Assert.Single(wl.Writes));
    }

    [Fact]
    public void FocusedAppScopeUsesTheAppsChannel()
    {
        var wl = Fake();
        wl.Focused = new FocusedApp { Id = "Spotify", Name = "Spotify", ChannelId = "music" };

        Executor(wl).Execute(new HotkeyAction { Type = "mute", Mode = "toggle", Scope = "focused_app" });

        Assert.Equal("music", Assert.Single(wl.ChannelWrites).Id);
    }

    [Fact]
    public void FocusedAppOutsideAnyChannelSaysSo()
    {
        var wl = Fake();
        wl.Focused = new FocusedApp { Id = "Notepad", Name = "Notepad", ChannelId = null };
        var osd = new List<(string, string?, int?)>();

        Executor(wl, osd).Execute(new HotkeyAction { Type = "mute", Mode = "toggle", Scope = "focused_app" });

        Assert.Empty(wl.ChannelWrites);
        Assert.Equal("Notepad isn't in a channel", Assert.Single(osd).Item2);
    }

    [Fact]
    public void ActiveTargetCanBeAMixOrAnOutput()
    {
        var wl = Fake();
        var state = Of(new TargetEntry { Id = "a", Scope = "mix", MixId = "stream" },
                       new TargetEntry { Id = "b", Scope = "output", DeviceId = "wave" });
        var exec = Executor(wl, state: state);
        var mute = new HotkeyAction { Type = "mute", Mode = "mute", Scope = "active" };

        exec.Execute(mute);
        state.Next();
        exec.Execute(mute);

        Assert.Equal(["setMix stream level= muted=True", "setOutput wave/hp level= muted=True"], wl.Writes);
    }

    [Fact]
    public void RepeatedStepsBuildOnTheValueSentNotAStaleEcho()
    {
        var wl = Fake();
        var exec = Executor(wl);
        var up = new HotkeyAction { Type = "volume", Mode = "up", Scope = "mix", MixId = "stream", Step = 10 };
        wl.Mixes[0].Level = 0.5;

        exec.Execute(up);            // 0.5 -> 0.6
        wl.Mixes[0].Level = 0.5;     // an echo of the old value lands late
        exec.Execute(up);            // must continue 0.6 -> 0.7, not restart at 0.5

        Assert.Equal("setMix stream level=0.7 muted=", wl.Writes[^1]);
    }

    // ─── Executor: microphone ───────────────────────────────────────────────────

    [Theory]
    [InlineData("gain_up", 2, null, 0.30)]   // 10 dB + 2 = 12 dB
    [InlineData("gain_down", 3, null, 0.175)] // 10 dB - 3 = 7 dB
    [InlineData("gain_set", null, 25, 0.625)] // 25 dB
    public void GainStepsInWholeDecibels(string mode, int? step, int? value, double expected)
    {
        var wl = Fake();
        Executor(wl).Execute(new HotkeyAction { Type = "mic", Mode = mode, DeviceId = "wave", Step = step, Value = value });
        Assert.Equal(expected, wl.Inputs[0].Inputs[0].Gain!.Value, 6);
    }

    [Fact]
    public void GainStopsAtTheDeviceLimits()
    {
        var wl = Fake();
        wl.Inputs[0].Inputs[0].Gain = 0.975; // 39 dB
        Executor(wl).Execute(new HotkeyAction { Type = "mic", Mode = "gain_up", DeviceId = "wave", Step = 5 });
        Assert.Equal(1.0, wl.Inputs[0].Inputs[0].Gain!.Value, 6);
    }

    [Fact]
    public void MicMuteMixAndLock()
    {
        var wl = Fake();
        var exec = Executor(wl);

        exec.Execute(new HotkeyAction { Type = "mic", Mode = "mute", DeviceId = "wave" });
        exec.Execute(new HotkeyAction { Type = "mic", Mode = "mix_set", DeviceId = "wave", Value = 30 });
        exec.Execute(new HotkeyAction { Type = "mic", Mode = "lock_toggle", DeviceId = "wave" });

        var port = wl.Inputs[0].Inputs[0];
        Assert.Equal((true, 0.3, true), (port.IsMuted, port.MicPcMix, port.IsGainLockOn));
    }

    [Fact]
    public void MicWithoutAFeatureSaysSoInsteadOfGuessing()
    {
        var wl = Fake();
        wl.Inputs[0].Inputs[0].Gain = null;
        var osd = new List<(string, string?, int?)>();

        Executor(wl, osd).Execute(new HotkeyAction { Type = "mic", Mode = "gain_up", DeviceId = "wave" });

        Assert.Empty(wl.Writes);
        Assert.Equal("No gain control on this device", Assert.Single(osd).Item2);
    }

    [Fact]
    public void MicDefaultsToTheFirstInputDevice()
    {
        var wl = Fake();
        Executor(wl).Execute(new HotkeyAction { Type = "mic", Mode = "unmute" });
        Assert.Equal("setInput wave/in muted=False gain= mix= lock=", Assert.Single(wl.Writes));
    }

    // ─── Executor: effects and app routing ──────────────────────────────────────

    [Fact]
    public void EffectTogglesById()
    {
        var wl = Fake();
        Executor(wl).Execute(new HotkeyAction { Type = "effect", Mode = "toggle", ChannelId = "mic", EffectId = "gate" });
        Assert.Equal("setEffect mic/gate on=False", Assert.Single(wl.Writes));
    }

    [Fact]
    public void EffectFollowsTheActiveTargetByName()
    {
        var wl = Fake();
        wl.Channels.First(c => c.Id == "music").Effects.Add(new ChannelEffect { Id = "gate2", Name = "Noise Gate", IsEnabled = false });
        var state = Of(Entry("a", "mic"), Entry("b", "music"));
        var exec = Executor(wl, state: state);
        var enable = new HotkeyAction { Type = "effect", Mode = "enable", Scope = "active", EffectName = "noise gate" };

        exec.Execute(enable);
        state.Next();
        exec.Execute(enable);

        Assert.Equal(["setEffect mic/gate on=True", "setEffect music/gate2 on=True"], wl.Writes);
    }

    [Fact]
    public void EffectsNeedAChannel()
    {
        var wl = Fake();
        var osd = new List<(string, string?, int?)>();
        var state = Of(new TargetEntry { Id = "a", Scope = "mix", MixId = "stream" });

        Executor(wl, osd, state).Execute(new HotkeyAction { Type = "effect", Mode = "toggle", Scope = "active", EffectName = "Noise Gate" });

        Assert.Empty(wl.Writes);
        Assert.Equal("Effects need a channel target", Assert.Single(osd).Item2);
    }

    [Fact]
    public void MoveFocusedAppRoutesItThroughWaveLink()
    {
        var wl = Fake();
        wl.Focused = new FocusedApp { Id = "Spotify", Name = "Spotify", ChannelId = "music" };
        var osd = new List<(string, string?, int?)>();

        Executor(wl, osd).Execute(new HotkeyAction { Type = "route_app", ChannelId = "mic" });

        Assert.Equal("addToChannel Spotify -> mic", Assert.Single(wl.Writes));
        Assert.Equal(("Spotify", "Moved to Mic", (int?)null), Assert.Single(osd));
    }

    // ─── Executor: Windows app volume ───────────────────────────────────────────

    [Theory]
    [InlineData("up", "Spotify", null, 60)]
    [InlineData("toggle_mute", "Spotify", "Muted", null)]
    public void AppVolumeReportsWhatWindowsDid(string mode, string title, string? text, int? percent)
    {
        var reply = mode == "up"
            ? new AppVolumeResult("Spotify", 60, null, null)
            : new AppVolumeResult("Spotify", null, true, null);
        var audio = new FakeAppAudio(reply);
        var osd = new List<(string, string?, int?)>();

        // Works with Wave Link down: this is Windows' mixer, not Wave Link's.
        var exec = new HotkeyActionExecutor(() => new AppConfig(), new FakeWaveLink { IsConnected = false },
            (a, b, c) => osd.Add((a, b, c)), new RecordingMediaSender(), new TargetState(), audio);
        exec.Execute(new HotkeyAction { Type = "app_volume", Mode = mode, Step = 10 });

        Assert.Equal((mode, 10), (audio.Requests[0].Mode, audio.Requests[0].Step));
        Assert.Equal((title, text, percent), Assert.Single(osd));
    }

    [Fact]
    public void AppVolumeProblemsAreShown()
    {
        var audio = new FakeAppAudio(new AppVolumeResult("Notepad", null, null, "No audio from this app"));
        var osd = new List<(string, string?, int?)>();
        var exec = new HotkeyActionExecutor(() => new AppConfig(), new FakeWaveLink(),
            (a, b, c) => osd.Add((a, b, c)), new RecordingMediaSender(), new TargetState(), audio);

        exec.Execute(new HotkeyAction { Type = "app_volume", Mode = "up" });

        Assert.Equal(("Notepad", "No audio from this app", (int?)null), Assert.Single(osd));
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private static InputPort WavePort(double gain)
    {
        var port = new InputPort { Id = "in", Gain = gain, GainMinDb = 0, GainMaxDb = 40, MicPcMix = 0.5, IsGainLockOn = false };
        for (int i = 0; i <= 40; i++) port.GainTable.Add((i * 0.025, i));
        return port;
    }

    private static FakeWaveLink Fake()
    {
        var wl = new FakeWaveLink().WithChannel("mic", "Mic").WithChannel("music", "Music");
        wl.Channels[0].Effects.Add(new ChannelEffect { Id = "gate", Name = "Noise Gate", IsEnabled = true });
        wl.Mixes.Add(new Mix { Id = "stream", Name = "Stream Mix", Level = 1.0 });
        wl.Outputs.Add(new OutputDevice { Id = "wave", Name = "Wave Mic", Outputs = [new OutputPort { Id = "hp", Level = 0.65 }] });
        wl.Inputs.Add(new InputDevice { Id = "wave", Name = "Wave Mic", Inputs = [WavePort(0.25)] });
        return wl;
    }

    private static HotkeyActionExecutor Executor(FakeWaveLink wl, List<(string, string?, int?)>? osd = null,
        TargetState? state = null) =>
        new(() => new AppConfig(), wl, (a, b, c) => osd?.Add((a, b, c)), new RecordingMediaSender(),
            state ?? new TargetState(), new FakeAppAudio(default));
}
