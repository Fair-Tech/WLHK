using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Wlhk.Core;

namespace Wlhk.UI;

/// <summary>
/// On-screen display: a per-pixel-alpha layered window (UpdateLayeredWindow),
/// always-on-top, click-through, never activated. Replicates the v1 Electron
/// OSD: dark rounded toast, 300x100 logical size, 9 anchor positions with 20 px
/// padding on the monitor under the cursor, slide-up fade-in, ~2 s visible.
/// Text mode (title + value) and slider mode (title + fill bar).
/// </summary>
public sealed class OsdForm : Form
{
    // Logical (96-dpi) metrics, scaled by the target monitor's DPI at render time.
    private const int BaseW = 300;
    private const int BaseH = 100;
    private const int ScreenPad = 20;
    private const int ToastMargin = 10;
    private const int FadeInMs = 180;
    private const int FadeOutMs = 220;
    private const int SlidePx = 20;

    private enum Phase { Hidden, In, Hold, Out }

    private Phase _phase = Phase.Hidden;
    private long _phaseStart;
    private Point _anchor;
    private Size _windowSize;
    private int _holdMs = 2000;

    // Native resources for the current toast bitmap
    private nint _memDc, _hBitmap, _oldBitmap;

    private readonly System.Windows.Forms.Timer _timer;
    private readonly ConfigStore _config;

