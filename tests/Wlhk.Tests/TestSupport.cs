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

    public FakeWaveLink WithChannel(string id, string name, double level = 0.5, bool muted = false,
        params ChannelMix[] mixes)
    {
        Channels.Add(new Channel { Id = id, Name = name, Level = level, IsMuted = muted, Mixes = [.. mixes] });
        return this;
    }

    public Channel? GetChannelById(string? id) => Channels.FirstOrDefault(c => c.Id == id);
    public Mix? GetMixById(string? id) => Mixes.FirstOrDefault(m => m.Id == id);
    public OutputDevice? GetOutputDeviceById(string? id) => null;
    public InputDevice? GetInputDeviceById(string? id) => null;

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
