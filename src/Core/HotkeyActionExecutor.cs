using Wlhk.WaveLink;

namespace Wlhk.Core;

/// <summary>
/// Per-press context: which hotkey fired, and which target a held target
/// modifier picked for this press (resolved by the hook at key-down so a delayed
/// hold or double-press still uses the target that was chosen when the key went down).
/// </summary>
public readonly record struct ActionContext(string? Key, string? TargetOverrideId);

public sealed class HotkeyActionExecutor
{
    private readonly Func<AppConfig> _getConfig;
    private readonly IWaveLinkControl _wl;
    private readonly Action<string, string?, int?> _showOsd;
    private readonly IMediaKeySender _media;
    private readonly TargetState _targets;

    public HotkeyActionExecutor(
        Func<AppConfig> getConfig,
        IWaveLinkControl wl,
        Action<string, string?, int?> showOsd,
        IMediaKeySender? media = null,
        TargetState? targets = null)
    {
        _getConfig = getConfig;
        _wl = wl;
        _showOsd = showOsd;
        _media = media ?? new MediaKeySender();
        _targets = targets ?? new TargetState();
    }

    public void Execute(HotkeyAction? action, ActionContext context = default)
    {
        if (action is null) return;
        var (kind, mode) = ActionCatalog.Resolve(action);
        var osd = new Osd(action.Osd != false, _showOsd);

        // Neither of these needs Wave Link: media keys go to Windows, and the
        // target list is local state. Run them before the connection check.
        if (kind == ActionCatalog.Media)
        {
            ExecuteMedia(mode, osd);
            return;
        }
        if (kind == ActionCatalog.Target)
        {
            ExecuteTarget(action, mode, context, osd);
            return;
        }

        if (!_wl.IsConnected)
        {
            osd.Show("Wave Link Disconnected", "Failed", null);
            return;
        }

        switch (kind)
        {
            case ActionCatalog.Mute: Mute(action, mode, context, osd); break;
            case ActionCatalog.Volume: Volume(action, mode, context, osd); break;
            case ActionCatalog.SwitchOutput: SwitchOutput(action, osd); break;
            case ActionCatalog.CycleOutput: CycleOutput(action, osd); break;
        }
    }

    /// <summary>Gate for the per-action OSD toggle.</summary>
    private readonly record struct Osd(bool Enabled, Action<string, string?, int?> Sink)
    {
        public void Show(string title, string? text, int? slider)
        {
            if (Enabled) Sink(title, text, slider);
        }
    }

    // ─── Media ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Transport keys get an OSD because Windows shows nothing for them unless
    /// the player happens to; the system volume keys deliberately don't, since
    /// Windows already draws its own volume bar and two overlays is just noise.
    /// </summary>
    private void ExecuteMedia(string mode, Osd osd)
    {
        (MediaKey Key, string? Label)? press = mode switch
        {
            "play_pause" => (MediaKey.PlayPause, "Play / Pause"),
            "next" => (MediaKey.NextTrack, "Next Track"),
            "previous" => (MediaKey.PreviousTrack, "Previous Track"),
            "stop" => (MediaKey.Stop, "Stop"),
            "volume_up" => (MediaKey.VolumeUp, null),
            "volume_down" => (MediaKey.VolumeDown, null),
            "volume_mute" => (MediaKey.VolumeMute, null),
            _ => null
        };
        if (press is null) return;
        _media.Send(press.Value.Key);
        if (press.Value.Label is not null)
            osd.Show("Media", press.Value.Label, null);
    }

    // ─── Active Target ──────────────────────────────────────────────────────────

    private void ExecuteTarget(HotkeyAction action, string mode, ActionContext context, Osd osd)
    {
        TargetEntry? entry = mode switch
        {
            "next" => _targets.Next(),
            "previous" => _targets.Previous(),
            "select" => _targets.Select(action.TargetId),
            "hold" when context.Key is not null => _targets.PushMomentary(context.Key, action.TargetId),
            "hold" => _targets.Select(action.TargetId),
            _ => null
        };

        if (entry is null)
        {
            osd.Show("Target", _targets.Entries.Count == 0 ? "No targets set up" : "Target not found", null);
            return;
        }
        osd.Show("Target", DescribeTarget(entry), null);
    }

    /// <summary>"Browser · All Mixes", tolerant of channels or mixes that have gone away.</summary>
    public string DescribeTarget(TargetEntry entry)
    {
        string channel = _wl.GetChannelById(entry.ChannelId)?.Name ?? entry.ChannelId ?? "(none)";
        string mix = entry.MixId is null ? "All Mixes" : MixName(entry.MixId);
        return $"{channel} · {mix}";
    }

    /// <summary>
    /// Channel + mix this action aims at: its own fixed target, or the Active
    /// Target. Null (with an OSD explaining why) if the Active Target is wanted
    /// but none is configured.
    /// </summary>
    private (string? ChannelId, string? MixId)? ResolveTarget(HotkeyAction action, ActionContext context, Osd osd)
    {
        if (action.UseActiveTarget != true)
            return (action.ChannelId, action.MixId);

        var entry = _targets.Current(context.TargetOverrideId);
        if (entry is null)
        {
            osd.Show("No Active Target", "Add one under Global Features", null);
            return null;
        }
        return (entry.ChannelId, entry.MixId);
    }

