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
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "Wave Link Hotkey Manager";

    /// <summary>The Run entry exists (regardless of which exe path it points at).</summary>
    public static bool IsRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
        catch { return false; }
    }

    /// <summary>
    /// HKCU Run entry (same location v1 used — visible in Task Manager's Startup tab).
    /// Called only on an explicit user toggle, never automatically at launch:
    /// enable registers the currently-running exe wherever it lives, disable removes
    /// the entry outright. If the exe is moved, disable + re-enable re-registers it.
    /// </summary>
    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                string exe = Environment.ProcessPath ?? Application.ExecutablePath;
                key.SetValue(ValueName, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            // Reset Windows' separate enabled/disabled bookkeeping (Task Manager's
            // Startup tab) so a re-created entry can't inherit a stale "disabled" state.
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
            approved?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch
        {
            // Registry access denied: non-fatal.
        }
    }
}
