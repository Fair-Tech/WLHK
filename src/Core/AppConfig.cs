using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wlhk.Core;

/// <summary>
/// One action bound to a trigger (normal / hold / double press).
/// Schema is v1-compatible; Step and Value are v2 additions.
/// </summary>
public sealed class HotkeyAction
{
    public string Type { get; set; } = "mute_channel";
    public string? ChannelId { get; set; }
    public string? MixId { get; set; }
    public string? DeviceId { get; set; }
    public List<string>? DeviceIds { get; set; }
    /// <summary>Hold time in ms (holdAction only).</summary>
    public int? Duration { get; set; }
    /// <summary>v2: per-action volume step override (volume_up/down).</summary>
    public int? Step { get; set; }
    /// <summary>v2: absolute volume percent 0-100 (set_volume).</summary>
    public int? Value { get; set; }

    /// <summary>
    /// v2.6: sub-option of a consolidated action, e.g. "toggle"/"mute"/"unmute"
    /// for mute, "up"/"down"/"set" for volume. See ActionCatalog.
    /// </summary>
    public string? Mode { get; set; }

    /// <summary>
    /// v2.6: what a Mute / Volume / Effect action aims at. Null or "channel" uses
    /// ChannelId (+ optional MixId); "active" the Active Target; "focused_app" the
    /// Wave Link channel of the focused app; "mix" a whole mix (MixId); "output"
    /// an output device (DeviceId).
    /// </summary>
    public string? Scope { get; set; }

    /// <summary>Pre-release 2.6 form of Scope = "active". Read only, upgraded on load.</summary>
    public bool? UseActiveTarget { get; set; }

    /// <summary>v2.6: channel effect for Effect actions, matched by id then by name.</summary>
    public string? EffectId { get; set; }
    public string? EffectName { get; set; }

    /// <summary>v2.6: target entry for "select" and "hold" target actions.</summary>
    public string? TargetId { get; set; }

    /// <summary>v2.6: per-action OSD. Null or true shows it, false hides it.</summary>
    public bool? Osd { get; set; }

    /// <summary>v2.6: hold actions only. Keep re-running the action while held.</summary>
    public bool? Repeat { get; set; }

    /// <summary>v2.6: delay between repeats while held, in ms.</summary>
    public int? RepeatIntervalMs { get; set; }
}

public sealed class HotkeyBinding
{
    public HotkeyAction? NormalAction { get; set; }
    public HotkeyAction? HoldAction { get; set; }
    public HotkeyAction? DoublePressAction { get; set; }

    [JsonIgnore]
    public bool IsEmpty => NormalAction is null && HoldAction is null && DoublePressAction is null;

    /// <summary>
    /// Runtime only: re-fire the normal action on the OS key-repeat stream. Set on
    /// bindings synthesised by the Volume Keys setting so a held volume key ramps the way
    /// it does natively. Never persisted.
    /// </summary>
    [JsonIgnore]
    public bool AutoRepeat { get; set; }
}

/// <summary>Pre-release 2.6 settings block. Read only so a tested build's choice carries over.</summary>
public sealed class LegacyGlobalFeatures
{
    public string? VolumeKeys { get; set; }
}

/// <summary>v2.6: one entry in the Active Target list.</summary>
public sealed class TargetEntry
{
    public string Id { get; set; } = "";
    /// <summary>
    /// Null or "channel", "mix", "output", "focused_app" (the focused app's Wave
    /// Link channel), "app_volume" (the focused app's own Windows volume) or
    /// "system" (the default Windows device); same meaning as HotkeyAction.Scope.
    /// </summary>
    public string? Scope { get; set; }
    public string? ChannelId { get; set; }
    /// <summary>Channel scope: null means All Mixes. Mix scope: the mix itself.</summary>
    public string? MixId { get; set; }
    /// <summary>Output scope: the output device.</summary>
    public string? DeviceId { get; set; }
    /// <summary>
    /// Keys that, while all held, aim Active Target actions at this entry: a
    /// custom modifier. Modifiers by family ("CTRL", "ALT", "SHIFT", "WIN") and any
    /// other key by name ("F13", "CAPS LOCK"), so "CTRL"+"SHIFT" or "F13" both work.
    /// </summary>
    public List<string>? HoldKeys { get; set; }

    /// <summary>Pre-release 2.6 single modifier ("ctrl"/"alt"/"shift"/"win"). Read only, upgraded to HoldKeys.</summary>
    public string? Modifier { get; set; }
}

public sealed class AppConfig
{
    public Dictionary<string, HotkeyBinding> Hotkeys { get; set; } = new();
    public bool HotkeysEnabled { get; set; } = true;
    public bool OsdEnabled { get; set; } = true;
    public string OsdPosition { get; set; } = "bottom-right";
    public bool AutoElevate { get; set; }
    public bool StartWithWindows { get; set; }

    // v2 additions
    public int VolumeStep { get; set; } = 5;
    public int OsdDurationMs { get; set; } = 2000;
    public int DoublePressMs { get; set; } = 300;

    /// <summary>
    /// Record new hotkeys with side-specific modifiers (LSHIFT+H rather than
    /// SHIFT+H) so left and right modifiers can trigger different actions.
    /// Existing side-agnostic bindings keep matching either side.
    /// </summary>
    public bool DistinguishModifierSides { get; set; }

    /// <summary>
    /// v2.6: "windows" (default) leaves the keyboard volume keys alone; "wavelink"
    /// makes Vol+ / Vol- / Mute control the Active Target.
    /// </summary>
    public string VolumeKeys { get; set; } = "windows";

    public List<TargetEntry> Targets { get; set; } = new();

    /// <summary>v2.6: remember the active target across restarts.</summary>
    public bool PersistentTarget { get; set; }

