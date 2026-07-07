using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;

namespace Wlhk.Core;

public static class Elevation
{
    private static bool? _isAdmin;

    /// <summary>In-process admin check (v1 spawned `net session` for this on every request).</summary>
    public static bool IsAdmin
    {
        get
        {
            _isAdmin ??= Compute();
            return _isAdmin.Value;
        }
    }

    private static bool Compute()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Relaunch this exe elevated via the shell "runas" verb.
    /// Returns true if the elevated process was started (caller should exit).
    /// Returns false if the user declined the UAC prompt or launch failed.
    /// </summary>
    public static bool RelaunchAsAdmin(string? extraArg = null)
    {
        string exe = Environment.ProcessPath ?? Application.ExecutablePath;
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory
            };
            if (extraArg is not null)
                psi.ArgumentList.Add(extraArg);
            Process.Start(psi);
            return true;
        }
        catch
        {
            // Win32Exception 1223: user cancelled the UAC prompt.
            return false;
        }
    }
}

public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Wave Link Hotkey Manager";

    /// <summary>
    /// HKCU Run entry (same location v1 used — visible in Task Manager's Startup tab).
    /// Always points at the real exe path; there is no temp-extraction path to get wrong anymore.
    /// </summary>
    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey);
            string exe = Environment.ProcessPath ?? Application.ExecutablePath;
            if (enabled)
            {
                key.SetValue(ValueName, $"\"{exe}\"");
            }
            else
            {
                // Only remove the entry if it points at *this* exe — never clobber
                // another install's startup entry (e.g. v1 coexisting during migration).
                if (key.GetValue(ValueName) is string current &&
                    current.Trim('"').Equals(exe, StringComparison.OrdinalIgnoreCase))
                {
                    key.DeleteValue(ValueName, throwOnMissingValue: false);
                }
            }
        }
        catch
        {
            // Registry access denied: non-fatal.
        }
    }
}
