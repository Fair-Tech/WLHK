using System.Runtime.InteropServices;

namespace Wlhk.Core;

/// <summary>
/// One key event handed to the engine. TargetOverrideId is set when a held
/// target modifier picked an Active Target for this press.
/// </summary>
public readonly record struct KeyInput(string Combo, bool IsDown, bool IsRepeat, string? TargetOverrideId);

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
    public event Action<KeyInput>? KeyEvent;

    // Lock-free published state, swapped whole on config changes.
    private HashSet<string> _suppressSet = new();
    private volatile bool _suppressionEnabled = true;

    /// <summary>Target-chord and Volume Keys data, published as one unit.</summary>
    private sealed record ResolutionInfo(
        IReadOnlySet<string> Dynamic,
        IReadOnlySet<string> GlobalOnly,
        IReadOnlyList<TargetChord> TargetChords,
        IReadOnlySet<string> ChordKeys);

    private ResolutionInfo _info = new(new HashSet<string>(), new HashSet<string>(), [], new HashSet<string>());
    private volatile bool _globalFeaturesActive;

    // Recording mode: capture the next combo instead of dispatching it.
    private volatile Action<string>? _recordingCallback;
    private volatile bool _recordSideSpecific;
    // The key captured while recording, so its matching UP is swallowed too.
    private uint _recordingSwallowVk;

    // Chord recording: capture every key held until the first is released.
    private volatile Action<List<string>>? _chordCallback;
    private readonly List<string> _chordRecorded = new();
    private readonly HashSet<uint> _chordHeldVks = new();
    // Non-modifier keys whose release must be swallowed (held when a recording ended).
    private readonly HashSet<uint> _swallowUntilUp = new();

    // Hook-thread-only state (no locking needed).
    private readonly HashSet<string> _heldKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, string> _activeCombos = new();
    private readonly Dictionary<uint, string?> _activeOverrides = new();
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

    /// <summary>Publish target modifiers and Volume Keys combos. Call on any config change.</summary>
    public void SetResolutionInfo(EffectiveBindings bindings) =>
        Volatile.Write(ref _info, new ResolutionInfo(
            bindings.Dynamic, bindings.GlobalOnly, bindings.TargetChords, bindings.ChordKeys));

    /// <summary>
    /// The Volume Keys setting only takes over the keys while Wave Link is connected.
    /// Otherwise the volume keys would do nothing at all, so they go back to Windows.
    /// </summary>
    public void SetGlobalFeaturesActive(bool active) => _globalFeaturesActive = active;

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

    /// <summary>
    /// Record a custom modifier for a target entry: every key pressed until the
    /// first one is released, e.g. Ctrl+Shift, F13, or Caps Lock+F13. Modifiers
    /// are reported by family ("CTRL"), other keys by name ("F13").
    /// </summary>
    public void BeginChordRecording(Action<List<string>> callback) => _chordCallback = callback;

    public void CancelChordRecording() => _chordCallback = null;

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

        // Media keys we injected ourselves: pass straight through. Otherwise a
        // hotkey whose action sends a media key that is itself mapped would
        // retrigger the engine, and a key mapped to its own media action would
        // loop until the queue gave out.
        if ((nuint)data.dwExtraInfo == MediaKeySender.InjectedTag)
            return CallNextHookEx(_hook, nCode, wParam, lParam);

        int msg = (int)wParam;
        bool isDown = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
        bool isUp = msg is WM_KEYUP or WM_SYSKEYUP;
        if (!isDown && !isUp)
            return CallNextHookEx(_hook, nCode, wParam, lParam);

        uint vk = data.vkCode;

        if (KeyNames.IsModifier((int)vk))
        {
            TrackModifier(vk, isDown);
            if (_chordCallback is not null)
            {
                // Modifiers are recorded but still reach Windows, so no key is left
                // looking held. Releasing one ends the recording.
                string family = ModifierFamilyName(vk);
                if (isDown && !_chordRecorded.Contains(family)) _chordRecorded.Add(family);
                else if (isUp && _chordRecorded.Count > 0) FinishChord();
            }
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        string? baseName = KeyNames.BaseName((int)vk);
        if (baseName is null)
            return CallNextHookEx(_hook, nCode, wParam, lParam);

        if (_chordCallback is not null)
        {
            if (isDown)
            {
                if (!_chordRecorded.Contains(baseName)) _chordRecorded.Add(baseName);
                _chordHeldVks.Add(vk);
            }
            else
            {
                _chordHeldVks.Remove(vk);
                FinishChord();
            }
            return 1;
        }

        if (_swallowUntilUp.Contains(vk))
        {
            if (isUp) _swallowUntilUp.Remove(vk);
            return 1;
        }

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

        if (isDown) _heldKeys.Add(baseName);
        else _heldKeys.Remove(baseName);

        // A key used in a target's custom modifier is held, not pressed: it never
        // types or triggers anything itself (unless hotkeys are switched off).
        if (_suppressionEnabled && Volatile.Read(ref _info).ChordKeys.Contains(baseName))
            return 1;

        string combo;
        string? targetOverride;
        bool isRepeat = false;
        if (isDown)
        {
            if (_activeCombos.TryGetValue(vk, out var held))
            {
                // OS key repeat: keep the combo and target of the original press,
                // even if modifiers changed since.
                combo = held;
                _activeOverrides.TryGetValue(vk, out targetOverride);
                isRepeat = true;
            }
            else
            {
                (combo, targetOverride) = ResolveWithTargets(baseName);
                _activeCombos[vk] = combo;
                _activeOverrides[vk] = targetOverride;
            }
        }
        else
        {
            // Reuse the combo from the DOWN so modifier release order doesn't matter.
            if (_activeCombos.Remove(vk, out var stored))
            {
                combo = stored;
                _activeOverrides.Remove(vk, out targetOverride);
            }
            else
            {
                (combo, targetOverride) = ResolveWithTargets(baseName);
            }
        }

        bool mapped = Volatile.Read(ref _suppressSet).Contains(combo);

        // A key mapped only by the Volume Keys setting goes back to Windows, untouched and
        // undispatched, while Wave Link is disconnected.
        if (mapped && !_globalFeaturesActive && Volatile.Read(ref _info).GlobalOnly.Contains(combo))
            return CallNextHookEx(_hook, nCode, wParam, lParam);

        KeyEvent?.Invoke(new KeyInput(combo, isDown, isRepeat, targetOverride));

        if (_suppressionEnabled && mapped)
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
    private string BuildCombo(string baseName, bool sideSpecific) =>
        ComposeCombo(baseName, sideSpecific, exclude: null);

    /// <summary>
    /// Resolve a press, then let a held target chord redirect it to its entry.
    ///
    /// Chords are custom modifiers. A chord of ordinary keys (F13, Caps Lock)
    /// doesn't change the combo at all: while it's held, any Active Target hotkey
    /// simply aims at the chord's entry. A chord with modifiers (Ctrl+Shift) is
    /// stripped from the combo first, so with Shift assigned to "Mic", SHIFT+F13
    /// runs your F13 binding against Mic. A combo bound in its own right is never
    /// stripped, so a chord can't steal an existing SHIFT+F13 hotkey. The most
    /// specific chord that applies wins.
    /// </summary>
    internal (string Combo, string? TargetOverride) ResolveWithTargets(string baseName)
    {
        string combo = ResolveCombo(baseName);
        var info = Volatile.Read(ref _info);
        if (info.TargetChords.Count == 0)
            return (combo, null);

        bool comboBound = Volatile.Read(ref _suppressSet).Contains(combo);
        foreach (var chord in info.TargetChords)
        {
            if (!chord.Keys.All(_heldKeys.Contains) || !chord.Modifiers.All(IsHeld))
                continue;

            if (chord.Modifiers.Count == 0)
            {
                if (info.Dynamic.Contains(combo)) return (combo, chord.TargetId);
                continue;
            }
            if (comboBound) continue;

            string specific = ComposeCombo(baseName, sideSpecific: true, chord.Modifiers);
            if (info.Dynamic.Contains(specific)) return (specific, chord.TargetId);
            string generic = ComposeCombo(baseName, sideSpecific: false, chord.Modifiers);
            if (info.Dynamic.Contains(generic)) return (generic, chord.TargetId);
        }
        return (combo, null);
    }

    private void FinishChord()
    {
        var callback = _chordCallback;
        _chordCallback = null;
        if (callback is null) return;

        // Modifiers first in the usual order, then keys in the order pressed.
        var families = new[] { "CTRL", "ALT", "SHIFT", "WIN" };
        var keys = families.Where(_chordRecorded.Contains)
            .Concat(_chordRecorded.Where(k => !families.Contains(k)))
            .ToList();
        _chordRecorded.Clear();

        // Keys still down were swallowed on the way down; swallow their release
        // too so it can't reach anything as an unpaired key-up.
        foreach (uint held in _chordHeldVks) _swallowUntilUp.Add(held);
        _chordHeldVks.Clear();

        if (keys.Count > 0) callback(keys);
    }

    private static string ModifierFamilyName(uint vk) => vk switch
    {
        0xA2 or 0xA3 or 0x11 => "CTRL",
        0xA4 or 0xA5 or 0x12 => "ALT",
        0xA0 or 0xA1 or 0x10 => "SHIFT",
        _ => "WIN"
    };

    private bool IsHeld(ModifierFamily family) => family switch
    {
        ModifierFamily.Ctrl => _lCtrl || _rCtrl,
        ModifierFamily.Alt => _lAlt || _rAlt,
        ModifierFamily.Shift => _lShift || _rShift,
        ModifierFamily.Win => _lWin || _rWin,
        _ => false
    };

    private string ComposeCombo(string baseName, bool sideSpecific, IReadOnlySet<ModifierFamily>? exclude)
    {
        bool lCtrl = _lCtrl, rCtrl = _rCtrl, lAlt = _lAlt, rAlt = _rAlt;
        bool lShift = _lShift, rShift = _rShift, lWin = _lWin, rWin = _rWin;
        if (exclude is not null)
        {
            if (exclude.Contains(ModifierFamily.Ctrl)) lCtrl = rCtrl = false;
            if (exclude.Contains(ModifierFamily.Alt)) lAlt = rAlt = false;
            if (exclude.Contains(ModifierFamily.Shift)) lShift = rShift = false;
            if (exclude.Contains(ModifierFamily.Win)) lWin = rWin = false;
        }

        bool ctrl = lCtrl || rCtrl, alt = lAlt || rAlt, shift = lShift || rShift, win = lWin || rWin;
        if (!ctrl && !alt && !shift && !win)
            return baseName;

        var sb = new System.Text.StringBuilder(48);
        if (sideSpecific)
        {
            if (lCtrl) sb.Append("LCTRL+");
            if (rCtrl) sb.Append("RCTRL+");
            if (lAlt) sb.Append("LALT+");
            if (rAlt) sb.Append("RALT+");
            if (lShift) sb.Append("LSHIFT+");
            if (rShift) sb.Append("RSHIFT+");
            if (lWin) sb.Append("LWIN+");
            if (rWin) sb.Append("RWIN+");
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
