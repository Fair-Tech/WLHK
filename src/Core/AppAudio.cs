using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Wlhk.Core;

/// <summary>
/// Result of a Windows volume change, for the OSD. ExePath lets the OSD show the
/// app's icon; Previous is the level before the change.
/// </summary>
public readonly record struct AppVolumeResult(string AppName, int? Percent, bool? Muted, string? Problem,
    string? ExePath = null, int? Previous = null)
{
    public const string NoAudio = "No audio from this app";
    public const string NoFocusedApp = "No focused app";

    /// <summary>Nothing focused is playing audio, so there was nothing to adjust.</summary>
    public bool Silent => Problem is NoAudio or NoFocusedApp;
}

/// <summary>The app behind the foreground window.</summary>
public readonly record struct ForegroundAppInfo(string Name, string? ProcessName, string? ExePath);

/// <summary>Windows volume: per-app (Volume Mixer) and the default device. Abstracted for tests.</summary>
public interface IAppAudio
{
    /// <summary>
    /// Apply <paramref name="mode"/> (up / down / set / toggle_mute / mute / unmute)
    /// to the focused app. Runs asynchronously; <paramref name="done"/> reports
    /// the outcome from a background thread.
    /// </summary>
    void ApplyToFocusedApp(string mode, int step, int value, Action<AppVolumeResult> done);

    /// <summary>Same modes, applied to the default Windows playback device.</summary>
    void ApplyToSystem(string mode, int step, int value, Action<AppVolumeResult> done);
}

/// <summary>
/// Adjusts the audio sessions of the foreground app, exactly the per-app slider
/// the Windows Volume Mixer shows.
///
/// Two details matter in practice:
///  - Sessions are searched on every active playback device, not just the
///    default one. With Wave Link each channel is its own playback device, so an
///    app routed to "Browser" plays on a different device than the system default.
///  - An app often plays audio from a different process than the one that owns
///    its window (Chrome, Edge, Discord, any Electron app), so sessions are
///    matched by executable name as well as by process id.
///
/// Core Audio is COM, and built-in COM interop isn't available under Native AOT,
/// so the few interfaces used here are called through their vtables directly.
/// All of it runs on one background MTA thread: enumeration can take a few
/// milliseconds, which must never land inside the keyboard hook's time budget.
/// </summary>
public sealed unsafe class AppAudio : IAppAudio, IDisposable
{
    public static AppAudio Default { get; } = new();

    private readonly BlockingCollection<Action> _work = new();
    private Thread? _thread;
    private readonly object _startGate = new();
    private nint _enumerator;

    // COM identifiers (mmdeviceapi.h / audiopolicy.h)
    private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IID_IMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid IID_IAudioSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
    private static readonly Guid IID_IAudioSessionControl2 = new("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D");
    private static readonly Guid IID_ISimpleAudioVolume = new("87CE5498-68D6-44E5-9215-6DA47EF883D8");
    private static readonly Guid IID_IAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    private static readonly Guid PKEY_Device_FriendlyName_Fmtid = new("A45C254E-DF1C-4EFD-8020-67D146A850E0");
    private const uint PKEY_Device_FriendlyName_Pid = 14;
    private const int eMultimedia = 1;

    private const int eRender = 0;
    private const uint DEVICE_STATE_ACTIVE = 1;
    private const uint CLSCTX_ALL = 0x17;
    private const int AudioSessionStateExpired = 2;

    public void ApplyToFocusedApp(string mode, int step, int value, Action<AppVolumeResult> done)
    {
        // Capture the window now, on the caller's thread, so the app that was
        // focused when the key was pressed is the one adjusted.
        nint window = GetForegroundWindow();
        Post(() =>
        {
            AppVolumeResult result;
            try { result = Apply(TargetProcessId(window), mode, step, value); }
            catch (Exception ex)
            {
                Log.Error("AppAudio", ex);
                result = new AppVolumeResult("App Volume", null, null, "Couldn't reach Windows audio");
            }
            done(result);
        });
    }

