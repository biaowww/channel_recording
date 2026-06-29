using System.Drawing;
using System.Windows.Forms;

namespace ChannelRecording;

/// <summary>覆盖整个虚拟桌面的半透明遮罩，让用户拖动框选一个屏幕区域。返回屏幕坐标矩形。</summary>
internal sealed class RegionSelector : Form
{
    private Point _start, _end;
    private bool _dragging;
    private Rectangle _result;

    public static Rectangle? Pick()
    {
        using var f = new RegionSelector();
        return f.ShowDialog() == DialogResult.OK ? f._result : null;
    }

    private RegionSelector()
    {
        var vb = ScreenCapture.VirtualBounds();
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;       // 不让 WinForms 再缩放，坐标按物理像素
        Bounds = vb;
        BackColor = Color.Black;
        Opacity = 0.35;
        TopMost = true;
        ShowInTaskbar = false;
        Cursor = Cursors.Cross;
        DoubleBuffered = true;
        KeyPreview = true;

        var hint = new Label
        {
            Text = "拖动框选要录制的区域，松开确认；Esc 取消",
            AutoSize = true,
            ForeColor = Color.White,
            BackColor = Color.FromArgb(40, 40, 40),
            Font = new Font("Microsoft YaHei UI", 11f),
            Location = new Point(24, 24),
        };
        Controls.Add(hint);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        _dragging = true;
        _start = _end = PointToScreen(e.Location);
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_dragging) return;
        _end = PointToScreen(e.Location);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        _result = FromPoints(_start, _end);
        if (_result.Width >= 8 && _result.Height >= 8) { DialogResult = DialogResult.OK; Close(); }
        else Invalidate();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (!_dragging) return;
        Rectangle client = RectangleToClient(FromPoints(_start, _end));
        using var fill = new SolidBrush(Color.FromArgb(60, 0, 150, 255));
        using var pen = new Pen(Color.FromArgb(255, 0, 170, 255), 2);
        e.Graphics.FillRectangle(fill, client);
        e.Graphics.DrawRectangle(pen, client);
    }

    private static Rectangle FromPoints(Point a, Point b)
        => Rectangle.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
}
