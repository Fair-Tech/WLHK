namespace Wlhk.Core;

/// <summary>
/// The Active Target: which channel + mix "Active Target" actions and the Global
/// Features keys currently control.
///
/// Resolution order, highest first:
///   1. a momentary target pushed by a "Target While Held" hotkey that is still down
///   2. a target picked by a held modifier for this particular key press
///   3. the active entry, changed by Next / Previous / Select
///
/// The active selection is runtime state and starts at the first entry on each
/// launch, so a volume knob always begins on a predictable target after a reboot.
/// Thread-safe: read from the hook/engine threads, written from the UI thread.
/// </summary>
public sealed class TargetState
{
    private readonly object _gate = new();
    private List<TargetEntry> _entries = new();
    private string? _activeId;
    private readonly List<(string Key, string TargetId)> _momentary = new();

    /// <summary>Raised after the effective target changes (any thread).</summary>
    public event Action? Changed;

    public static string NewId() => Guid.NewGuid().ToString("N")[..8];

    public IReadOnlyList<TargetEntry> Entries
    {
        get { lock (_gate) return _entries.ToList(); }
    }

    /// <summary>Replace the list (config change), keeping the active entry if it still exists.</summary>
    public void Update(IEnumerable<TargetEntry> entries)
    {
        lock (_gate)
        {
            _entries = entries.Select(Clone).ToList();
            if (_activeId is null || _entries.All(e => e.Id != _activeId))
                _activeId = _entries.FirstOrDefault()?.Id;
            _momentary.RemoveAll(m => _entries.All(e => e.Id != m.TargetId));
        }
        Changed?.Invoke();
    }

    /// <summary>The target an action should use right now, or null if none is configured.</summary>
    public TargetEntry? Current(string? modifierOverrideId = null)
    {
        lock (_gate)
        {
            if (_momentary.Count > 0)
            {
                var top = Find(_momentary[^1].TargetId);
                if (top is not null) return top;
            }
            if (modifierOverrideId is not null)
            {
                var overridden = Find(modifierOverrideId);
                if (overridden is not null) return overridden;
            }
            return Find(_activeId) ?? _entries.FirstOrDefault();
        }
    }

    public TargetEntry? Next() => Step(+1);
    public TargetEntry? Previous() => Step(-1);

    public TargetEntry? Select(string? id)
    {
        TargetEntry? entry;
        lock (_gate)
        {
            entry = Find(id);
            if (entry is null) return null;
            _activeId = entry.Id;
        }
        Changed?.Invoke();
        return entry;
    }

    /// <summary>Aim at a target for as long as <paramref name="key"/> stays down.</summary>
    public TargetEntry? PushMomentary(string key, string? id)
    {
        TargetEntry? entry;
        lock (_gate)
        {
            entry = Find(id);
            if (entry is null) return null;
            _momentary.RemoveAll(m => m.Key == key);
            _momentary.Add((key, entry.Id));
        }
        Changed?.Invoke();
        return entry;
    }

    /// <summary>Drop any momentary target held by <paramref name="key"/>. Cheap when there is none.</summary>
    public void ReleaseMomentary(string key)
    {
        bool removed;
        lock (_gate) removed = _momentary.RemoveAll(m => m.Key == key) > 0;
        if (removed) Changed?.Invoke();
    }

    private TargetEntry? Step(int delta)
    {
        TargetEntry? entry;
        lock (_gate)
        {
            if (_entries.Count == 0) return null;
            int index = Math.Max(0, _entries.FindIndex(e => e.Id == _activeId));
            index = ((index + delta) % _entries.Count + _entries.Count) % _entries.Count;
            entry = _entries[index];
            _activeId = entry.Id;
        }
        Changed?.Invoke();
        return entry;
    }

    private TargetEntry? Find(string? id) => id is null ? null : _entries.FirstOrDefault(e => e.Id == id);

    private static TargetEntry Clone(TargetEntry e) =>
        new() { Id = e.Id, ChannelId = e.ChannelId, MixId = e.MixId, Modifier = e.Modifier };
}