    public void ApplyToSystem(string mode, int step, int value, Action<AppVolumeResult> done)
    {
        Post(() =>
        {
            AppVolumeResult result;
            try { result = ApplySystem(mode, step, value); }
            catch (Exception ex)
            {
                Log.Error("AppAudio system", ex);
                result = new AppVolumeResult("Windows", null, null, "Couldn't reach Windows audio");
            }
            done(result);
        });
    }

    /// <summary>The app behind the foreground window right now, or null. Any thread.</summary>
    public static ForegroundAppInfo? Foreground()
    {
        uint pid = TargetProcessId(GetForegroundWindow());
        if (pid == 0) return null;
        string? exe = ProcessName(pid);
        var (name, path) = Identity(pid, exe);
        return new ForegroundAppInfo(name, exe, path);
    }

    /// <summary>Diagnostics: the default device's name, level and mute.</summary>
    internal AppVolumeResult InspectSystem() => RunOnWorker(() => ApplySystem("inspect", 0, 0));

    /// <summary>Diagnostics: apply a mode to the default device synchronously.</summary>
    internal AppVolumeResult ApplyToSystemNow(string mode, int step, int value) =>
        RunOnWorker(() => ApplySystem(mode, step, value));

    /// <summary>Diagnostics: every live session on every playback device.</summary>
    internal readonly record struct SessionInfo(uint Pid, string? Process, float Volume, bool Muted);

    internal List<SessionInfo> InspectAll() => RunOnWorker(() =>
    {
        var result = new List<SessionInfo>();
        ForEachSession((pid, control) =>
        {
            nint v = QueryInterface(control, IID_ISimpleAudioVolume);
            if (v == 0) return;
            try { result.Add(new SessionInfo(pid, ProcessName(pid), GetVolume(v), GetMute(v))); }
            finally { Release(v); }
        });
        return result;
    });

    /// <summary>Apply a mode to a given process's sessions, synchronously. Used by diagnostics.</summary>
    internal AppVolumeResult ApplyToProcess(uint pid, string mode, int step, int value) =>
        RunOnWorker(() => Apply(pid, mode, step, value));

    private void Post(Action job)
    {
        EnsureStarted();
        _work.Add(job);
    }

