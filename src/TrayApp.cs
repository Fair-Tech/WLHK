using Microsoft.Win32;
using Wlhk.Core;
using Wlhk.UI;
using Wlhk.WaveLink;

namespace Wlhk;

/// <summary>
/// Application root: wires the config store, keyboard hook, Wave Link client,
/// hotkey engine, OSD, tray icon and config window together. Runs as a tray-only
/// message loop (no main window).
/// </summary>
public sealed class TrayApp : ApplicationContext
{
    private readonly ConfigStore _store;
    private readonly KeyboardHook _hook;
    private readonly WaveLinkClient _wl;
    private readonly HotkeyEngine _engine;
    private readonly OsdForm _osd;
    private readonly NotifyIcon _tray;
    private readonly Icon _appIcon;
    private ConfigForm? _configForm;
    private readonly EventWaitHandle _showConfigSignal;
    private readonly TaskbarWatcher _taskbarWatcher;
    private readonly System.Windows.Forms.Timer _trayRetryTimer;
    private int _trayRetries;

    public TrayApp(ConfigStore store, EventWaitHandle showConfigSignal)
    {
        _store = store;
        _showConfigSignal = showConfigSignal;
        _appIcon = LoadAppIcon();

        // OSD form doubles as the UI-thread marshaler (its handle is created here,
        // on the thread that runs the message loop).
        _osd = new OsdForm(_store);

        _wl = new WaveLinkClient();
        _engine = new HotkeyEngine(_store, _wl, ShowOsd);
        _hook = new KeyboardHook();
        _hook.KeyEvent += _engine.OnKey;

        _tray = new NotifyIcon
        {
            Icon = _appIcon,
            Text = "Wave Link Hotkey Manager",
            Visible = true
        };
        _tray.MouseDoubleClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowConfig(); };
        RebuildTrayMenu();
        Log.Write("Tray icon created.");

        // At logon the notification area may not exist yet, and an icon added
        // before it does is dropped silently. Re-add on the shell's TaskbarCreated
        // broadcast, and re-assert a few times over the first half minute.
        _taskbarWatcher = new TaskbarWatcher();
        _taskbarWatcher.TaskbarCreated += () => OnUi(() =>
        {
            Log.Write("TaskbarCreated received; re-adding tray icon.");
            ReassertTrayIcon();
        });

        _trayRetryTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        _trayRetryTimer.Tick += (_, _) =>
        {
            if (++_trayRetries > 6)
            {
                _trayRetryTimer.Stop();
                return;
            }
            ReassertTrayIcon();
        };
        _trayRetryTimer.Start();

        _wl.Ready += () => OnUi(RebuildTrayMenu);
        _wl.Disconnected += () => OnUi(RebuildTrayMenu);
        _wl.ConnectionFailing += () => OnUi(() =>
        {
            _tray.BalloonTipIcon = ToolTipIcon.Warning;
            _tray.BalloonTipTitle = "Wave Link Hotkey Manager";
            _tray.BalloonTipText = "Could not connect to Wave Link. Make sure Wave Link is running — " +
                                   "retrying in the background, or click \"Reconnect to Wave Link\" in the tray menu.";
            _tray.ShowBalloonTip(5000);
        });

        // Reconnect promptly after wake from sleep (v1 parity).
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        // Second app instances signal this to pop the config window.
        var waitThread = new Thread(() =>
        {
            while (_showConfigSignal.WaitOne())
            {
                if (_disposed) return;
                OnUi(ShowConfig);
            }
        })
        { IsBackground = true, Name = "WLHK-ShowConfigSignal" };
        waitThread.Start();

