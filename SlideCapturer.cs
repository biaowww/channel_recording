using System.Drawing;
using System.Drawing.Imaging;

namespace ChannelRecording;

internal sealed record SlideImage(string Path, int Width, int Height);

/// <summary>
/// 定时抓取投屏区域，用感知哈希(aHash)判断“换页”，把每张不同的 slide 存成 JPEG。
/// 去抖：只有当前帧既明显不同于“上次保存的slide”、又与“上一帧”接近(画面已稳定)时才保存，
/// 以避免把翻页动画/过渡帧也存进去。
/// </summary>
internal sealed class SlideCapturer
{
    private const int HashSide = 16;          // 16x16 -> 256 bit aHash
    private const int HashBits = HashSide * HashSide;

    public int NewSlideDistance { get; set; } = 30;   // 与上次保存差异 > 此值 => 可能换页
    public int StableDistance   { get; set; } = 10;   // 与上一帧差异 <= 此值 => 画面已稳定

    private readonly Func<Rectangle> _region;   // 每次取一次，支持跟随移动的窗口
    private readonly string _dir;
    private readonly int _intervalMs;
    private readonly EventWaitHandle _stop = new(false, EventResetMode.ManualReset);
    private readonly List<SlideImage> _slides = new();
    private Thread _thread;

    private ulong[] _lastSavedHash;
    private ulong[] _prevHash;

    public IReadOnlyList<SlideImage> Slides => _slides;
    public int Count => _slides.Count;

    public SlideCapturer(Func<Rectangle> regionProvider, string slidesDir, int intervalMs = 1000)
    {
        _region = regionProvider;
        _dir = slidesDir;
        _intervalMs = Math.Max(250, intervalMs);
        Directory.CreateDirectory(_dir);
    }

    public void Start()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "slide-capturer" };
        _thread.Start();
    }

    public void Stop()
    {
        _stop.Set();
        _thread?.Join();
    }

    private void Loop()
    {
        do
        {
            try { Tick(); }
            catch { /* 抓屏偶发失败，忽略本次 */ }
        }
        while (!_stop.WaitOne(_intervalMs));
    }

    private void Tick()
    {
        Rectangle r = _region();
        if (r.Width <= 0 || r.Height <= 0) return;   // 窗口最小化/无效，跳过本次
        using var bmp = ScreenCapture.Capture(r);
        ulong[] cur = AverageHash(bmp);

        if (_lastSavedHash == null)
        {
            Save(bmp);                 // 第一帧直接作为 slide 1
            _lastSavedHash = cur;
            _prevHash = cur;
            return;
        }

        int dSaved = Distance(cur, _lastSavedHash);
        int dPrev = Distance(cur, _prevHash);
        if (dSaved > NewSlideDistance && dPrev <= StableDistance)
        {
            Save(bmp);
            _lastSavedHash = cur;
        }
        _prevHash = cur;
    }

    private void Save(Bitmap bmp)
    {
        string path = Path.Combine(_dir, $"slide_{_slides.Count + 1:D3}.jpg");
        SaveJpeg(bmp, path, 88);
        _slides.Add(new SlideImage(path, bmp.Width, bmp.Height));
    }

    // ── 感知哈希 ──────────────────────────────────────────────────────────────
    private static ulong[] AverageHash(Bitmap src)
    {
        using var small = new Bitmap(HashSide, HashSide, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(src, 0, 0, HashSide, HashSide);
        }

        var gray = new int[HashBits];
        var rect = new Rectangle(0, 0, HashSide, HashSide);
        var data = small.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            int stride = data.Stride;
            unsafe
            {
                byte* p = (byte*)data.Scan0;
                long sum = 0;
                int k = 0;
                for (int y = 0; y < HashSide; y++)
                {
                    byte* row = p + y * stride;
                    for (int x = 0; x < HashSide; x++)
                    {
                        byte b = row[x * 3], gr = row[x * 3 + 1], r = row[x * 3 + 2];
                        int lum = (r * 30 + gr * 59 + b * 11) / 100;
                        gray[k++] = lum;
                        sum += lum;
                    }
                }
                int avg = (int)(sum / HashBits);
                var bits = new ulong[(HashBits + 63) / 64];
                for (int i = 0; i < HashBits; i++)
                    if (gray[i] >= avg) bits[i >> 6] |= 1UL << (i & 63);
                return bits;
            }
        }
        finally { small.UnlockBits(data); }
    }

    private static int Distance(ulong[] a, ulong[] b)
    {
        int d = 0;
        for (int i = 0; i < a.Length; i++)
            d += System.Numerics.BitOperations.PopCount(a[i] ^ b[i]);
        return d;
    }

    // ── JPEG 编码（带质量） ─────────────────────────────────────────────────────
    private static ImageCodecInfo _jpegCodec;
    private static ImageCodecInfo JpegCodec =>
        _jpegCodec ??= ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    public static void SaveJpeg(Bitmap bmp, string path, long quality)
    {
        using var ep = new EncoderParameters(1);
        ep.Param[0] = new EncoderParameter(Encoder.Quality, quality);
        bmp.Save(path, JpegCodec, ep);
    }
}
