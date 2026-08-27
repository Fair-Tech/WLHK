using Wlhk.Core;

namespace Wlhk;

internal static class Program
{
    private const string MutexName = @"Local\WLHK_SingleInstance";
    private const string ShowConfigEventName = @"Local\WLHK_ShowConfig";

    [STAThread]
    private static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) Log.Error("UnhandledException", ex);
        };
        Application.ThreadException += (_, e) => Log.Error("ThreadException", e.Exception);

        try
        {
            Run(args);
        }
        catch (Exception ex)
        {
            Log.Error("Fatal", ex);
            throw;
        }
    }

    private static void Run(string[] args)
    {
        bool noElevate = args.Contains("--no-elevate", StringComparer.OrdinalIgnoreCase);

        Log.Write($"--- Launch --- exe={Environment.ProcessPath} args=[{string.Join(' ', args)}] " +
                  $"admin={Elevation.IsAdmin} session={Environment.UserName}");

        // Single instance: second launch pokes the first to open its config window, then exits.
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out bool isFirstInstance);
        var showConfigSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowConfigEventName);
        if (!isFirstInstance)
        {
            Log.Write("Another instance is running; signalling it to show the config window.");
            showConfigSignal.Set();
            return;
        }

        var store = new ConfigStore();
        store.Load();
        Log.Write($"Config loaded from {store.ConfigPath} (portable={store.IsPortable}); " +
                  $"autoElevate={store.Current.AutoElevate} startWithWindows={store.Current.StartWithWindows} " +
                  $"autostartMethod={Autostart.Current()}");

        // Windows silently skips Run-key entries that need elevation at logon, so
        // warn when the registered mechanism cannot actually start this install.
        bool forcedElevation = Autostart.HasRunAsAdminLayer(Environment.ProcessPath ?? "");
        if (store.Current.StartWithWindows && Autostart.Current() == Autostart.Method.RunKey
            && (store.Current.AutoElevate || forcedElevation))
        {
            Log.Write("WARNING: startup is registered via the Run key but this app starts elevated " +
                      "(autoElevate or RUNASADMIN), which Windows skips at logon. " +
                      "Re-toggle \"Start with Windows\" to switch to the scheduled task.");
        }

        // Auto-elevate before any subsystem starts. When the scheduled-task startup
        // path is used this is already satisfied at logon and no relaunch happens.
        if (store.Current.AutoElevate && !Elevation.IsAdmin && !noElevate)
        {
            Log.Write("Auto-elevate: requesting elevation...");
            // Release the mutex first so the elevated instance doesn't see us and quit.
            mutex.ReleaseMutex();
            mutex.Dispose();

            if (Elevation.RelaunchAsAdmin())
                return;

            // Elevation unavailable (declined, or the consent flow could not run —
            // common very early in the logon sequence). Keep running unelevated
            // rather than exiting with nothing started; hotkeys still work outside
            // elevated apps, and the config window shows the admin banner.
            Log.Write("Auto-elevate failed; continuing unelevated.");
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetColorMode(SystemColorMode.System); // WinForms dark mode (Win11+)

        Log.Write("Starting tray application.");
        Application.Run(new TrayApp(store, showConfigSignal));
        Log.Write("Exited cleanly.");
    }
}
