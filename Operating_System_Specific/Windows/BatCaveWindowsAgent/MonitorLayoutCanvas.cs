using System.Drawing.Drawing2D;
using System.ComponentModel;
using BatCave.Universal.Topology;

namespace BatCave.WindowsAgent;

internal sealed class MonitorLayoutCanvas : Control
{
    private RectangleF _peerRect;
    private bool _dragging;
    private float _scale;
    private PointF _origin;
    private Point _dragStart;
    private Point2D _dragCenter;
    private float _dragScale;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public MonitorLayoutScene? Scene { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string PeerLabel { get; set; } = "Other computer";
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool PeerConnected { get; set; }
    public event Action<PeerSide, int>? PositionChanged;
    public MonitorLayoutCanvas()
    {
        DoubleBuffered = true;
        BackColor = Color.FromArgb(238, 243, 250);
        TabStop = true;
        AccessibleName = "Monitor arrangement";
        AccessibleDescription = "Drag the other computer around the selected monitor. Arrow keys choose a side.";
        AccessibleRole = AccessibleRole.Graphic;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var dot = new SolidBrush(Color.FromArgb(216, 225, 238));
        for (var x = 18; x < Width; x += 22)
            for (var y = 18; y < Height; y += 22)
            {
                g.FillEllipse(dot, x, y, 2, 2);
            }
        if (Scene is not { } scene)
        {
            TextRenderer.DrawText(g, "Choose an outside monitor edge to show the layout.", Font, ClientRectangle,
                Color.FromArgb(82, 100, 126), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            _peerRect = RectangleF.Empty;
            return;
        }
        var all = scene.LocalMonitors.Append(scene.Remote).Select(m => m.Bounds).ToArray();
        var left = all.Min(b => b.Left); var top = all.Min(b => b.Top);
        var width = all.Max(b => b.Right) - left + 1; var height = all.Max(b => b.Bottom) - top + 1;
        _scale = Math.Min((Width - 72f) / width, (Height - 84f) / height);
        _origin = new PointF((Width - width * _scale) / 2 - left * _scale, (Height - height * _scale) / 2 - top * _scale);
        RectangleF Rect(VirtualDesktopBounds b) => new(_origin.X + b.Left * _scale, _origin.Y + b.Top * _scale, b.Width * _scale, b.Height * _scale);
        foreach (var monitor in scene.LocalMonitors)
            DrawMonitor(g, Rect(monitor.Bounds), monitor.Label, monitor.Bounds, monitor.Id == scene.Local.Id, false);
        _peerRect = Rect(scene.Remote.Bounds);
        DrawMonitor(g, _peerRect, PeerLabel, scene.Remote.Bounds, false, true);
        using var seamPen = new Pen(Color.FromArgb(39, 176, 153), 5);
        foreach (var seam in scene.Seams)
        {
            var b = scene.Local.Bounds;
            var edge = scene.Side switch { PeerSide.Right => b.Right, PeerSide.Left => b.Left, PeerSide.Above => b.Top, _ => b.Bottom };
            PointF P(double axis) => scene.VerticalEdge
                ? new(_origin.X + edge * _scale, _origin.Y + (float)axis * _scale)
                : new(_origin.X + (float)axis * _scale, _origin.Y + edge * _scale);
            g.DrawLine(seamPen, P(seam.Start), P(seam.End));
        }
        TextRenderer.DrawText(g, "Green edge = cross here", Font, new Rectangle(14, Height - 31, Width - 28, 24),
            Color.FromArgb(53, 103, 105), TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        if (Focused) ControlPaint.DrawFocusRectangle(g, new Rectangle(3, 3, Width - 6, Height - 6));
    }
    private void DrawMonitor(Graphics g, RectangleF r, string title, VirtualDesktopBounds bounds, bool selected, bool remote)
    {
        using var path = Rounded(r, Math.Min(10, r.Height / 8));
        using var fill = new SolidBrush(remote ? Color.FromArgb(36, 54, 83) : Color.White);
        using var border = new Pen(selected ? Color.FromArgb(46, 116, 229) : Color.FromArgb(194, 208, 228), selected ? 3 : 1);
        g.FillPath(fill, path); g.DrawPath(border, path);
        var area = Rectangle.Round(r); area.Inflate(-8, -5);
        using var titleFont = new Font(Font.FontFamily, r.Width < 130 ? 9 : 11, FontStyle.Bold);
        var compact = r.Height < 75;
        var shortTitle = remote && !PeerConnected ? "Other computer" : title.Split('·')[0].Trim();
        TextRenderer.DrawText(g, shortTitle, titleFont, new Rectangle(area.Left, area.Top + area.Height / 2 - (compact ? 19 : 25), area.Width, compact ? 22 : 32),
            remote ? Color.White : Color.FromArgb(37, 52, 76), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        var subtitle = remote && !PeerConnected ? "Offline" : $"{bounds.Width} × {bounds.Height}";
        using var detailFont = new Font(Font.FontFamily, compact ? 8 : Font.Size);
        TextRenderer.DrawText(g, subtitle, detailFont, new Rectangle(area.Left, area.Top + area.Height / 2 + (compact ? 1 : 9), area.Width, compact ? 19 : 24),
            remote ? Color.FromArgb(184, 204, 231) : Color.FromArgb(107, 124, 149), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath(); var d = radius * 2;
        p.AddArc(r.Left, r.Top, d, d, 180, 90); p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure(); return p;
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e); Focus();
        if (e.Button == MouseButtons.Left && _peerRect.Contains(e.Location) && Scene is { } scene)
        {
            _dragging = true; Capture = true; _dragStart = e.Location; _dragScale = _scale;
            _dragCenter = new((scene.Remote.Bounds.Left + scene.Remote.Bounds.Right) / 2.0, (scene.Remote.Bounds.Top + scene.Remote.Bounds.Bottom) / 2.0);
        }
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Cursor = _peerRect.Contains(e.Location) || _dragging ? Cursors.Hand : Cursors.Default;
        if (!_dragging || Scene is not { } scene || _dragScale <= 0) return;
        var b = scene.Local.Bounds;
        var x = _dragCenter.X + (e.X - _dragStart.X) / _dragScale;
        var y = _dragCenter.Y + (e.Y - _dragStart.Y) / _dragScale;
        var dx = x - (b.Left + b.Right) / 2.0; var dy = y - (b.Top + b.Bottom) / 2.0;
        var side = Math.Abs(dx) / b.Width >= Math.Abs(dy) / b.Height
            ? (dx >= 0 ? PeerSide.Right : PeerSide.Left) : (dy >= 0 ? PeerSide.Below : PeerSide.Above);
        var vertical = side is PeerSide.Right or PeerSide.Left;
        var localLength = vertical ? b.Height : b.Width;
        var remoteLength = vertical ? scene.Remote.Bounds.Height : scene.Remote.Bounds.Width;
        var shift = Math.Max(1, ((localLength - 1) + (remoteLength - 1)) / 2.0 - Math.Min(48, Math.Min(localLength, remoteLength) / 2.0));
        PositionChanged?.Invoke(side, (int)Math.Round(Math.Clamp((vertical ? dy : dx) / shift * 100, -100, 100)));
    }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _dragging = false; Capture = false; }
    protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (!Capture) _dragging = false; }
    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        PeerSide? side = e.KeyCode switch { Keys.Left => PeerSide.Left, Keys.Right => PeerSide.Right, Keys.Up => PeerSide.Above, Keys.Down => PeerSide.Below, _ => null };
        if (side is { } value) { PositionChanged?.Invoke(value, 0); e.Handled = true; }
    }
}
