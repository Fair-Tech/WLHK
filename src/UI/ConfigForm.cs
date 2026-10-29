using static Wlhk.Core.ActionCatalog;
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

    // Target dropdown ids. Channels, mixes and outputs are prefixed so one list can
    // hold all of them; see EncodeTarget / DecodeTarget.
    private const string ActiveItem = "@active";
    private const string FocusedItem = "@focused";
    private const string AppVolumeItem = "@appvolume";
    private const string SystemItem = "@system";
    private const string MainOutputItem = "@mainoutput";

    private static readonly (string Value, string Label)[] VolumeKeyModes =
        { ("windows", "Windows default"), ("wavelink", "Control the Active Target") };
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
            _hook.CancelChordRecording();
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

        // Volume Keys and Active Target subsections, rebuilt as the target list changes.
        _globalCard = card;
        // Inset by a pixel so the panel doesn't paint over the card's border.
        _subsections = new Panel
        {
            Location = new Point(1, GeneralSectionHeight),
            Width = card.Width - 2,
            Height = 10,
            BackColor = _t.Card,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };
        card.Controls.Add(_subsections);
        RebuildFeaturesCard();

        return card;
    }

    private const int GeneralSectionHeight = 146;
    private Panel _globalCard = null!;
    private Panel _subsections = null!;

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
        bool changed = false;
        foreach (var binding in _store.Current.Hotkeys.Values)
            foreach (var action in new[] { binding.NormalAction, binding.HoldAction, binding.DoublePressAction })
                changed |= InitializeDefaults(action);
        return changed;
    }

    /// <summary>
    /// Fill in what the editor is about to show, so what's displayed is what runs.
    /// A dropdown showing its first item while the config holds nothing looks
    /// configured but silently does nothing.
    /// </summary>
    private bool InitializeDefaults(HotkeyAction? action)
    {
        if (action is null) return false;
        var (kind, mode) = Resolve(action);
        bool changed = false;

        bool wantsChannel = (IsScoped(kind) && ScopeOf(action) == ScopeChannel) || kind == RouteApp;
        if (wantsChannel && action.ChannelId is null && _wl.GetChannels().FirstOrDefault() is { } channel)
        {
            action.ChannelId = channel.Id;
            changed = true;
        }

        if (kind == Microphone && action.DeviceId is null && _wl.GetInputDevices().FirstOrDefault() is { } input)
        {
            action.DeviceId = input.Id;
            changed = true;
        }

        if (kind == Effect && action.EffectId is null && action.EffectName is null &&
            EffectChoices(action).FirstOrDefault() is { } fx)
        {
            (action.EffectId, action.EffectName) = (fx.Id, fx.Name);
            changed = true;
        }

        if (kind == Target && mode is "select" or "hold" && action.TargetId is null &&
            _store.Current.Targets.FirstOrDefault() is { } first)
        {
            action.TargetId = first.Id;
            changed = true;
        }
        return changed;
    }

    /// <summary>Switch an action to another kind, dropping fields that no longer apply.</summary>
    private void ChangeKind(HotkeyAction action, string kind)
    {
        action.Type = kind;
        action.Mode = ModesFor(kind).Length > 0 ? DefaultMode(kind) : null;
        action.UseActiveTarget = null;

        // Mute, Volume and Effect share a target, so moving between them keeps it,
        // except that effects only exist on channels.
        if (!IsScoped(kind)) action.Scope = null;
        if (kind == Effect && action.Scope is ScopeMix or ScopeOutput or ScopeMainOutput) action.Scope = null;
        if (!IsScoped(kind) && kind != RouteApp) action.ChannelId = null;
        if (!IsScoped(kind) || kind == Effect || ScopeOf(action) is not (ScopeChannel or ScopeMix)) action.MixId = null;
        bool keepsDevice = kind is SwitchOutput or CycleOutput || (IsScoped(kind) && ScopeOf(action) == ScopeOutput);
        if (!keepsDevice) action.DeviceId = null;
        if (kind is not (SwitchOutput or CycleOutput)) action.DeviceIds = null;
        if (kind != Effect) (action.EffectId, action.EffectName) = (null, null);
        if (kind != Target) action.TargetId = null;
        if (!IsRepeatable(kind, action.Mode ?? ""))
        {
            action.Repeat = null;
            action.RepeatIntervalMs = null;
        }
        // Steps and levels mean dB for gain and % elsewhere; don't carry them across.
        action.Step = null;
        action.Value = null;
        InitializeDefaults(action);
    }

    /// <summary>"db" for mic gain modes, "%" for everything else.</summary>
    private static string UnitOf(string kind, string mode) =>
        kind == Microphone && mode.StartsWith("gain_", StringComparison.Ordinal) ? "db" : "%";

    // Trigger row geometry. Row 1 holds the action, its mode and OSD; row 2 what
    // it applies to. L = small label, C = control, H = inline hint.
    private const int RowHeight = 96, RowPitch = 102;
    private const int L1 = 4, C1 = 20, L2 = 50, C2 = 66, H2 = 60;

    private Panel BuildHotkeyCard(string combo, HotkeyBinding binding)
    {
        var card = Card(52 + RowPitch * 3 + 12);

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
        card.Controls.Add(BuildTriggerRow(binding, "Normal Press", binding.NormalAction,
            a => binding.NormalAction = a, new Point(12, y), card.Width - 24, showHold: false));
        y += RowPitch;
        card.Controls.Add(BuildTriggerRow(binding, "Hold", binding.HoldAction,
            a => binding.HoldAction = a, new Point(12, y), card.Width - 24, showHold: true));
        y += RowPitch;
        card.Controls.Add(BuildTriggerRow(binding, "Double Press", binding.DoublePressAction,
            a => binding.DoublePressAction = a, new Point(12, y), card.Width - 24, showHold: false));

        return card;
    }

    private Panel BuildTriggerRow(HotkeyBinding binding, string label, HotkeyAction? action,
        Action<HotkeyAction?> setAction, Point loc, int width, bool showHold)
    {
        bool enabled = action is not null;
        var row = new Panel
        {
            Location = loc,
            Size = new Size(width, RowHeight),
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
            Location = new Point(12, 34),
            Width = 125
        };
        cb.CheckedChanged += (_, _) =>
        {
            if (_updatingUi) return;
            HotkeyAction? newAction = cb.Checked ? new HotkeyAction { Type = Mute, Mode = DefaultMode(Mute) } : null;
            InitializeDefaults(newAction);
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
                Location = new Point(150, 36),
                AutoSize = true
            });
            return row;
        }

        int x = 145;
        var (kind, mode) = Resolve(action!);

        // Row 1: what kind of action, its mode, and whether it shows the OSD.
        row.Controls.Add(SmallLabel("Action", x, L1));
        var ddKind = new NoWheelComboBox { Location = new Point(x, C1), Width = 170, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
        foreach (var (_, kindLabel) in Kinds) ddKind.Items.Add(kindLabel);
        ddKind.SelectedIndex = Math.Max(0, Array.FindIndex(Kinds, k => k.Kind == kind));
        ddKind.SelectedIndexChanged += (_, _) =>
        {
            if (_updatingUi) return;
            ChangeKind(action!, Kinds[ddKind.SelectedIndex].Kind);
            Apply();
            RebuildHotkeyList();
        };
        row.Controls.Add(ddKind);

        string trigger = label == "Normal Press" ? "normal" : showHold ? "hold" : "double";
        var modes = ModesFor(kind, trigger);
        // A mode no longer offered on this trigger stays listed while it's the one
        // selected, rather than silently changing what the key does.
        if (modes.Length > 0 && modes.All(m => m.Mode != mode) && ModesFor(kind).FirstOrDefault(m => m.Mode == mode) is { Mode: not null } kept)
            modes = [.. modes, kept];
        if (modes.Length > 0)
        {
            row.Controls.Add(SmallLabel("Mode", 325, L1));
            var ddMode = new NoWheelComboBox { Location = new Point(325, C1), Width = 150, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
            foreach (var (_, modeLabel) in modes) ddMode.Items.Add(modeLabel);
            ddMode.SelectedIndex = Math.Max(0, Array.FindIndex(modes, m => m.Mode == mode));
            ddMode.SelectedIndexChanged += (_, _) =>
            {
                if (_updatingUi) return;
                string newMode = modes[ddMode.SelectedIndex].Mode;
                if (UnitOf(kind, mode) != UnitOf(kind, newMode))
                {
                    action!.Step = null;
                    action.Value = null;
                }
                action!.Type = kind;
                action.Mode = newMode;
                if (!IsRepeatable(kind, newMode))
                {
                    action.Repeat = null;
                    action.RepeatIntervalMs = null;
                }
                InitializeDefaults(action);
                Apply();
                RebuildHotkeyList();
            };
            row.Controls.Add(ddMode);
        }

        var cbOsd = new CheckBox { Text = "OSD", Checked = action!.Osd != false, Location = new Point(490, C1 + 2), AutoSize = true };
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

        if (IsScoped(kind))
        {
            string scope = ScopeOf(action);
            bool effect = kind == Effect;

            row.Controls.Add(SmallLabel("Target", x, L2));
            var dd = TargetDropdown(TargetItems(includeActive: true, channelsOnly: effect),
                EncodeTarget(scope, action.ChannelId, action.MixId, action.DeviceId), new Point(x, C2), id =>
                {
                    if (id is null) return;
                    var (newScope, value) = DecodeTarget(id);
                    ApplyTarget(newScope, value,
                        sc => action.Scope = sc == ScopeChannel ? null : sc,
                        v => action.ChannelId = v,
                        () => action.MixId, v => action.MixId = v,
                        v => action.DeviceId = v);
                    action.UseActiveTarget = null;
                    if (effect)
                    {
                        // Keep the effect if the new channel has one by that name.
                        var match = EffectChoices(action).FirstOrDefault(f =>
                            string.Equals(f.Name, action.EffectName, StringComparison.OrdinalIgnoreCase));
                        (action.EffectId, action.EffectName) = match is null ? (null, null) : (match.Id, match.Name);
                    }
                    InitializeDefaults(action);
                    Apply();
                    RebuildHotkeyList();
                });
            dd.DropDownWidth = 320;
            _tips.SetToolTip(dd, "Active Target follows the active entry in Global Settings. "
                + "Focused App's Channel is the Wave Link channel of whichever app has focus; "
                + "Focused App's Volume is that app's own volume, as in the Windows Volume Mixer. "
                + "Native Volume Controls is the default Windows playback device. "
                + "Output: Current Device is whichever output is Wave Link's main output at the time.");
            row.Controls.Add(dd);
            x += 195;

            if (scope == ScopeChannel && !effect)
            {
                var mixes = MixTargetChoices.Build(_wl.GetChannelById(action.ChannelId), _wl.GetMixes(), action.MixId)
                    .Select(m => new ComboItem(m.Id, m.Name)).ToList();
                // Only "All Mixes" available: this channel belongs to no mix, so the
                // dropdown would be a no-op choice.
                if (mixes.Count > 1)
                {
                    row.Controls.Add(SmallLabel("Target Mix", x, L2));
                    var ddMix = TargetDropdown(mixes, action.MixId, new Point(x, C2), id => { action.MixId = id; Apply(); });
                    row.Controls.Add(ddMix);
                    x += 195;
                }
            }

            if (effect)
            {
                var choices = EffectChoices(action);
                row.Controls.Add(SmallLabel("Effect", x, L2));
                if (choices.Count == 0)
                {
                    row.Controls.Add(Hint("This target has no effects.", x, H2 + 4));
                }
                else
                {
                    var items = choices.Select(f => new ComboItem(f.Id.Length > 0 ? f.Id : "name:" + f.Name, f.Name)).ToList();
                    string? selected = action.EffectId ?? (action.EffectName is null ? null : "name:" + action.EffectName);
                    var ddFx = TargetDropdown(items, selected, new Point(x, C2), id =>
                    {
                        if (id is null) return;
                        var picked = items.First(i => i.Id == id);
                        action.EffectId = id.StartsWith("name:", StringComparison.Ordinal) ? null : id;
                        action.EffectName = picked.Name;
                        Apply();
                    });
                    row.Controls.Add(ddFx);
                }
            }

            if (kind == ActionCatalog.Volume)
                AddLevelBox(row, action, x, mode == "set");
        }
        else if (kind == Microphone)
        {
            var inputs = _wl.GetInputDevices().Select(d => new ComboItem(d.Id, d.Name)).ToList();
            row.Controls.Add(SmallLabel("Microphone", x, L2));
            row.Controls.Add(TargetDropdown(inputs, action.DeviceId, new Point(x, C2), id =>
            {
                action.DeviceId = id;
                Apply();
                RebuildHotkeyList(); // gain ranges differ between devices
            }));
            x += 195;

            var port = (_wl.GetInputDeviceById(action.DeviceId) ?? _wl.GetInputDevices().FirstOrDefault())?.Inputs.FirstOrDefault();
            if (mode is "gain_up" or "gain_down")
            {
                row.Controls.Add(SmallLabel("Step (dB)", x, L2));
                var num = Numeric(x, C2, 1, 20, v => { action.Step = v; Apply(); }, width: 70);
                num.Value = Math.Clamp(action.Step ?? 1, 1, 20);
                row.Controls.Add(num);
            }
            else if (mode == "gain_set")
            {
                int min = (int)Math.Floor(port?.GainMinDb ?? 0), max = (int)Math.Ceiling(port?.GainMaxDb ?? 40);
                if (max <= min) max = min + 40;
                int current = port?.Gain is double g ? (int)Math.Round(WaveLinkProtocol.GainToDb(port, g)) : min;
                row.Controls.Add(SmallLabel("Gain (dB)", x, L2));
                var num = Numeric(x, C2, min, max, v => { action.Value = v; Apply(); }, width: 70);
                num.Value = Math.Clamp(action.Value ?? current, min, max);
                row.Controls.Add(num);
            }
            else if (mode is "mix_up" or "mix_down" or "mix_set")
            {
                AddLevelBox(row, action, x, mode == "mix_set");
            }
            else if (port is not null && mode == "lock_toggle" && port.IsGainLockOn is null)
            {
                row.Controls.Add(Hint("This device has no gain lock.", x, H2));
            }
        }
        else if (kind == RouteApp)
        {
            var channels = _wl.GetChannels().Select(c => new ComboItem(c.Id, c.Name)).ToList();
            row.Controls.Add(SmallLabel("Move To Channel", x, L2));
            row.Controls.Add(TargetDropdown(channels, action.ChannelId, new Point(x, C2), id => { action.ChannelId = id; Apply(); }));
            row.Controls.Add(Hint("Routes the app Wave Link sees as focused.", x + 195, H2));
        }
        else if (kind == SwitchOutput)
        {
            row.Controls.Add(SmallLabel("Target Output", x, L2));
            var dd = TargetDropdown(outputs, action.DeviceId, new Point(x, C2), id => { action.DeviceId = id; Apply(); });
            row.Controls.Add(dd);
        }
        else if (kind == CycleOutput)
        {
            action.DeviceIds ??= new List<string>();
            while (action.DeviceIds.Count < 2)
                action.DeviceIds.Add(outputs.ElementAtOrDefault(action.DeviceIds.Count)?.Id ?? outputs.FirstOrDefault()?.Id ?? "");

            row.Controls.Add(SmallLabel("Target 1", x, L2));
            var dd1 = TargetDropdown(outputs, action.DeviceIds[0], new Point(x, C2), id =>
            {
                if (id is not null) action.DeviceIds[0] = id;
                Apply();
            });
            row.Controls.Add(dd1);
            x += 195;
            row.Controls.Add(SmallLabel("Target 2", x, L2));
            var dd2 = TargetDropdown(outputs, action.DeviceIds.Count > 1 ? action.DeviceIds[1] : null,
                new Point(x, C2), id =>
                {
                    if (id is not null) action.DeviceIds[1] = id;
                    Apply();
                });
            row.Controls.Add(dd2);
        }
        else if (kind == Media)
        {
            row.Controls.Add(Hint(mode.StartsWith("volume_", StringComparison.Ordinal)
                ? "Controls the Windows default output device."
                : "Sent to whichever app currently has media control.", x, H2));
        }
        else if (kind == Target)
        {
            var entries = _store.Current.Targets;
            if (entries.Count == 0)
            {
                row.Controls.Add(Hint("Add targets to the Active Target list in Global Settings first.", x, H2));
            }
            else if (mode is "select" or "hold")
            {
                var items = entries.Select((e, i) => new ComboItem(e.Id, $"{i + 1}. {DescribeTarget(e)}")).ToList();
                row.Controls.Add(SmallLabel("Target", x, L2));
                var dd = TargetDropdown(items, action.TargetId, new Point(x, C2), id => { action.TargetId = id; Apply(); });
                dd.Width = 300;
                if (mode == "hold")
                    _tips.SetToolTip(dd, "Aims at this target only while the key is held down. "
                        + "Use it on Hold, or on a Normal Press with no other triggers set.");
                row.Controls.Add(dd);
            }
            else
            {
                row.Controls.Add(Hint("Steps through the Active Target list in Global Settings.", x, H2));
            }
        }

        if (showHold)
        {
            var holdNum = Numeric(0, C1, 100, 5000, v => { action.Duration = v; Apply(); }, increment: 50, width: 80);
            holdNum.Value = Math.Clamp(action.Duration ?? 500, 100, 5000);
            holdNum.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            holdNum.Location = new Point(row.Width - holdNum.Width - 12, C1);
            var holdLbl = SmallLabel("Hold Time (ms)", 0, L1);
            holdLbl.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            holdLbl.Location = new Point(row.Width - holdNum.Width - 12, L1);
            row.Controls.Add(holdLbl);
            row.Controls.Add(holdNum);

            if (IsRepeatable(kind, mode))
            {
                // With Repeat on, the interval also decides when the press becomes a
                // hold (see HotkeyEngine.HoldThreshold), so Hold Time isn't used.
                holdNum.Enabled = action.Repeat != true;

                var interval = Numeric(0, C2, MinRepeatIntervalMs, MaxRepeatIntervalMs,
                    v => { action.RepeatIntervalMs = v; Apply(); }, increment: 10, width: 80);
                interval.Value = Math.Clamp(action.RepeatIntervalMs ?? DefaultRepeatIntervalMs,
                    MinRepeatIntervalMs, MaxRepeatIntervalMs);
                interval.Enabled = action.Repeat == true;
                interval.Anchor = AnchorStyles.Top | AnchorStyles.Right;
                interval.Location = new Point(row.Width - interval.Width - 12, C2);
                var intervalLbl = SmallLabel("Every (ms)", 0, L2);
                intervalLbl.Anchor = AnchorStyles.Top | AnchorStyles.Right;
                intervalLbl.Location = new Point(row.Width - interval.Width - 12, L2);

                var cbRepeat = new CheckBox
                {
                    Text = "Repeat",
                    Checked = action.Repeat == true,
                    AutoSize = false,
                    Size = new Size(74, 22),
                    Anchor = AnchorStyles.Top | AnchorStyles.Right,
                    Location = new Point(row.Width - interval.Width - 12 - 84, C2 + 1)
                };
                bool hasTap = binding.NormalAction is not null || binding.DoublePressAction is not null;
                _tips.SetToolTip(cbRepeat, "Keep repeating this action while the key is held. The first repeat comes "
                    + "after one interval" + (hasTap ? $" (at least {MinRepeatHoldThresholdMs} ms, so quick taps still count as taps)" : "")
                    + ", so Hold Time isn't used.");
                cbRepeat.CheckedChanged += (_, _) =>
                {
                    if (_updatingUi) return;
                    action.Repeat = cbRepeat.Checked ? true : null;
                    interval.Enabled = cbRepeat.Checked;
                    holdNum.Enabled = !cbRepeat.Checked;
                    Apply();
                };
                row.Controls.Add(cbRepeat);
                row.Controls.Add(intervalLbl);
                row.Controls.Add(interval);
            }
        }

        return row;
    }

    /// <summary>Step (%) for relative modes, Level (%) for absolute ones.</summary>
    private void AddLevelBox(Panel row, HotkeyAction action, int x, bool absolute)
    {
        if (absolute)
        {
            row.Controls.Add(SmallLabel("Level (%)", x, L2));
            var num = Numeric(x, C2, 0, 100, v => { action.Value = v; Apply(); }, width: 70);
            num.Value = Math.Clamp(action.Value ?? 50, 0, 100);
            row.Controls.Add(num);
        }
        else
        {
            row.Controls.Add(SmallLabel("Step (%)", x, L2));
            var num = Numeric(x, C2, 1, 50, v => { action.Step = v; Apply(); }, width: 70);
            num.Value = Math.Clamp(action.Step ?? _store.Current.VolumeStep, 1, 50);
            row.Controls.Add(num);
        }
    }

    // ─── Targets shared by actions and the Active Target list ──────────────────

    private List<ComboItem> TargetItems(bool includeActive, bool channelsOnly)
    {
        var items = new List<ComboItem>();
        if (includeActive) items.Add(new(ActiveItem, "Active Target"));
        items.Add(new(FocusedItem, "Focused App's Channel"));
        if (!channelsOnly)
        {
            items.Add(new(AppVolumeItem, "Focused App's Volume"));
            items.Add(new(SystemItem, "Native Volume Controls"));
        }
        items.AddRange(_wl.GetChannels().Select(c => new ComboItem("c|" + c.Id, c.Name)));
        if (!channelsOnly)
        {
            items.AddRange(_wl.GetMixes().Select(m => new ComboItem("m|" + m.Id, "Mix: " + m.Name)));
            // Ahead of the specific devices: follows whichever one is the main output.
            items.Add(new(MainOutputItem, "Output: Current Device"));
            items.AddRange(_wl.GetOutputDevices().Where(d => d.Outputs.Count > 0)
                .Select(d => new ComboItem("o|" + d.Id, "Output: " + d.Name)));
        }
        return items;
    }

    private static string? EncodeTarget(string scope, string? channelId, string? mixId, string? deviceId) => scope switch
    {
        ScopeActive => ActiveItem,
        ScopeFocusedApp => FocusedItem,
        ScopeAppVolume => AppVolumeItem,
        ScopeSystem => SystemItem,
        ScopeMainOutput => MainOutputItem,
        ScopeMix => mixId is null ? null : "m|" + mixId,
        ScopeOutput => deviceId is null ? null : "o|" + deviceId,
        _ => channelId is null ? null : "c|" + channelId
    };

    private static (string Scope, string? Id) DecodeTarget(string id)
    {
        if (id == ActiveItem) return (ScopeActive, null);
        if (id == FocusedItem) return (ScopeFocusedApp, null);
        if (id == AppVolumeItem) return (ScopeAppVolume, null);
        if (id == SystemItem) return (ScopeSystem, null);
        if (id == MainOutputItem) return (ScopeMainOutput, null);
        if (id.StartsWith("m|", StringComparison.Ordinal)) return (ScopeMix, id[2..]);
        if (id.StartsWith("o|", StringComparison.Ordinal)) return (ScopeOutput, id[2..]);
        if (id.StartsWith("c|", StringComparison.Ordinal)) return (ScopeChannel, id[2..]);
        return (ScopeChannel, id);
    }

    /// <summary>Write a picked target into an action or a list entry (they share the fields).</summary>
    private void ApplyTarget(string scope, string? id, Action<string> setScope,
        Action<string?> setChannel, Func<string?> getMix, Action<string?> setMix, Action<string?> setDevice)
    {
        setScope(scope);
        switch (scope)
        {
            case ScopeChannel:
                setChannel(id);
                setDevice(null);
                // A channel-in-mix target survives only if the new channel is in that mix.
                if (getMix() is string mix && _wl.GetChannelById(id)?.Mixes.All(m => m.Id != mix) != false)
                    setMix(null);
                break;
            case ScopeMix:
                setChannel(null);
                setMix(id);
                setDevice(null);
                break;
            case ScopeOutput:
                setChannel(null);
                setMix(null);
                setDevice(id);
                break;
            default:
                setChannel(null);
                setMix(null);
                setDevice(null);
                break;
        }
    }

    /// <summary>
    /// Effects to offer: the channel's own when it's a fixed channel, otherwise
    /// every effect name in use, matched by name when the action runs.
    /// </summary>
    private List<ChannelEffect> EffectChoices(HotkeyAction action)
    {
        if (ScopeOf(action) == ScopeChannel)
            return _wl.GetChannelById(action.ChannelId)?.Effects.ToList() ?? [];
        return _wl.GetChannels().SelectMany(c => c.Effects)
            .GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ChannelEffect { Id = "", Name = g.First().Name })
            .OrderBy(e => e.Name)
            .ToList();
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

    // ─── Global Settings subsections: Volume Keys, Active Target ───────────────

    private Label? _activeTargetLabel;

    /// <summary>
    /// Rebuilds the Volume Keys and Active Target subsections of Global Settings
    /// whenever the target list or Wave Link's channels change, the same way the
    /// hotkey cards are, so every dropdown always reflects reality.
    /// </summary>
    private void RebuildFeaturesCard()
    {
        var panel = _subsections;
        bool wasUpdatingUi = _updatingUi;
        _updatingUi = true;
        int y = 0;
        panel.SuspendLayout();
        try
        {
            foreach (Control c in panel.Controls.Cast<Control>().ToList())
                c.Dispose();
            panel.Controls.Clear();

            // Volume Keys
            AddDivider();
            AddSubheader("Volume Keys");
            panel.Controls.Add(new Label { Text = "Keyboard volume keys (Vol+ / Vol- / Mute)", Location = new Point(16, y + 4), AutoSize = true });
            var ddVolume = new NoWheelComboBox { Location = new Point(300, y), Width = 230, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
            foreach (var (_, text) in VolumeKeyModes) ddVolume.Items.Add(text);
            ddVolume.SelectedIndex = Math.Max(0, Array.FindIndex(VolumeKeyModes, m => m.Value == _store.Current.VolumeKeys));
            ddVolume.SelectedIndexChanged += (_, _) =>
            {
                if (_updatingUi) return;
                _store.Current.VolumeKeys = VolumeKeyModes[ddVolume.SelectedIndex].Value;
                Apply();
            };
            panel.Controls.Add(ddVolume);
            y += 34;
            panel.Controls.Add(Hint("Only takes over while Wave Link is connected; otherwise the keys work as normal. "
                + "A hotkey you've recorded for the same key takes priority.", 16, y));
            y += 44;

            // Active Target
            AddDivider();
            AddSubheader("Active Target");
            _activeTargetLabel = new Label { Location = new Point(130, y - 27), AutoSize = true, ForeColor = _t.Primary };
            panel.Controls.Add(_activeTargetLabel);
            UpdateActiveTargetLabel();

            var cbPersist = new CheckBox
            {
                Text = "Persistent Target",
                Checked = _store.Current.PersistentTarget,
                AutoSize = true,
                Location = new Point(16, y)
            };
            _tips.SetToolTip(cbPersist, "Remember the active target when WLHK restarts. "
                + "Off: it starts on the first entry each time.");
            cbPersist.CheckedChanged += (_, _) =>
            {
                if (_updatingUi) return;
                _store.Current.PersistentTarget = cbPersist.Checked;
                _store.Current.ActiveTargetId = cbPersist.Checked ? _targets.ActiveId : null;
                Apply();
            };
            panel.Controls.Add(cbPersist);
            y += 30;

            // Fallbacks for the two focused-app targets when there's nothing to adjust.
            AddFallback("If the focused app isn't in a channel, use", FocusedItem,
                () => _store.Current.FocusedAppFallback, fb => _store.Current.FocusedAppFallback = fb,
                "Used by \"Focused App's Channel\" when the focused app isn't routed to any "
                + "Wave Link channel. Focused App's Volume adjusts that app in the Windows Volume Mixer instead.");
            AddFallback("If the focused app has no audio, use", AppVolumeItem,
                () => _store.Current.FocusedAppNoAudioFallback, fb => _store.Current.FocusedAppNoAudioFallback = fb,
                "Used by \"Focused App's Volume\" when the focused app isn't playing any audio Windows can adjust "
                + "(the OSD would say \"No audio from this app\"), or nothing is focused.");

            panel.Controls.Add(Hint("Hotkeys aimed at \"Active Target\", and the volume keys above, control the active entry. "
                + "Switch it with a Change Target action, or hold an entry's Hold keys to aim at it temporarily. "
                + "Order sets Next / Previous and which Hold keys win when several match.", 16, y));
            y += 44;

            var targets = _store.Current.Targets;
            var targetItems = TargetItems(includeActive: false, channelsOnly: false);
            for (int i = 0; i < targets.Count; i++)
            {
                var entry = targets[i];
                string scope = ScopeOf(entry);
                panel.Controls.Add(new Label { Text = $"{i + 1}.", Location = new Point(16, y + 4), AutoSize = true });

                var ddTarget = TargetDropdown(targetItems, EncodeTarget(scope, entry.ChannelId, entry.MixId, entry.DeviceId),
                    new Point(40, y), id =>
                    {
                        if (id is null) return;
                        var (newScope, value) = DecodeTarget(id);
                        ApplyTarget(newScope, value,
                            sc => entry.Scope = sc == ScopeChannel ? null : sc,
                            v => entry.ChannelId = v,
                            () => entry.MixId, v => entry.MixId = v,
                            v => entry.DeviceId = v);
                        Apply();
                        RebuildFeaturesCard();
                        RebuildHotkeyList();
                    });
                ddTarget.DropDownWidth = 320;
                panel.Controls.Add(ddTarget);

                if (scope == ScopeChannel)
                {
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
                    panel.Controls.Add(ddMix);
                }

                int index = i;
                panel.Controls.Add(new Label { Text = "Hold", Location = new Point(412, y + 4), AutoSize = true, ForeColor = _t.Subtle });
                string chordText = EffectiveBindings.DescribeChord(entry.HoldKeys);
                var hold = InputButton(chordText, 446, y, 150);
                hold.AlignLeft = true;
                if (entry.HoldKeys is not { Count: > 0 }) hold.ForeColor = _t.Subtle;
                hold.Click += (_, _) =>
                {
                    hold.Text = "Press keys...";
                    hold.ForeColor = _t.Primary;
                    _hook.BeginChordRecording(keys =>
                    {
                        try
                        {
                            BeginInvoke(() =>
                            {
                                entry.HoldKeys = keys;
                                Apply();
                                RebuildFeaturesCard();
                            });
                        }
                        catch { }
                    });
                };
                _tips.SetToolTip(hold, chordText + ". A custom modifier: hold these keys while using an \"Active Target\" hotkey or a "
                    + "volume key, and it aims at this entry for as long as they're held. Click, then press the keys "
                    + "(e.g. Ctrl+Shift, or F13) and let go. Left and right modifiers count as different keys. "
                    + "Other keys used here stop typing on their own.");
                panel.Controls.Add(hold);

                var clear = SmallButton("✕", 600, y, 26, () =>
                {
                    entry.HoldKeys = null;
                    Apply();
                    RebuildFeaturesCard();
                });
                clear.Enabled = entry.HoldKeys is { Count: > 0 };
                _tips.SetToolTip(clear, "Clear the hold keys.");
                panel.Controls.Add(clear);

                var up = SmallButton("▲", 632, y, 26, () => MoveTarget(index, -1));
                up.Enabled = index > 0;
                _tips.SetToolTip(up, "Move up");
                panel.Controls.Add(up);
                var down = SmallButton("▼", 660, y, 26, () => MoveTarget(index, +1));
                down.Enabled = index < targets.Count - 1;
                _tips.SetToolTip(down, "Move down");
                panel.Controls.Add(down);

                var remove = new Button
                {
                    Text = "Remove",
                    BackColor = _t.Danger,
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Location = new Point(692, y - 1),
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
                panel.Controls.Add(remove);
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
            panel.Controls.Add(add);
            y += 44;

            panel.Height = y;
            _globalCard.Height = GeneralSectionHeight + y + 1;
        }
        finally
        {
            panel.ResumeLayout();
            _updatingUi = wasUpdatingUi;
        }

        void AddFallback(string label, string excludeItem, Func<TargetEntry?> get, Action<TargetEntry?> set, string tip)
        {
            panel.Controls.Add(new Label { Text = label, Location = new Point(16, y + 4), AutoSize = true });
            var items = new List<ComboItem> { new("", "Nothing (show a message)") };
            items.AddRange(TargetItems(includeActive: false, channelsOnly: false).Where(item => item.Id != excludeItem));
            var current = get();
            var dd = TargetDropdown(items,
                current is null ? "" : EncodeTarget(ScopeOf(current), current.ChannelId, current.MixId, current.DeviceId),
                new Point(300, y), id =>
                {
                    if (string.IsNullOrEmpty(id))
                    {
                        set(null);
                    }
                    else
                    {
                        var (scope, value) = DecodeTarget(id);
                        var entry = new TargetEntry();
                        ApplyTarget(scope, value, sc => entry.Scope = sc == ScopeChannel ? null : sc,
                            v => entry.ChannelId = v, () => entry.MixId, v => entry.MixId = v, v => entry.DeviceId = v);
                        set(entry);
                    }
                    Apply();
                });
            dd.Width = 230;
            dd.DropDownWidth = 320;
            _tips.SetToolTip(dd, tip);
            panel.Controls.Add(dd);
            y += 34;
        }

        void AddDivider()
        {
            panel.Controls.Add(new Panel
            {
                Location = new Point(16, y),
                Size = new Size(Math.Max(100, panel.Width - 32), 1),
                BackColor = _t.Border,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            });
            y += 12;
        }

        void AddSubheader(string text)
        {
            panel.Controls.Add(new Label
            {
                Text = text,
                ForeColor = _t.Primary,
                Font = new Font("Segoe UI Semibold", 10f),
                Location = new Point(16, y),
                AutoSize = true
            });
            y += 28;
        }
    }

    private void MoveTarget(int index, int delta)
    {
        var targets = _store.Current.Targets;
        int to = index + delta;
        if (to < 0 || to >= targets.Count) return;
        (targets[index], targets[to]) = (targets[to], targets[index]);
        Apply();
        RebuildFeaturesCard();
        RebuildHotkeyList();
    }

    private InputButton SmallButton(string text, int x, int y, int width, Action onClick)
    {
        var b = InputButton(text, x, y, width);
        b.Font = new Font("Segoe UI", 8f);
        b.Click += (_, _) => onClick();
        return b;
    }

    /// <summary>A box drawn like the dropdowns beside it, for controls that act as inputs.</summary>
    private InputButton InputButton(string text, int x, int y, int width) => new(_t)
    {
        Text = text,
        Location = new Point(x, y),
        Size = new Size(width, 23)
    };

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

    /// <summary>"Browser", "Browser · Stream Mix", "Mix: Stream Mix", ... tolerant of things that have gone away.</summary>
    private string DescribeTarget(TargetEntry entry) => ScopeOf(entry) switch
    {
        ScopeFocusedApp => "Focused App's Channel",
        ScopeAppVolume => "Focused App's Volume",
        ScopeSystem => "Native Volume Controls",
        ScopeMainOutput => "Output: Current Device",
        ScopeMix => $"Mix: {_wl.GetMixById(entry.MixId)?.Name ?? "(unavailable)"}",
        ScopeOutput => $"Output: {_wl.GetOutputDeviceById(entry.DeviceId)?.Name ?? "(unavailable)"}",
        _ => (_wl.GetChannelById(entry.ChannelId)?.Name ?? (entry.ChannelId is null ? "(no channel)" : "(unavailable)"))
             + (entry.MixId is null ? "" : " · " + (_wl.GetMixById(entry.MixId)?.Name ?? "(unavailable mix)"))
    };

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
