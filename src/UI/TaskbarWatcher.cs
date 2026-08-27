using System.Runtime.InteropServices;

namespace Wlhk.UI;

/// <summary>
/// Watches for the shell's "TaskbarCreated" broadcast so the tray icon can be
/// re-added. This matters at logon: startup apps commonly launch before Explorer
/// has created the notification area, and an icon added too early is silently
/// dropped — the app is running but invisible with no way to reach it.
/// (Broadcast messages only reach real top-level windows, so this deliberately
/// is not a message-only window.)
/// </summary>
public sealed class TaskbarWatcher : NativeWindow, IDisposable
{
    private const int WS_POPUP = unchecked((int)0x80000000);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string lpString);

    private static readonly uint WM_TASKBARCREATED = RegisterWindowMessageW("TaskbarCreated");

    public event Action? TaskbarCreated;

    public TaskbarWatcher()
    {
        CreateHandle(new CreateParams
        {
            Caption = "WLHK.TaskbarWatcher",
            Style = WS_POPUP,
            X = -10000,
            Y = -10000,
            Width = 1,
            Height = 1
        });
    }

    protected override void WndProc(ref Message m)
    {
        if (WM_TASKBARCREATED != 0 && (uint)m.Msg == WM_TASKBARCREATED)
            TaskbarCreated?.Invoke();
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (Handle != 0)
            DestroyHandle();
    }
}