    // ─── Wave Link channel actions ──────────────────────────────────────────────

    private void Mute(HotkeyAction action, string mode, ActionContext context, Osd osd)
    {
        var target = ResolveTarget(action, context, osd);
        if (target is null) return;
        var (channelId, mixId) = target.Value;

        // "mute" and "unmute" set an absolute state and are sent even if it
        // already matches, so other tools watching Wave Link can rely on them as
        // on/off signals rather than having to track toggles.
        bool Next(bool current) => mode switch
        {
            "mute" => true,
            "unmute" => false,
            _ => !current
        };

        if (mixId is not null)
        {
            var pair = ResolvePair(channelId, mixId);
            if (pair is null) return;

            bool newMute = Next(pair.Value.Mix.IsMuted);
            _wl.SetChannelMix(pair.Value.Channel.Id, pair.Value.Mix.Id, isMuted: newMute);
            osd.Show(PairTitle(pair.Value.Channel, pair.Value.Mix.Id), newMute ? "Muted" : "Unmuted", null);
            return;
        }

        var channel = _wl.GetChannelById(channelId);
        if (channel is not null)
        {
            bool newMute = Next(channel.IsMuted);
            _wl.SetChannel(channel.Id, isMuted: newMute);
            osd.Show($"Channel: {channel.Name}", newMute ? "Muted" : "Unmuted", null);
            return;
        }

        // v1 fallback: the id may refer to an input device rather than a channel.
        var device = _wl.GetInputDeviceById(channelId);
        if (device is { Inputs.Count: > 0 })
        {
            bool newMute = Next(device.Inputs[0].IsMuted);
            _wl.SetInputMute(device.Id, device.Inputs[0].Id, newMute);
            osd.Show($"Input: {device.Name}", newMute ? "Muted" : "Unmuted", null);
        }
    }

    private void Volume(HotkeyAction action, string mode, ActionContext context, Osd osd)
    {
        var target = ResolveTarget(action, context, osd);
        if (target is null) return;
        var (channelId, mixId) = target.Value;

        int Apply(double currentLevel) => mode switch
        {
            "set" => Math.Clamp(action.Value ?? 50, 0, 100),
            "down" => Math.Clamp((int)Math.Round(currentLevel * 100) - ResolveStep(action), 0, 100),
            _ => Math.Clamp((int)Math.Round(currentLevel * 100) + ResolveStep(action), 0, 100)
        };

        if (mixId is not null)
        {
            var pair = ResolvePair(channelId, mixId);
            if (pair is null) return;

            int newVolume = Apply(pair.Value.Mix.Level);
            _wl.SetChannelMix(pair.Value.Channel.Id, pair.Value.Mix.Id, level: newVolume / 100.0);
            osd.Show($"Volume: {pair.Value.Channel.Name} · {MixName(pair.Value.Mix.Id)}", null, newVolume);
            return;
        }

        var channel = _wl.GetChannelById(channelId);
        if (channel is null) return;
        int newChannelVolume = Apply(channel.Level);
        _wl.SetChannel(channel.Id, level: newChannelVolume / 100.0);
        osd.Show($"Volume: {channel.Name}", null, newChannelVolume);
    }

    private int ResolveStep(HotkeyAction action)
    {
        int step = action.Step ?? _getConfig().VolumeStep;
        return Math.Clamp(step, 1, 50);
    }

    private void SwitchOutput(HotkeyAction action, Osd osd)
    {
        var target = _wl.GetOutputDeviceById(action.DeviceId);
        if (target is null) return;
        _wl.SetMainOutput(target.Id);
        osd.Show("Output Device:", target.Name, null);
    }

    private void CycleOutput(HotkeyAction action, Osd osd)
    {
        var ids = action.DeviceIds;
        if (ids is null || ids.Count == 0) return;

        int currentIndex = ids.IndexOf(_wl.MainOutputId);
        int nextIndex = (currentIndex + 1) % ids.Count;
        var target = _wl.GetOutputDeviceById(ids[nextIndex]);
        if (target is null) return;
        _wl.SetMainOutput(target.Id);
        osd.Show("Output Device:", target.Name, null);
    }

    private (Channel Channel, ChannelMix Mix)? ResolvePair(string? channelId, string mixId)
    {
        var channel = _wl.GetChannelById(channelId);
        var mix = channel?.Mixes.FirstOrDefault(m => m.Id == mixId);
        return channel is null || mix is null ? null : (channel, mix);
    }

    private string PairTitle(Channel channel, string mixId) =>
        $"Channel: {channel.Name} · {MixName(mixId)}";

    private string MixName(string mixId) => _wl.GetMixById(mixId)?.Name ?? mixId;
}
