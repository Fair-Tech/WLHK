using System.Diagnostics;
using Wlhk.Core;
using Wlhk.WaveLink;

namespace Wlhk.UI;

/// <summary>
/// Configuration window — WinForms port of v1's config page: global settings
/// card with connection status, admin banner, "Record New Hotkey", and one card
/// per combo with Normal / Hold / Double Press trigger rows.
/// </summary>
public sealed class ConfigForm : Form
{
    private readonly ConfigStore _store;
    private readonly WaveLinkClient _wl;
    private readonly KeyboardHook _hook;
    private readonly Action _onConfigApplied;
    private readonly Action _onReconnect;
    private readonly Action _onRelaunchAdmin;
    private readonly Theme _t = Theme.Current();
    private readonly TargetState _targets;

    private Panel _scroll = null!;
    private Panel _hotkeyList = null!;
    private Label _statusBadge = null!;
    private Panel _adminBanner = null!;
    private Panel? _recordOverlay;
    private bool _updatingUi;
    private string _waveSignature = "";

    private sealed record ComboItem(string? Id, string Name)
    {
        public override string ToString() => Name;
    }

    /// <summary>Dropdown id standing in for "Active Target" in the channel list.</summary>
    private const string ActiveTargetItemId = "\u0001active";

    private static readonly (string Value, string Label)[] VolumeKeyModes =
        { ("windows", "Windows default"), ("wavelink", "Control the Active Target") };
    private static readonly (string Value, string Label)[] TrackKeyModes =
        { ("windows", "Windows default"), ("target", "Switch the Active Target") };
    private static readonly (string Value, string Label)[] PlayPauseModes =
        { ("windows", "Windows default"), ("mute", "Mute the Active Target") };
    private static readonly (string Value, string Label)[] TargetModifierChoices =
        { ("", "None"), ("ctrl", "Ctrl"), ("alt", "Alt"), ("shift", "Shift"), ("win", "Win") };

