using System.Runtime.CompilerServices;
using Wlhk.Core;
using Wlhk.WaveLink;

namespace Wlhk.Tests;

internal static class TestSetup
{
    /// <summary>Keep the suite from writing to the user's real %APPDATA%\WLHK\wlhk.log.</summary>
    [ModuleInitializer]
    internal static void RedirectLog()
    {
        string dir = Path.Combine(Path.GetTempPath(), "WLHK.Tests");
        Directory.CreateDirectory(dir);
        Log.UseFile(Path.Combine(dir, "wlhk.log"));
    }
}

/// <summary>In-memory Wave Link that records every write.</summary>
internal sealed class FakeWaveLink : IWaveLinkControl
{
    public bool IsConnected { get; set; } = true;
    public string MainOutputId { get; set; } = "";
    public List<Channel> Channels { get; } = [];
    public List<Mix> Mixes { get; } = [];
    public List<(string Id, double? Level, bool? Muted)> ChannelWrites { get; } = [];
    public List<(string ChannelId, string MixId, double? Level, bool? Muted)> PairWrites { get; } = [];
    public List<OutputDevice> Outputs { get; } = [];
    public List<InputDevice> Inputs { get; } = [];
    public FocusedApp? Focused { get; set; }
    /// <summary>Every 2.6 write, as "method arg=value ..." for easy assertions.</summary>
    public List<string> Writes { get; } = [];

    public FakeWaveLink WithChannel(string id, string name, double level = 0.5, bool muted = false,
        params ChannelMix[] mixes)
    {
        Channels.Add(new Channel { Id = id, Name = name, Level = level, IsMuted = muted, Mixes = [.. mixes] });
        return this;
    }

    public Channel? GetChannelById(string? id) => Channels.FirstOrDefault(c => c.Id == id);
    public Mix? GetMixById(string? id) => Mixes.FirstOrDefault(m => m.Id == id);
    public OutputDevice? GetOutputDeviceById(string? id) => Outputs.FirstOrDefault(d => d.Id == id);
    public InputDevice? GetInputDeviceById(string? id) => Inputs.FirstOrDefault(d => d.Id == id);
    public IReadOnlyList<Channel> GetChannels() => Channels;
    public IReadOnlyList<InputDevice> GetInputDevices() => Inputs;
    public FocusedApp? FocusedApp => Focused;

    public void SetMix(string mixId, double? level = null, bool? isMuted = null)
    {
        Writes.Add($"setMix {mixId} level={level} muted={isMuted}");
        var mix = GetMixById(mixId);
        if (mix is null) return;
        if (level is double l) mix.Level = l;
        if (isMuted is bool m) mix.IsMuted = m;
    }

    public void SetOutput(string deviceId, string outputId, double? level = null, bool? isMuted = null)
    {
        Writes.Add($"setOutput {deviceId}/{outputId} level={level} muted={isMuted}");
        var port = GetOutputDeviceById(deviceId)?.Outputs.FirstOrDefault(o => o.Id == outputId);
        if (port is null) return;
        if (level is double l) port.Level = l;
        if (isMuted is bool m) port.IsMuted = m;
    }

    public void SetInput(string deviceId, string inputId, bool? isMuted = null, double? gain = null,
        double? micPcMix = null, bool? gainLock = null)
    {
        Writes.Add($"setInput {deviceId}/{inputId} muted={isMuted} gain={gain} mix={micPcMix} lock={gainLock}");
        var port = GetInputDeviceById(deviceId)?.Inputs.FirstOrDefault(i => i.Id == inputId);
        if (port is null) return;
        if (isMuted is bool m) port.IsMuted = m;
        if (gain is double g) port.Gain = g;
        if (micPcMix is double x) port.MicPcMix = x;
        if (gainLock is bool k) port.IsGainLockOn = k;
    }

    public void SetChannelEffect(string channelId, string effectId, bool isEnabled)
    {
        Writes.Add($"setEffect {channelId}/{effectId} on={isEnabled}");
        var fx = GetChannelById(channelId)?.Effects.FirstOrDefault(e => e.Id == effectId);
        if (fx is not null) fx.IsEnabled = isEnabled;
    }

    public void AddToChannel(string appId, string channelId) => Writes.Add($"addToChannel {appId} -> {channelId}");

    public void SetChannel(string id, double? level = null, bool? isMuted = null)
    {
        lock (ChannelWrites) ChannelWrites.Add((id, level, isMuted));
        var channel = GetChannelById(id);
        if (channel is null) return;
        if (level is double l) channel.Level = l;
        if (isMuted is bool m) channel.IsMuted = m;
    }

    public void SetChannelMix(string channelId, string mixId, double? level = null, bool? isMuted = null)
    {
        lock (PairWrites) PairWrites.Add((channelId, mixId, level, isMuted));
        var mix = GetChannelById(channelId)?.Mixes.FirstOrDefault(m => m.Id == mixId);
        if (mix is null) return;
        if (level is double l) mix.Level = l;
        if (isMuted is bool m) mix.IsMuted = m;
    }

    public void SetMainOutput(string outputDeviceId) => MainOutputId = outputDeviceId;
    public void SetInputMute(string deviceId, string inputId, bool isMuted) { }

    public int ChannelWriteCount
    {
        get { lock (ChannelWrites) return ChannelWrites.Count; }
    }
}

/// <summary>App volume double: records requests and answers with a canned result.</summary>
internal sealed class FakeAppAudio(AppVolumeResult reply) : IAppAudio
{
    public List<(string Mode, int Step, int Value)> Requests { get; } = [];

    /// <summary>Which Windows target each request went to: "app" or "system".</summary>
    public List<string> Scopes { get; } = [];

    public void ApplyToFocusedApp(string mode, int step, int value, Action<AppVolumeResult> done)
    {
        Requests.Add((mode, step, value));
        Scopes.Add("app");
        done(reply);
    }

    public void ApplyToSystem(string mode, int step, int value, Action<AppVolumeResult> done)
    {
        Requests.Add((mode, step, value));
        Scopes.Add("system");
        done(reply);
    }
}

internal sealed class RecordingMediaSender : IMediaKeySender
{
    public List<MediaKey> Sent { get; } = [];
    public void Send(MediaKey key) => Sent.Add(key);
}

internal static class Targets
{
    public static TargetState Of(params TargetEntry[] entries)
    {
        var state = new TargetState();
        state.Update(entries);
        return state;
    }

    public static TargetEntry Entry(string id, string channelId, string? mixId = null, string? modifier = null) =>
        new() { Id = id, ChannelId = channelId, MixId = mixId, Modifier = modifier };
}
