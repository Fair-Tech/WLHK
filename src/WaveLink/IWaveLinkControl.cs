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

    // 2.6 additions. Defaulted so a test double only implements what it exercises.
    IReadOnlyList<Channel> GetChannels() => [];
    IReadOnlyList<InputDevice> GetInputDevices() => [];
    FocusedApp? FocusedApp => null;
    void SetMix(string mixId, double? level = null, bool? isMuted = null) { }
    void SetOutput(string deviceId, string outputId, double? level = null, bool? isMuted = null) { }
    void SetInput(string deviceId, string inputId, bool? isMuted = null, double? gain = null,
        double? micPcMix = null, bool? gainLock = null) { }
    void SetChannelEffect(string channelId, string effectId, bool isEnabled) { }
    void AddToChannel(string appId, string channelId) { }
}
