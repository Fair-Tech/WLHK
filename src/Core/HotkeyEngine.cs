using Wlhk.WaveLink;
using Timer = System.Threading.Timer;

namespace Wlhk.Core;

/// <summary>
/// Tap / hold / double-press state machine — a faithful port of v1's
/// hotkeys.js semantics:
///  - auto-repeat DOWNs are ignored (unless the binding opts into AutoRepeat),
///  - double-press within DoublePressMs of the last UP fires doublePressAction
///    and cancels the pending hold,
///  - holdAction fires after its per-action duration (default 500 ms), and with
///    Repeat set keeps firing every RepeatIntervalMs until the key is released,
///  - normalAction fires immediately on DOWN when it's the only trigger,
///    otherwise it is deferred to UP (and further deferred DoublePressMs when a
///    doublePressAction could still turn the press into a double),
///  - media keys that only emit UP get a synthetic DOWN.
///
/// OnKey is called from the hook thread; all state is guarded by one lock and
/// every operation on this path is in-memory (Wave Link sends are async
/// fire-and-forget, OSD display is posted to the UI thread by the callback).
/// </summary>
public sealed class HotkeyEngine
{
    private const int DefaultHoldMs = 500;

    /// <summary>
    /// Hard stop for hold-repeat. Keys we swallow never reach the async key state,
    /// so a key release lost to a secure-desktop switch (UAC, Ctrl+Alt+Del) can't
    /// be detected by polling. Without a cap that would ramp a volume to 0 or 100
    /// and keep it pinned; 30 s is far longer than any deliberate hold.
    /// </summary>
    private const int MaxRepeatDurationMs = 30_000;

    private sealed class KeyState
    {
        public long DownTime;
        public long LastUpTime;
        public Timer? HoldTimer;
        public Timer? RepeatTimer;
        public bool IsHeld;
        public string? TargetOverrideId;
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, KeyState> _states = new();

