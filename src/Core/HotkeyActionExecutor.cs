using Wlhk.WaveLink;
using static Wlhk.Core.ActionCatalog;

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
    private readonly IAppAudio _appAudio;

    /// <summary>
    /// Last value sent per non-channel level (mix, output, gain, Mic/PC mix).
    /// Channels have full echo protection in the client; these don't, so a fast
    /// repeat could read back a stale echo and step from it. For a short window
    /// after a send, steps build on the value we sent instead.
    /// </summary>
    private readonly Dictionary<string, (double Value, long At)> _recent = new();
    private const int RecentWindowMs = 1500;

    public HotkeyActionExecutor(
        Func<AppConfig> getConfig,
        IWaveLinkControl wl,
        Action<string, string?, int?> showOsd,
        IMediaKeySender? media = null,
        TargetState? targets = null,
        IAppAudio? appAudio = null)
    {
        _getConfig = getConfig;
        _wl = wl;
        _showOsd = showOsd;
        _media = media ?? new MediaKeySender();
        _targets = targets ?? new TargetState();
        _appAudio = appAudio ?? AppAudio.Default;
    }

    public void Execute(HotkeyAction? action, ActionContext context = default)
    {
        if (action is null) return;
        var (kind, mode) = Resolve(action);
        var osd = new Osd(action.Osd != false, _showOsd);

        // These don't need Wave Link: media keys and app volume go to Windows,
        // and the target list is local state. Run them before the connection check.
        switch (kind)
        {
            case Media: ExecuteMedia(mode, osd); return;
            case Target: ExecuteTarget(action, mode, context, osd); return;
            case AppVolume: ExecuteAppVolume(action, mode, osd); return;
        }

        if (!_wl.IsConnected)
        {
            osd.Show("Wave Link Disconnected", "Failed", null);
            return;
        }

        switch (kind)
        {
            case ActionCatalog.Mute: ExecuteMute(action, mode, context, osd); break;
            case ActionCatalog.Volume: ExecuteVolume(action, mode, context, osd); break;
            case Microphone: ExecuteMicrophone(action, mode, osd); break;
            case Effect: ExecuteEffect(action, mode, context, osd); break;
            case RouteApp: ExecuteRouteApp(action, osd); break;
            case SwitchOutput: ExecuteSwitchOutput(action, osd); break;
            case CycleOutput: ExecuteCycleOutput(action, osd); break;
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

    // ─── Targets ────────────────────────────────────────────────────────────────

    /// <summary>A resolved target: always concrete, never "active" or "focused_app".</summary>
    private readonly record struct Aim(string Scope, string? ChannelId, string? MixId, string? DeviceId);

    /// <summary>
    /// What this action aims at right now. Follows the Active Target and the
    /// focused app's channel to something concrete. Null (with an OSD saying why)
    /// when there is nothing to aim at.
    /// </summary>
    private Aim? ResolveAim(HotkeyAction action, ActionContext context, Osd osd)
    {
        string scope = ScopeOf(action);
        string? channelId = action.ChannelId, mixId = action.MixId, deviceId = action.DeviceId;

        if (scope == ScopeActive)
        {
            var entry = _targets.Current(context.TargetOverrideId);
            if (entry is null)
            {
                osd.Show("No Active Target", "Add one under Global Settings", null);
                return null;
            }
            scope = ScopeOf(entry);
            channelId = entry.ChannelId;
            mixId = entry.MixId;
            deviceId = entry.DeviceId;
        }

        if (scope == ScopeFocusedApp)
        {
            var app = _wl.FocusedApp;
            if (app?.ChannelId is null)
            {
                osd.Show("Focused App", app is null ? "No focused app" : $"{app.Name} isn't in a channel", null);
                return null;
            }
            return new Aim(ScopeChannel, app.ChannelId, null, null);
        }

        return new Aim(scope, channelId, mixId, deviceId);
    }

    /// <summary>"Browser · All Mixes", "Mix: Stream Mix", ... tolerant of things that have gone away.</summary>
    public string DescribeTarget(TargetEntry entry) => ScopeOf(entry) switch
    {
        ScopeFocusedApp => "Focused App's Channel",
        ScopeMix => $"Mix: {_wl.GetMixById(entry.MixId)?.Name ?? entry.MixId ?? "(none)"}",
        ScopeOutput => $"Output: {_wl.GetOutputDeviceById(entry.DeviceId)?.Name ?? entry.DeviceId ?? "(none)"}",
        _ => $"{_wl.GetChannelById(entry.ChannelId)?.Name ?? entry.ChannelId ?? "(none)"} · " +
             (entry.MixId is null ? "All Mixes" : MixName(entry.MixId))
    };

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

    // ─── Mute / Volume ──────────────────────────────────────────────────────────

    private void ExecuteMute(HotkeyAction action, string mode, ActionContext context, Osd osd)
    {
        var aim = ResolveAim(action, context, osd);
        if (aim is null) return;

        // "mute" and "unmute" set an absolute state and are sent even if it
        // already matches, so other tools watching Wave Link can rely on them as
        // on/off signals rather than having to track toggles.
        bool Next(bool current) => mode switch
        {
            "mute" => true,
            "unmute" => false,
            _ => !current
        };
        static string Word(bool muted) => muted ? "Muted" : "Unmuted";

        switch (aim.Value.Scope)
        {
            case ScopeMix:
            {
                var mix = _wl.GetMixById(aim.Value.MixId);
                if (mix is null) return;
                bool muted = Next(mix.IsMuted);
                _wl.SetMix(mix.Id, isMuted: muted);
                osd.Show($"Mix: {mix.Name}", Word(muted), null);
                return;
            }
            case ScopeOutput:
            {
                var output = ResolveOutput(aim.Value.DeviceId);
                if (output is null) return;
                var (device, port) = output.Value;
                bool muted = Next(port.IsMuted);
                _wl.SetOutput(device.Id, port.Id, isMuted: muted);
                osd.Show($"Output: {device.Name}", Word(muted), null);
                return;
            }
        }

        string? channelId = aim.Value.ChannelId, mixId = aim.Value.MixId;
        if (mixId is not null)
        {
            var pair = ResolvePair(channelId, mixId);
            if (pair is null) return;

            bool newMute = Next(pair.Value.Mix.IsMuted);
            _wl.SetChannelMix(pair.Value.Channel.Id, pair.Value.Mix.Id, isMuted: newMute);
            osd.Show(PairTitle(pair.Value.Channel, pair.Value.Mix.Id), Word(newMute), null);
            return;
        }

        var channel = _wl.GetChannelById(channelId);
        if (channel is not null)
        {
            bool newMute = Next(channel.IsMuted);
            _wl.SetChannel(channel.Id, isMuted: newMute);
            osd.Show($"Channel: {channel.Name}", Word(newMute), null);
            return;
        }

        // v1 fallback: the id may refer to an input device rather than a channel.
        var inputDevice = _wl.GetInputDeviceById(channelId);
        if (inputDevice is { Inputs.Count: > 0 })
        {
            bool newMute = Next(inputDevice.Inputs[0].IsMuted);
            _wl.SetInputMute(inputDevice.Id, inputDevice.Inputs[0].Id, newMute);
            osd.Show($"Input: {inputDevice.Name}", Word(newMute), null);
        }
    }

    private void ExecuteVolume(HotkeyAction action, string mode, ActionContext context, Osd osd)
    {
        var aim = ResolveAim(action, context, osd);
        if (aim is null) return;

        int Apply(double currentLevel) => mode switch
        {
            "set" => Math.Clamp(action.Value ?? 50, 0, 100),
            "down" => Math.Clamp((int)Math.Round(currentLevel * 100) - ResolveStep(action), 0, 100),
            _ => Math.Clamp((int)Math.Round(currentLevel * 100) + ResolveStep(action), 0, 100)
        };

        switch (aim.Value.Scope)
        {
            case ScopeMix:
            {
                var mix = _wl.GetMixById(aim.Value.MixId);
                if (mix is null) return;
                string key = "mix:" + mix.Id;
                int level = Apply(Base(key, mix.Level));
                Remember(key, level / 100.0);
                _wl.SetMix(mix.Id, level: level / 100.0);
                osd.Show($"Mix Volume: {mix.Name}", null, level);
                return;
            }
            case ScopeOutput:
            {
                var output = ResolveOutput(aim.Value.DeviceId);
                if (output is null) return;
                var (device, port) = output.Value;
                string key = "out:" + device.Id + "/" + port.Id;
                int level = Apply(Base(key, port.Level));
                Remember(key, level / 100.0);
                _wl.SetOutput(device.Id, port.Id, level: level / 100.0);
                osd.Show($"Output Volume: {device.Name}", null, level);
                return;
            }
        }

        string? channelId = aim.Value.ChannelId, mixId = aim.Value.MixId;
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

    // ─── Microphone ─────────────────────────────────────────────────────────────

    private void ExecuteMicrophone(HotkeyAction action, string mode, Osd osd)
    {
        var device = _wl.GetInputDeviceById(action.DeviceId) ?? _wl.GetInputDevices().FirstOrDefault();
        var port = device?.Inputs.FirstOrDefault();
        if (device is null || port is null)
        {
            osd.Show("Microphone", "No input device", null);
            return;
        }

        void Unsupported(string what) => osd.Show($"Mic: {device.Name}", $"No {what} on this device", null);

        switch (mode)
        {
            case "mute_toggle" or "mute" or "unmute":
            {
                bool muted = mode switch { "mute" => true, "unmute" => false, _ => !port.IsMuted };
                _wl.SetInput(device.Id, port.Id, isMuted: muted);
                osd.Show($"Mic: {device.Name}", muted ? "Muted" : "Unmuted", null);
                return;
            }

            case "gain_up" or "gain_down" or "gain_set":
            {
                if (port.Gain is not double gain) { Unsupported("gain control"); return; }
                string key = "gain:" + device.Id + "/" + port.Id;
                double currentDb = WaveLinkProtocol.GainToDb(port, Base(key, gain));
                // Steps and levels are in whole dB, which is what Wave Link displays.
                double db = mode switch
                {
                    "gain_set" => action.Value ?? 0,
                    "gain_down" => Math.Round(currentDb) - Math.Clamp(action.Step ?? 1, 1, 20),
                    _ => Math.Round(currentDb) + Math.Clamp(action.Step ?? 1, 1, 20)
                };
                db = Math.Clamp(db, port.GainMinDb, port.GainMaxDb);
                double value = WaveLinkProtocol.DbToGain(port, db);
                Remember(key, value);
                _wl.SetInput(device.Id, port.Id, gain: value);
                osd.Show($"Mic Gain: {device.Name} · {Math.Round(db)} dB", null, (int)Math.Round(value * 100));
                return;
            }

            case "mix_up" or "mix_down" or "mix_set":
            {
                if (port.MicPcMix is not double mix) { Unsupported("Mic/PC mix"); return; }
                string key = "micpc:" + device.Id + "/" + port.Id;
                int current = (int)Math.Round(Base(key, mix) * 100);
                int percent = Math.Clamp(mode switch
                {
                    "mix_set" => action.Value ?? 50,
                    "mix_down" => current - ResolveStep(action),
                    _ => current + ResolveStep(action)
                }, 0, 100);
                Remember(key, percent / 100.0);
                _wl.SetInput(device.Id, port.Id, micPcMix: percent / 100.0);
                osd.Show($"Mic/PC Mix: {device.Name}", null, percent);
                return;
            }

            case "lock_toggle":
            {
                if (port.IsGainLockOn is not bool locked) { Unsupported("gain lock"); return; }
                _wl.SetInput(device.Id, port.Id, gainLock: !locked);
                osd.Show($"Gain Lock: {device.Name}", !locked ? "On" : "Off", null);
                return;
            }
        }
    }

    // ─── Effects ────────────────────────────────────────────────────────────────

    private void ExecuteEffect(HotkeyAction action, string mode, ActionContext context, Osd osd)
    {
        var aim = ResolveAim(action, context, osd);
        if (aim is null) return;
        if (aim.Value.Scope != ScopeChannel)
        {
            osd.Show("Effect", "Effects need a channel target", null);
            return;
        }

        var channel = _wl.GetChannelById(aim.Value.ChannelId);
        if (channel is null) return;

        // Effect ids are per channel, so the name is what lets one Effect action
        // follow the Active Target or the focused app across channels.
        var effect = channel.Effects.FirstOrDefault(e => e.Id == action.EffectId)
                     ?? channel.Effects.FirstOrDefault(e =>
                         string.Equals(e.Name, action.EffectName, StringComparison.OrdinalIgnoreCase));
        if (effect is null)
        {
            osd.Show($"Effect: {channel.Name}", $"No {action.EffectName ?? "effect"} on this channel", null);
            return;
        }

        bool on = mode switch { "enable" => true, "disable" => false, _ => !effect.IsEnabled };
        _wl.SetChannelEffect(channel.Id, effect.Id, on);
        osd.Show($"{effect.Name}: {channel.Name}", on ? "On" : "Off", null);
    }

    // ─── Apps ───────────────────────────────────────────────────────────────────

    private void ExecuteRouteApp(HotkeyAction action, Osd osd)
    {
        var app = _wl.FocusedApp;
        if (app is null)
        {
            osd.Show("Move Focused App", "No focused app", null);
            return;
        }
        var channel = _wl.GetChannelById(action.ChannelId);
        if (channel is null) return;
        _wl.AddToChannel(app.Id, channel.Id);
        osd.Show(app.Name, $"Moved to {channel.Name}", null);
    }

    private void ExecuteAppVolume(HotkeyAction action, string mode, Osd osd)
    {
        int step = ResolveStep(action);
        int value = Math.Clamp(action.Value ?? 50, 0, 100);
        _appAudio.ApplyToFocusedApp(mode, step, value, result =>
        {
            if (result.Problem is not null)
                osd.Show(result.AppName, result.Problem, null);
            else if (result.Muted is bool muted)
                osd.Show($"App: {result.AppName}", muted ? "Muted" : "Unmuted", null);
            else
                osd.Show($"App Volume: {result.AppName}", null, result.Percent);
        });
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

    // ─── Outputs ────────────────────────────────────────────────────────────────

    private void ExecuteSwitchOutput(HotkeyAction action, Osd osd)
    {
        var target = _wl.GetOutputDeviceById(action.DeviceId);
        if (target is null) return;
        _wl.SetMainOutput(target.Id);
        osd.Show("Output Device:", target.Name, null);
    }

    private void ExecuteCycleOutput(HotkeyAction action, Osd osd)
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

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private (OutputDevice Device, OutputPort Port)? ResolveOutput(string? deviceId)
    {
        var device = _wl.GetOutputDeviceById(deviceId);
        var port = device?.Outputs.FirstOrDefault();
        return device is null || port is null ? null : (device, port);
    }

    private (Channel Channel, ChannelMix Mix)? ResolvePair(string? channelId, string mixId)
    {
        var channel = _wl.GetChannelById(channelId);
        var mix = channel?.Mixes.FirstOrDefault(m => m.Id == mixId);
        return channel is null || mix is null ? null : (channel, mix);
    }

    private double Base(string key, double current)
    {
        lock (_recent)
            return _recent.TryGetValue(key, out var r) && Environment.TickCount64 - r.At < RecentWindowMs
                ? r.Value
                : current;
    }

    private void Remember(string key, double value)
    {
        lock (_recent) _recent[key] = (value, Environment.TickCount64);
    }

    private string PairTitle(Channel channel, string mixId) =>
        $"Channel: {channel.Name} · {MixName(mixId)}";

    private string MixName(string mixId) => _wl.GetMixById(mixId)?.Name ?? mixId;
}
