using Wlhk.WaveLink;

namespace Wlhk.Core;

public sealed class HotkeyActionExecutor
{
    private readonly Func<AppConfig> _getConfig;
    private readonly IWaveLinkControl _wl;
    private readonly Action<string, string?, int?> _showOsd;

    public HotkeyActionExecutor(
        Func<AppConfig> getConfig,
        IWaveLinkControl wl,
        Action<string, string?, int?> showOsd)
    {
        _getConfig = getConfig;
        _wl = wl;
        _showOsd = showOsd;
    }

    public void Execute(HotkeyAction? action)
    {
        if (action is null) return;
        if (!_wl.IsConnected)
        {
            _showOsd("Wave Link Disconnected", "Failed", null);
            return;
        }

        switch (action.Type)
        {
            case "mute_channel": MuteChannel(action); break;
            case "volume_up_channel": AdjustVolume(action, +ResolveStep(action)); break;
            case "volume_down_channel": AdjustVolume(action, -ResolveStep(action)); break;
            case "set_volume": SetVolume(action); break;
            case "switch_output": SwitchOutput(action); break;
            case "cycle_output": CycleOutput(action); break;
        }
    }

    private int ResolveStep(HotkeyAction action)
    {
        int step = action.Step ?? _getConfig().VolumeStep;
        return Math.Clamp(step, 1, 50);
    }

    private void MuteChannel(HotkeyAction action)
    {
        if (action.MixId is not null)
        {
            var pair = ResolvePair(action);
            if (pair is null) return;

            bool newMute = !pair.Value.Mix.IsMuted;
            _wl.SetChannelMix(pair.Value.Channel.Id, pair.Value.Mix.Id, isMuted: newMute);
            _showOsd(PairTitle(pair.Value.Channel, pair.Value.Mix.Id), newMute ? "Muted" : "Unmuted", null);
            return;
        }

        var channel = _wl.GetChannelById(action.ChannelId);
        if (channel is not null)
        {
            bool newMute = !channel.IsMuted;
            _wl.SetChannel(channel.Id, isMuted: newMute);
            _showOsd($"Channel: {channel.Name}", newMute ? "Muted" : "Unmuted", null);
            return;
        }

        // v1 fallback: the id may refer to an input device rather than a channel.
        var device = _wl.GetInputDeviceById(action.ChannelId);
        if (device is { Inputs.Count: > 0 })
        {
            bool newMute = !device.Inputs[0].IsMuted;
            _wl.SetInputMute(device.Id, device.Inputs[0].Id, newMute);
            _showOsd($"Input: {device.Name}", newMute ? "Muted" : "Unmuted", null);
        }
    }

    private void AdjustVolume(HotkeyAction action, int delta)
    {
        if (action.MixId is not null)
        {
            var pair = ResolvePair(action);
            if (pair is null) return;

            int newVolume = Math.Clamp((int)Math.Round(pair.Value.Mix.Level * 100) + delta, 0, 100);
            _wl.SetChannelMix(pair.Value.Channel.Id, pair.Value.Mix.Id, level: newVolume / 100.0);
            _showOsd($"Volume: {pair.Value.Channel.Name} · {MixName(pair.Value.Mix.Id)}", null, newVolume);
            return;
        }

        var channel = _wl.GetChannelById(action.ChannelId);
        if (channel is null) return;
        int newChannelVolume = Math.Clamp((int)Math.Round(channel.Level * 100) + delta, 0, 100);
        _wl.SetChannel(channel.Id, level: newChannelVolume / 100.0);
        _showOsd($"Volume: {channel.Name}", null, newChannelVolume);
    }

    private void SetVolume(HotkeyAction action)
    {
        if (action.MixId is not null)
        {
            var pair = ResolvePair(action);
            if (pair is null) return;

            int target = Math.Clamp(action.Value ?? 50, 0, 100);
            _wl.SetChannelMix(pair.Value.Channel.Id, pair.Value.Mix.Id, level: target / 100.0);
            _showOsd($"Volume: {pair.Value.Channel.Name} · {MixName(pair.Value.Mix.Id)}", null, target);
            return;
        }

        var channel = _wl.GetChannelById(action.ChannelId);
        if (channel is null) return;
        int channelTarget = Math.Clamp(action.Value ?? 50, 0, 100);
        _wl.SetChannel(channel.Id, level: channelTarget / 100.0);
        _showOsd($"Volume: {channel.Name}", null, channelTarget);
    }

    private void SwitchOutput(HotkeyAction action)
    {
        var target = _wl.GetOutputDeviceById(action.DeviceId);
        if (target is null) return;
        _wl.SetMainOutput(target.Id);
        _showOsd("Output Device:", target.Name, null);
    }

    private void CycleOutput(HotkeyAction action)
    {
        var ids = action.DeviceIds;
        if (ids is null || ids.Count == 0) return;

        int currentIndex = ids.IndexOf(_wl.MainOutputId);
        int nextIndex = (currentIndex + 1) % ids.Count;
        var target = _wl.GetOutputDeviceById(ids[nextIndex]);
        if (target is null) return;
        _wl.SetMainOutput(target.Id);
        _showOsd("Output Device:", target.Name, null);
    }

    private (Channel Channel, ChannelMix Mix)? ResolvePair(HotkeyAction action)
    {
        if (action.MixId is null) return null;
        var channel = _wl.GetChannelById(action.ChannelId);
        var mix = channel?.Mixes.FirstOrDefault(m => m.Id == action.MixId);
        return channel is null || mix is null ? null : (channel, mix);
    }

    private string PairTitle(Channel channel, string mixId) =>
        $"Channel: {channel.Name} · {MixName(mixId)}";

    private string MixName(string mixId) => _wl.GetMixById(mixId)?.Name ?? mixId;
}
