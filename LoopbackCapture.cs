using System.Runtime.InteropServices;

namespace ChannelRecording;

/// <summary>
/// 用 WASAPI 进程级回环捕获，把指定进程(及其子进程)的音频输出录成 WAV。
/// 实现 IActivateAudioInterfaceCompletionHandler，激活完成的回调里完成初始化。
/// </summary>
internal sealed class LoopbackCapture : IActivateAudioInterfaceCompletionHandler
{
    // 录制格式固定为 44100Hz / 立体声 / 16bit（与微软官方示例一致；引擎会自动重采样）
    private const ushort Channels = 2;
    private const uint   SampleRate = 44100;
    private const ushort BitsPerSample = 16;
    private const int    BlockAlign = Channels * BitsPerSample / 8;

    private IAudioClient _audioClient;
    private IAudioCaptureClient _captureClient;
    private WavWriter _wav;

    private readonly EventWaitHandle _sampleReady = new(false, EventResetMode.AutoReset);
    private readonly EventWaitHandle _stop        = new(false, EventResetMode.ManualReset);
    private readonly ManualResetEventSlim _activated = new(false);
    private int _activateHr;

    private Thread _captureThread;
    private byte[] _buffer = new byte[BlockAlign * 4096];

    private MicCapture _mic;                               // 可选：把麦克风混进来
    private byte[] _micBuf = new byte[BlockAlign * 4096];

    private long _bytesCaptured;
    public long BytesCaptured => Interlocked.Read(ref _bytesCaptured);  // 跨线程读，原子

    // ── 音量/静音追踪（用于自动停止）──────────────────────────────────────────
    // 16-bit 峰值阈值：高于它才算“有声音”。约 -42 dBFS，能区分人声与静音/底噪。
    public int SilenceThreshold { get; set; } = 300;
    private long _lastSoundTicks;       // 最近一次“有声音”的 UTC ticks
    private long _soundEverDetected;    // 是否至少出现过一次声音（0/1）

    /// <summary>是否曾经检测到声音（静音停止只在有过声音后才生效）。</summary>
    public bool HasDetectedSound => Interlocked.Read(ref _soundEverDetected) != 0;

    /// <summary>距离最近一次“有声音”过去了多少秒。</summary>
    public double SecondsSinceSound
    {
        get
        {
            long t = Interlocked.Read(ref _lastSoundTicks);
            if (t == 0) return 0;
            return (DateTime.UtcNow - new DateTime(t, DateTimeKind.Utc)).TotalSeconds;
        }
    }

    /// <summary>开始录制；阻塞直到激活+启动完成。之后由后台线程持续抓取。mic 非空则混入麦克风。</summary>
    public void Start(uint targetPid, bool includeProcessTree, string outputPath, MicCapture mic = null)
    {
        _mic = mic;
        // 整个激活+启动都放到 MTA：ActivateAudioInterfaceAsync 要求 MTA，且回调里创建的
        // IAudioClient 之后由采集线程(亦 MTA)使用，避免 STA(GUI/CLI) 跨套间访问。
        Mta.Run(() => StartCore(targetPid, includeProcessTree, outputPath));
    }

    private void StartCore(uint targetPid, bool includeProcessTree, string outputPath)
    {
        var activationParams = new AudioClientActivationParams
        {
            ActivationType = (int)AudioClientActivationType.ProcessLoopback,
            TargetProcessId = targetPid,
            ProcessLoopbackMode = (int)(includeProcessTree
                ? ProcessLoopbackMode.IncludeTargetProcessTree
                : ProcessLoopbackMode.ExcludeTargetProcessTree),
        };

        IntPtr pParams = Marshal.AllocHGlobal(Marshal.SizeOf<AudioClientActivationParams>());
        IntPtr pPropVariant = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariantBlob>());
        try
        {
            Marshal.StructureToPtr(activationParams, pParams, false);
            var pv = new PropVariantBlob
            {
                vt = AudClnt.VT_BLOB,
                cbSize = (uint)Marshal.SizeOf<AudioClientActivationParams>(),
                pBlobData = pParams,
            };
            Marshal.StructureToPtr(pv, pPropVariant, false);

            // 触发异步激活；完成时回调 ActivateCompleted（在 MTA 线程上）
            NativeMethods.ActivateAudioInterfaceAsync(
                NativeMethods.VirtualAudioDeviceProcessLoopback,
                AudioGuids.IID_IAudioClient, pPropVariant, this);

            if (!_activated.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("音频接口激活超时（进程回环可能不受支持，需 Win10 2004+）。");
            Marshal.ThrowExceptionForHR(_activateHr);
        }
        finally
        {
            Marshal.FreeHGlobal(pPropVariant);
            Marshal.FreeHGlobal(pParams);
        }

        _wav = new WavWriter(outputPath, Channels, SampleRate, BitsPerSample);
        _lastSoundTicks = DateTime.UtcNow.Ticks;   // 从开录起计时
        Marshal.ThrowExceptionForHR(_audioClient.Start());
        _mic?.Flush();   // 丢弃激活期间麦克风积压，使首个混音样本与应用声音对齐

        _captureThread = new Thread(CaptureLoop) { IsBackground = true, Name = "wasapi-capture" };
        _captureThread.Start();
    }

