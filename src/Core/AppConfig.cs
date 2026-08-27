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
}

public sealed class HotkeyBinding
{
    public HotkeyAction? NormalAction { get; set; }
    public HotkeyAction? HoldAction { get; set; }
    public HotkeyAction? DoublePressAction { get; set; }

    [JsonIgnore]
    public bool IsEmpty => NormalAction is null && HoldAction is null && DoublePressAction is null;
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
                    Current = Normalize(loaded);
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
