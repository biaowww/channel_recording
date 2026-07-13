using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using WinRT;

namespace ChannelRecording;

/// <summary>
/// Windows.Graphics.Capture (WGC)：走 GPU、无撕裂，能干净抓硬件加速的共享画面，被遮挡的窗口也照抓。
/// 纯 WinRT + 少量 P/Invoke（D3D 设备），不依赖 Win2D/Vortice。缓存最新一帧（限流），静止画面也能取到上一帧。
/// </summary>
internal sealed class WgcCapturer : IDisposable
{
    private static readonly Guid GraphicsCaptureItemGuid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
        IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
    }

    [ComImport, Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private unsafe interface IMemoryBufferByteAccess { void GetBuffer(out byte* buffer, out uint capacity); }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags,
        IntPtr featureLevels, uint numFeatureLevels, uint sdkVersion, out IntPtr device, out int featureLevel, out IntPtr context);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    public static bool IsSupported
    {
        get { try { return GraphicsCaptureSession.IsSupported(); } catch { return false; } }
    }


    private readonly IDirect3DDevice _device;
    private Direct3D11CaptureFramePool _pool;
    private GraphicsCaptureSession _session;
    private SizeInt32 _poolSize;

    private readonly object _gate = new();
    private byte[] _latest;   // 最新一帧 BGRA(紧密排列 w*h*4)
    private int _w, _h;
    private long _lastMs;     // 限流：最近一次做 GPU→CPU 读回的时刻

    private readonly object _frameLock = new();   // 串行化 OnFrameArrived 与 Dispose，避免停止时用已释放的 pool/device
    private volatile bool _disposed;

    public static WgcCapturer ForWindow(IntPtr hwnd) => new(CreateItemForWindow(hwnd));
    public static WgcCapturer ForMonitor(IntPtr hmon) => new(CreateItemForMonitor(hmon));

    private static GraphicsCaptureItem CreateItemForWindow(IntPtr hwnd)
    {
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var iid = GraphicsCaptureItemGuid;
        var ptr = interop.CreateForWindow(hwnd, ref iid);
        var item = GraphicsCaptureItem.FromAbi(ptr);
        Marshal.Release(ptr);
        return item;
    }

    private static GraphicsCaptureItem CreateItemForMonitor(IntPtr hmon)
    {
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var iid = GraphicsCaptureItemGuid;
        var ptr = interop.CreateForMonitor(hmon, ref iid);
        var item = GraphicsCaptureItem.FromAbi(ptr);
        Marshal.Release(ptr);
        return item;
    }

    private static IDirect3DDevice CreateD3DDevice()
    {
        const int HARDWARE = 1, WARP = 5;
        const uint BGRA_SUPPORT = 0x20, SDK_VERSION = 7;
        int hr = D3D11CreateDevice(IntPtr.Zero, HARDWARE, IntPtr.Zero, BGRA_SUPPORT,
            IntPtr.Zero, 0, SDK_VERSION, out IntPtr devicePtr, out _, out IntPtr context);
        if (hr < 0)   // 硬件不行退回 WARP 软件渲染
            Marshal.ThrowExceptionForHR(D3D11CreateDevice(IntPtr.Zero, WARP, IntPtr.Zero, BGRA_SUPPORT,
                IntPtr.Zero, 0, SDK_VERSION, out devicePtr, out _, out context));
        try
        {
            var iidDxgi = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c"); // IDXGIDevice
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(devicePtr, ref iidDxgi, out IntPtr dxgiDevice));
            try
            {
                Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out IntPtr graphicsDevice));
                try { return MarshalInterface<IDirect3DDevice>.FromAbi(graphicsDevice); }
                finally { Marshal.Release(graphicsDevice); }
            }
            finally { Marshal.Release(dxgiDevice); }
        }
        finally
        {
            if (context != IntPtr.Zero) Marshal.Release(context);
            Marshal.Release(devicePtr);
        }
    }

    private WgcCapturer(GraphicsCaptureItem item)
    {
        _device = CreateD3DDevice();
        try
        {
            _poolSize = item.Size;
            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _poolSize);
            _pool.FrameArrived += OnFrameArrived;
            _session = _pool.CreateCaptureSession(item);
            TrySet(() => _session.IsCursorCaptureEnabled = false);
            _session.StartCapture();
        }
        catch { Dispose(); throw; }   // 半途失败时释放已创建的 D3D 设备/池/会话
    }

    private static void TrySet(Action a) { try { a(); } catch { } }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        // 回调在 WGC 自己的线程池线程上；与 Dispose 用同一把锁串行化，且已释放后直接返回，
        // 避免"边释放 pool/device 边取帧"造成后台线程崩进程（try/catch 兜底不可捕获的除外的一切）。
        lock (_frameLock)
        {
            if (_disposed) return;
            try
            {
                using var frame = sender.TryGetNextFrame();
                if (frame == null) return;

                long now = Environment.TickCount64;
                if (now - _lastMs >= 350)   // 限流：WGC 可能 60fps 到帧，抓帧只需 ~1fps
                {
                    _lastMs = now;
                    try { ReadFrame(frame); } catch { }
                }

                var cs = frame.ContentSize;
                if (cs.Width != _poolSize.Width || cs.Height != _poolSize.Height)
                {
                    _poolSize = cs;
                    try { _pool.Recreate(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _poolSize); } catch { }
                }
            }
            catch { }
        }
    }

    private void ReadFrame(Direct3D11CaptureFrame frame)
    {
        using var raw = SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface).AsTask().GetAwaiter().GetResult();
        SoftwareBitmap converted = raw.BitmapPixelFormat == BitmapPixelFormat.Bgra8
            ? null : SoftwareBitmap.Convert(raw, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        var sb = converted ?? raw;
        try
        {
            int w = sb.PixelWidth, h = sb.PixelHeight;
            var px = new byte[w * h * 4];
            using (var buf = sb.LockBuffer(BitmapBufferAccessMode.Read))
            using (var reference = buf.CreateReference())
            {
                var plane = buf.GetPlaneDescription(0);
                var byteAccess = reference.As<IMemoryBufferByteAccess>();   // CsWinRT: 用 .As 做 QI，不能强转
                unsafe
                {
                    byteAccess.GetBuffer(out byte* dataPtr, out uint _);
                    for (int y = 0; y < h; y++)   // 逐行拷贝（stride 可能有 padding）
                        Marshal.Copy((IntPtr)(dataPtr + plane.StartIndex + y * plane.Stride), px, y * w * 4, w * 4);
                }
            }
            lock (_gate) { _latest = px; _w = w; _h = h; }
        }
        finally { converted?.Dispose(); }
    }

    /// <summary>取最新一帧为 32bpp Bitmap（还没有帧则 null）。调用方负责 Dispose。</summary>
    public Bitmap TryGrab()
    {
        byte[] px; int w, h;
        lock (_gate) { px = _latest; w = _w; h = _h; }
        if (px == null || w <= 0 || h <= 0) return null;

        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try { Marshal.Copy(px, 0, data.Scan0, px.Length); }
        finally { bmp.UnlockBits(data); }
        return bmp;
    }

    public void Dispose()
    {
        // 先退订，阻止新回调派发；再拿锁（会等待正在执行的回调结束），置位后释放，确保无回调在飞。
        try { if (_pool != null) _pool.FrameArrived -= OnFrameArrived; } catch { }
        lock (_frameLock)
        {
            _disposed = true;
            try { _session?.Dispose(); } catch { }
            try { _pool?.Dispose(); } catch { }
            try { _device?.Dispose(); } catch { }
        }
    }
}
