namespace Wlhk.Core;

public enum ModifierFamily { Ctrl, Alt, Shift, Win }

/// <summary>
/// Everything the hook and engine need to act on a config: the recorded hotkeys
/// plus the bindings the Volume Keys setting synthesises for the keyboard volume keys.
///
/// The Volume Keys setting is expressed as ordinary bindings rather than special cases,
/// so hold, repeat, OSD and Active Target behave identically whether a key was
/// recorded or taken over by the Volume Keys setting. Recorded hotkeys take priority:
/// a synthesised action only fills a trigger the recorded binding leaves empty,
/// so a recorded "hold VOLUME_MUTE to cycle outputs" keeps working alongside the
/// synthesised "tap to mute".
/// </summary>
public sealed class EffectiveBindings
{
    public IReadOnlyDictionary<string, HotkeyBinding> Bindings { get; private init; } =
        new Dictionary<string, HotkeyBinding>();

    /// <summary>Every combo the hook should handle and suppress.</summary>
    public IReadOnlySet<string> Mapped { get; private init; } = new HashSet<string>();

    /// <summary>
    /// Combos with at least one Active Target action. Only these accept a target
    /// modifier, so holding Shift can't hijack an unrelated SHIFT+key elsewhere.
    /// </summary>
    public IReadOnlySet<string> Dynamic { get; private init; } = new HashSet<string>();

    /// <summary>
    /// Combos that exist only because of the Volume Keys setting. The hook lets these
    /// through to Windows while Wave Link is disconnected, so taking over the
    /// volume keys never leaves you with no volume control at all.
    /// </summary>
    public IReadOnlySet<string> GlobalOnly { get; private init; } = new HashSet<string>();

    /// <summary>Target modifiers in list order (first match wins).</summary>
    public IReadOnlyList<(ModifierFamily Family, string TargetId)> TargetModifiers { get; private init; } = [];

    public static EffectiveBindings Build(AppConfig config)
    {
        var bindings = new Dictionary<string, HotkeyBinding>(StringComparer.Ordinal);
        foreach (var (combo, binding) in config.Hotkeys)
            if (binding is not null)
                bindings[combo] = binding;

        var globalOnly = new HashSet<string>(StringComparer.Ordinal);

        if (config.VolumeKeys == "wavelink")
        {
            AddGlobal("VOLUME_UP", ActiveVolume("up"), autoRepeat: true);
            AddGlobal("VOLUME_DOWN", ActiveVolume("down"), autoRepeat: true);
            AddGlobal("VOLUME_MUTE", ActiveMute(), autoRepeat: false);
        }

        var dynamic = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (combo, binding) in bindings)
            if (UsesActiveTarget(binding.NormalAction) || UsesActiveTarget(binding.HoldAction) ||
                UsesActiveTarget(binding.DoublePressAction))
                dynamic.Add(combo);

        var modifiers = new List<(ModifierFamily, string)>();
        foreach (var target in config.Targets ?? [])
            if (ParseModifier(target.Modifier) is ModifierFamily family && !string.IsNullOrEmpty(target.Id))
                modifiers.Add((family, target.Id));

        return new EffectiveBindings
        {
            Bindings = bindings,
            Mapped = new HashSet<string>(bindings.Keys, StringComparer.Ordinal),
            Dynamic = dynamic,
            GlobalOnly = globalOnly,
            TargetModifiers = modifiers
        };

        void AddGlobal(string combo, HotkeyAction action, bool autoRepeat)
        {
            if (!bindings.TryGetValue(combo, out var existing))
            {
                bindings[combo] = new HotkeyBinding { NormalAction = action, AutoRepeat = autoRepeat };
                globalOnly.Add(combo);
                return;
            }
            if (existing.NormalAction is not null)
                return; // recorded hotkey owns the tap; leave it alone

            // Merge into a copy so the stored config is never modified.
            bindings[combo] = new HotkeyBinding
            {
                NormalAction = action,
                HoldAction = existing.HoldAction,
                DoublePressAction = existing.DoublePressAction,
                // Key-repeat ramping conflicts with hold/double detection.
                AutoRepeat = autoRepeat && existing.HoldAction is null && existing.DoublePressAction is null
            };
        }
    }

    public static ModifierFamily? ParseModifier(string? value) => value?.ToLowerInvariant() switch
    {
        "ctrl" => ModifierFamily.Ctrl,
        "alt" => ModifierFamily.Alt,
        "shift" => ModifierFamily.Shift,
        "win" => ModifierFamily.Win,
        _ => null
    };

    private static bool UsesActiveTarget(HotkeyAction? action) =>
        action is not null && ActionCatalog.IsScoped(ActionCatalog.Resolve(action).Kind) &&
        ActionCatalog.ScopeOf(action) == ActionCatalog.ScopeActive;

    private static HotkeyAction ActiveVolume(string mode) =>
        new() { Type = ActionCatalog.Volume, Mode = mode, Scope = ActionCatalog.ScopeActive };

    private static HotkeyAction ActiveMute() =>
        new() { Type = ActionCatalog.Mute, Mode = "toggle", Scope = ActionCatalog.ScopeActive };
}