        ApplyConfig();
        _hook.Start();
        _wl.Start();
    }

    private volatile bool _disposed;

    /// <summary>Force the shell to re-add our icon (no API exists to query whether it is present).</summary>
    private void ReassertTrayIcon()
    {
        if (_disposed) return;
        try
        {
            _tray.Visible = false;
            _tray.Visible = true;
        }
        catch (Exception ex)
        {
            Log.Error("ReassertTrayIcon", ex);
        }
    }

    private void OnUi(Action action)
    {
        if (_disposed) return;
        try { _osd.BeginInvoke(action); } catch { }
    }

    private void ShowOsd(string title, string? textValue, int? sliderPercent)
    {
        OnUi(() =>
        {
            if (_store.Snapshot.OsdEnabled)
                _osd.ShowToast(title, textValue, sliderPercent);
        });
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
            _wl.ManualReconnect();
    }

    /// <summary>Push current config into every subsystem. Called on load and after every save.</summary>
    private void ApplyConfig()
    {
        var c = _store.Snapshot;
        _hook.SetMappedCombos(c.Hotkeys.Keys);
        _hook.SetEnabled(c.HotkeysEnabled);
        _engine.Enabled = c.HotkeysEnabled;
        // Note: autostart is deliberately NOT touched here — the Run entry changes
        // only on an explicit "Start with Windows" toggle in the config window.
        RebuildTrayMenu();
    }

    // ─── Tray menu ─────────────────────────────────────────────────────────────

    private void RebuildTrayMenu()
    {
        var c = _store.Current;
        var menu = new ContextMenuStrip();

        var status = new ToolStripMenuItem(_wl.IsConnected ? "✓ Wave Link Connected" : "✗ Wave Link Disconnected")
        {
            Enabled = false
        };
        menu.Items.Add(status);
        menu.Items.Add("Reconnect to Wave Link", null, (_, _) => _wl.ManualReconnect());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Configure Hotkeys", null, (_, _) => ShowConfig());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(c.HotkeysEnabled ? "Disable Hotkeys" : "Enable Hotkeys", null, (_, _) =>
        {
            _store.Current.HotkeysEnabled = !_store.Current.HotkeysEnabled;
            _store.Save();
            ApplyConfig();
            _configForm?.SyncFromConfig();
        });
        menu.Items.Add(c.OsdEnabled ? "Disable OSD" : "Enable OSD", null, (_, _) =>
        {
            _store.Current.OsdEnabled = !_store.Current.OsdEnabled;
            _store.Save();
            ApplyConfig();
            _configForm?.SyncFromConfig();
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Quit());

        var old = _tray.ContextMenuStrip;
        _tray.ContextMenuStrip = menu;
        old?.Dispose();
    }

    // ─── Config window ─────────────────────────────────────────────────────────

    private void ShowConfig()
    {
        if (_configForm is { IsDisposed: false })
        {
            if (_configForm.WindowState == FormWindowState.Minimized)
                _configForm.WindowState = FormWindowState.Normal;
            _configForm.Activate();
            return;
        }

        _configForm = new ConfigForm(
            _store, _wl, _hook,
            onConfigApplied: ApplyConfig,
            onReconnect: _wl.ManualReconnect,
            onRelaunchAdmin: RelaunchAsAdmin,
            appIcon: _appIcon);
        _configForm.FormClosed += (_, _) => _configForm = null;
        _configForm.Show();
    }

    private void RelaunchAsAdmin()
    {
        if (Elevation.RelaunchAsAdmin())
            Quit();
    }

    private void Quit()
    {
        if (_disposed) return;
        _disposed = true;

        Log.Write("Quit requested.");
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _trayRetryTimer.Stop();
        _trayRetryTimer.Dispose();
        _taskbarWatcher.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _hook.Dispose();
        _wl.Dispose();
        _configForm?.Close();
        _osd.Dispose();
        ExitThread();
    }

    private static Icon LoadAppIcon()
    {
        try
        {
            using var stream = typeof(TrayApp).Assembly.GetManifestResourceStream("WLHK.ico");
            if (stream is not null)
                return new Icon(stream);
        }
        catch { }
        return SystemIcons.Application;
    }
}
