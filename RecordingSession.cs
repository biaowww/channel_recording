using System.Diagnostics;
using System.Drawing;

namespace ChannelRecording;

/// <summary>
/// 一次录制会话的编排：应用声音(可选混麦克风) + 可选投屏抓帧 + 自动停止 + 收尾导出。
/// CLI 与 GUI 共用同一套逻辑。线程模型：Start 在调用方线程跑（COM 内部自转 MTA）；
/// 停止与收尾经状态机协调，保证 Stopped 恰好触发一次、且不会与 Start 竞态导致孤儿线程。
/// </summary>
internal sealed class RecordingSession
{
    // ── 配置（Start 前设置）──────────────────────────────────────────────────
    public uint Pid;
    public bool IncludeTree = true;
    public bool Mic;
    public bool EncodeAac = true;       // 录完转 AAC(.m4a) 压体积；false=保留 WAV
    public int AacBitrate = 96000;      // 96kbps 立体声 ≈ 0.7MB/分（200MB≈4.6小时）
    public int SilenceSeconds = 60;     // 0 = 关闭静音停止
    public bool StopOnExit = true;
    public bool Slides;
    public SlideSource SlideSource;   // 投屏抓取源（显示器 / 窗口 / 框选区域）
    public int SlideIntervalMs = 1000;
    public string DocFormat = "pdf";    // pdf | docx | both
    public string MeetingName = "recording";
    public string OutPathOverride;
    public string DirOverride;

    // ── 产物 / 状态 ─────────────────────────────────────────────────────────
    public string RecordingDir { get; private set; }
    public string SessionBase { get; private set; }
    public string WavPath { get; private set; }
    public string AudioPath { get; private set; }   // 最终音频路径（.m4a 或 .wav）
    public string SlidesDir { get; private set; }
    public string[] DocPaths { get; private set; } = Array.Empty<string>();
    public bool MicRequested => Mic;
    public bool MicActive { get; private set; }
    public bool IsRunning { get; private set; }

    public TimeSpan Elapsed => _sw.Elapsed;
    public long Bytes => _capture?.BytesCaptured ?? 0;
    public int SlideCount => _slideCap?.Count ?? 0;
    public bool HasSound => _capture?.HasDetectedSound ?? false;
    public double SecondsSinceSound => _capture?.SecondsSinceSound ?? 0;

    /// <summary>停止并收尾完成后触发一次（含 WAV 收尾与文档导出），参数为停止原因。可能在后台线程触发。</summary>
    public event Action<string> Stopped;

    private enum SState { Idle, Running, Stopping, Done }
    private SState _state = SState.Idle;

    private LoopbackCapture _capture;
    private MicCapture _mic;
    private SlideCapturer _slideCap;
    private WgcCapturer _wgc;
    private Process _target;
    private Thread _monitor;
    private readonly ManualResetEventSlim _done = new(false);
    private readonly Stopwatch _sw = new();
    private readonly object _gate = new();
    private string _stopReason;
    private int _finished;

