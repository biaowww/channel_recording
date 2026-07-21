using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace ChannelRecording;

internal readonly record struct MonitorInfo(int Index, Rectangle Bounds, bool Primary, IntPtr Hmon);
internal readonly record struct WindowInfo(IntPtr Hwnd, uint Pid, string Process, string Title);

/// <summary>用 GDI/System.Drawing 抓屏。支持整屏、指定显示器、或自定义区域。</summary>
internal static class ScreenCapture
{
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
    private const int SM_CXSCREEN = 0, SM_CYSCREEN = 1;

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    private delegate bool MonitorEnumProc(IntPtr hMon, IntPtr hdc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFO info);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int max);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT val, int size);
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; }

    /// <summary>尽早调用：让进程按物理像素抓屏，高 DPI 下截图才清晰、坐标才准。</summary>
    public static void EnableDpiAwareness()
    {
        try { SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); }
        catch { /* 老系统忽略 */ }
    }

    public static List<MonitorInfo> Monitors()
    {
        var list = new List<MonitorInfo>();
        int idx = 0;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr dc, ref RECT r, IntPtr d) =>
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            bool primary = false;
            var bounds = new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
            if (GetMonitorInfo(h, ref mi))
            {
                bounds = new Rectangle(mi.rcMonitor.Left, mi.rcMonitor.Top,
                    mi.rcMonitor.Right - mi.rcMonitor.Left, mi.rcMonitor.Bottom - mi.rcMonitor.Top);
                primary = (mi.dwFlags & 1) != 0; // MONITORINFOF_PRIMARY
            }
            list.Add(new MonitorInfo(++idx, bounds, primary, h));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>主显示器信息（含 HMONITOR）。</summary>
    public static MonitorInfo PrimaryMonitor()
    {
        var mons = Monitors();
        foreach (var m in mons) if (m.Primary) return m;
        return mons.Count > 0 ? mons[0] : new MonitorInfo(1, PrimaryBounds(), true, IntPtr.Zero);
    }

    /// <summary>包含给定矩形（中心）的显示器；否则取相交最多的；再否则主显示器。</summary>
    public static MonitorInfo MonitorForRect(Rectangle r)
    {
        var mons = Monitors();
        var center = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
        foreach (var m in mons) if (m.Bounds.Contains(center)) return m;

        MonitorInfo best = default; long bestArea = -1;
        foreach (var m in mons)
        {
            var it = Rectangle.Intersect(m.Bounds, r);
            long a = (long)it.Width * it.Height;
            if (a > bestArea) { bestArea = a; best = m; }
        }
        return bestArea > 0 ? best : PrimaryMonitor();
    }

    public static Rectangle PrimaryBounds()
    {
        var primary = Monitors().FirstOrDefault(m => m.Primary);
        if (primary.Bounds.Width > 0) return primary.Bounds;
        return new Rectangle(0, 0, GetSystemMetrics(SM_CXSCREEN), GetSystemMetrics(SM_CYSCREEN));
    }

    public static Rectangle MonitorBounds(int oneBasedIndex)
    {
        var mons = Monitors();
        var m = mons.FirstOrDefault(x => x.Index == oneBasedIndex);
        return m.Bounds.Width > 0 ? m.Bounds : PrimaryBounds();
    }

    /// <summary>整个虚拟桌面（所有显示器并集），用于把请求区域裁进可见范围。</summary>
    public static Rectangle VirtualBounds()
    {
        var mons = Monitors();
        if (mons.Count == 0) return PrimaryBounds();
        var u = mons[0].Bounds;
        foreach (var m in mons) u = Rectangle.Union(u, m.Bounds);
        return u;
    }

    /// <summary>枚举可见的、有标题的顶层窗口（供用户选某个会议窗口）。</summary>
    public static List<WindowInfo> Windows()
    {
        var list = new List<WindowInfo>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            int len = GetWindowTextLength(h);
            if (len == 0) return true;
            var sb = new StringBuilder(len + 1);
            GetWindowText(h, sb, sb.Capacity);
            string title = sb.ToString();
            if (string.IsNullOrWhiteSpace(title)) return true;

            GetWindowThreadProcessId(h, out uint pid);
            string proc = "?";
            try { using var p = Process.GetProcessById((int)pid); proc = p.ProcessName; } catch { }
            list.Add(new WindowInfo(h, pid, proc, title));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>窗口当前在屏幕上的矩形（随窗口移动而变）。最小化/无效返回空，便于跳过。</summary>
    public static Rectangle WindowBounds(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd) || IsIconic(hwnd)) return Rectangle.Empty;
        // 优先用 DWM 的可见边界（不含 Win10 不可见外边框）；失败回退 GetWindowRect
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, Marshal.SizeOf<RECT>()) != 0)
            if (!GetWindowRect(hwnd, out r)) return Rectangle.Empty;
        var rect = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        return Rectangle.Intersect(rect, VirtualBounds());
    }

    /// <summary>抓取指定矩形区域，返回 24bpp 位图（调用方负责 Dispose）。</summary>
    public static Bitmap Capture(Rectangle region)
    {
        var bmp = new Bitmap(region.Width, region.Height, PixelFormat.Format24bppRgb);
        try
        {
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(region.Left, region.Top, 0, 0, region.Size, CopyPixelOperation.SourceCopy);
        }
        catch { bmp.Dispose(); throw; }   // 抓屏失败时别把位图泄漏掉（锁屏/UAC 期间会抛）
        return bmp;
    }
}
