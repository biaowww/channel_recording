using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ChannelRecording;

internal readonly record struct AudioApp(uint Pid, string ProcessName, string Title);

/// <summary>
/// 枚举默认输出设备上的音频会话，列出“当前有音频会话的进程 + PID”，
/// 对应你在 Windows 音量合成器里看到的那一列 App。
/// </summary>
internal static class AudioSessionLister
{
    private const int eRender = 0;
    private const int eConsole = 0;
    private const uint CLSCTX_ALL = 0x17;

    // COM 激活需在 MTA；GUI 是 STA，故统一跳到 MTA 执行（只回传纯数据，不外泄 COM 对象）
    public static List<AudioApp> List() => Mta.Run(ListCore);

    private static List<AudioApp> ListCore()
    {
        var result = new List<AudioApp>();
        var seenPids = new HashSet<uint>();

        var enumType = Type.GetTypeFromCLSID(AudioGuids.CLSID_MMDeviceEnumerator);
        var deviceEnum = (IMMDeviceEnumerator)Activator.CreateInstance(enumType);

        if (deviceEnum.GetDefaultAudioEndpoint(eRender, eConsole, out IMMDevice device) < 0 || device == null)
            return result;

        if (device.Activate(AudioGuids.IID_IAudioSessionManager2, CLSCTX_ALL, IntPtr.Zero, out object mgrObj) < 0)
            return result;
        var mgr = (IAudioSessionManager2)mgrObj;

        if (mgr.GetSessionEnumerator(out IAudioSessionEnumerator sessions) < 0 || sessions == null)
            return result;

        if (sessions.GetCount(out int count) < 0) return result;

        for (int i = 0; i < count; i++)
        {
            if (sessions.GetSession(i, out object sessionObj) < 0 || sessionObj == null) continue;

            IAudioSessionControl2 ctl;
            try { ctl = (IAudioSessionControl2)sessionObj; }
            catch { continue; }

            // 跳过系统声音会话
            if (ctl.IsSystemSoundsSession() == 0) continue;
            if (ctl.GetProcessId(out uint pid) < 0 || pid == 0) continue;
            if (!seenPids.Add(pid)) continue;

            string name = "(已退出)";
            string title = "";
            try
            {
                using var p = Process.GetProcessById((int)pid);
                name = p.ProcessName;
                title = p.MainWindowTitle ?? "";
            }
            catch { /* 进程可能刚退出 */ }

            result.Add(new AudioApp(pid, name, title));
        }

        return result;
    }
}
