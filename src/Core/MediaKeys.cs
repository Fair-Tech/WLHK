using System.Runtime.InteropServices;

namespace Wlhk.Core;

public enum MediaKey
{
    PlayPause,
    NextTrack,
    PreviousTrack,
    Stop,
    VolumeUp,
    VolumeDown,
    VolumeMute
}

/// <summary>Sends a media key to Windows. Abstracted so tests don't drive the real session.</summary>
public interface IMediaKeySender
{
    void Send(MediaKey key);
}

/// <summary>
/// Presses the standard Windows media keys, which does exactly what the physical
/// keys on a keyboard do: whichever app currently holds media focus handles
/// transport, and the volume keys move the default output device. Going through
/// the OS this way is what makes one hotkey work across Spotify, browsers,
/// players and anything else that registers for media keys, rather than us
/// needing to know about each app.
///
/// Every injected event is tagged in dwExtraInfo so our own low-level hook can
/// recognise and skip it. Without that, mapping a hotkey to a media key that is
/// itself mapped (very easy to do, since the media keys are prime hotkey
/// material) would feed our own injection back into the engine.
/// </summary>
public sealed class MediaKeySender : IMediaKeySender
{
    /// <summary>"WLHK" in ASCII, written to dwExtraInfo on everything we inject.</summary>
    internal const nuint InjectedTag = 0x574C484B;

    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    // Virtual key codes (winuser.h)
    private const byte VK_VOLUME_MUTE = 0xAD;
    private const byte VK_VOLUME_DOWN = 0xAE;
    private const byte VK_VOLUME_UP = 0xAF;
    private const byte VK_MEDIA_NEXT_TRACK = 0xB0;
    private const byte VK_MEDIA_PREV_TRACK = 0xB1;
    private const byte VK_MEDIA_STOP = 0xB2;
    private const byte VK_MEDIA_PLAY_PAUSE = 0xB3;

    // keybd_event rather than SendInput: it takes dwExtraInfo directly, needs no
    // INPUT union layout (which is easy to get subtly wrong under Native AOT),
    // and still routes through the same input queue. Legacy, but exact.
    [DllImport("user32.dll", SetLastError = true)]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, nuint dwExtraInfo);

    public void Send(MediaKey key)
    {
        byte vk = key switch
        {
            MediaKey.PlayPause => VK_MEDIA_PLAY_PAUSE,
            MediaKey.NextTrack => VK_MEDIA_NEXT_TRACK,
            MediaKey.PreviousTrack => VK_MEDIA_PREV_TRACK,
            MediaKey.Stop => VK_MEDIA_STOP,
            MediaKey.VolumeUp => VK_VOLUME_UP,
            MediaKey.VolumeDown => VK_VOLUME_DOWN,
            MediaKey.VolumeMute => VK_VOLUME_MUTE,
            _ => 0
        };
        if (vk == 0) return;

        // Media and volume keys are extended keys; some apps check that flag.
        keybd_event(vk, 0, KEYEVENTF_EXTENDEDKEY, InjectedTag);
        keybd_event(vk, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, InjectedTag);
    }
}