    /// <summary>v2.6: active target saved when PersistentTarget is on.</summary>
    public string? ActiveTargetId { get; set; }

    /// <summary>
    /// v2.6: what "Focused App's Channel" uses when the focused app isn't routed
    /// to any Wave Link channel. Null shows a message instead. Never "active" or
    /// "focused_app" (that would loop).
    /// </summary>
    public TargetEntry? FocusedAppFallback { get; set; }

    /// <summary>
    /// What Focused App's Volume uses when the focused app has no audio to adjust
    /// (or nothing is focused). Null shows a message instead.
    /// </summary>
    public TargetEntry? FocusedAppNoAudioFallback { get; set; }

    [JsonPropertyName("globalFeatures")]
    public LegacyGlobalFeatures? LegacyGlobalFeatures { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppConfig))]
public sealed partial class ConfigJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Owns the on-disk config. Storage resolution order:
///   1. "WLHK_data" folder next to the exe (portable / self-contained install layout)
///   2. %APPDATA%\WLHK
/// First run migrates the v1 Electron config from %APPDATA%\wavelinknode\config.json.
/// </summary>
public sealed class ConfigStore
{
    public AppConfig Current { get; private set; } = new();

    /// <summary>Immutable-by-convention snapshot for lock-free reads from the hook/engine threads.</summary>
    public AppConfig Snapshot => Volatile.Read(ref _snapshot);
    private AppConfig _snapshot = new();

    public string ConfigPath { get; }
    public bool IsPortable { get; }

    public event Action? Changed;

    public ConfigStore()
    {
        string exeDir = AppContext.BaseDirectory;
        string portableDir = Path.Combine(exeDir, "WLHK_data");
        if (Directory.Exists(portableDir))
        {
            IsPortable = true;
            ConfigPath = Path.Combine(portableDir, "config.json");
        }
        else
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WLHK");
            Directory.CreateDirectory(dir);
            ConfigPath = Path.Combine(dir, "config.json");
        }
    }

    /// <summary>Test seam: bind the store to an explicit path instead of resolving one.</summary>
    internal ConfigStore(string configPath, bool isPortable = false)
    {
        ConfigPath = configPath;
        IsPortable = isPortable;
    }

    public void Load()
    {
        try
        {
            if (!File.Exists(ConfigPath))
                MigrateV1();

            if (File.Exists(ConfigPath))
            {
                var loaded = JsonSerializer.Deserialize(File.ReadAllText(ConfigPath), ConfigJsonContext.Default.AppConfig);
                if (loaded is not null)
                {
                    Current = Normalize(loaded);
                    if (ActionCatalog.UpgradeAll(Current))
                    {
                        // Older versions can't read the consolidated action types, so
                        // keep a copy of the original before rewriting it.
                        string backup = ConfigPath + ".pre-2.6.bak";
                        try { if (!File.Exists(backup)) File.Copy(ConfigPath, backup); } catch { }
                        Save();
                        Log.Write($"Config upgraded to 2.6 action types (backup: {backup})");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Corrupt config: keep it for inspection, start fresh.
            try { File.Copy(ConfigPath, ConfigPath + ".corrupt", overwrite: true); } catch { }
            System.Diagnostics.Debug.WriteLine($"Config load failed: {ex.Message}");
            Current = new AppConfig();
        }
        Publish();
    }

    private void MigrateV1()
    {
        try
        {
            string v1Path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "wavelinknode", "config.json");
            if (File.Exists(v1Path))
                File.Copy(v1Path, ConfigPath, overwrite: false);
        }
        catch { }
    }

    private static AppConfig Normalize(AppConfig c)
    {
        c.Hotkeys ??= new();
        // Drop null bindings and clamp numeric settings to sane ranges.
        foreach (var key in c.Hotkeys.Keys.ToList())
            c.Hotkeys[key] ??= new HotkeyBinding();
        c.VolumeStep = Math.Clamp(c.VolumeStep, 1, 50);
        c.OsdDurationMs = Math.Clamp(c.OsdDurationMs, 250, 15000);
        c.DoublePressMs = Math.Clamp(c.DoublePressMs, 100, 1000);
        if (string.IsNullOrEmpty(c.OsdPosition)) c.OsdPosition = "bottom-right";
        if (c.LegacyGlobalFeatures?.VolumeKeys is "wavelink")
            c.VolumeKeys = "wavelink";
        c.LegacyGlobalFeatures = null;
        if (c.VolumeKeys is not ("windows" or "wavelink")) c.VolumeKeys = "windows";
        c.Targets ??= new();
        c.Targets.RemoveAll(t => t is null);
        foreach (var t in c.Targets)
        {
            if (string.IsNullOrEmpty(t.Id)) t.Id = TargetState.NewId();
            if (t.HoldKeys is null && EffectiveBindings.ParseModifier(t.Modifier) is not null)
                t.HoldKeys = [t.Modifier!.ToUpperInvariant()];
            t.Modifier = null;
            if (t.HoldKeys is { Count: 0 }) t.HoldKeys = null;
        }
        return c;
    }

    /// <summary>Atomic write (temp file + rename) so a crash can't corrupt the config.</summary>
    public void Save()
    {
        string json = JsonSerializer.Serialize(Current, ConfigJsonContext.Default.AppConfig);
        string tmp = ConfigPath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, ConfigPath, overwrite: true);
        Publish();
        Changed?.Invoke();
    }

    /// <summary>Clone Current into the lock-free snapshot read by the hook/engine threads.</summary>
    private void Publish()
    {
        string json = JsonSerializer.Serialize(Current, ConfigJsonContext.Default.AppConfig);
        var clone = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.AppConfig) ?? new AppConfig();
        Volatile.Write(ref _snapshot, clone);
    }
}
