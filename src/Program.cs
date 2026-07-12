using Wlhk.Core;

namespace Wlhk;

internal static class Program
{
    private const string MutexName = @"Local\WLHK_SingleInstance";
    private const string ShowConfigEventName = @"Local\WLHK_ShowConfig";

    [STAThread]
    private static void Main(string[] args)
    {
        bool noElevate = args.Contains("--no-elevate", StringComparer.OrdinalIgnoreCase);

        // Single instance: second launch pokes the first to open its config window, then exits.
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out bool isFirstInstance);
        var showConfigSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowConfigEventName);
        if (!isFirstInstance)
        {
            showConfigSignal.Set();
            return;
        }

        var store = new ConfigStore();
        store.Load();

        // Auto-elevate before any subsystem starts (v1 parity, minus v1's bug of
        // relaunching the temp-extracted exe). --no-elevate is the safety valve.
        if (store.Current.AutoElevate && !Elevation.IsAdmin && !noElevate)
        {
            // Release the mutex first so the elevated instance doesn't see us and quit.
            mutex.ReleaseMutex();
            mutex.Dispose();
            if (Elevation.RelaunchAsAdmin())
                return;
            // UAC declined: continue unelevated (re-acquire nothing; we're exiting the using scope
            // with a disposed mutex, so just run — worst case a second manual launch focuses us
            // via the event, which still exists).
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetColorMode(SystemColorMode.System); // WinForms dark mode (Win11+)

        Application.Run(new TrayApp(store, showConfigSignal));
    }
}
