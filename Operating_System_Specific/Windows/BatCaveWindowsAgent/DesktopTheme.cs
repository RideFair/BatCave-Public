using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace BatCave.WindowsAgent;

internal static class DesktopTheme
{
    public static readonly Color Background = Color.FromArgb(242, 244, 248);
    public static readonly Color Ink = Color.FromArgb(31, 38, 57);
    public static readonly Color Muted = Color.FromArgb(100, 110, 131);
    public static readonly Color Accent = Color.FromArgb(99, 79, 225);
    public static readonly Color Sidebar = Color.FromArgb(24, 29, 47);
    public static readonly Color Border = Color.FromArgb(222, 226, 235);
    public static readonly Color Green = Color.FromArgb(26, 123, 98);

    public static GraphicsPath Rounded(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

// Native Button behavior retains keyboard activation and UI Automation support.
internal class DesktopButton : Button
{
    private bool _hover, _pressed;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Primary { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Quiet { get; set; }

    public DesktopButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        AutoSize = true;
        Padding = new Padding(14, 8, 14, 8);
        MinimumSize = new Size(0, 42);
        Cursor = Cursors.Hand;
        Margin = new Padding(0, 0, 10, 0);
        UseVisualStyleBackColor = false;
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var text = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.SingleLine);
        return new Size(Math.Max(MinimumSize.Width, text.Width + Padding.Horizontal + 4),
            Math.Max(MinimumSize.Height, text.Height + Padding.Vertical + 4));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? DesktopTheme.Background);
        var fill = !Enabled ? Color.FromArgb(232, 235, 242) : Primary ? DesktopTheme.Accent
            : Quiet ? Parent?.BackColor ?? DesktopTheme.Background : Color.White;
        if (Enabled && (_hover || _pressed)) fill = Primary ? Color.FromArgb(79, 60, 200)
            : BackColor == DesktopTheme.Sidebar ? Color.FromArgb(49, 46, 79) : Color.FromArgb(234, 231, 253);
        using var path = DesktopTheme.Rounded(new RectangleF(1, 1, Width - 3, Height - 3), 8 * DeviceDpi / 96f);
        using var brush = new SolidBrush(fill);
        g.FillPath(brush, path);
        if (!Primary && !Quiet)
        {
            using var border = new Pen(DesktopTheme.Border);
            g.DrawPath(border, path);
        }
        var color = !Enabled ? DesktopTheme.Muted : Primary ? Color.White : DesktopTheme.Ink;
        if (Quiet && BackColor == DesktopTheme.Sidebar) color = Color.FromArgb(203, 210, 232);
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(ClientRectangle, -6, -6), color, fill);
    }
    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _pressed = e.Button == MouseButtons.Left; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
}

internal sealed class SetupStepButton(int step, string title, string description) : DesktopButton
{
    private bool _selected;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Selected { get => _selected; set { if (_selected != value) { _selected = value; Invalidate(); } } }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(DesktopTheme.Sidebar);
        var scale = DeviceDpi / 96f;
        if (Selected)
        {
            using var path = DesktopTheme.Rounded(new RectangleF(0, 0, Width - 1, Height - 1), 10 * scale);
            using var fill = new SolidBrush(Color.FromArgb(49, 46, 79));
            g.FillPath(fill, path);
        }
        var circle = new Rectangle((int)(12 * scale), (Height - (int)(28 * scale)) / 2, (int)(28 * scale), (int)(28 * scale));
        using var circleFill = new SolidBrush(Selected ? DesktopTheme.Accent : Color.FromArgb(44, 51, 72));
        g.FillEllipse(circleFill, circle);
        TextRenderer.DrawText(g, (step + 1).ToString(), Font, circle, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        var x = (int)(51 * scale);
        using var heading = new Font(Font, FontStyle.Bold);
        using var detail = new Font(Font.FontFamily, 8.5f);
        TextRenderer.DrawText(g, title, heading, new Rectangle(x, (int)(12 * scale), Width - x - 4, (int)(23 * scale)),
            Selected ? Color.White : Color.FromArgb(206, 213, 231), TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, description, detail, new Rectangle(x, (int)(36 * scale), Width - x - 4, (int)(21 * scale)),
            Color.FromArgb(151, 162, 187), TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(ClientRectangle, -4, -4));
    }
}

internal sealed class SetupPage : FlowLayoutPanel
{
    public SetupPage()
    {
        DoubleBuffered = true;
        Dock = DockStyle.Fill;
        BackColor = Color.White;
        Padding = new Padding(26, 22, 26, 22);
        FlowDirection = FlowDirection.TopDown;
        WrapContents = false;
        AutoScroll = true;
    }
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? DesktopTheme.Background);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = DesktopTheme.Rounded(new RectangleF(1, 1, Width - 3, Height - 3), 12 * DeviceDpi / 96f);
        using var fill = new SolidBrush(Color.White);
        using var border = new Pen(DesktopTheme.Border);
        e.Graphics.FillPath(fill, path); e.Graphics.DrawPath(border, path);
    }
    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        var width = Math.Max(100, ClientSize.Width - Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 8);
        foreach (Control child in Controls)
        {
            if (child is Label label) label.MaximumSize = new Size(width, 0);
            else if (child is FlowLayoutPanel or TableLayoutPanel or Panel) child.Width = width;
        }
    }
}
