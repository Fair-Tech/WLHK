namespace Wlhk.Core;

/// <summary>
/// The consolidated action kinds introduced in 2.6, their modes, and the upgrade
/// path from the older one-type-per-behaviour action strings.
///
/// Before 2.6 every variation was its own type ("volume_up_channel",
/// "set_volume", "media_next", ...). Now similar behaviours share one kind with a
/// Mode, so the editor shows one "Volume" action with Up / Down / Set Level
/// instead of three near-identical entries. Old types are still understood
/// everywhere (Resolve maps them), and configs are rewritten to the new form on
/// load.
/// </summary>
public static class ActionCatalog
{
    public const string Mute = "mute";
    public const string Volume = "volume";
    public const string Microphone = "mic";
    public const string Effect = "effect";
    public const string SwitchOutput = "switch_output";
    public const string CycleOutput = "cycle_output";
    public const string RouteApp = "route_app";
    /// <summary>Pre-release 2.6 kind, now Volume / Mute with Scope = "app_volume". Upgraded on load.</summary>
    public const string AppVolume = "app_volume";
    public const string Media = "media";
    public const string Target = "target";

    // What a Mute / Volume / Effect action aims at (HotkeyAction.Scope).
    public const string ScopeChannel = "channel";
    public const string ScopeActive = "active";
    public const string ScopeFocusedApp = "focused_app";
    public const string ScopeMix = "mix";
    public const string ScopeOutput = "output";
    /// <summary>The focused app's own volume, as in the Windows Volume Mixer.</summary>
    public const string ScopeAppVolume = "app_volume";
    /// <summary>The default Windows playback device ("Native Volume Controls").</summary>
    public const string ScopeSystem = "system";

    /// <summary>Scopes handled by Windows rather than Wave Link, so they work without it.</summary>
    public static bool IsWindowsScope(string scope) => scope is ScopeAppVolume or ScopeSystem;

    /// <summary>Editor order and labels.</summary>
    public static readonly (string Kind, string Label)[] Kinds =
    {
        (Mute, "Mute"),
        (Volume, "Volume"),
        (Microphone, "Microphone"),
        (Effect, "Effect"),
        (SwitchOutput, "Switch Output Device"),
        (CycleOutput, "Cycle Output Devices"),
        (RouteApp, "Move Focused App"),
        (Media, "Media Key"),
        (Target, "Change Target"),
    };

    /// <summary>Modes per kind, first entry is the default. Kinds without modes return empty.</summary>
    public static (string Mode, string Label)[] ModesFor(string kind) => kind switch
    {
        Mute => [("toggle", "Toggle"), ("mute", "Mute"), ("unmute", "Unmute")],
        Volume => [("up", "Up"), ("down", "Down"), ("set", "Set Level")],
        Microphone =>
        [
            ("mute_toggle", "Toggle Mute"), ("mute", "Mute"), ("unmute", "Unmute"),
            ("gain_up", "Gain Up"), ("gain_down", "Gain Down"), ("gain_set", "Set Gain"),
            ("mix_up", "Mic/PC Mix Up"), ("mix_down", "Mic/PC Mix Down"), ("mix_set", "Set Mic/PC Mix"),
            ("lock_toggle", "Toggle Gain Lock")
        ],
        Effect => [("toggle", "Toggle"), ("enable", "Turn On"), ("disable", "Turn Off")],
        Media =>
        [
            ("play_pause", "Play / Pause"), ("next", "Next Track"), ("previous", "Previous Track"),
            ("stop", "Stop"), ("volume_up", "System Volume Up"), ("volume_down", "System Volume Down"),
            ("volume_mute", "System Mute")
        ],
        Target =>
        [
            ("next", "Next Target"), ("previous", "Previous Target"),
            ("select", "Select Target"), ("hold", "Target While Held")
        ],
        _ => []
    };

    /// <summary>
    /// Modes offered on a given trigger ("normal", "hold" or "double"). Target
    /// While Held needs the key to still be down when it runs, which a Normal
    /// Press with other triggers can't promise, so it's offered on Hold and
    /// Double Press (double-tap, then hold) only.
    /// </summary>
    public static (string Mode, string Label)[] ModesFor(string kind, string trigger) =>
        kind == Target && trigger == "normal"
            ? ModesFor(kind).Where(m => m.Mode != "hold").ToArray()
            : ModesFor(kind);

    public static string DefaultMode(string kind)
    {
        var modes = ModesFor(kind);
        return modes.Length > 0 ? modes[0].Mode : "";
    }

