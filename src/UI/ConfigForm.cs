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

    private static readonly (string Type, string Label)[] ActionTypes =
    {
        ("mute_channel", "Toggle Mute"),
        ("volume_up_channel", "Volume Up"),
        ("volume_down_channel", "Volume Down"),
        ("set_volume", "Set Volume"),
        ("switch_output", "Switch Output Device"),
        ("cycle_output", "Cycle Output Devices"),
    };

    public ConfigForm(ConfigStore store, WaveLinkClient wl, KeyboardHook hook,
        Action onConfigApplied, Action onReconnect, Action onRelaunchAdmin, Icon appIcon)
    {
        _store = store;
        _wl = wl;
        _hook = hook;
        _onConfigApplied = onConfigApplied;
        _onReconnect = onReconnect;
        _onRelaunchAdmin = onRelaunchAdmin;

        Text = "Wave Link Hotkey Manager";
        Icon = appIcon;
        ClientSize = new Size(860, 640);
        MinimumSize = new Size(720, 480);
        BackColor = _t.Bg;
        ForeColor = _t.Text;
        Font = new Font("Segoe UI", 9.5f);
        DoubleBuffered = true;

        BuildLayout();
        RefreshAll();

        _wl.StateChanged += OnWlChanged;
        _wl.Ready += OnWlChanged;
        _wl.Disconnected += OnWlChanged;
        FormClosed += (_, _) =>
        {
            _wl.StateChanged -= OnWlChanged;
            _wl.Ready -= OnWlChanged;
            _wl.Disconnected -= OnWlChanged;
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
                    try { RebuildHotkeyList(); }
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
            AutoSize = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        card.Controls.Add(_statusBadge);
        card.Resize += (_, _) => _statusBadge.Location = new Point(card.Width - _statusBadge.Width - 16, 14);

        // Row 1: toggle checkboxes
        int y1 = 48;
        _cbHotkeys = Check("Enable Hotkeys", 16, y1, (v) => { _store.Current.HotkeysEnabled = v; Apply(); });
        _cbOsd = Check("Enable OSD", 150, y1, (v) => { _store.Current.OsdEnabled = v; Apply(); });
        _cbElevate = Check("Auto-Elevate to Admin on Start", 268, y1, (v) => { _store.Current.AutoElevate = v; Apply(); });
        _cbStartup = Check("Start with Windows", 495, y1, (v) =>
        {
            _store.Current.StartWithWindows = v;
            Autostart.Apply(v); // registry changes only on explicit toggle
            Apply();
        });
        card.Controls.AddRange(new Control[] { _cbHotkeys, _cbOsd, _cbElevate, _cbStartup });

        // Row 2: OSD position, volume step, OSD duration, reconnect
        int labY = 84, ctlY = 102;
        card.Controls.Add(SmallLabel("OSD Position", 16, labY));
        _ddOsdPos = new ComboBox { Location = new Point(16, ctlY), Width = 150, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
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

        var reconnect = PrimaryButton("⟳  Reconnect to Wave Link", 30);
        reconnect.Width = 190;
        reconnect.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        reconnect.Location = new Point(card.Width - reconnect.Width - 16, ctlY - 2);
        reconnect.Click += (_, _) => _onReconnect();
        card.Controls.Add(reconnect);
        card.Resize += (_, _) => reconnect.Location = new Point(card.Width - reconnect.Width - 16, ctlY - 2);

        return card;
    }

    private CheckBox _cbHotkeys = null!, _cbOsd = null!, _cbElevate = null!, _cbStartup = null!;
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

        _hook.BeginRecording(combo =>
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

    private Panel BuildHotkeyCard(string combo, HotkeyBinding binding)
    {
        const int rowH = 104;
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
            Size = new Size(width, 98),
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
            Location = new Point(12, 27),
            Width = 125
        };
        cb.CheckedChanged += (_, _) =>
        {
            if (_updatingUi) return;
            setAction(cb.Checked ? new HotkeyAction { Type = "mute_channel" } : null);
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

        // Action type
        row.Controls.Add(SmallLabel("Action", x, 6));
        var ddType = new ComboBox { Location = new Point(x, 24), Width = 165, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
        foreach (var (_, lbl2) in ActionTypes) ddType.Items.Add(lbl2);
        ddType.SelectedIndex = Math.Max(0, Array.FindIndex(ActionTypes, a => a.Type == action!.Type));
        ddType.SelectedIndexChanged += (_, _) =>
        {
            if (_updatingUi) return;
            action!.Type = ActionTypes[ddType.SelectedIndex].Type;
            // Output actions cannot retain either half of a channel/mix target.
            // Channel actions deliberately preserve both IDs when switching types.
            if (action.Type is "switch_output" or "cycle_output")
            {
                action.ChannelId = null;
                action.MixId = null;
            }
            else { action.DeviceId = null; action.DeviceIds = null; }
            Apply();
            RebuildHotkeyList();
        };
        row.Controls.Add(ddType);
        // Targets
        var channels = _wl.GetChannels().Select(c => new ComboItem(c.Id, c.Name)).ToList();
        var outputs = _wl.GetOutputDevices().Select(d => new ComboItem(d.Id, d.Name)).ToList();

        if (action!.Type is "mute_channel" or "volume_up_channel" or "volume_down_channel" or "set_volume")
        {
            row.Controls.Add(SmallLabel("Target Channel", x, 50));
            var dd = TargetDropdown(channels, action.ChannelId, new Point(x, 68), id =>
            {
                if (id is null) return;
                action.ChannelId = id;
                if (action.MixId is not null &&
                    _wl.GetChannelById(id)?.Mixes.All(m => m.Id != action.MixId) != false)
                    action.MixId = null;
                Apply();
                RebuildHotkeyList();
            });
            row.Controls.Add(dd);
            x += 195;

            var mixes = MixTargetChoices.Build(_wl.GetChannelById(action.ChannelId), _wl.GetMixes(), action.MixId)
                .Select(m => new ComboItem(m.Id, m.Name)).ToList();
            row.Controls.Add(SmallLabel("Target Mix", x, 50));
            var ddMix = TargetDropdown(mixes, action.MixId, new Point(x, 68), id => { action.MixId = id; Apply(); });
            row.Controls.Add(ddMix);
            x += 195;

            if (action.Type is "volume_up_channel" or "volume_down_channel")
            {
                row.Controls.Add(SmallLabel("Step (%)", x, 50));
                var num = Numeric(x, 68, 1, 50, v => { action.Step = v; Apply(); }, width: 70);
                num.Value = Math.Clamp(action.Step ?? _store.Current.VolumeStep, 1, 50);
                row.Controls.Add(num);
            }
            else if (action.Type == "set_volume")
            {
                row.Controls.Add(SmallLabel("Level (%)", x, 50));
                var num = Numeric(x, 68, 0, 100, v => { action.Value = v; Apply(); }, width: 70);
                num.Value = Math.Clamp(action.Value ?? 50, 0, 100);
                row.Controls.Add(num);
            }
        }
        else if (action.Type == "switch_output")
        {
            row.Controls.Add(SmallLabel("Target Output", x, 50));
            var dd = TargetDropdown(outputs, action.DeviceId, new Point(x, 68), id => { action.DeviceId = id; Apply(); });
            row.Controls.Add(dd);
        }
        else if (action.Type == "cycle_output")
        {
            action.DeviceIds ??= new List<string>();
            while (action.DeviceIds.Count < 2)
                action.DeviceIds.Add(outputs.ElementAtOrDefault(action.DeviceIds.Count)?.Id ?? outputs.FirstOrDefault()?.Id ?? "");

            row.Controls.Add(SmallLabel("Target 1", x, 50));
            var dd1 = TargetDropdown(outputs, action.DeviceIds[0], new Point(x, 68), id =>
            {
                if (id is not null) action.DeviceIds[0] = id;
                Apply();
            });
            row.Controls.Add(dd1);
            x += 195;
            row.Controls.Add(SmallLabel("Target 2", x, 50));
            var dd2 = TargetDropdown(outputs, action.DeviceIds.Count > 1 ? action.DeviceIds[1] : null,
                new Point(x, 68), id =>
                {
                    if (id is not null) action.DeviceIds[1] = id;
                    Apply();
                });
            row.Controls.Add(dd2);
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
        }

        return row;
    }

    private ComboBox TargetDropdown(List<ComboItem> items, string? selectedId, Point loc, Action<string?> onChange)
    {
        var dd = new ComboBox { Location = loc, Width = 180, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
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
        var num = new NumericUpDown
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
