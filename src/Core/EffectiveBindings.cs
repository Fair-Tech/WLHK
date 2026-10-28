namespace Wlhk.Core;

public enum ModifierFamily { Ctrl, Alt, Shift, Win }

/// <summary>
/// A target entry's custom modifier: every listed modifier and key must be held.
/// Modifiers are matched by family (either Ctrl key counts as CTRL).
/// </summary>
public sealed record TargetChord(IReadOnlySet<ModifierFamily> Modifiers, IReadOnlySet<string> Keys, string TargetId)
{
    public int Size => Modifiers.Count + Keys.Count;
}

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

    /// <summary>Target chords, most specific first, then in list order.</summary>
    public IReadOnlyList<TargetChord> TargetChords { get; private init; } = [];

    /// <summary>
    /// Non-modifier keys used in a chord. They're held as modifiers rather than
    /// pressed as keys, so the hook swallows them instead of letting them type
    /// or trigger anything.
    /// </summary>
    public IReadOnlySet<string> ChordKeys { get; private init; } = new HashSet<string>();

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

        var chords = new List<TargetChord>();
        foreach (var target in config.Targets ?? [])
        {
            if (string.IsNullOrEmpty(target.Id)) continue;
            // The pre-release single Modifier only ever held a modifier name; anything
            // else in it is junk, not a key to hold.
            var keys = target.HoldKeys
                       ?? (ParseModifier(target.Modifier) is not null ? [target.Modifier!.ToUpperInvariant()] : null);
            if (keys is not { Count: > 0 }) continue;

            var families = new HashSet<ModifierFamily>();
            var plain = new HashSet<string>(StringComparer.Ordinal);
            foreach (string key in keys)
            {
                if (ParseModifier(key) is ModifierFamily family) families.Add(family);
                else if (!string.IsNullOrWhiteSpace(key)) plain.Add(key);
            }
            if (families.Count + plain.Count > 0)
                chords.Add(new TargetChord(families, plain, target.Id));
        }
        // Most specific first, so holding Ctrl+Shift picks a Ctrl+Shift entry over
        // a plain Shift one. OrderBy is stable, keeping list order among equals.
        chords = chords.OrderByDescending(c => c.Size).ToList();

        return new EffectiveBindings
        {
            Bindings = bindings,
            Mapped = new HashSet<string>(bindings.Keys, StringComparer.Ordinal),
            Dynamic = dynamic,
            GlobalOnly = globalOnly,
            TargetChords = chords,
            ChordKeys = new HashSet<string>(chords.SelectMany(c => c.Keys), StringComparer.Ordinal)
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

    /// <summary>Modifier family for a key name, side-agnostic (LCTRL and RCTRL are both CTRL).</summary>
    public static ModifierFamily? ParseModifier(string? value) => value?.ToUpperInvariant() switch
    {
        "CTRL" or "LCTRL" or "RCTRL" => ModifierFamily.Ctrl,
        "ALT" or "LALT" or "RALT" => ModifierFamily.Alt,
        "SHIFT" or "LSHIFT" or "RSHIFT" => ModifierFamily.Shift,
        "WIN" or "LWIN" or "RWIN" => ModifierFamily.Win,
        _ => null
    };

    /// <summary>"Ctrl + Shift", "F13", "Caps Lock + F13" for display.</summary>
    public static string DescribeChord(IReadOnlyList<string>? keys)
    {
        if (keys is not { Count: > 0 }) return "None";
        return string.Join(" + ", keys.Select(k => ParseModifier(k) switch
        {
            ModifierFamily.Ctrl => "Ctrl",
            ModifierFamily.Alt => "Alt",
            ModifierFamily.Shift => "Shift",
            ModifierFamily.Win => "Win",
            _ => TitleCase(k)
        }));
    }

    /// <summary>"CAPS LOCK" -> "Caps Lock", "F13" stays "F13".</summary>
    private static string TitleCase(string key) =>
        string.Join(" ", key.Split(' ').Select(w =>
            w.Length > 1 && w.All(char.IsLetter) ? w[0] + w[1..].ToLowerInvariant() : w));

    private static bool UsesActiveTarget(HotkeyAction? action) =>
        action is not null && ActionCatalog.IsScoped(ActionCatalog.Resolve(action).Kind) &&
        ActionCatalog.ScopeOf(action) == ActionCatalog.ScopeActive;

    private static HotkeyAction ActiveVolume(string mode) =>
        new() { Type = ActionCatalog.Volume, Mode = mode, Scope = ActionCatalog.ScopeActive };

    private static HotkeyAction ActiveMute() =>
        new() { Type = ActionCatalog.Mute, Mode = "toggle", Scope = ActionCatalog.ScopeActive };
}
