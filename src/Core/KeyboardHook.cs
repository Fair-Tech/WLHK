using System.Runtime.InteropServices;

namespace Wlhk.Core;

/// <summary>
/// Single persistent WH_KEYBOARD_LL hook on a dedicated high-priority thread.
///
/// Design constraints:
///  - The hook callback has a hard OS time budget (~300 ms before Windows silently
///    drops the hook), so the suppress decision is a lock-free HashSet lookup and
///    the engine dispatch is a fast in-memory state machine (no I/O on this path;
///    Wave Link sends are fire-and-forget async).
///  - Suppression: a mapped combo is swallowed (return 1) so the OS default
///    (e.g. Windows master volume for VOLUME_MUTE) never fires — v1 parity.
///  - The combo resolved at key-DOWN is remembered per VK and reused for the UP
///    event, so releasing a modifier before the base key can't mis-route the UP
///    (fixes v1's modifier-release-order bug).
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const int WM_QUIT = 0x0012;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public nint dwExtraInfo;
    }

    private delegate nint HookProc(int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookExW(int idHook, HookProc lpfn, nint hMod, uint dwThreadId);
    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(nint hhk);
    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);
    [DllImport("kernel32.dll")]
    private static extern nint GetModuleHandleW(string? lpModuleName);
    [DllImport("user32.dll")]
    private static extern int GetMessageW(out NativeMsg lpMsg, nint hWnd, uint min, uint max);
    [DllImport("user32.dll")]
    private static extern bool PostThreadMessageW(uint idThread, uint msg, nint wParam, nint lParam);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMsg
    {
        public nint hwnd; public uint message; public nint wParam; public nint lParam; public uint time; public int ptX; public int ptY;
    }

    /// <summary>Raised on the hook thread for every non-modifier key event. Handlers must be fast.</summary>
    public event Action<string, bool>? KeyEvent;

    // Lock-free published state, swapped whole on config changes.
    private HashSet<string> _suppressSet = new();
    private volatile bool _suppressionEnabled = true;

    // Recording mode: capture the next combo instead of dispatching it.
    private volatile Action<string>? _recordingCallback;
    private volatile bool _recordSideSpecific;
    // The key captured while recording, so its matching UP is swallowed too.
    private uint _recordingSwallowVk;

    // Hook-thread-only state (no locking needed).
    private readonly Dictionary<uint, string> _activeCombos = new();
    private bool _lCtrl, _rCtrl, _lAlt, _rAlt, _lShift, _rShift, _lWin, _rWin;

    private readonly HookProc _proc;   // field ref keeps the delegate alive for the native hook
    private nint _hook;
    private Thread? _thread;
    private uint _threadId;
    private volatile bool _disposed;

    public KeyboardHook()
    {
        _proc = HookCallback;
    }

    public void Start()
    {
        _thread = new Thread(HookThread)
        {
            IsBackground = true,
            Name = "WLHK-KeyboardHook",
            Priority = ThreadPriority.AboveNormal
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>Publish the set of combos that should be suppressed/handled. Call on any config change.</summary>
    public void SetMappedCombos(IEnumerable<string> combos)
    {
        Volatile.Write(ref _suppressSet, new HashSet<string>(combos, StringComparer.Ordinal));
    }

    public void SetEnabled(bool enabled) => _suppressionEnabled = enabled;

    /// <summary>
    /// Capture the next key combo instead of dispatching it. When
    /// <paramref name="sideSpecific"/> is set, held modifiers are recorded per
    /// side (LSHIFT/RSHIFT/...) so left and right can be bound separately.
    /// </summary>
    public void BeginRecording(bool sideSpecific, Action<string> callback)
    {
        _recordSideSpecific = sideSpecific;
        _recordingCallback = callback;
    }

    public void CancelRecording() => _recordingCallback = null;

    public bool IsRecording => _recordingCallback is not null;

    private void HookThread()
    {
        _threadId = GetCurrentThreadId();
        _hook = SetWindowsHookExW(WH_KEYBOARD_LL, _proc, GetModuleHandleW(null), 0);
        if (_hook == 0)
            return;

        // A message loop is required for a low-level hook to receive events.
        while (!_disposed && GetMessageW(out _, 0, 0, 0) > 0)
        {
        }

        UnhookWindowsHookEx(_hook);
        _hook = 0;
    }

    private nint HookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode < 0)
            return CallNextHookEx(_hook, nCode, wParam, lParam);

        var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        int msg = (int)wParam;
        bool isDown = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
        bool isUp = msg is WM_KEYUP or WM_SYSKEYUP;
        if (!isDown && !isUp)
            return CallNextHookEx(_hook, nCode, wParam, lParam);

        uint vk = data.vkCode;

        if (KeyNames.IsModifier((int)vk))
        {
            TrackModifier(vk, isDown);
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        string? baseName = KeyNames.BaseName((int)vk);
        if (baseName is null)
            return CallNextHookEx(_hook, nCode, wParam, lParam);

        // Recording mode: swallow the first non-modifier event and report it (v1 behavior,
        // except the main engine is paused during recording — fixes a v1 quirk where a
        // mapped key would fire its action while being re-recorded).
        var recording = _recordingCallback;
        if (recording is not null)
        {
            _recordingCallback = null;
            _recordingSwallowVk = vk;
            _activeCombos.Remove(vk);
            recording(BuildCombo(baseName, _recordSideSpecific));
            return 1;
        }

        // Swallow the release of the key that was just recorded, so it cannot
        // reach the engine as an unpaired UP.
        if (_recordingSwallowVk != 0 && _recordingSwallowVk == vk)
        {
            if (isUp) _recordingSwallowVk = 0;
            return 1;
        }

        string combo;
        if (isDown)
        {
            combo = ResolveCombo(baseName);
            _activeCombos[vk] = combo;
        }
        else
        {
            // Reuse the combo from the DOWN so modifier release order doesn't matter.
            if (_activeCombos.Remove(vk, out var stored))
                combo = stored;
            else
                combo = ResolveCombo(baseName);
        }

        KeyEvent?.Invoke(combo, isDown);

        if (_suppressionEnabled && Volatile.Read(ref _suppressSet).Contains(combo))
            return 1; // swallow: block the OS default for mapped combos

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private void TrackModifier(uint vk, bool isDown)
    {
        switch (vk)
        {
            case 0xA0: _lShift = isDown; break;
            case 0xA1: _rShift = isDown; break;
            case 0xA2: _lCtrl = isDown; break;
            case 0xA3: _rCtrl = isDown; break;
            case 0xA4: _lAlt = isDown; break;
            case 0xA5: _rAlt = isDown; break;
            case 0x5B: _lWin = isDown; break;
            case 0x5C: _rWin = isDown; break;
            case 0x10: _lShift = isDown; break; // generic fallbacks (rare from LL hook)
            case 0x11: _lCtrl = isDown; break;
            case 0x12: _lAlt = isDown; break;
        }
    }

    /// <summary>
    /// Picks the combo string this key press should act as. A side-specific
    /// binding (LSHIFT+H) wins when one exists; otherwise the side-agnostic form
    /// (SHIFT+H) is used, so bindings recorded before left/right support — and
    /// bindings deliberately left side-agnostic — keep matching either side.
    /// </summary>
    private string ResolveCombo(string baseName)
    {
        string specific = BuildCombo(baseName, sideSpecific: true);
        if (Volatile.Read(ref _suppressSet).Contains(specific))
            return specific;
        return BuildCombo(baseName, sideSpecific: false);
    }

    /// <summary>
    /// Modifier prefix order matches v1: CTRL+ALT+SHIFT+WIN. In side-specific
    /// form each held modifier is named by side (LCTRL/RCTRL/...), left before
    /// right when both are down.
    /// </summary>
    private string BuildCombo(string baseName, bool sideSpecific)
    {
        bool ctrl = _lCtrl || _rCtrl, alt = _lAlt || _rAlt, shift = _lShift || _rShift, win = _lWin || _rWin;
        if (!ctrl && !alt && !shift && !win)
            return baseName;

        var sb = new System.Text.StringBuilder(48);
        if (sideSpecific)
        {
            if (_lCtrl) sb.Append("LCTRL+");
            if (_rCtrl) sb.Append("RCTRL+");
            if (_lAlt) sb.Append("LALT+");
            if (_rAlt) sb.Append("RALT+");
            if (_lShift) sb.Append("LSHIFT+");
            if (_rShift) sb.Append("RSHIFT+");
            if (_lWin) sb.Append("LWIN+");
            if (_rWin) sb.Append("RWIN+");
        }
        else
        {
            if (ctrl) sb.Append("CTRL+");
            if (alt) sb.Append("ALT+");
            if (shift) sb.Append("SHIFT+");
            if (win) sb.Append("WIN+");
        }
        sb.Append(baseName);
        return sb.ToString();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_threadId != 0)
            PostThreadMessageW(_threadId, WM_QUIT, 0, 0);
        _thread?.Join(1000);
    }
}