    public void Start()
    {
        RecordingDir = PathUtil.EnsureRecordingDir(DirOverride);
        string baseName = PathUtil.SessionBaseName(MeetingName);
        SessionBase = baseName;
        if (OutPathOverride == null)
            // 去重要覆盖最终产物：AAC 会把 .wav 删掉只留 .m4a，故 .wav/.m4a/_slides 任一存在就换名
            for (int n = 2;
                 File.Exists(Path.Combine(RecordingDir, SessionBase + ".wav")) ||
                 File.Exists(Path.Combine(RecordingDir, SessionBase + ".m4a")) ||
                 Directory.Exists(Path.Combine(RecordingDir, SessionBase + "_slides"));
                 n++)
                SessionBase = $"{baseName}_{n}";

        WavPath = OutPathOverride != null
            ? Path.GetFullPath(OutPathOverride)
            : Path.Combine(RecordingDir, SessionBase + ".wav");
        Directory.CreateDirectory(Path.GetDirectoryName(WavPath));

        try
        {
            if (Mic)
            {
                _mic = new MicCapture();
                try { _mic.Start(); MicActive = _mic.Active; }
                catch { _mic = null; MicActive = false; }   // 麦克风不可用：降级为只录应用声音
            }

            if (Slides && SlideSource != null)
            {
                SlidesDir = Path.Combine(RecordingDir, SessionBase + "_slides");
                _slideCap = new SlideCapturer(BuildFrameProvider(SlideSource), SlidesDir, SlideIntervalMs);
            }

            _capture = new LoopbackCapture();
            _capture.Start(Pid, IncludeTree, WavPath, _mic);   // 激活失败会抛
            _slideCap?.Start();
            try { _target = Process.GetProcessById((int)Pid); } catch { }
        }
        catch
        {
            // 启动失败：尽力清理已起来的部分，再向上抛
            try { _slideCap?.Stop(); } catch { }
            try { _wgc?.Dispose(); } catch { }
            try { _capture?.Stop(); } catch { }
            try { _mic?.Stop(); } catch { }
            throw;
        }

        _sw.Start();

        // 若在启动过程中已被请求停止，则此处直接收尾（由本线程唯一地拆掉刚起来的采集），不进入运行态
        bool teardownNow;
        lock (_gate)
        {
            if (_state == SState.Idle) { _state = SState.Running; IsRunning = true; teardownNow = false; }
            else teardownNow = true;   // RequestStop 已把状态置为 Stopping
        }
        if (teardownNow) { Finish(); return; }

        _monitor = new Thread(MonitorLoop) { IsBackground = true, Name = "session-monitor" };
        _monitor.Start();
    }

    private void MonitorLoop()
    {
        while (!_done.IsSet)
        {
            _done.Wait(400);
            if (_done.IsSet) break;
            if (SilenceSeconds > 0 && HasSound && SecondsSinceSound >= SilenceSeconds)
                RequestStop($"静音超过 {SilenceSeconds}s");
            else if (StopOnExit && _target != null && HasExitedSafe(_target))
                RequestStop("目标进程已退出");
        }
    }

    /// <summary>请求停止（幂等）。收尾在后台线程完成，结束后触发 Stopped。</summary>
    public void Stop(string reason = "手动停止") => RequestStop(reason);

    private void RequestStop(string reason)
    {
        bool queueFinish = false;
        lock (_gate)
        {
            _stopReason ??= reason;
            if (_state == SState.Done || _state == SState.Stopping) { _done.Set(); return; }
            // Running → 这里负责收尾；Idle(启动中) → 只置位，由 Start 完成后收尾
            if (_state == SState.Running) queueFinish = true;
            _state = SState.Stopping;
        }
        _done.Set();
        if (queueFinish) ThreadPool.QueueUserWorkItem(_ => Finish());
    }

