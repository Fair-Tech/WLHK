using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Wlhk.Core;

/// <summary>Result of an app-volume change, for the OSD.</summary>
public readonly record struct AppVolumeResult(string AppName, int? Percent, bool? Muted, string? Problem);

/// <summary>Per-app volume, as in the Windows Volume Mixer. Abstracted for tests.</summary>
public interface IAppAudio
{
    /// <summary>
    /// Apply <paramref name="mode"/> (up / down / set / toggle_mute / mute / unmute)
    /// to the focused app. Runs asynchronously; <paramref name="done"/> reports
    /// the outcome from a background thread.
    /// </summary>
    void ApplyToFocusedApp(string mode, int step, int value, Action<AppVolumeResult> done);
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
            return new AppVolumeResult("App Volume", null, null, "No focused app");

        string? exe = ProcessName(pid);
        string display = DisplayName(pid, exe);
        var sessions = FindSessions(pid, exe);
        try
        {
            if (sessions.Count == 0)
                return new AppVolumeResult(display, null, null, "No audio from this app");

            if (mode == "refresh")
            {
                // Diagnostics: write every session's exact current state back, a
                // true no-op that still exercises the write path.
                foreach (var v in sessions)
                {
                    SetVolume(v, GetVolume(v));
                    SetMute(v, GetMute(v));
                }
                return new AppVolumeResult(display, (int)Math.Round(sessions.Max(GetVolume) * 100), null, null);
            }

            if (mode is "toggle_mute" or "mute" or "unmute")
            {
                bool anyAudible = sessions.Any(v => !GetMute(v));
                bool mute = mode switch { "mute" => true, "unmute" => false, _ => anyAudible };
                foreach (var v in sessions) SetMute(v, mute);
                return new AppVolumeResult(display, null, mute, null);
            }

            // Steps start from the loudest session, so one quiet stray session
            // can't make the whole app jump.
            float current = sessions.Max(GetVolume);
            int percent = mode switch
            {
                "set" => value,
                "down" => (int)Math.Round(current * 100) - step,
                _ => (int)Math.Round(current * 100) + step
            };
            percent = Math.Clamp(percent, 0, 100);
            foreach (var v in sessions) SetVolume(v, percent / 100f);
            return new AppVolumeResult(display, percent, null, null);
        }
        finally
        {
            foreach (var v in sessions) Release(v);
        }
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

    /// <summary>"Google Chrome" rather than "chrome" when the exe says so.</summary>
    private static string DisplayName(uint pid, string? exe)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            string? path = p.MainModule?.FileName;
            if (path is not null)
            {
                string? description = FileVersionInfo.GetVersionInfo(path).FileDescription;
                if (!string.IsNullOrWhiteSpace(description)) return description.Trim();
            }
        }
        catch { }
        return exe ?? "App";
    }

    public void Dispose()
    {
        _work.CompleteAdding();
    }

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint coInit);
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(Guid* clsid, nint outer, uint context, Guid* iid, out nint ppv);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint parent, EnumWindowsProc callback, nint lParam);
}