    private T RunOnWorker<T>(Func<T> job)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try { done.SetResult(job()); }
            catch (Exception ex) { done.SetException(ex); }
        });
        return done.Task.GetAwaiter().GetResult();
    }

    private void EnsureStarted()
    {
        if (_thread is not null) return;
        lock (_startGate)
        {
            if (_thread is not null) return;
            var thread = new Thread(() =>
            {
                CoInitializeEx(0, 0 /* COINIT_MULTITHREADED */);
                foreach (var job in _work.GetConsumingEnumerable())
                {
                    try { job(); } catch (Exception ex) { Log.Error("AppAudio worker", ex); }
                }
            })
            { IsBackground = true, Name = "WLHK-AppAudio" };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
            _thread = thread;
        }
    }

    private AppVolumeResult Apply(uint pid, string mode, int step, int value)
    {
        if (pid == 0)
            return new AppVolumeResult("App Volume", null, null, AppVolumeResult.NoFocusedApp);

        string? exe = ProcessName(pid);
        var (display, exePath) = Identity(pid, exe);
        var sessions = FindSessions(pid, exe);
        try
        {
            if (sessions.Count == 0)
                return new AppVolumeResult(display, null, null, AppVolumeResult.NoAudio, exePath);

            // Steps start from the loudest session, so one quiet stray session
            // can't make the whole app jump.
            int before = (int)Math.Round(sessions.Max(GetVolume) * 100);

            if (mode == "refresh")
            {
                // Diagnostics: write every session's exact current state back, a
                // true no-op that still exercises the write path.
                foreach (var v in sessions)
                {
                    SetVolume(v, GetVolume(v));
                    SetMute(v, GetMute(v));
                }
                return new AppVolumeResult(display, before, null, null, exePath, before);
            }

            if (mode is "toggle_mute" or "mute" or "unmute")
            {
                bool anyAudible = sessions.Any(v => !GetMute(v));
                bool mute = mode switch { "mute" => true, "unmute" => false, _ => anyAudible };
                foreach (var v in sessions) SetMute(v, mute);
                return new AppVolumeResult(display, null, mute, null, exePath);
            }

            int percent = Math.Clamp(Step(before, mode, step, value), 0, 100);
            foreach (var v in sessions) SetVolume(v, percent / 100f);
            return new AppVolumeResult(display, percent, null, null, exePath, before);
        }
        finally
        {
            foreach (var v in sessions) Release(v);
        }
    }

    private static int Step(int current, string mode, int step, int value) => mode switch
    {
        "set" => value,
        "down" => current - step,
        _ => current + step
    };

    /// <summary>
    /// The default playback device through IAudioEndpointVolume: the same level
    /// the Windows volume slider and keyboard volume keys change.
    /// </summary>
    private AppVolumeResult ApplySystem(string mode, int step, int value)
    {
        nint enumerator = Enumerator();
        if (enumerator == 0)
            return new AppVolumeResult("Windows", null, null, "Couldn't reach Windows audio");

        // IMMDeviceEnumerator: 4 GetDefaultAudioEndpoint
        nint device = 0;
        if (((delegate* unmanaged[Stdcall]<nint, int, int, nint*, int>)Slot(enumerator, 4))
                (enumerator, eRender, eMultimedia, &device) < 0 || device == 0)
            return new AppVolumeResult("Windows", null, null, "No playback device");
        try
        {
            string name = DeviceName(device) ?? "Windows";
            nint endpoint = 0;
            Guid iid = IID_IAudioEndpointVolume;
            if (((delegate* unmanaged[Stdcall]<nint, Guid*, uint, nint, nint*, int>)Slot(device, 3))
                    (device, &iid, CLSCTX_ALL, 0, &endpoint) < 0)
                return new AppVolumeResult(name, null, null, "Couldn't reach this device");
            try
            {
                // IAudioEndpointVolume: 7 SetMasterVolumeLevelScalar, 9 GetMasterVolumeLevelScalar,
                // 14 SetMute, 15 GetMute
                float level = 0;
                ((delegate* unmanaged[Stdcall]<nint, float*, int>)Slot(endpoint, 9))(endpoint, &level);
                int muted = 0;
                ((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(endpoint, 15))(endpoint, &muted);
                int before = (int)Math.Round(level * 100);

                switch (mode)
                {
                    case "inspect":
                        return new AppVolumeResult(name, before, muted != 0, null, null, before);
                    case "refresh":
                        ((delegate* unmanaged[Stdcall]<nint, float, Guid*, int>)Slot(endpoint, 7))(endpoint, level, null);
                        ((delegate* unmanaged[Stdcall]<nint, int, Guid*, int>)Slot(endpoint, 14))(endpoint, muted, null);
                        return new AppVolumeResult(name, before, muted != 0, null, null, before);
                    case "toggle_mute" or "mute" or "unmute":
                    {
                        bool mute = mode switch { "mute" => true, "unmute" => false, _ => muted == 0 };
                        ((delegate* unmanaged[Stdcall]<nint, int, Guid*, int>)Slot(endpoint, 14))(endpoint, mute ? 1 : 0, null);
                        return new AppVolumeResult(name, null, mute, null);
                    }
                    default:
                    {
                        int percent = Math.Clamp(Step(before, mode, step, value), 0, 100);
                        ((delegate* unmanaged[Stdcall]<nint, float, Guid*, int>)Slot(endpoint, 7))(endpoint, percent / 100f, null);
                        return new AppVolumeResult(name, percent, null, null, null, before);
                    }
                }
            }
            finally { Release(endpoint); }
        }
        finally { Release(device); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid Fmtid;
        public uint Pid;
    }

    /// <summary>"Speakers (NVIDIA High Definition Audio)" from the device's property store.</summary>
    private static string? DeviceName(nint device)
    {
        // IMMDevice: 4 OpenPropertyStore (STGM_READ = 0)
        nint store = 0;
        if (((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(device, 4))(device, 0, &store) < 0 || store == 0)
            return null;
        try
        {
            var key = new PropertyKey { Fmtid = PKEY_Device_FriendlyName_Fmtid, Pid = PKEY_Device_FriendlyName_Pid };
            // PROPVARIANT is 24 bytes on x64: vt at 0, value at 8.
            byte* value = stackalloc byte[24];
            new Span<byte>(value, 24).Clear();
            // IPropertyStore: 5 GetValue
            if (((delegate* unmanaged[Stdcall]<nint, PropertyKey*, byte*, int>)Slot(store, 5))(store, &key, value) < 0)
                return null;
            try
            {
                const ushort VT_LPWSTR = 31;
                return *(ushort*)value == VT_LPWSTR && *(char**)(value + 8) != null
                    ? new string(*(char**)(value + 8))
                    : null;
            }
            finally { PropVariantClear(value); }
        }
        finally { Release(store); }
    }

    /// <summary>The process behind the foreground window, seeing through UWP's frame host.</summary>
    private static uint TargetProcessId(nint window)
    {
        if (window == 0) return 0;
        GetWindowThreadProcessId(window, out uint pid);

        // Store apps run inside ApplicationFrameHost; the real app owns a child window.
        if (string.Equals(ProcessName(pid), "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
        {
            uint child = 0;
            EnumChildWindows(window, (hwnd, _) =>
            {
                GetWindowThreadProcessId(hwnd, out uint candidate);
                if (candidate != pid) { child = candidate; return false; }
                return true;
            }, 0);
            if (child != 0) return child;
        }
        return pid;
    }

    /// <summary>ISimpleAudioVolume pointers for every live session of the app. Caller releases.</summary>
    private List<nint> FindSessions(uint pid, string? exe)
    {
        var result = new List<nint>();
        var names = new Dictionary<uint, string?>();
        ForEachSession((sessionPid, control) =>
        {
            bool match = sessionPid == pid;
            if (!match && exe is not null)
            {
                if (!names.TryGetValue(sessionPid, out var name))
                    names[sessionPid] = name = ProcessName(sessionPid);
                match = string.Equals(name, exe, StringComparison.OrdinalIgnoreCase);
            }
            if (!match) return;
            nint volume = QueryInterface(control, IID_ISimpleAudioVolume);
            if (volume != 0) result.Add(volume);
        });
        return result;
    }

    /// <summary>
    /// Visit every live, non-system session on every active playback device.
    /// The control pointer is only valid during the callback.
    /// </summary>
    private void ForEachSession(Action<uint, nint> visit)
    {
        nint enumerator = Enumerator();
        if (enumerator == 0) return;

        // IMMDeviceEnumerator: 3 EnumAudioEndpoints
        nint collection = 0;
        if (((delegate* unmanaged[Stdcall]<nint, int, uint, nint*, int>)Slot(enumerator, 3))
                (enumerator, eRender, DEVICE_STATE_ACTIVE, &collection) < 0)
            return;
        try
        {
            // IMMDeviceCollection: 3 GetCount, 4 Item
            uint deviceCount = 0;
            ((delegate* unmanaged[Stdcall]<nint, uint*, int>)Slot(collection, 3))(collection, &deviceCount);
            for (uint d = 0; d < deviceCount; d++)
            {
                nint device = 0;
                if (((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(collection, 4))(collection, d, &device) < 0)
                    continue;
                try { VisitDevice(device, visit); }
                finally { Release(device); }
            }
        }
        finally { Release(collection); }
    }

    private static void VisitDevice(nint device, Action<uint, nint> visit)
    {
        // IMMDevice: 3 Activate
        nint manager = 0;
        Guid iidManager = IID_IAudioSessionManager2;
        if (((delegate* unmanaged[Stdcall]<nint, Guid*, uint, nint, nint*, int>)Slot(device, 3))
                (device, &iidManager, CLSCTX_ALL, 0, &manager) < 0)
            return;
        try
        {
            // IAudioSessionManager2: 5 GetSessionEnumerator
            nint sessions = 0;
            if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(manager, 5))(manager, &sessions) < 0) return;
            try
            {
                // IAudioSessionEnumerator: 3 GetCount, 4 GetSession
                int count = 0;
                ((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(sessions, 3))(sessions, &count);
                for (int i = 0; i < count; i++)
                {
                    nint control = 0;
                    if (((delegate* unmanaged[Stdcall]<nint, int, nint*, int>)Slot(sessions, 4))(sessions, i, &control) < 0)
                        continue;
                    try
                    {
                        // IAudioSessionControl: 3 GetState
                        int state = 0;
                        ((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(control, 3))(control, &state);
                        if (state == AudioSessionStateExpired) continue;

                        // IAudioSessionControl2: 14 GetProcessId
                        nint control2 = QueryInterface(control, IID_IAudioSessionControl2);
                        if (control2 == 0) continue;
                        uint pid = 0;
                        try { ((delegate* unmanaged[Stdcall]<nint, uint*, int>)Slot(control2, 14))(control2, &pid); }
                        finally { Release(control2); }
                        if (pid == 0) continue; // system sounds

                        visit(pid, control);
                    }
                    finally { Release(control); }
                }
            }
            finally { Release(sessions); }
        }
        finally { Release(manager); }
    }

    private nint Enumerator()
    {
        if (_enumerator != 0) return _enumerator;
        Guid clsid = CLSID_MMDeviceEnumerator, iid = IID_IMMDeviceEnumerator;
        return CoCreateInstance(&clsid, 0, CLSCTX_ALL, &iid, out _enumerator) >= 0 ? _enumerator : 0;
    }

    // ISimpleAudioVolume: 3 SetMasterVolume, 4 GetMasterVolume, 5 SetMute, 6 GetMute
    private static float GetVolume(nint v)
    {
        float f = 0;
        ((delegate* unmanaged[Stdcall]<nint, float*, int>)Slot(v, 4))(v, &f);
        return f;
    }

    private static void SetVolume(nint v, float f) =>
        ((delegate* unmanaged[Stdcall]<nint, float, Guid*, int>)Slot(v, 3))(v, f, null);

    private static bool GetMute(nint v)
    {
        int b = 0;
        ((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(v, 6))(v, &b);
        return b != 0;
    }

    private static void SetMute(nint v, bool m) =>
        ((delegate* unmanaged[Stdcall]<nint, int, Guid*, int>)Slot(v, 5))(v, m ? 1 : 0, null);

    private static nint QueryInterface(nint obj, Guid iid)
    {
        nint result = 0;
        return ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(obj, 0))(obj, &iid, &result) >= 0 ? result : 0;
    }

    private static void Release(nint obj)
    {
        if (obj != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(obj, 2))(obj);
    }

    /// <summary>Function pointer stored in vtable slot N of a COM object.</summary>
    private static nint Slot(nint obj, int slot) => (*(nint**)obj)[slot];

    private static string? ProcessName(uint pid)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch { return null; }
    }

    /// <summary>
    /// Display name ("Google Chrome" rather than "chrome" when the exe says so)
    /// and executable path, for the OSD's title and icon.
    /// </summary>
    private static (string Name, string? Path) Identity(uint pid, string? exe)
    {
        string? path = null;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            path = p.MainModule?.FileName;
            if (path is not null)
            {
                string? description = FileVersionInfo.GetVersionInfo(path).FileDescription;
                if (!string.IsNullOrWhiteSpace(description)) return (description.Trim(), path);
            }
        }
        catch { }
        return (exe ?? "App", path);
    }

    public void Dispose()
    {
        _work.CompleteAdding();
    }

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint coInit);
    [DllImport("ole32.dll")] private static extern int PropVariantClear(byte* propVariant);
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(Guid* clsid, nint outer, uint context, Guid* iid, out nint ppv);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint parent, EnumWindowsProc callback, nint lParam);
}