    private readonly ConfigStore _config;
    private readonly TargetState _targets;
    private readonly HotkeyActionExecutor _executor;
    private IReadOnlyDictionary<string, HotkeyBinding> _bindings;
    private volatile bool _enabled = true;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            if (!value) StopAll();
        }
    }

    public HotkeyEngine(ConfigStore config, IWaveLinkControl wl, Action<string, string?, int?> showOsd,
        TargetState targets, IMediaKeySender? media = null)
    {
        _config = config;
        _targets = targets;
        _executor = new HotkeyActionExecutor(() => _config.Snapshot, wl, showOsd, media, targets);
        _bindings = EffectiveBindings.Build(config.Snapshot).Bindings;
    }

    /// <summary>Replace the live bindings (recorded hotkeys + Global Features). Call on config change.</summary>
    public void SetBindings(IReadOnlyDictionary<string, HotkeyBinding> bindings) =>
        Volatile.Write(ref _bindings, bindings);

    public void OnKey(KeyInput input)
    {
        if (!_enabled)
        {
            // Still honour releases, so a "Target While Held" key that was down
            // when hotkeys got disabled can't leave its target stuck on.
            if (!input.IsDown) _targets.ReleaseMomentary(input.Combo);
            return;
        }
        lock (_lock)
        {
            if (input.IsDown) HandleKeyDown(input.Combo, input.IsRepeat, input.TargetOverrideId);
            else HandleKeyUp(input.Combo);
        }
    }

    private void HandleKeyDown(string key, bool isRepeat, string? targetOverrideId)
    {
        if (!_states.TryGetValue(key, out var state))
            _states[key] = state = new KeyState();

        var binding = GetBinding(key);

        // Keyboard auto-repeat. Ignored, except for bindings that ramp on it
        // (Global Features volume keys), which re-fire their tap action so a held
        // volume key behaves exactly like the native one.
        if (isRepeat || IsDown(state))
        {
            if (binding is { AutoRepeat: true, NormalAction: not null, HoldAction: null, DoublePressAction: null })
                Execute(binding.NormalAction, key, state);
            return;
        }

        long now = Environment.TickCount64;
        state.DownTime = now;
        state.IsHeld = false;
        state.TargetOverrideId = targetOverrideId;

        if (binding is null) return;

        var cfg = _config.Snapshot;

        // Double press?
        if (binding.DoublePressAction is not null && now - state.LastUpTime < cfg.DoublePressMs)
        {
            CancelTimers(state);
            Execute(binding.DoublePressAction, key, state);
            state.LastUpTime = 0; // prevent triple-press from firing double again
            return;
        }

        if (binding.HoldAction is not null)
        {
            var hold = binding.HoldAction;
            int holdMs = hold.Duration ?? DefaultHoldMs;
            CancelTimers(state);
            state.HoldTimer = new Timer(_ =>
            {
                lock (_lock)
                {
                    // The release may have raced the timer firing.
                    if (!IsDown(state)) return;
                    state.IsHeld = true;
                    Execute(hold, key, state);
                    if (hold.Repeat == true)
                        StartRepeat(key, state, hold);
                }
            }, null, holdMs, Timeout.Infinite);
        }
        else if (binding.NormalAction is not null && binding.DoublePressAction is null)
        {
            // Only trigger configured: fire instantly on DOWN.
            Execute(binding.NormalAction, key, state);
        }
    }

    private void HandleKeyUp(string key)
    {
        // UP with no tracked DOWN (some media keys only emit UP): synthesize the DOWN.
        if (!_states.ContainsKey(key))
            HandleKeyDown(key, isRepeat: false, targetOverrideId: null);
        var state = _states[key];

        long now = Environment.TickCount64;
        state.LastUpTime = now;
        CancelTimers(state);
        _targets.ReleaseMomentary(key);

        var binding = GetBinding(key);
        if (binding is null) return;

        if (!state.IsHeld && binding.NormalAction is not null &&
            (binding.DoublePressAction is not null || binding.HoldAction is not null))
        {
            if (binding.DoublePressAction is not null)
            {
                // Wait out the double-press window; if a double fires it resets LastUpTime to 0.
                int wait = _config.Snapshot.DoublePressMs;
                var normal = binding.NormalAction;
                _ = new Timer(_ =>
                {
                    lock (_lock)
                    {
                        if (_states.TryGetValue(key, out var s) && s.LastUpTime == now)
                            Execute(normal, key, s);
                    }
                }, null, wait, Timeout.Infinite);
            }
            else
            {
                Execute(binding.NormalAction, key, state);
            }
        }
    }

    private void StartRepeat(string key, KeyState state, HotkeyAction hold)
    {
        int interval = Math.Clamp(hold.RepeatIntervalMs ?? ActionCatalog.DefaultRepeatIntervalMs,
            ActionCatalog.MinRepeatIntervalMs, ActionCatalog.MaxRepeatIntervalMs);
        long started = Environment.TickCount64;

        state.RepeatTimer?.Dispose();
        state.RepeatTimer = new Timer(_ =>
        {
            lock (_lock)
            {
                if (!_enabled || !IsDown(state) || Environment.TickCount64 - started > MaxRepeatDurationMs)
                {
                    state.RepeatTimer?.Dispose();
                    state.RepeatTimer = null;
                    return;
                }
                Execute(hold, key, state);
            }
        }, null, interval, interval);
    }

    /// <summary>
    /// Run an action with this press's context. An action that runs after the
    /// key is already up (deferred tap, delayed double-press window) can't hold a
    /// momentary target, so release it straight away rather than leave it stuck.
    /// </summary>
    private void Execute(HotkeyAction? action, string key, KeyState state)
    {
        _executor.Execute(action, new ActionContext(key, state.TargetOverrideId));
        if (!IsDown(state))
            _targets.ReleaseMomentary(key);
    }

    private static bool IsDown(KeyState state) => state.DownTime > state.LastUpTime;

    private static void CancelTimers(KeyState state)
    {
        state.HoldTimer?.Dispose();
        state.HoldTimer = null;
        state.RepeatTimer?.Dispose();
        state.RepeatTimer = null;
    }

    private void StopAll()
    {
        lock (_lock)
        {
            foreach (var (key, state) in _states)
            {
                CancelTimers(state);
                _targets.ReleaseMomentary(key);
            }
        }
    }

    private HotkeyBinding? GetBinding(string key) =>
        Volatile.Read(ref _bindings).TryGetValue(key, out var b) ? b : null;
}
