namespace Wlhk.UI;

/// <summary>
/// A clickable box painted like the dropdowns around it: same fill, no outline
/// in dark mode, a hover tint. A plain flat Button can't do this, because
/// WinForms dark mode draws its own light outline on flat buttons whatever
/// their FlatAppearance says, which made these stand out from everything else.
/// </summary>
internal sealed class InputButton : Control
{
    private readonly Theme _t;
    private bool _hover;

    /// <summary>Left-align the text (as in a dropdown) instead of centring it.</summary>
    public bool AlignLeft;

    public InputButton(Theme theme)
    {
        _t = theme;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        BackColor = theme.Input;
        ForeColor = theme.Text;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        using (var fill = new SolidBrush(Enabled && _hover ? _t.InputHover : BackColor))
            g.FillRectangle(fill, ClientRectangle);
        if (_t.InputBorder != _t.Input)
        {
            using var pen = new Pen(_t.InputBorder);
            g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }

        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine |
                    (AlignLeft ? TextFormatFlags.Left : TextFormatFlags.HorizontalCenter);
        var text = ClientRectangle;
        if (AlignLeft) text.Inflate(-4, 0);
        TextRenderer.DrawText(g, Text, Font, text, Enabled ? ForeColor : Color.FromArgb(110, _t.Subtle), flags);
    }
}
