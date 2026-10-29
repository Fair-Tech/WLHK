using System.Runtime.InteropServices;

namespace Wlhk.UI;

/// <summary>
/// The config window is one long scrolling page full of dropdowns and number
/// boxes, and by default WinForms hands the mouse wheel to whichever of those the
/// pointer happens to be over. Scrolling down the page then silently changes
/// settings as they pass under the cursor.
///
/// These controls refuse the wheel and pass it to the nearest scrolling parent
/// instead, so the wheel always scrolls the page. An open dropdown list still
/// scrolls normally, since that is the one time the wheel is clearly meant for it.
/// </summary>
internal static class WheelForwarding
{
    internal const int WM_MOUSEWHEEL = 0x020A;

    [DllImport("user32.dll")]
    private static extern nint SendMessageW(nint hWnd, int msg, nint wParam, nint lParam);

    /// <summary>Send the wheel message to the nearest auto-scrolling ancestor. Always consumes it.</summary>
    internal static void Forward(Control from, ref Message m)
    {
        for (var parent = from.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent is ScrollableControl { AutoScroll: true } && parent.IsHandleCreated)
            {
                SendMessageW(parent.Handle, m.Msg, m.WParam, m.LParam);
                break;
            }
        }
        m.Result = 0;
    }
}

internal sealed class NoWheelComboBox : ComboBox
{
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WheelForwarding.WM_MOUSEWHEEL && !DroppedDown)
        {
            WheelForwarding.Forward(this, ref m);
            return;
        }
        base.WndProc(ref m);
    }
}

internal sealed class NoWheelNumericUpDown : NumericUpDown
{
    // NumericUpDown handles the wheel in OnMouseWheel on itself and on its inner
    // edit box, so intercepting the message alone misses half the cases. Marking
    // the event handled stops the value change; the page scroll is forwarded here.
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (e is HandledMouseEventArgs handled)
            handled.Handled = true;

        // Rebuild the wheel message and forward it, so the page scrolls by exactly
        // the amount it would anywhere else.
        var screen = PointToScreen(e.Location);
        nint wParam = (nint)((e.Delta & 0xFFFF) << 16);
        nint lParam = (nint)(((screen.Y & 0xFFFF) << 16) | (screen.X & 0xFFFF));
        var m = Message.Create(Handle, WheelForwarding.WM_MOUSEWHEEL, wParam, lParam);
        WheelForwarding.Forward(this, ref m);
    }
}
