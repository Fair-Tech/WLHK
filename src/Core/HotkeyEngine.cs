using Wlhk.WaveLink;
using Timer = System.Threading.Timer;

namespace Wlhk.Core;

/// <summary>
/// Tap / hold / double-press state machine — a faithful port of v1's
/// hotkeys.js semantics:
///  - auto-repeat DOWNs are ignored,
///  - double-press within DoublePressMs of the last UP fires doublePressAction
///    and cancels the pending hold,
///  - holdAction fires after its per-action duration (default 500 ms),
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

    private sealed class KeyState
    {
        public long DownTime;
        public long LastUpTime;
        public Timer? HoldTimer;
        public bool IsHeld;
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, KeyState> _states = new();

    private readonly ConfigStore _config;
    private readonly HotkeyActionExecutor _executor;

    public bool Enabled { get; set; } = true;

    public HotkeyEngine(ConfigStore config, WaveLinkClient wl, Action<string, string?, int?> showOsd)
    {
        _config = config;
        _executor = new HotkeyActionExecutor(() => _config.Snapshot, wl, showOsd);
    }

    public void OnKey(string combo, bool isDown)
    {
        if (!Enabled) return;
        lock (_lock)
        {
            if (isDown) HandleKeyDown(combo);
            else HandleKeyUp(combo);
        }
    }

    private void HandleKeyDown(string key)
    {
        long now = Environment.TickCount64;
        if (!_states.TryGetValue(key, out var state))
            _states[key] = state = new KeyState();

        // Ignore keyboard auto-repeat.
        if (state.DownTime > state.LastUpTime) return;

        state.DownTime = now;
        state.IsHeld = false;

        var binding = GetBinding(key);
        if (binding is null) return;

        var cfg = _config.Snapshot;

        // Double press?
        if (binding.DoublePressAction is not null && now - state.LastUpTime < cfg.DoublePressMs)
        {
            CancelHoldTimer(state);
            ExecuteAction(binding.DoublePressAction);
            state.LastUpTime = 0; // prevent triple-press from firing double again
            return;
        }

        if (binding.HoldAction is not null)
        {
            int holdMs = binding.HoldAction.Duration ?? DefaultHoldMs;
            CancelHoldTimer(state);
            state.HoldTimer = new Timer(_ =>
            {
                lock (_lock)
                {
                    state.IsHeld = true;
                    ExecuteAction(binding.HoldAction);
                }
            }, null, holdMs, Timeout.Infinite);
        }
        else if (binding.NormalAction is not null && binding.DoublePressAction is null)
        {
            // Only trigger configured: fire instantly on DOWN.
            ExecuteAction(binding.NormalAction);
        }
    }

    private void HandleKeyUp(string key)
    {
        // UP with no tracked DOWN (some media keys only emit UP): synthesize the DOWN.
        if (!_states.ContainsKey(key))
            HandleKeyDown(key);
        var state = _states[key];

        long now = Environment.TickCount64;
        state.LastUpTime = now;

        var binding = GetBinding(key);
        if (binding is null) return;

        CancelHoldTimer(state);

        if (!state.IsHeld && binding.NormalAction is not null &&
            (binding.DoublePressAction is not null || binding.HoldAction is not null))
        {
            if (binding.DoublePressAction is not null)
            {
                // Wait out the double-press window; if a double fires it resets LastUpTime to 0.
                int wait = _config.Snapshot.DoublePressMs;
                _ = new Timer(_ =>
                {
                    lock (_lock)
                    {
                        if (_states.TryGetValue(key, out var s) && s.LastUpTime == now)
                            ExecuteAction(binding.NormalAction);
                    }
                }, null, wait, Timeout.Infinite);
            }
            else
            {
                ExecuteAction(binding.NormalAction);
            }
        }
    }

    private static void CancelHoldTimer(KeyState state)
    {
        state.HoldTimer?.Dispose();
        state.HoldTimer = null;
    }

    private HotkeyBinding? GetBinding(string key) =>
        _config.Snapshot.Hotkeys.TryGetValue(key, out var b) ? b : null;

    private void ExecuteAction(HotkeyAction? action) => _executor.Execute(action);
}