    public ConfigForm(ConfigStore store, WaveLinkClient wl, KeyboardHook hook,
        Action onConfigApplied, Action onReconnect, Action onRelaunchAdmin, Icon appIcon,
        TargetState? targets = null)
    {
        _store = store;
        if (targets is null)
        {
            targets = new TargetState();
            targets.Update(store.Current.Targets);
        }
        _targets = targets;

        // Older action types are normally upgraded on load, but a config handed
        // in directly (tests, or a hand-edited file) may still use them.
        ActionCatalog.UpgradeAll(store.Current);
        _wl = wl;
        _hook = hook;
        _onConfigApplied = onConfigApplied;
        _onReconnect = onReconnect;
        _onRelaunchAdmin = onRelaunchAdmin;

        Text = "Wave Link Hotkey Manager";
        Icon = appIcon;
        ClientSize = new Size(900, 720);
        MinimumSize = new Size(800, 520);
        BackColor = _t.Bg;
        ForeColor = _t.Text;
        Font = new Font("Segoe UI", 9.5f);
        DoubleBuffered = true;

        BuildLayout();
        RefreshAll();

        _wl.StateChanged += OnWlChanged;
        _wl.Ready += OnWlChanged;
        _wl.Disconnected += OnWlChanged;
        _targets.Changed += OnTargetsChanged;
        FormClosed += (_, _) =>
        {
            _wl.StateChanged -= OnWlChanged;
            _wl.Ready -= OnWlChanged;
            _wl.Disconnected -= OnWlChanged;
            _targets.Changed -= OnTargetsChanged;
            _hook.CancelRecording();
        };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            // WS_EX_COMPOSITED: the window and all children are painted in one
            // buffered pass — removes the flicker when the hotkey list rebuilds.
            var cp = base.CreateParams;
            cp.ExStyle |= 0x02000000;
            return cp;
        }
    }

    private void OnWlChanged()
    {
        if (IsDisposed) return;
        try
        {
            BeginInvoke(() =>
            {
                if (IsDisposed) return;
                UpdateStatusBadge();
                // Only rebuild the cards when the channel/device *lists* change.
                // Volume/mute notifications (incl. echoes of our own hotkey actions)
                // don't affect the dropdown contents and shouldn't repaint anything.
                string sig = WaveSignature();
                if (sig != _waveSignature)
                {
                    _waveSignature = sig;
                    _updatingUi = true;
                    try
                    {
                        EnsureChannelTargetsInitialized();
                        RebuildFeaturesCard();
                        RebuildHotkeyList();
                    }
                    finally { _updatingUi = false; }
                }
            });
        }
        catch { }
    }

    private string WaveSignature() =>
        string.Join("|", _wl.GetChannels().Select(c =>
            c.Id + "\u0001" + c.Name + "\u0001" + string.Join("\u0003", c.Mixes.Select(m => m.Id)))) + "\u0002" +
        string.Join("|", _wl.GetMixes().Select(m => m.Id + "\u0001" + m.Name)) + "\u0002" +
        string.Join("|", _wl.GetOutputDevices().Select(d => d.Id + "\u0001" + d.Name)) + "\u0002" +
        _wl.IsConnected;

    private void RefreshAll()
    {
        if (IsDisposed) return;
        _updatingUi = true;
        try
        {
            UpdateStatusBadge();
            UpdateGlobalControls();
            _waveSignature = WaveSignature();
            EnsureChannelTargetsInitialized();
            RebuildHotkeyList();
        }
        finally { _updatingUi = false; }
    }

    // ─── Layout ────────────────────────────────────────────────────────────────

    private void BuildLayout()
    {
        _scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(24, 16, 24, 16), BackColor = _t.Bg };
        Controls.Add(_scroll);

        // Added top-down; AddTop() keeps document order.
        _adminBanner = BuildAdminBanner();
        AddTop(_adminBanner);
        AddTop(Spacer(8));

        var title = new Label
        {
            Text = "Wave Link Hotkey Manager",
            Font = new Font("Segoe UI Semibold", 16f),
            TextAlign = ContentAlignment.MiddleCenter,
            Height = 44,
            Dock = DockStyle.Top
        };
        AddTop(title);
        AddTop(Spacer(8));

        AddTop(BuildGlobalCard());
        AddTop(Spacer(12));

        AddTop(BuildFeaturesCard());
        AddTop(Spacer(12));

        var recordBtn = PrimaryButton("+  Record New Hotkey", 44);
        recordBtn.Dock = DockStyle.Top;
        recordBtn.Font = new Font("Segoe UI Semibold", 11f);
        recordBtn.Click += (_, _) => StartRecording();
        AddTop(recordBtn);
        AddTop(Spacer(12));

        _hotkeyList = new Panel { Dock = DockStyle.Top, AutoSize = true, BackColor = _t.Bg };
        AddTop(_hotkeyList);

        AddTop(BuildFooter());
    }

    private void AddTop(Control c)
    {
        c.Dock = DockStyle.Top;
        _scroll.Controls.Add(c);
        c.BringToFront(); // keep insertion order top-to-bottom with Dock.Top
    }

    private static Control Spacer(int h) => new Panel { Height = h };

    private Panel BuildAdminBanner()
    {
        var banner = new Panel { Height = 46, BackColor = _t.BannerBg, Visible = !Elevation.IsAdmin, Padding = new Padding(12, 8, 12, 8) };
        var lbl = new Label
        {
            Text = "⚠  Not running as Administrator. Hotkeys may not work in elevated apps (e.g. Task Manager).",
            ForeColor = _t.BannerText,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        };
        var btn = PrimaryButton("Relaunch as Admin", 28);
        btn.Dock = DockStyle.Right;
        btn.Width = 150;
        btn.Click += (_, _) => _onRelaunchAdmin();
        banner.Controls.Add(lbl);
        banner.Controls.Add(btn);
        banner.Paint += (_, e) =>
            e.Graphics.DrawRectangle(new Pen(_t.BannerBorder), 0, 0, banner.Width - 1, banner.Height - 1);
        return banner;
    }

    private Panel BuildGlobalCard()
    {
        var card = Card(168);

        var header = new Label
        {
            Text = "Global Settings",
            ForeColor = _t.Primary,
            Font = new Font("Segoe UI Semibold", 11.5f),
            Location = new Point(16, 12),
            AutoSize = true
        };
        card.Controls.Add(header);

        _statusBadge = new Label
        {
            Text = "● Disconnected",
            ForeColor = _t.Danger,
            Font = new Font("Segoe UI Semibold", 9.5f),
            AutoSize = false,
            Size = new Size(240, 22),
            TextAlign = ContentAlignment.MiddleRight,
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        card.Controls.Add(_statusBadge);
        card.Resize += (_, _) => _statusBadge.Location = new Point(card.Width - _statusBadge.Width - 16, 12);

        // Row 1: toggle checkboxes
        int y1 = 48;
        _cbHotkeys = Check("Enable Hotkeys", 16, y1, (v) => { _store.Current.HotkeysEnabled = v; Apply(); });
        _cbOsd = Check("Enable OSD", 150, y1, (v) => { _store.Current.OsdEnabled = v; Apply(); });
        _cbElevate = Check("Auto-Elevate to Admin on Start", 268, y1, (v) =>
        {
            _store.Current.AutoElevate = v;
            // Startup mechanism depends on whether elevation is wanted, so
            // re-register when this changes while startup is enabled.
            if (Autostart.IsRegistered())
                Autostart.Apply(true, v);
            Apply();
        });
        _cbStartup = Check("Start with Windows", 495, y1, (v) =>
        {
            _store.Current.StartWithWindows = v;
            Autostart.Apply(v, _store.Current.AutoElevate); // only on explicit toggle
            Apply();
            _cbStartup.Checked = Autostart.IsRegistered();
        });
        card.Controls.AddRange(new Control[] { _cbHotkeys, _cbOsd, _cbElevate, _cbStartup });

        // Row 2: OSD position, volume step, OSD duration, reconnect
        int labY = 84, ctlY = 102;
        card.Controls.Add(SmallLabel("OSD Position", 16, labY));
        _ddOsdPos = new NoWheelComboBox { Location = new Point(16, ctlY), Width = 150, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
        foreach (var (val, label) in OsdPositions) _ddOsdPos.Items.Add(label);
        _ddOsdPos.SelectedIndexChanged += (_, _) =>
        {
            if (_updatingUi) return;
            _store.Current.OsdPosition = OsdPositions[_ddOsdPos.SelectedIndex].Value;
            Apply();
        };
        card.Controls.Add(_ddOsdPos);

        card.Controls.Add(SmallLabel("Volume Step (%)", 186, labY));
        _numStep = Numeric(186, ctlY, 1, 50, v => { _store.Current.VolumeStep = v; Apply(); });
        card.Controls.Add(_numStep);

        card.Controls.Add(SmallLabel("OSD Duration (ms)", 296, labY));
        _numOsdMs = Numeric(296, ctlY, 250, 15000, v => { _store.Current.OsdDurationMs = v; Apply(); }, increment: 250);
        card.Controls.Add(_numOsdMs);

        _cbSides = Check("Distinguish L/R Modifiers", 406, ctlY + 2, v =>
        {
            _store.Current.DistinguishModifierSides = v;
            Apply();
        });
        _tips.SetToolTip(_cbSides,
            "Record new hotkeys with side-specific modifiers, e.g. LSHIFT+H and RSHIFT+H as separate hotkeys. "
            + "Hotkeys already recorded without a side keep matching either modifier.");
        card.Controls.Add(_cbSides);

        var reconnect = PrimaryButton("⟳  Reconnect to Wave Link", 30);
        reconnect.Width = 190;
        reconnect.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        reconnect.Location = new Point(card.Width - reconnect.Width - 16, ctlY - 2);
        reconnect.Click += (_, _) => _onReconnect();
        card.Controls.Add(reconnect);
        card.Resize += (_, _) => reconnect.Location = new Point(card.Width - reconnect.Width - 16, ctlY - 2);

        return card;
    }

    private CheckBox _cbHotkeys = null!, _cbOsd = null!, _cbElevate = null!, _cbStartup = null!, _cbSides = null!;
    private readonly ToolTip _tips = new();
    private ComboBox _ddOsdPos = null!;
    private NumericUpDown _numStep = null!, _numOsdMs = null!;

    private static readonly (string Value, string Label)[] OsdPositions =
    {
        ("top-left", "Top Left"), ("top-center", "Top Center"), ("top-right", "Top Right"),
        ("left-center", "Left Center"), ("center", "Center"), ("right-center", "Right Center"),
        ("bottom-left", "Bottom Left"), ("bottom-center", "Bottom Center"), ("bottom-right", "Bottom Right"),
    };

    private Control BuildFooter()
    {
        var footer = new Label
        {
            Text = "Built by FairTech",
            Height = 56,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = _t.Subtle,
            Font = new Font("Segoe UI Semibold", 9.5f),
            Cursor = Cursors.Hand
        };
        footer.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("https://FairTech.app") { UseShellExecute = true }); } catch { }
        };
        return footer;
    }

    // ─── Global state sync ─────────────────────────────────────────────────────

    public void UpdateStatusBadge()
    {
        bool connected = _wl.IsConnected;
        _statusBadge.Text = connected ? "●  Wave Link Connected" : "●  Wave Link Disconnected";
        _statusBadge.ForeColor = connected ? _t.Connected : _t.Danger;
    }

    private void UpdateGlobalControls()
    {
        var c = _store.Current;
        _cbHotkeys.Checked = c.HotkeysEnabled;
        _cbOsd.Checked = c.OsdEnabled;
        _cbElevate.Checked = c.AutoElevate;
        // Show the actual registry state, not the config flag — they can drift
        // (entry removed in Task Manager, exe moved, another install toggled it).
        _cbStartup.Checked = Autostart.IsRegistered();
        _cbSides.Checked = c.DistinguishModifierSides;
        int posIdx = Array.FindIndex(OsdPositions, p => p.Value == c.OsdPosition);
        _ddOsdPos.SelectedIndex = posIdx >= 0 ? posIdx : 8;
        _numStep.Value = Math.Clamp(c.VolumeStep, (int)_numStep.Minimum, (int)_numStep.Maximum);
        _numOsdMs.Value = Math.Clamp(c.OsdDurationMs, (int)_numOsdMs.Minimum, (int)_numOsdMs.Maximum);
    }

    /// <summary>Called externally (tray toggles) to re-sync the UI.</summary>
    public void SyncFromConfig()
    {
        if (IsDisposed) return;
        _updatingUi = true;
        try { UpdateGlobalControls(); }
        finally { _updatingUi = false; }
    }

    private void Apply()
    {
        if (_updatingUi) return;
        _store.Save();
        _onConfigApplied();
    }

    // ─── Recording ─────────────────────────────────────────────────────────────

    private void StartRecording()
    {
        if (_recordOverlay is not null) return;
        _recordOverlay = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(30, 30, 30) };
        var lbl = new Label
        {
            Text = "Listening for Keystroke...\n\n(click anywhere to cancel)",
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 15f),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter
        };
        _recordOverlay.Controls.Add(lbl);
        Controls.Add(_recordOverlay);
        _recordOverlay.BringToFront();

        EventHandler cancel = (_, _) => { _hook.CancelRecording(); EndRecording(); };
        _recordOverlay.Click += cancel;
        lbl.Click += cancel;

        _hook.BeginRecording(_store.Current.DistinguishModifierSides, combo =>
        {
            try
            {
                BeginInvoke(() =>
                {
                    EndRecording();
                    if (!_store.Current.Hotkeys.ContainsKey(combo))
                    {
                        _store.Current.Hotkeys[combo] = new HotkeyBinding();
                        Apply();
                    }
                    RebuildHotkeyList();
                });
            }
            catch { }
        });
    }

    private void EndRecording()
    {
        if (_recordOverlay is null) return;
        Controls.Remove(_recordOverlay);
        _recordOverlay.Dispose();
        _recordOverlay = null;
    }

    // ─── Hotkey cards ──────────────────────────────────────────────────────────

    /// <summary>
    /// Give channel actions that have no target yet the first available channel,
    /// so a freshly enabled trigger acts on something instead of silently doing
    /// nothing. Kept out of the render path: it writes config, so it runs only
    /// when the window opens or the channel list actually changes.
    /// </summary>
    private void EnsureChannelTargetsInitialized()
    {
        if (!InitializeDefaultChannelTargets()) return;
        _store.Save();
        _onConfigApplied();
    }

    private void RebuildHotkeyList()
    {
        bool wasUpdatingUi = _updatingUi;
        _updatingUi = true;
        var scrollPos = _scroll.AutoScrollPosition; // returns negative offsets
        _scroll.SuspendLayout();
        _hotkeyList.SuspendLayout();
        try
        {
            foreach (Control c in _hotkeyList.Controls.Cast<Control>().ToList())
                c.Dispose();
            _hotkeyList.Controls.Clear();

            foreach (var (combo, binding) in _store.Current.Hotkeys)
            {
                var card = BuildHotkeyCard(combo, binding);
                card.Dock = DockStyle.Top;
                _hotkeyList.Controls.Add(card);
                card.BringToFront();
                var spacer = Spacer(12);
                spacer.Dock = DockStyle.Top;
                _hotkeyList.Controls.Add(spacer);
                spacer.BringToFront();
            }
        }
        finally
        {
            _hotkeyList.ResumeLayout();
            _scroll.ResumeLayout();
            _scroll.AutoScrollPosition = new Point(-scrollPos.X, -scrollPos.Y);
            _updatingUi = wasUpdatingUi;
        }
    }

    private bool InitializeDefaultChannelTargets()
    {
        string? firstChannelId = _wl.GetChannels().FirstOrDefault()?.Id;
        if (firstChannelId is null) return false;

        bool changed = false;
        foreach (var binding in _store.Current.Hotkeys.Values)
        {
            foreach (var action in new[]
                     {
                         binding.NormalAction, binding.HoldAction, binding.DoublePressAction
                     })
            {
                changed |= InitializeDefaultChannelTarget(action, firstChannelId);
                changed |= InitializeDefaultTargetId(action);
            }
        }
        return changed;
    }

    private static bool InitializeDefaultChannelTarget(HotkeyAction? action, string? firstChannelId)
    {
        if (action is null || action.ChannelId is not null || action.UseActiveTarget == true ||
            firstChannelId is null || !ActionCatalog.IsChannelKind(ActionCatalog.Resolve(action).Kind))
            return false;

        action.ChannelId = firstChannelId;
        return true;
    }

    /// <summary>Select / hold target actions start on the first list entry, same reasoning as channels.</summary>
    private bool InitializeDefaultTargetId(HotkeyAction? action)
    {
        if (action is null || action.TargetId is not null) return false;
        var (kind, mode) = ActionCatalog.Resolve(action);
        if (kind != ActionCatalog.Target || mode is not ("select" or "hold")) return false;
        string? first = _store.Current.Targets.FirstOrDefault()?.Id;
        if (first is null) return false;
        action.TargetId = first;
        return true;
    }

    /// <summary>Switch an action to another kind, dropping fields that no longer apply.</summary>
    private void ChangeKind(HotkeyAction action, string kind)
    {
        action.Type = kind;
        action.Mode = ActionCatalog.ModesFor(kind).Length > 0 ? ActionCatalog.DefaultMode(kind) : null;
        if (ActionCatalog.IsChannelKind(kind))
        {
            // Mute <-> Volume deliberately keeps its channel and mix.
            action.DeviceId = null;
            action.DeviceIds = null;
            action.TargetId = null;
            InitializeDefaultChannelTarget(action, _wl.GetChannels().FirstOrDefault()?.Id);
        }
        else
        {
            action.ChannelId = null;
            action.MixId = null;
            action.UseActiveTarget = null;
            if (kind is not (ActionCatalog.SwitchOutput or ActionCatalog.CycleOutput))
            {
                action.DeviceId = null;
                action.DeviceIds = null;
            }
            if (kind != ActionCatalog.Target)
                action.TargetId = null;
        }
        if (!ActionCatalog.IsRepeatable(kind, action.Mode ?? ""))
        {
            action.Repeat = null;
            action.RepeatIntervalMs = null;
        }
        InitializeDefaultTargetId(action);
    }

    private Panel BuildHotkeyCard(string combo, HotkeyBinding binding)
    {
        const int rowH = 92;
        var card = Card(52 + rowH * 3 + 12);

        var header = new Label
        {
            Text = combo,
            ForeColor = _t.Primary,
            Font = new Font("Segoe UI Semibold", 11.5f),
            Location = new Point(16, 14),
            AutoSize = true
        };
        card.Controls.Add(header);

        var del = new Button
        {
            Text = "Delete Hotkey",
            BackColor = _t.Danger,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Height = 28,
            Width = 120,
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        del.FlatAppearance.BorderSize = 0;
        del.Location = new Point(card.Width - del.Width - 16, 12);
        del.Click += (_, _) =>
        {
            if (MessageBox.Show(this, $"Delete hotkey {combo}?", "Wave Link Hotkey Manager",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                _store.Current.Hotkeys.Remove(combo);
                Apply();
                RebuildHotkeyList();
            }
        };
        card.Controls.Add(del);
        card.Resize += (_, _) => del.Location = new Point(card.Width - del.Width - 16, 12);

        int y = 48;
        card.Controls.Add(BuildTriggerRow(combo, "Normal Press", binding.NormalAction,
            a => binding.NormalAction = a, new Point(12, y), card.Width - 24, showHold: false));
        y += rowH;
        card.Controls.Add(BuildTriggerRow(combo, "Hold", binding.HoldAction,
            a => binding.HoldAction = a, new Point(12, y), card.Width - 24, showHold: true));
        y += rowH;
        card.Controls.Add(BuildTriggerRow(combo, "Double Press", binding.DoublePressAction,
            a => binding.DoublePressAction = a, new Point(12, y), card.Width - 24, showHold: false));

        return card;
    }

    private Panel BuildTriggerRow(string combo, string label, HotkeyAction? action,
        Action<HotkeyAction?> setAction, Point loc, int width, bool showHold)
    {
        bool enabled = action is not null;
        var row = new Panel
        {
            Location = loc,
            Size = new Size(width, 86),
            BackColor = _t.Bg,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };
        if (enabled)
            row.Paint += (_, e) => e.Graphics.FillRectangle(new SolidBrush(_t.Primary), 0, 0, 3, row.Height);

        var cb = new CheckBox
        {
            Text = label,
            Checked = enabled,
            Font = new Font("Segoe UI Semibold", 9.5f),
            Location = new Point(12, 22),
            Width = 125
        };
        cb.CheckedChanged += (_, _) =>
        {
            if (_updatingUi) return;
            HotkeyAction? newAction = cb.Checked
                ? new HotkeyAction { Type = ActionCatalog.Mute, Mode = ActionCatalog.DefaultMode(ActionCatalog.Mute) }
                : null;
            InitializeDefaultChannelTarget(newAction, _wl.GetChannels().FirstOrDefault()?.Id);
            setAction(newAction);
            Apply();
            RebuildHotkeyList();
        };
        row.Controls.Add(cb);

        if (!enabled)
        {
            row.Controls.Add(new Label
            {
                Text = "Disabled",
                ForeColor = _t.Subtle,
                Font = new Font("Segoe UI", 9f, FontStyle.Italic),
                Location = new Point(150, 20),
                AutoSize = true
            });
            return row;
        }

        int x = 145;
        var (kind, mode) = ActionCatalog.Resolve(action!);

        // Row 1: what kind of action, its mode, and whether it shows the OSD.
        row.Controls.Add(SmallLabel("Action", x, 4));
        var ddKind = new NoWheelComboBox { Location = new Point(x, 20), Width = 170, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
        foreach (var (_, kindLabel) in ActionCatalog.Kinds) ddKind.Items.Add(kindLabel);
        ddKind.SelectedIndex = Math.Max(0, Array.FindIndex(ActionCatalog.Kinds, k => k.Kind == kind));
        ddKind.SelectedIndexChanged += (_, _) =>
        {
            if (_updatingUi) return;
            ChangeKind(action!, ActionCatalog.Kinds[ddKind.SelectedIndex].Kind);
            Apply();
            RebuildHotkeyList();
        };
        row.Controls.Add(ddKind);

        var modes = ActionCatalog.ModesFor(kind);
        if (modes.Length > 0)
        {
            row.Controls.Add(SmallLabel("Mode", 325, 4));
            var ddMode = new NoWheelComboBox { Location = new Point(325, 20), Width = 150, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
            foreach (var (_, modeLabel) in modes) ddMode.Items.Add(modeLabel);
            ddMode.SelectedIndex = Math.Max(0, Array.FindIndex(modes, m => m.Mode == mode));
            ddMode.SelectedIndexChanged += (_, _) =>
            {
                if (_updatingUi) return;
                action!.Type = kind;
                action.Mode = modes[ddMode.SelectedIndex].Mode;
                if (!ActionCatalog.IsRepeatable(kind, action.Mode))
                {
                    action.Repeat = null;
                    action.RepeatIntervalMs = null;
                }
                InitializeDefaultTargetId(action);
                Apply();
                RebuildHotkeyList();
            };
            row.Controls.Add(ddMode);
        }

        var cbOsd = new CheckBox { Text = "OSD", Checked = action!.Osd != false, Location = new Point(490, 22), AutoSize = true };
        _tips.SetToolTip(cbOsd, "Show the on-screen display when this action runs.");
        cbOsd.CheckedChanged += (_, _) =>
        {
            if (_updatingUi) return;
            action.Osd = cbOsd.Checked ? null : false;
            Apply();
        };
        row.Controls.Add(cbOsd);

        // Row 2: what the action applies to.
        var outputs = _wl.GetOutputDevices().Select(d => new ComboItem(d.Id, d.Name)).ToList();

        if (ActionCatalog.IsChannelKind(kind))
        {
            bool useActive = action.UseActiveTarget == true;
            var channels = new List<ComboItem> { new(ActiveTargetItemId, "Active Target") };
            channels.AddRange(_wl.GetChannels().Select(c => new ComboItem(c.Id, c.Name)));

            row.Controls.Add(SmallLabel("Target Channel", x, 46));
            var dd = TargetDropdown(channels, useActive ? ActiveTargetItemId : action.ChannelId, new Point(x, 62), id =>
            {
                if (id is null) return;
                if (id == ActiveTargetItemId)
                {
                    action.UseActiveTarget = true;
                    action.ChannelId = null;
                    action.MixId = null;
                }
                else
                {
                    action.UseActiveTarget = null;
                    action.ChannelId = id;
                    if (action.MixId is not null &&
                        _wl.GetChannelById(id)?.Mixes.All(m => m.Id != action.MixId) != false)
                        action.MixId = null;
                }
                Apply();
                RebuildHotkeyList();
            });
            _tips.SetToolTip(dd, "\"Active Target\" follows whichever entry is active in Global Features.");
            row.Controls.Add(dd);
            x += 195;

            if (!useActive)
            {
                var mixes = MixTargetChoices.Build(_wl.GetChannelById(action.ChannelId), _wl.GetMixes(), action.MixId)
                    .Select(m => new ComboItem(m.Id, m.Name)).ToList();
                // Only "All Mixes" available: this channel belongs to no mix, so the
                // dropdown would be a no-op choice.
                if (mixes.Count > 1)
                {
                    row.Controls.Add(SmallLabel("Target Mix", x, 46));
                    var ddMix = TargetDropdown(mixes, action.MixId, new Point(x, 62), id => { action.MixId = id; Apply(); });
                    row.Controls.Add(ddMix);
                    x += 195;
                }
            }

            if (kind == ActionCatalog.Volume && mode == "set")
            {
                row.Controls.Add(SmallLabel("Level (%)", x, 46));
                var num = Numeric(x, 62, 0, 100, v => { action.Value = v; Apply(); }, width: 70);
                num.Value = Math.Clamp(action.Value ?? 50, 0, 100);
                row.Controls.Add(num);
            }
            else if (kind == ActionCatalog.Volume)
            {
                row.Controls.Add(SmallLabel("Step (%)", x, 46));
                var num = Numeric(x, 62, 1, 50, v => { action.Step = v; Apply(); }, width: 70);
                num.Value = Math.Clamp(action.Step ?? _store.Current.VolumeStep, 1, 50);
                row.Controls.Add(num);
            }
        }
        else if (kind == ActionCatalog.SwitchOutput)
        {
            row.Controls.Add(SmallLabel("Target Output", x, 46));
            var dd = TargetDropdown(outputs, action.DeviceId, new Point(x, 62), id => { action.DeviceId = id; Apply(); });
            row.Controls.Add(dd);
        }
        else if (kind == ActionCatalog.CycleOutput)
        {
            action.DeviceIds ??= new List<string>();
            while (action.DeviceIds.Count < 2)
                action.DeviceIds.Add(outputs.ElementAtOrDefault(action.DeviceIds.Count)?.Id ?? outputs.FirstOrDefault()?.Id ?? "");

            row.Controls.Add(SmallLabel("Target 1", x, 46));
            var dd1 = TargetDropdown(outputs, action.DeviceIds[0], new Point(x, 62), id =>
            {
                if (id is not null) action.DeviceIds[0] = id;
                Apply();
            });
            row.Controls.Add(dd1);
            x += 195;
            row.Controls.Add(SmallLabel("Target 2", x, 46));
            var dd2 = TargetDropdown(outputs, action.DeviceIds.Count > 1 ? action.DeviceIds[1] : null,
                new Point(x, 62), id =>
                {
                    if (id is not null) action.DeviceIds[1] = id;
                    Apply();
                });
            row.Controls.Add(dd2);
        }
        else if (kind == ActionCatalog.Media)
        {
            row.Controls.Add(Hint(mode.StartsWith("volume_", StringComparison.Ordinal)
                ? "Controls the Windows default output device."
                : "Sent to whichever app currently has media control.", x, 50));
        }
        else if (kind == ActionCatalog.Target)
        {
            var entries = _store.Current.Targets;
            if (entries.Count == 0)
            {
                row.Controls.Add(Hint("Add targets to the Active Target list under Global Features first.", x, 50));
            }
            else if (mode is "select" or "hold")
            {
                var items = entries.Select((e, i) => new ComboItem(e.Id, $"{i + 1}. {DescribeTarget(e)}")).ToList();
                row.Controls.Add(SmallLabel("Target", x, 46));
                var dd = TargetDropdown(items, action.TargetId, new Point(x, 62), id => { action.TargetId = id; Apply(); });
                dd.Width = 300;
                if (mode == "hold")
                    _tips.SetToolTip(dd, "Aims at this target only while the key is held down. "
                        + "Use it on Hold, or on a Normal Press with no other triggers set.");
                row.Controls.Add(dd);
            }
            else
            {
                row.Controls.Add(Hint("Steps through the Active Target list under Global Features.", x, 50));
            }
        }

        if (showHold)
        {
            var holdNum = Numeric(0, 24, 100, 5000, v => { action.Duration = v; Apply(); }, increment: 50, width: 80);
            holdNum.Value = Math.Clamp(action.Duration ?? 500, 100, 5000);
            holdNum.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            holdNum.Location = new Point(row.Width - holdNum.Width - 12, 24);
            var holdLbl = SmallLabel("Hold Time (ms)", 0, 6);
            holdLbl.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            holdLbl.Location = new Point(row.Width - holdNum.Width - 12, 6);
            row.Controls.Add(holdLbl);
            row.Controls.Add(holdNum);

            if (ActionCatalog.IsRepeatable(kind, mode))
            {
                var interval = Numeric(0, 62, ActionCatalog.MinRepeatIntervalMs, ActionCatalog.MaxRepeatIntervalMs,
                    v => { action.RepeatIntervalMs = v; Apply(); }, increment: 10, width: 80);
                interval.Value = Math.Clamp(action.RepeatIntervalMs ?? ActionCatalog.DefaultRepeatIntervalMs,
                    ActionCatalog.MinRepeatIntervalMs, ActionCatalog.MaxRepeatIntervalMs);
                interval.Enabled = action.Repeat == true;
                interval.Anchor = AnchorStyles.Top | AnchorStyles.Right;
                interval.Location = new Point(row.Width - interval.Width - 12, 62);
                var intervalLbl = SmallLabel("Every (ms)", 0, 46);
                intervalLbl.Anchor = AnchorStyles.Top | AnchorStyles.Right;
                intervalLbl.Location = new Point(row.Width - interval.Width - 12, 46);

                var cbRepeat = new CheckBox
                {
                    Text = "Repeat",
                    Checked = action.Repeat == true,
                    AutoSize = false,
                    Size = new Size(74, 22),
                    Anchor = AnchorStyles.Top | AnchorStyles.Right,
                    Location = new Point(row.Width - interval.Width - 12 - 84, 63)
                };
                _tips.SetToolTip(cbRepeat, "Keep repeating this action for as long as the key is held, "
                    + "starting once the Hold Time is reached.");
                cbRepeat.CheckedChanged += (_, _) =>
                {
                    if (_updatingUi) return;
                    action.Repeat = cbRepeat.Checked ? true : null;
                    interval.Enabled = cbRepeat.Checked;
                    Apply();
                };
                row.Controls.Add(cbRepeat);
                row.Controls.Add(intervalLbl);
                row.Controls.Add(interval);
            }
        }

        return row;
    }

    private ComboBox TargetDropdown(List<ComboItem> items, string? selectedId, Point loc, Action<string?> onChange)
    {
        var dd = new NoWheelComboBox { Location = loc, Width = 180, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
        foreach (var item in items) dd.Items.Add(item);

        int idx = items.FindIndex(i => i.Id == selectedId);
        if (idx < 0 && selectedId is not null)
        {
            // Target currently unavailable (device unplugged / WL disconnected): show the raw id.
            dd.Items.Add(new ComboItem(selectedId, $"(unavailable) {selectedId}"));
            idx = dd.Items.Count - 1;
        }
        if (idx < 0 && items.Count > 0) idx = 0;
        if (idx >= 0) dd.SelectedIndex = idx;

        dd.SelectedIndexChanged += (_, _) =>
        {
            if (_updatingUi) return;
            if (dd.SelectedItem is ComboItem ci) onChange(ci.Id);
        };
        return dd;
    }

    // ─── Global Features ───────────────────────────────────────────────────────

    private Panel _featuresCard = null!;
    private Label? _activeTargetLabel;

    private Panel BuildFeaturesCard()
    {
        _featuresCard = Card(200);
        RebuildFeaturesCard();
        return _featuresCard;
    }

    /// <summary>
    /// Rebuilt whole whenever the target list or Wave Link's channels change, the
    /// same way the hotkey cards are, so every dropdown always reflects reality.
    /// </summary>
    private void RebuildFeaturesCard()
    {
        var card = _featuresCard;
        bool wasUpdatingUi = _updatingUi;
        _updatingUi = true;
        int y = 48;
        card.SuspendLayout();
        try
        {
            foreach (Control c in card.Controls.Cast<Control>().ToList())
                c.Dispose();
            card.Controls.Clear();

            var gf = _store.Current.GlobalFeatures;
            card.Controls.Add(new Label
            {
                Text = "Global Features",
                ForeColor = _t.Primary,
                Font = new Font("Segoe UI Semibold", 11.5f),
                Location = new Point(16, 12),
                AutoSize = true
            });

            AddFeatureRow("Volume keys (Vol+ / Vol- / Mute)", VolumeKeyModes, gf.VolumeKeys, v => gf.VolumeKeys = v);
            AddFeatureRow("Track keys (Next / Previous)", TrackKeyModes, gf.TrackKeys, v => gf.TrackKeys = v);
            AddFeatureRow("Play / Pause key", PlayPauseModes, gf.PlayPauseKey, v => gf.PlayPauseKey = v);
            card.Controls.Add(Hint("These only take over while Wave Link is connected; otherwise the keys work as normal. "
                + "A hotkey you've recorded for the same key takes priority.", 16, y + 2));
            y += 42;

            card.Controls.Add(new Label
            {
                Text = "Active Target list",
                Font = new Font("Segoe UI Semibold", 9.5f),
                Location = new Point(16, y),
                AutoSize = true
            });
            _activeTargetLabel = new Label { Location = new Point(160, y + 1), AutoSize = true, ForeColor = _t.Primary };
            card.Controls.Add(_activeTargetLabel);
            UpdateActiveTargetLabel();
            y += 24;
            card.Controls.Add(Hint("Hotkeys aimed at \"Active Target\", and the keys above, control the active entry. "
                + "Switch it with a Change Target action, or hold an entry's modifier while pressing the hotkey.", 16, y));
            y += 42;

            var targets = _store.Current.Targets;
            var channelItems = _wl.GetChannels().Select(c => new ComboItem(c.Id, c.Name)).ToList();
            for (int i = 0; i < targets.Count; i++)
            {
                var entry = targets[i];
                card.Controls.Add(new Label { Text = $"{i + 1}.", Location = new Point(16, y + 4), AutoSize = true });

                var ddChannel = TargetDropdown(channelItems, entry.ChannelId, new Point(40, y), id =>
                {
                    if (id is null) return;
                    entry.ChannelId = id;
                    if (entry.MixId is not null &&
                        _wl.GetChannelById(id)?.Mixes.All(m => m.Id != entry.MixId) != false)
                        entry.MixId = null;
                    Apply();
                    RebuildFeaturesCard();
                    RebuildHotkeyList();
                });
                card.Controls.Add(ddChannel);

                var mixItems = MixTargetChoices.Build(_wl.GetChannelById(entry.ChannelId), _wl.GetMixes(), entry.MixId)
                    .Select(m => new ComboItem(m.Id, m.Name)).ToList();
                var ddMix = TargetDropdown(mixItems, entry.MixId, new Point(230, y), id =>
                {
                    entry.MixId = id;
                    Apply();
                    RebuildHotkeyList();
                });
                ddMix.Width = 170;
                ddMix.Enabled = mixItems.Count > 1;
                card.Controls.Add(ddMix);

                card.Controls.Add(new Label { Text = "Hold", Location = new Point(412, y + 4), AutoSize = true, ForeColor = _t.Subtle });
                var ddModifier = new NoWheelComboBox { Location = new Point(448, y), Width = 90, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
                foreach (var (_, modLabel) in TargetModifierChoices) ddModifier.Items.Add(modLabel);
                ddModifier.SelectedIndex = Math.Max(0, Array.FindIndex(TargetModifierChoices, m => m.Value == (entry.Modifier ?? "")));
                ddModifier.SelectedIndexChanged += (_, _) =>
                {
                    if (_updatingUi) return;
                    string value = TargetModifierChoices[ddModifier.SelectedIndex].Value;
                    entry.Modifier = value.Length == 0 ? null : value;
                    Apply();
                };
                _tips.SetToolTip(ddModifier, "Hold this modifier with any \"Active Target\" hotkey (or a volume key in "
                    + "Global Features) to aim it at this entry for that press. If two entries share a modifier, the first wins.");
                card.Controls.Add(ddModifier);

                var remove = new Button
                {
                    Text = "Remove",
                    BackColor = _t.Danger,
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Location = new Point(552, y - 1),
                    Size = new Size(80, 26)
                };
                remove.FlatAppearance.BorderSize = 0;
                remove.Click += (_, _) =>
                {
                    _store.Current.Targets.Remove(entry);
                    Apply();
                    RebuildFeaturesCard();
                    RebuildHotkeyList();
                };
                card.Controls.Add(remove);
                y += 34;
            }

            var add = PrimaryButton("+  Add Target", 28);
            add.Width = 130;
            add.Location = new Point(16, y + 4);
            add.Click += (_, _) =>
            {
                _store.Current.Targets.Add(new TargetEntry
                {
                    Id = TargetState.NewId(),
                    ChannelId = _wl.GetChannels().FirstOrDefault()?.Id
                });
                Apply();
                RebuildFeaturesCard();
                RebuildHotkeyList();
            };
            card.Controls.Add(add);
            card.Height = y + 48;
        }
        finally
        {
            card.ResumeLayout();
            _updatingUi = wasUpdatingUi;
        }

        void AddFeatureRow(string text, (string Value, string Label)[] choices, string current, Action<string> set)
        {
            card.Controls.Add(new Label { Text = text, Location = new Point(16, y + 4), AutoSize = true });
            var dd = new NoWheelComboBox { Location = new Point(250, y), Width = 230, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
            foreach (var (_, label) in choices) dd.Items.Add(label);
            dd.SelectedIndex = Math.Max(0, Array.FindIndex(choices, c => c.Value == current));
            dd.SelectedIndexChanged += (_, _) =>
            {
                if (_updatingUi) return;
                set(choices[dd.SelectedIndex].Value);
                Apply();
            };
            card.Controls.Add(dd);
            y += 32;
        }
    }

    private void OnTargetsChanged()
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(UpdateActiveTargetLabel); } catch { }
    }

    private void UpdateActiveTargetLabel()
    {
        if (_activeTargetLabel is null || _activeTargetLabel.IsDisposed) return;
        var current = _targets.Current();
        _activeTargetLabel.Text = current is null ? "(no targets yet)" : $"Active now: {DescribeTarget(current)}";
    }

    /// <summary>"Browser · All Mixes", tolerant of channels or mixes that have gone away.</summary>
    private string DescribeTarget(TargetEntry entry)
    {
        string channel = _wl.GetChannelById(entry.ChannelId)?.Name
                         ?? (entry.ChannelId is null ? "(no channel)" : "(unavailable)");
        string mix = entry.MixId is null ? "All Mixes" : _wl.GetMixById(entry.MixId)?.Name ?? "(unavailable mix)";
        return $"{channel} · {mix}";
    }

    private Label Hint(string text, int x, int y) => new()
    {
        Text = text,
        Location = new Point(x, y),
        AutoSize = true,
        MaximumSize = new Size(720, 0),
        ForeColor = _t.Subtle,
        Font = new Font("Segoe UI", 9f, FontStyle.Italic)
    };

    // ─── Small control factories ───────────────────────────────────────────────

    private Panel Card(int height)
    {
        var card = new Panel { Height = height, BackColor = _t.Card, Padding = new Padding(16) };
        card.Paint += (_, e) =>
            e.Graphics.DrawRectangle(new Pen(_t.Border), 0, 0, card.Width - 1, card.Height - 1);
        return card;
    }

    private Button PrimaryButton(string text, int height)
    {
        var b = new Button
        {
            Text = text,
            BackColor = _t.Primary,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Height = height
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = _t.PrimaryHover;
        return b;
    }

    private CheckBox Check(string text, int x, int y, Action<bool> onChange)
    {
        var cb = new CheckBox { Text = text, Location = new Point(x, y), AutoSize = true };
        cb.CheckedChanged += (_, _) => { if (!_updatingUi) onChange(cb.Checked); };
        return cb;
    }

    private Label SmallLabel(string text, int x, int y) => new()
    {
        Text = text,
        Location = new Point(x, y),
        AutoSize = true,
        ForeColor = _t.Subtle,
        Font = new Font("Segoe UI Semibold", 7.75f)
    };

    private NumericUpDown Numeric(int x, int y, int min, int max, Action<int> onChange, int increment = 1, int width = 90)
    {
        var num = new NoWheelNumericUpDown
        {
            Location = new Point(x, y),
            Width = width,
            Minimum = min,
            Maximum = max,
            Increment = increment
        };
        num.ValueChanged += (_, _) => { if (!_updatingUi) onChange((int)num.Value); };
        return num;
    }
}