    public OsdForm(ConfigStore config)
    {
        _config = config;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Bounds = new Rectangle(-10000, -10000, 1, 1); // parked offscreen until shown

        _timer = new System.Windows.Forms.Timer { Interval = 15 };
        _timer.Tick += (_, _) => Animate();

        _ = Handle; // force handle creation so ShowToast works before any Show()
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00080000  // WS_EX_LAYERED
                        | 0x00000080  // WS_EX_TOOLWINDOW (no alt-tab entry)
                        | 0x08000000  // WS_EX_NOACTIVATE
                        | 0x00000020  // WS_EX_TRANSPARENT (click-through)
                        | 0x00000008; // WS_EX_TOPMOST
            return cp;
        }
    }

    /// <summary>Show (or refresh) the toast. Must be called on the UI thread.</summary>
    public void ShowToast(string title, string? textValue, int? sliderPercent)
    {
        _holdMs = Math.Max(250, _config.Snapshot.OsdDurationMs);

        (Point anchor, float scale) = ComputePlacement();
        _anchor = anchor;
        RenderBitmap(title, textValue, sliderPercent, scale);

        bool alreadyVisible = _phase is Phase.In or Phase.Hold;
        _phaseStart = Environment.TickCount64;
        _phase = alreadyVisible ? Phase.Hold : Phase.In;

        ShowWindow(Handle, 4 /* SW_SHOWNOACTIVATE */);
        SetWindowPos(Handle, new nint(-1) /* HWND_TOPMOST */, _anchor.X, _anchor.Y, 0, 0,
            0x0001 | 0x0010 | 0x0040 /* NOSIZE | NOACTIVATE | SHOWWINDOW */);

        Push(alreadyVisible ? (byte)255 : (byte)0, alreadyVisible ? 0 : SlideOffset(0f));
        _timer.Start();
    }

    public void HideNow()
    {
        if (_phase is Phase.In or Phase.Hold)
        {
            _phase = Phase.Out;
            _phaseStart = Environment.TickCount64;
        }
    }

    private void Animate()
    {
        long elapsed = Environment.TickCount64 - _phaseStart;
        switch (_phase)
        {
            case Phase.In:
            {
                float t = Math.Min(1f, elapsed / (float)FadeInMs);
                Push((byte)(255 * EaseOut(t)), SlideOffset(t));
                if (t >= 1f) { _phase = Phase.Hold; _phaseStart = Environment.TickCount64; }
                break;
            }
            case Phase.Hold:
                if (elapsed >= _holdMs) { _phase = Phase.Out; _phaseStart = Environment.TickCount64; }
                break;

            case Phase.Out:
            {
                float t = Math.Min(1f, elapsed / (float)FadeOutMs);
                Push((byte)(255 * (1f - t)), SlideOffset(1f - t));
                if (t >= 1f)
                {
                    _phase = Phase.Hidden;
                    _timer.Stop();
                    ShowWindow(Handle, 0 /* SW_HIDE */);
                    FreeBitmap();
                }
                break;
            }
            default:
                _timer.Stop();
                break;
        }
    }

    private static float EaseOut(float t) => 1f - (1f - t) * (1f - t);

    private int SlideOffset(float t) => (int)(SlidePx * (1f - t) * (_windowSize.Height / (float)BaseH));

    /// <summary>Apply position + constant alpha to the layered window (bitmap unchanged).</summary>
    private void Push(byte alpha, int yOffset)
    {
        if (_memDc == 0) return;
        var pos = new NativePoint { X = _anchor.X, Y = _anchor.Y + yOffset };
        var size = new NativeSize { W = _windowSize.Width, H = _windowSize.Height };
        var src = new NativePoint { X = 0, Y = 0 };
        var blend = new BlendFunction { BlendOp = 0, SourceConstantAlpha = alpha, AlphaFormat = 1 /* AC_SRC_ALPHA */ };
        UpdateLayeredWindow(Handle, 0, ref pos, ref size, _memDc, ref src, 0, ref blend, 2 /* ULW_ALPHA */);
    }

    // ─── Placement ──────────────────────────────────────────────────────────────

    private (Point anchor, float scale) ComputePlacement()
    {
        GetCursorPos(out var cursor);
        nint monitor = MonitorFromPoint(cursor, 2 /* MONITOR_DEFAULTTONEAREST */);

        float scale = 1f;
        if (GetDpiForMonitor(monitor, 0, out uint dpiX, out _) == 0 && dpiX > 0)
            scale = dpiX / 96f;

        var mi = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        Rectangle work = GetMonitorInfoW(monitor, ref mi)
            ? Rectangle.FromLTRB(mi.WorkLeft, mi.WorkTop, mi.WorkRight, mi.WorkBottom)
            : Screen.PrimaryScreen!.WorkingArea;

        _windowSize = new Size((int)(BaseW * scale), (int)(BaseH * scale));
        int pad = (int)(ScreenPad * scale);

        string pos = _config.Snapshot.OsdPosition ?? "bottom-right";

        int x, y;
        if (pos.Contains("right")) x = work.Right - _windowSize.Width - pad;
        else if (pos.Contains("left")) x = work.Left + pad;
        else x = work.Left + (work.Width - _windowSize.Width) / 2;

        if (pos.Contains("bottom")) y = work.Bottom - _windowSize.Height - pad;
        else if (pos.Contains("top")) y = work.Top + pad;
        else y = work.Top + (work.Height - _windowSize.Height) / 2;

        return (new Point(x, y), scale);
    }

    // ─── Rendering ──────────────────────────────────────────────────────────────

    private void RenderBitmap(string title, string? textValue, int? sliderPercent, float s)
    {
        FreeBitmap();

        using var bmp = new Bitmap(_windowSize.Width, _windowSize.Height,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            int m = (int)(ToastMargin * s);
            var toast = new Rectangle(m, m, _windowSize.Width - 2 * m, _windowSize.Height - 2 * m);
            float radius = 8 * s;

            using (var path = RoundedRect(toast, radius))
            {
                using var bg = new SolidBrush(Color.FromArgb(209, 30, 30, 30));
                g.FillPath(bg, path);
                using var border = new Pen(Color.FromArgb(26, 255, 255, 255), Math.Max(1f, s));
                g.DrawPath(border, path);
            }

            int padX = (int)(20 * s);
            int padY = (int)(16 * s);
            float contentX = toast.X + padX;
            float contentW = toast.Width - 2 * padX;

            using var titleFont = new Font("Segoe UI", 14 * s, FontStyle.Bold, GraphicsUnit.Pixel);
            using var valueFont = new Font("Segoe UI", 18 * s, FontStyle.Regular, GraphicsUnit.Pixel);
            using var white = new SolidBrush(Color.White);

            var titleRect = new RectangleF(contentX, toast.Y + padY, contentW, 20 * s);
            using var fmt = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            g.DrawString(title, titleFont, white, titleRect, fmt);

            float contentY = toast.Y + padY + 26 * s;
            if (sliderPercent is int percent)
            {
                float trackH = 4 * s;
                float trackY = contentY + 8 * s;
                var track = new RectangleF(contentX, trackY, contentW, trackH);
                using var trackPath = RoundedRect(Rectangle.Round(track), trackH / 2);
                using var trackBrush = new SolidBrush(Color.FromArgb(51, 255, 255, 255));
                g.FillPath(trackBrush, trackPath);

                float fillW = contentW * Math.Clamp(percent, 0, 100) / 100f;
                if (fillW > 1)
                {
                    var fill = new RectangleF(contentX, trackY, fillW, trackH);
                    using var fillPath = RoundedRect(Rectangle.Round(fill), trackH / 2);
                    using var fillBrush = new SolidBrush(Color.FromArgb(0, 120, 212)); // Windows blue
                    g.FillPath(fillBrush, fillPath);
                }
            }
            else if (textValue is not null)
            {
                var valueRect = new RectangleF(contentX, contentY, contentW, 26 * s);
                g.DrawString(textValue, valueFont, white, valueRect, fmt);
            }
        }

        // Select the ARGB bitmap into a memory DC for UpdateLayeredWindow.
        nint screenDc = GetDC(0);
        _memDc = CreateCompatibleDC(screenDc);
        ReleaseDC(0, screenDc);
        _hBitmap = bmp.GetHbitmap(Color.FromArgb(0)); // preserves per-pixel alpha
        _oldBitmap = SelectObject(_memDc, _hBitmap);
    }

    private void FreeBitmap()
    {
        if (_memDc != 0)
        {
            if (_oldBitmap != 0) SelectObject(_memDc, _oldBitmap);
            DeleteDC(_memDc);
            _memDc = 0;
        }
        if (_hBitmap != 0)
        {
            DeleteObject(_hBitmap);
            _hBitmap = 0;
        }
        _oldBitmap = 0;
    }

    private static GraphicsPath RoundedRect(Rectangle r, float radius)
    {
        var path = new GraphicsPath();
        float d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            FreeBitmap();
        }
        base.Dispose(disposing);
    }

    // ─── Native interop ─────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeSize { public int W, H; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlendFunction
    {
        public byte BlendOp;             // AC_SRC_OVER = 0
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;         // AC_SRC_ALPHA = 1
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public int MonLeft, MonTop, MonRight, MonBottom;
        public int WorkLeft, WorkTop, WorkRight, WorkBottom;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern bool UpdateLayeredWindow(nint hwnd, nint hdcDst, ref NativePoint pptDst,
        ref NativeSize psize, nint hdcSrc, ref NativePoint pptSrc, int crKey, ref BlendFunction pblend, int dwFlags);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint pt);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(NativePoint pt, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfoW(nint hMonitor, ref MonitorInfo lpmi);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint hmonitor, int dpiType, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern nint GetDC(nint hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hWnd, nint hDC);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint hdc);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint hdc, nint hgdiobj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint hObject);
}
