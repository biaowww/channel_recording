using System.Runtime.InteropServices;

namespace ChannelRecording;

/// <summary>
/// 用 WASAPI 捕获默认麦克风/录音设备，统一转成 44100/16bit/立体声，写入环形缓冲，
/// 供 LoopbackCapture 取出与应用声音混音。设备不可用时 Start 抛异常，由上层降级处理。
/// </summary>
internal sealed class MicCapture
{
    private const ushort Channels = 2;
    private const uint   SampleRate = 44100;
    private const ushort BitsPerSample = 16;
    private const int    BlockAlign = Channels * BitsPerSample / 8;

    private const int eCapture = 1, eConsole = 0;
    private const uint CLSCTX_ALL = 0x17;

    private IAudioClient _client;
    private IAudioCaptureClient _capture;
    private readonly EventWaitHandle _ready = new(false, EventResetMode.AutoReset);
    private readonly EventWaitHandle _stop  = new(false, EventResetMode.ManualReset);
    private Thread _thread;
    private byte[] _buf = new byte[BlockAlign * 4096];
    private readonly ByteRing _ring = new((int)SampleRate * BlockAlign * 2); // ~2 秒

    public bool Active { get; private set; }

    public void Start()
    {
        Mta.Run(SetupCore);   // 激活必须在 MTA；客户端在 MTA 内创建后由采集线程(亦 MTA)使用
        _thread = new Thread(Loop) { IsBackground = true, Name = "mic-capture" };
        _thread.Start();
    }

    private void SetupCore()
    {
        var t = Type.GetTypeFromCLSID(AudioGuids.CLSID_MMDeviceEnumerator);
        var en = (IMMDeviceEnumerator)Activator.CreateInstance(t);
        if (en.GetDefaultAudioEndpoint(eCapture, eConsole, out IMMDevice dev) < 0 || dev == null)
            throw new InvalidOperationException("没有可用的麦克风/录音设备。");
        if (dev.Activate(AudioGuids.IID_IAudioClient, CLSCTX_ALL, IntPtr.Zero, out object o) < 0 || o == null)
            throw new InvalidOperationException("无法激活麦克风音频接口。");

        _client = (IAudioClient)o;
        var fmt = new WaveFormatEx
        {
            wFormatTag = AudClnt.WaveFormatPcm,
            nChannels = Channels,
            nSamplesPerSec = SampleRate,
            wBitsPerSample = BitsPerSample,
            nBlockAlign = BlockAlign,
            nAvgBytesPerSec = SampleRate * BlockAlign,
            cbSize = 0,
        };
        // 普通采集：不加 LOOPBACK，加 AUTOCONVERTPCM 让引擎把麦克风(常为单声道/48k)转成统一格式
        uint flags = AudClnt.StreamFlagsEventCallback | AudClnt.StreamFlagsAutoConvertPcm;
        Marshal.ThrowExceptionForHR(_client.Initialize(AudClnt.ShareModeShared, flags, 0, 0, ref fmt, IntPtr.Zero));
        Marshal.ThrowExceptionForHR(_client.GetService(AudioGuids.IID_IAudioCaptureClient, out object svc));
        _capture = (IAudioCaptureClient)svc;
        Marshal.ThrowExceptionForHR(_client.SetEventHandle(_ready.SafeWaitHandle.DangerousGetHandle()));
        Marshal.ThrowExceptionForHR(_client.Start());

        Active = true;
    }

    /// <summary>取出最多 count 字节麦克风 PCM 到 dst，返回实际字节数。</summary>
    public int Read(byte[] dst, int count) => _ring.Read(dst, count);

    /// <summary>丢弃当前积压（用于与应用声音对齐起点）。</summary>
    public void Flush() => _ring.Clear();

    public void Stop()
    {
        try { _client?.Stop(); } catch { /* ignore */ }
        _stop.Set();
        _thread?.Join();
    }

    private void Loop()
    {
        WaitHandle[] handles = { _ready, _stop };
        bool running = true;
        while (running)
        {
            int idx = WaitHandle.WaitAny(handles);
            if (idx == 1) running = false;
            Drain();
        }
    }

    private void Drain()
    {
        if (_capture == null) return;
        if (_capture.GetNextPacketSize(out uint packet) < 0) return;
        while (packet > 0)
        {
            int hr = _capture.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out _);
            if (hr < 0) return;

            int bytes = (int)frames * BlockAlign;
            if (bytes > 0)
            {
                if (bytes > _buf.Length) _buf = new byte[bytes];
                if ((flags & AudClnt.BufferFlagsSilent) != 0) Array.Clear(_buf, 0, bytes);
                else Marshal.Copy(data, _buf, 0, bytes);
                _ring.Write(_buf, 0, bytes);
            }

            _capture.ReleaseBuffer(frames);
            if (_capture.GetNextPacketSize(out packet) < 0) return;
        }
    }
}