    /// <summary>停止录制、收尾并写好 WAV。</summary>
    public void Stop()
    {
        // 先停音频客户端（停止产生新帧，但已缓冲的数据仍可取），再唤醒线程做最后一次排空，
        // 这样结尾不会漏掉缓冲里的尾音，符合 WASAPI 推荐的关闭顺序。
        try { _audioClient?.Stop(); } catch { /* ignore */ }
        _stop.Set();
        _captureThread?.Join();
        _wav?.Dispose();
    }

    // IActivateAudioInterfaceCompletionHandler：激活完成回调
    public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
    {
        try
        {
            int hr = operation.GetActivateResult(out int activateResult, out object unk);
            if (hr < 0) { _activateHr = hr; return 0; }
            if (activateResult < 0) { _activateHr = activateResult; return 0; }

            _audioClient = (IAudioClient)unk;

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

            uint streamFlags = AudClnt.StreamFlagsLoopback
                             | AudClnt.StreamFlagsEventCallback
                             | AudClnt.StreamFlagsAutoConvertPcm;

            _activateHr = _audioClient.Initialize(
                AudClnt.ShareModeShared, streamFlags,
                0, 0, ref fmt, IntPtr.Zero);
            if (_activateHr < 0) return 0;

            _activateHr = _audioClient.GetService(AudioGuids.IID_IAudioCaptureClient, out object svc);
            if (_activateHr < 0) return 0;
            _captureClient = (IAudioCaptureClient)svc;

            _activateHr = _audioClient.SetEventHandle(_sampleReady.SafeWaitHandle.DangerousGetHandle());
        }
        catch (Exception ex)
        {
            _activateHr = ex.HResult != 0 ? ex.HResult : unchecked((int)0x80004005); // E_FAIL 兜底
        }
        finally
        {
            _activated.Set();
        }
        return 0;
    }

    private void CaptureLoop()
    {
        WaitHandle[] handles = { _sampleReady, _stop };
        bool running = true;
        while (running)
        {
            int idx = WaitHandle.WaitAny(handles);
            if (idx == 1) running = false;   // 收到停止信号：再排空一次后退出
            DrainPackets();
        }
    }

    private void DrainPackets()
    {
        if (_captureClient == null) return;

        if (_captureClient.GetNextPacketSize(out uint packetFrames) < 0) return;
        while (packetFrames > 0)
        {
            int hr = _captureClient.GetBuffer(out IntPtr data, out uint frames,
                out uint flags, out _, out _);
            if (hr < 0) return;

            int bytes = (int)frames * BlockAlign;
            if (bytes > 0)
            {
                if (bytes > _buffer.Length) _buffer = new byte[bytes];
                if ((flags & AudClnt.BufferFlagsSilent) != 0)
                {
                    Array.Clear(_buffer, 0, bytes);      // 静音包：写零，保持时间轴对齐
                }
                else
                {
                    Marshal.Copy(data, _buffer, 0, bytes);
                    UpdateLevel(_buffer, bytes);         // 静音检测只看“应用声音”，不含麦克风
                }

                if (_mic != null) MixMic(bytes);         // 把麦克风叠加到应用声音之上

                _wav.Write(_buffer, bytes);
                Interlocked.Add(ref _bytesCaptured, bytes);
            }

            _captureClient.ReleaseBuffer(frames);
            if (_captureClient.GetNextPacketSize(out packetFrames) < 0) return;
        }
    }

    /// <summary>取出等量麦克风样本，逐样本相加(带限幅)叠到 _buffer 上。</summary>
    private void MixMic(int bytes)
    {
        if (_micBuf.Length < bytes) _micBuf = new byte[bytes];
        int got = _mic.Read(_micBuf, bytes);   // 麦克风落后时 got<bytes，缺口按静音处理
        for (int i = 0; i + 1 < bytes; i += 2)
        {
            short a = (short)(_buffer[i] | (_buffer[i + 1] << 8));
            short b = (i + 1 < got) ? (short)(_micBuf[i] | (_micBuf[i + 1] << 8)) : (short)0;
            int s = a + b;
            if (s > short.MaxValue) s = short.MaxValue;
            else if (s < short.MinValue) s = short.MinValue;
            _buffer[i] = (byte)s;
            _buffer[i + 1] = (byte)(s >> 8);
        }
    }

    /// <summary>扫描 16-bit PCM 取峰值；超过阈值则刷新“最近有声音”时间。</summary>
    private void UpdateLevel(byte[] buf, int bytes)
    {
        int peak = 0;
        for (int i = 0; i + 1 < bytes; i += 2)
        {
            int s = (short)(buf[i] | (buf[i + 1] << 8));
            int a = s < 0 ? -s : s;
            if (a > peak) { peak = a; if (peak > SilenceThreshold) break; }
        }
        if (peak > SilenceThreshold)
        {
            Interlocked.Exchange(ref _lastSoundTicks, DateTime.UtcNow.Ticks);
            Interlocked.Exchange(ref _soundEverDetected, 1);
        }
    }
}