    private void Finish()
    {
        if (Interlocked.Exchange(ref _finished, 1) != 0) return;

        try { _slideCap?.Stop(); } catch { }
        try { _wgc?.Dispose(); } catch { }
        try { _capture?.Stop(); } catch { }   // 先停应用声音消费端（含最后一次混音排空 + WAV 收尾）
        try { _mic?.Stop(); } catch { }        // 再停麦克风生产端
        try { _target?.Dispose(); } catch { }
        _sw.Stop();
        IsRunning = false;

        // 音频收尾：默认把 WAV 转成 AAC(.m4a) 压体积并删掉大 WAV；转码失败则保留 WAV
        AudioPath = WavPath;
        string audioErr = null;
        if (EncodeAac && File.Exists(WavPath))
        {
            string m4a = Path.ChangeExtension(WavPath, ".m4a");
            try
            {
                AudioEncoder.WavToAac(WavPath, m4a, AacBitrate);
                AudioPath = m4a;
                try { File.Delete(WavPath); } catch { }
            }
            catch (Exception ex)
            {
                audioErr = ex.Message; AudioPath = WavPath;
                try { File.Delete(m4a); } catch { }   // 清理半截 m4a，避免和保留的 WAV 并存造成困惑
            }
        }

        // 文档导出可能抛（输出被占用/磁盘满/图片被删等）——绝不能因此跳过 Stopped，否则上层会卡死
        string buildErr = null;
        try
        {
            var paths = new List<string>();
            if (_slideCap != null && _slideCap.Count > 0)
            {
                string docBase = Path.Combine(RecordingDir, SessionBase);
                if (DocFormat is "pdf" or "both") { PdfBuilder.Build(_slideCap.Slides, docBase + ".pdf"); paths.Add(docBase + ".pdf"); }
                if (DocFormat is "docx" or "both") { DocxBuilder.Build(_slideCap.Slides, docBase + ".docx"); paths.Add(docBase + ".docx"); }
            }
            DocPaths = paths.ToArray();
        }
        catch (Exception ex) { buildErr = ex.Message; }

        lock (_gate) { _state = SState.Done; }

        string reason = _stopReason ?? "已停止";
        if (audioErr != null) reason += $"（AAC 转码失败，已保留 WAV: {audioErr}）";
        if (buildErr != null) reason += $"（文档导出失败: {buildErr}）";
        try { Stopped?.Invoke(reason); } catch { }   // 订阅者异常不得反噬收尾线程
    }

    /// <summary>按抓取源建"取一帧"函数：WGC 优先（无撕裂、抓硬件加速/被遮挡窗口），不支持则退回 GDI。</summary>
    private Func<Bitmap> BuildFrameProvider(SlideSource src)
    {
        if (WgcCapturer.IsSupported)
        {
            try
            {
                _wgc = src.Kind == SlideSourceKind.Window
                    ? WgcCapturer.ForWindow(src.Hwnd)
                    : WgcCapturer.ForMonitor(src.Hmon);
                var cap = _wgc;
                if (src.Kind == SlideSourceKind.Region)
                {
                    Rectangle region = src.Region, mon = src.MonitorBounds;
                    return () => CropRegion(cap.TryGrab(), region, mon);
                }
                return cap.TryGrab;
            }
            catch { try { _wgc?.Dispose(); } catch { } _wgc = null; }   // WGC 建失败 → 退回 GDI
        }

        // GDI 退路
        Func<Rectangle> rect = src.Kind switch
        {
            SlideSourceKind.Window => () => ScreenCapture.WindowBounds(src.Hwnd),
            SlideSourceKind.Region => () => src.Region,
            _ => () => src.MonitorBounds.Width > 0 ? src.MonitorBounds : ScreenCapture.PrimaryBounds(),
        };
        return () =>
        {
            var r = rect();
            return (r.Width <= 0 || r.Height <= 0) ? null : ScreenCapture.Capture(r);
        };
    }

    /// <summary>从整块显示器帧里裁出区域（虚拟坐标 → 该显示器内坐标）。</summary>
    private static Bitmap CropRegion(Bitmap monitorFrame, Rectangle region, Rectangle monitorBounds)
    {
        if (monitorFrame == null) return null;
        var crop = new Rectangle(region.X - monitorBounds.X, region.Y - monitorBounds.Y, region.Width, region.Height);
        crop = Rectangle.Intersect(crop, new Rectangle(0, 0, monitorFrame.Width, monitorFrame.Height));
        if (crop.Width <= 0 || crop.Height <= 0) { monitorFrame.Dispose(); return null; }
        var sub = monitorFrame.Clone(crop, monitorFrame.PixelFormat);
        monitorFrame.Dispose();
        return sub;
    }

    private static bool HasExitedSafe(Process p)
    {
        try { return p.HasExited; } catch { return false; }
    }
}
