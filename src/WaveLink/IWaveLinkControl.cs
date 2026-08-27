namespace Wlhk.WaveLink;

public interface IWaveLinkControl
{
    bool IsConnected { get; }
    string MainOutputId { get; }
    Channel? GetChannelById(string? id);
    Mix? GetMixById(string? id);
    OutputDevice? GetOutputDeviceById(string? id);
    InputDevice? GetInputDeviceById(string? id);
    void SetChannel(string id, double? level = null, bool? isMuted = null);
    void SetChannelMix(string channelId, string mixId, double? level = null, bool? isMuted = null);
    void SetMainOutput(string outputDeviceId);
    void SetInputMute(string deviceId, string inputId, bool isMuted);
}