    /// <summary>Kinds whose target is chosen with Scope (channel, Active Target, mix, ...).</summary>
    public static bool IsScoped(string kind) => kind is Mute or Volume or Effect;

    /// <summary>Kinds that act on a Wave Link channel (fixed or Active Target).</summary>
    public static bool IsChannelKind(string kind) => IsScoped(kind);

    /// <summary>Effective scope of an action, reading the pre-release UseActiveTarget flag too.</summary>
    public static string ScopeOf(HotkeyAction action) =>
        !string.IsNullOrEmpty(action.Scope) ? action.Scope!
        : action.Type == AppVolume ? ScopeAppVolume
        : action.UseActiveTarget == true ? ScopeActive
        : ScopeChannel;

    /// <summary>Effective scope of a target list entry.</summary>
    public static string ScopeOf(TargetEntry entry) =>
        string.IsNullOrEmpty(entry.Scope) ? ScopeChannel : entry.Scope!;

    /// <summary>
    /// Effective (kind, mode) for an action, whether it uses a 2.6 type or a
    /// legacy one. Does not modify the action.
    /// </summary>
    public static (string Kind, string Mode) Resolve(HotkeyAction action) => action.Type switch
    {
        "mute_channel" => (Mute, "toggle"),
        "volume_up_channel" => (Volume, "up"),
        "volume_down_channel" => (Volume, "down"),
        "set_volume" => (Volume, "set"),
        "media_play_pause" => (Media, "play_pause"),
        "media_next" => (Media, "next"),
        "media_previous" => (Media, "previous"),
        "media_stop" => (Media, "stop"),
        "media_volume_up" => (Media, "volume_up"),
        "media_volume_down" => (Media, "volume_down"),
        AppVolume => action.Mode switch
        {
            "toggle_mute" => (Mute, "toggle"),
            "mute" or "unmute" => (Mute, action.Mode!),
            "down" or "set" => (Volume, action.Mode!),
            _ => (Volume, "up")
        },
        _ => (action.Type, string.IsNullOrEmpty(action.Mode) ? DefaultMode(action.Type) : action.Mode!)
    };

    /// <summary>Rewrite a legacy action into kind + mode + scope. Returns true if it changed.</summary>
    public static bool Upgrade(HotkeyAction? action)
    {
        if (action is null) return false;
        var (kind, mode) = Resolve(action);
        string? newMode = ModesFor(kind).Length > 0 ? mode : null;
        bool changed = action.Type != kind || action.Mode != newMode;
        if (action.Type == AppVolume && string.IsNullOrEmpty(action.Scope))
            action.Scope = ScopeAppVolume;
        action.Type = kind;
        action.Mode = newMode;

        if (action.UseActiveTarget is not null)
        {
            if (action.UseActiveTarget == true && string.IsNullOrEmpty(action.Scope))
                action.Scope = ScopeActive;
            action.UseActiveTarget = null;
            changed = true;
        }
        return changed;
    }

    /// <summary>Upgrade every action in a config. Returns true if anything changed.</summary>
    public static bool UpgradeAll(AppConfig config)
    {
        bool changed = false;
        foreach (var binding in config.Hotkeys.Values)
        {
            if (binding is null) continue;
            changed |= Upgrade(binding.NormalAction);
            changed |= Upgrade(binding.HoldAction);
            changed |= Upgrade(binding.DoublePressAction);
        }
        return changed;
    }

    /// <summary>
    /// Actions that make sense to fire repeatedly while a key is held (stepping
    /// a level, skipping tracks, cycling targets). Toggles and absolute sets do
    /// not, so the editor only offers Repeat for these.
    /// </summary>
    public static bool IsRepeatable(string kind, string mode) => (kind, mode) switch
    {
        (Volume, "up" or "down") => true,
        (Microphone, "gain_up" or "gain_down" or "mix_up" or "mix_down") => true,
        (Media, "volume_up" or "volume_down" or "next" or "previous") => true,
        (Target, "next" or "previous") => true,
        _ => false
    };

    public const int DefaultRepeatIntervalMs = 150;
    public const int MinRepeatIntervalMs = 30;
    public const int MaxRepeatIntervalMs = 2000;

    /// <summary>
    /// With Repeat on, the repeat interval also decides when a press becomes a
    /// hold. If the key has a tap or double-tap action too, that is floored here
    /// so a normal quick tap can't be mistaken for the start of a hold.
    /// </summary>
    public const int MinRepeatHoldThresholdMs = 250;
}
