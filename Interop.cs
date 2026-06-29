using System.Runtime.InteropServices;

namespace ChannelRecording;

// ─────────────────────────────────────────────────────────────────────────────
//  Windows Core Audio (WASAPI) COM 互操作定义
//  关键能力：进程级回环捕获 (Process Loopback)，Win10 2004 / build 19041+ 才有。
//  这能在“系统音频引擎”层单独抓某个进程(及其子进程)输出的声音，与目标 App
//  自身的录制权限完全无关 —— 因为我们录的是它播给系统的声音。
// ─────────────────────────────────────────────────────────────────────────────

internal static class NativeMethods
{
    // ActivateAudioInterfaceAsync 是 Mmdevapi.dll 的平坦 C 导出
    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    internal static extern IActivateAudioInterfaceAsyncOperation ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        IntPtr activationParams,          // 指向 PROPVARIANT(VT_BLOB)
        IActivateAudioInterfaceCompletionHandler completionHandler);

    // 进程回环用的“虚拟设备”路径
    internal const string VirtualAudioDeviceProcessLoopback = "VAD\\Process_Loopback";
}

internal static class AudioGuids
{
    internal static readonly Guid IID_IAudioClient        = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    internal static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
    internal static readonly Guid IID_IAudioSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
    internal static readonly Guid CLSID_MMDeviceEnumerator  = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
}

// ── 进程回环激活参数 ─────────────────────────────────────────────────────────

internal enum AudioClientActivationType
{
    Default = 0,
    ProcessLoopback = 1,
}

internal enum ProcessLoopbackMode
{
    IncludeTargetProcessTree = 0,  // 目标进程 + 其所有子进程
    ExcludeTargetProcessTree = 1,  // 整机声音里“扣掉”该进程树
}

// 原生 AUDIOCLIENT_ACTIVATION_PARAMS：ActivationType(4) + union{ TargetProcessId(4) + Mode(4) }
[StructLayout(LayoutKind.Sequential)]
internal struct AudioClientActivationParams
{
    public int ActivationType;       // AudioClientActivationType
    public uint TargetProcessId;
    public int ProcessLoopbackMode;  // ProcessLoopbackMode
}

// PROPVARIANT 仅取 VT_BLOB 用法。x64 下：vt@0, cbSize@8, pBlobData@16，整体 24 字节。
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PropVariantBlob
{
    [FieldOffset(0)]  public ushort vt;        // VT_BLOB = 65
    [FieldOffset(8)]  public uint cbSize;
    [FieldOffset(16)] public IntPtr pBlobData;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct WaveFormatEx
{
    public ushort wFormatTag;
    public ushort nChannels;
    public uint nSamplesPerSec;
    public uint nAvgBytesPerSec;
    public ushort nBlockAlign;
    public ushort wBitsPerSample;
    public ushort cbSize;
}

internal static class AudClnt
{
    public const int  ShareModeShared     = 0;
    public const uint StreamFlagsLoopback      = 0x00020000;
    public const uint StreamFlagsEventCallback = 0x00040000;
    public const uint StreamFlagsAutoConvertPcm= 0x80000000;
    public const uint BufferFlagsSilent        = 0x2;
    public const ushort WaveFormatPcm          = 1;
    public const ushort VT_BLOB                = 65;
}

// ── 激活相关 COM 接口 ────────────────────────────────────────────────────────

[ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceAsyncOperation
{
    [PreserveSig]
    int GetActivateResult(out int activateResult,
        [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
}

[ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceCompletionHandler
{
    [PreserveSig]
    int ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
}

// ── IAudioClient / IAudioCaptureClient (vtable 顺序必须与 Audioclient.h 完全一致) ──

[ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    [PreserveSig] int Initialize(int shareMode, uint streamFlags,
        long hnsBufferDuration, long hnsPeriodicity,
        ref WaveFormatEx format, IntPtr audioSessionGuid);
    [PreserveSig] int GetBufferSize(out uint numBufferFrames);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out uint numPaddingFrames);
    [PreserveSig] int IsFormatSupported(int shareMode, ref WaveFormatEx format, IntPtr closestMatch);
    [PreserveSig] int GetMixFormat(out IntPtr deviceFormat);
    [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr eventHandle);
    [PreserveSig] int GetService([MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
}

[ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
    [PreserveSig] int GetBuffer(out IntPtr data, out uint numFramesToRead,
        out uint flags, out ulong devicePosition, out ulong qpcPosition);
    [PreserveSig] int ReleaseBuffer(uint numFramesRead);
    [PreserveSig] int GetNextPacketSize(out uint numFramesInNextPacket);
}

// ── 音频会话枚举（用于 list 命令：列出当前在发声的 App + PID） ───────────────────

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out IntPtr devices);
    [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
    [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig] int Activate([MarshalAs(UnmanagedType.LPStruct)] Guid iid, uint clsCtx,
        IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
    [PreserveSig] int OpenPropertyStore(uint stgmAccess, out IntPtr properties);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string strId);
    [PreserveSig] int GetState(out uint state);
}

[ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionManager2
{
    // 继承自 IAudioSessionManager（前两个槽位，未使用，仅占位保持 vtable 顺序）
    [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, uint streamFlags, out IntPtr sessionControl);
    [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, uint streamFlags, out IntPtr audioVolume);
    // IAudioSessionManager2 新增
    [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
    [PreserveSig] int RegisterSessionNotification(IntPtr sessionNotification);
    [PreserveSig] int UnregisterSessionNotification(IntPtr sessionNotification);
    [PreserveSig] int RegisterDuckNotification(IntPtr sessionId, IntPtr duckNotification);
    [PreserveSig] int UnregisterDuckNotification(IntPtr duckNotification);
}

[ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionEnumerator
{
    [PreserveSig] int GetCount(out int sessionCount);
    [PreserveSig] int GetSession(int sessionN, [MarshalAs(UnmanagedType.IUnknown)] out object session);
}

[ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionControl2
{
    // IAudioSessionControl（9 个槽位）
    [PreserveSig] int GetState(out int state);
    [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
    [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr eventContext);
    [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
    [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr eventContext);
    [PreserveSig] int GetGroupingParam(out Guid groupingParam);
    [PreserveSig] int SetGroupingParam(ref Guid groupingParam, IntPtr eventContext);
    [PreserveSig] int RegisterAudioSessionNotification(IntPtr newNotifications);
    [PreserveSig] int UnregisterAudioSessionNotification(IntPtr newNotifications);
    // IAudioSessionControl2 新增（5 个槽位）
    [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string retVal);
    [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string retVal);
    [PreserveSig] int GetProcessId(out uint pid);
    [PreserveSig] int IsSystemSoundsSession();   // S_OK(0)=是系统声音
    [PreserveSig] int SetDuckingPreference(bool optOut);
}
