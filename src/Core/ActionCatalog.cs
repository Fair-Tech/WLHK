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
    public const string SwitchOutput = "switch_output";
    public const string CycleOutput = "cycle_output";
    public const string Media = "media";
    public const string Target = "target";

    /// <summary>Editor order and labels.</summary>
    public static readonly (string Kind, string Label)[] Kinds =
    {
        (Mute, "Mute"),
        (Volume, "Volume"),
        (SwitchOutput, "Switch Output Device"),
        (CycleOutput, "Cycle Output Devices"),
        (Media, "Media Key"),
        (Target, "Change Target"),
    };

    /// <summary>Modes per kind, first entry is the default. Kinds without modes return empty.</summary>
    public static (string Mode, string Label)[] ModesFor(string kind) => kind switch
    {
        Mute => [("toggle", "Toggle"), ("mute", "Mute"), ("unmute", "Unmute")],
        Volume => [("up", "Up"), ("down", "Down"), ("set", "Set Level")],
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

    public static string DefaultMode(string kind)
    {
        var modes = ModesFor(kind);
        return modes.Length > 0 ? modes[0].Mode : "";
    }

    /// <summary>Kinds that act on a Wave Link channel (fixed or Active Target).</summary>
    public static bool IsChannelKind(string kind) => kind is Mute or Volume;

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
        _ => (action.Type, string.IsNullOrEmpty(action.Mode) ? DefaultMode(action.Type) : action.Mode!)
    };

    /// <summary>Rewrite a legacy action type into kind + mode. Returns true if it changed.</summary>
    public static bool Upgrade(HotkeyAction? action)
    {
        if (action is null) return false;
        var (kind, mode) = Resolve(action);
        bool changed = action.Type != kind || (ModesFor(kind).Length > 0 && action.Mode != mode);
        action.Type = kind;
        action.Mode = ModesFor(kind).Length > 0 ? mode : null;
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
    /// volume, skipping tracks, cycling targets). Toggles and absolute sets do
    /// not, so the editor only offers Repeat for these.
    /// </summary>
    public static bool IsRepeatable(string kind, string mode) => (kind, mode) switch
    {
        (Volume, "up" or "down") => true,
        (Media, "volume_up" or "volume_down" or "next" or "previous") => true,
        (Target, "next" or "previous") => true,
        _ => false
    };

    public const int DefaultRepeatIntervalMs = 150;
    public const int MinRepeatIntervalMs = 30;
    public const int MaxRepeatIntervalMs = 2000;
}
