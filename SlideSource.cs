using System.Drawing;

namespace ChannelRecording;

internal enum SlideSourceKind { Monitor, Window, Region }

/// <summary>投屏抓取源的描述（显示器 / 窗口 / 框选区域）。由 GUI/CLI 构造，RecordingSession 据此建 WGC 捕获。</summary>
internal sealed class SlideSource
{
    public SlideSourceKind Kind;
    public IntPtr Hwnd;              // Window
    public IntPtr Hmon;             // Monitor / Region 所在显示器
    public Rectangle MonitorBounds; // Region 裁剪用（该显示器物理边界，虚拟桌面坐标）
    public Rectangle Region;        // Region（虚拟桌面坐标）

    public static SlideSource FromMonitor(IntPtr hmon, Rectangle bounds)
        => new() { Kind = SlideSourceKind.Monitor, Hmon = hmon, MonitorBounds = bounds };

    public static SlideSource FromWindow(IntPtr hwnd)
        => new() { Kind = SlideSourceKind.Window, Hwnd = hwnd };

    public static SlideSource FromRegion(Rectangle region, IntPtr hmon, Rectangle monitorBounds)
        => new() { Kind = SlideSourceKind.Region, Region = region, Hmon = hmon, MonitorBounds = monitorBounds };
}
