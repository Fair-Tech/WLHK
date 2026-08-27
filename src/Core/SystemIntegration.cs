using System.Diagnostics;
using System.Security.Principal;
using System.Text;
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
            var proc = Process.Start(psi);
            Log.Write($"Elevation: relaunch started (pid {proc?.Id.ToString() ?? "?"})");
            return true;
        }
        catch (Exception ex)
        {
            // Win32Exception 1223: user cancelled the UAC prompt. At logon the
            // consent flow can also fail outright before the shell is ready.
            Log.Error("Elevation.RelaunchAsAdmin", ex);
            return false;
        }
    }
}

public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "Wave Link Hotkey Manager";
    private const string TaskName = "Wave Link Hotkey Manager";

    public enum Method { None, RunKey, ScheduledTask }

    /// <summary>Which mechanism (if any) currently starts the app at logon.</summary>
    public static Method Current()
    {
        if (TaskExists()) return Method.ScheduledTask;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            if (key?.GetValue(ValueName) is string) return Method.RunKey;
        }
        catch { }
        return Method.None;
    }

    public static bool IsRegistered() => Current() != Method.None;

    /// <summary>
    /// Register or unregister logon startup for the currently-running exe.
    ///
    /// Two mechanisms, because a plain HKCU\Run entry cannot start an elevated
    /// app: at logon the Run entry launches with a filtered token, and the
    /// self-relaunch through ShellExecute("runas") is unreliable that early in
    /// the session (the shell and AppInfo service may not be ready, and any
    /// consent UI has nowhere to display — the process then exits having started
    /// nothing). When elevation is wanted we instead register a scheduled task
    /// with RunLevel=HighestAvailable, which Windows starts elevated at logon
    /// with no prompt and no timing dependency.
    /// </summary>
    public static void Apply(bool enabled, bool wantElevated)
    {
        string exe = Environment.ProcessPath ?? Application.ExecutablePath;

        // "Run this program as an administrator" (the RUNASADMIN compatibility
        // layer) forces elevation regardless of our own setting, and Windows
        // silently skips Run-key entries that require elevation at logon. Detect
        // it so those installs get the scheduled task automatically.
        if (!wantElevated && HasRunAsAdminLayer(exe))
        {
            Log.Write("Autostart: exe has the RUNASADMIN compatibility flag; using the elevated task path.");
            wantElevated = true;
        }

        if (!enabled)
        {
            DeleteTask();
            SetRunKey(null);
            Log.Write($"Autostart disabled (method now {Current()})");
            return;
        }

        if (wantElevated && Elevation.IsAdmin && CreateTask(exe))
        {
            // The task supersedes the Run entry; keeping both would start it twice.
            SetRunKey(null);
            Log.Write($"Autostart enabled via scheduled task -> {exe}");
            return;
        }

        if (wantElevated && !Elevation.IsAdmin)
            Log.Write("Autostart: elevated task requested but process is not elevated; using Run key");

        DeleteTask();
        SetRunKey($"\"{exe}\"");
        Log.Write($"Autostart enabled via Run key -> {exe}");
    }

    /// <summary>True if this exe is flagged to always run elevated ("Run as administrator").</summary>
    public static bool HasRunAsAdminLayer(string exe)
    {
        const string layers = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var key = root.OpenSubKey(layers);
                if (key?.GetValue(exe) is string v &&
                    v.Contains("RUNASADMIN", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch { }
        }
        return false;
    }

    private static void SetRunKey(string? value)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (value is null)
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            else
                key.SetValue(ValueName, value);

            // Clear Windows' separate enable/disable bookkeeping (Task Manager's
            // Startup tab) so a re-created entry can't inherit a stale disabled flag.
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
            approved?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Log.Error("Autostart.SetRunKey", ex);
        }
    }

    // ─── Scheduled task ────────────────────────────────────────────────────────

    private static bool TaskExists() => RunSchTasks($"/Query /TN \"{TaskName}\"") == 0;

    private static bool CreateTask(string exe)
    {
        string? xmlPath = null;
        try
        {
            string user = WindowsIdentity.GetCurrent().Name;
            xmlPath = Path.Combine(Path.GetTempPath(), $"wlhk-task-{Guid.NewGuid():N}.xml");
            // schtasks /XML requires a Unicode-encoded file.
            File.WriteAllText(xmlPath, TaskXml(exe, user), Encoding.Unicode);

            int code = RunSchTasks($"/Create /TN \"{TaskName}\" /XML \"{xmlPath}\" /F");
            if (code != 0)
                Log.Write($"Autostart: schtasks /Create failed with exit code {code}");
            return code == 0;
        }
        catch (Exception ex)
        {
            Log.Error("Autostart.CreateTask", ex);
            return false;
        }
        finally
        {
            try { if (xmlPath is not null) File.Delete(xmlPath); } catch { }
        }
    }

    private static void DeleteTask()
    {
        if (TaskExists())
            RunSchTasks($"/Delete /TN \"{TaskName}\" /F");
    }

    private static int RunSchTasks(string args)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo("schtasks.exe", args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (proc is null) return -1;
            proc.WaitForExit(10000);
            return proc.HasExited ? proc.ExitCode : -1;
        }
        catch (Exception ex)
        {
            Log.Error("Autostart.RunSchTasks", ex);
            return -1;
        }
    }

    private static string TaskXml(string exe, string user)
    {
        string workDir = Path.GetDirectoryName(exe) ?? "";
        return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\n" +
            "<Task version=\"1.4\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\n" +
            "  <RegistrationInfo>\n" +
            "    <Author>FairTech</Author>\n" +
            "    <Description>Starts Wave Link Hotkey Manager at logon.</Description>\n" +
            "  </RegistrationInfo>\n" +
            "  <Triggers>\n" +
            "    <LogonTrigger>\n" +
            "      <Enabled>true</Enabled>\n" +
            $"      <UserId>{Escape(user)}</UserId>\n" +
            "      <Delay>PT10S</Delay>\n" +
            "    </LogonTrigger>\n" +
            "  </Triggers>\n" +
            "  <Principals>\n" +
            "    <Principal id=\"Author\">\n" +
            $"      <UserId>{Escape(user)}</UserId>\n" +
            "      <LogonType>InteractiveToken</LogonType>\n" +
            "      <RunLevel>HighestAvailable</RunLevel>\n" +
            "    </Principal>\n" +
            "  </Principals>\n" +
            "  <Settings>\n" +
            "    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\n" +
            "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\n" +
            "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\n" +
            "    <AllowHardTerminate>false</AllowHardTerminate>\n" +
            "    <StartWhenAvailable>true</StartWhenAvailable>\n" +
            "    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>\n" +
            "    <IdleSettings>\n" +
            "      <StopOnIdleEnd>false</StopOnIdleEnd>\n" +
            "      <RestartOnIdle>false</RestartOnIdle>\n" +
            "    </IdleSettings>\n" +
            "    <AllowStartOnDemand>true</AllowStartOnDemand>\n" +
            "    <Enabled>true</Enabled>\n" +
            "    <Hidden>false</Hidden>\n" +
            "    <RunOnlyIfIdle>false</RunOnlyIfIdle>\n" +
            "    <DisallowStartOnRemoteAppSession>false</DisallowStartOnRemoteAppSession>\n" +
            "    <UseUnifiedSchedulingEngine>true</UseUnifiedSchedulingEngine>\n" +
            "    <WakeToRun>false</WakeToRun>\n" +
            "    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\n" +
            "    <Priority>7</Priority>\n" +
            "  </Settings>\n" +
            "  <Actions Context=\"Author\">\n" +
            "    <Exec>\n" +
            $"      <Command>{Escape(exe)}</Command>\n" +
            $"      <WorkingDirectory>{Escape(workDir)}</WorkingDirectory>\n" +
            "    </Exec>\n" +
            "  </Actions>\n" +
            "</Task>\n";
    }

    private static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
