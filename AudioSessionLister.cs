using System.Diagnostics;

namespace ChannelRecording;

internal readonly record struct AudioApp(uint Pid, string ProcessName, string Title);

/// <summary>
/// 枚举「有音频会话的进程 + PID」——对应 Windows 音量合成器里那一列 App。
/// 注意：必须遍历**所有活动的输出设备**，不能只看默认设备 —— Windows 允许给单个程序
/// 单独指定输出设备（如 chrome 被指到别的声卡），只看默认设备会整个漏掉它。
/// </summary>
internal static class AudioSessionLister
{
    private const int eRender = 0;
    private const uint DEVICE_STATE_ACTIVE = 0x1;
    private const uint CLSCTX_ALL = 0x17;

    // COM 激活需在 MTA；GUI 是 STA，故统一跳到 MTA 执行（只回传纯数据，不外泄 COM 对象）
    public static List<AudioApp> List() => Mta.Run(ListCore);

    private static List<AudioApp> ListCore()
    {
        var result = new List<AudioApp>();
        var seenPids = new HashSet<uint>();

        var enumType = Type.GetTypeFromCLSID(AudioGuids.CLSID_MMDeviceEnumerator);
        var deviceEnum = (IMMDeviceEnumerator)Activator.CreateInstance(enumType);

        if (deviceEnum.EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, out IMMDeviceCollection devices) < 0
            || devices == null) return result;
        if (devices.GetCount(out uint count) < 0) return result;

        for (uint i = 0; i < count; i++)
        {
            if (devices.Item(i, out IMMDevice device) < 0 || device == null) continue;
            try { CollectFrom(device, result, seenPids); } catch { /* 单个设备失败不影响其它 */ }
        }
        return result;
    }

    /// <summary>临时诊断：把每个输出设备上的所有会话原样打出来（含状态/系统声音标记）。</summary>
    public static List<string> Diagnose() => Mta.Run(DiagCore);

    private static List<string> DiagCore()
    {
        var lines = new List<string>();
        var enumType = Type.GetTypeFromCLSID(AudioGuids.CLSID_MMDeviceEnumerator);
        var de = (IMMDeviceEnumerator)Activator.CreateInstance(enumType);
        const uint ALL_STATES = 0xF;

        if (de.EnumAudioEndpoints(eRender, ALL_STATES, out IMMDeviceCollection devs) < 0 || devs == null)
        { lines.Add("枚举输出设备失败"); return lines; }
        devs.GetCount(out uint c);
        lines.Add($"输出设备数(全部状态) = {c}");

        for (uint i = 0; i < c; i++)
        {
            if (devs.Item(i, out IMMDevice d) < 0 || d == null) { lines.Add($"[{i}] Item 失败"); continue; }
            d.GetState(out uint st);
            string id = "?"; try { d.GetId(out id); } catch { }
            short tail = 0;
            int hr = d.Activate(AudioGuids.IID_IAudioSessionManager2, CLSCTX_ALL, IntPtr.Zero, out object mo);
            if (hr < 0 || mo == null) { lines.Add($"[{i}] state={st} Activate 失败 0x{hr:X8}"); continue; }
            var mgr = (IAudioSessionManager2)mo;
            if (mgr.GetSessionEnumerator(out IAudioSessionEnumerator se) < 0 || se == null)
            { lines.Add($"[{i}] state={st} 取不到会话枚举器"); continue; }
            se.GetCount(out int n);

            var names = new List<string>();
            for (int k = 0; k < n; k++)
            {
                if (se.GetSession(k, out object so) < 0 || so == null) continue;
                IAudioSessionControl2 ctl; try { ctl = (IAudioSessionControl2)so; } catch { continue; }
                ctl.GetProcessId(out uint pid);
                ctl.GetState(out int sst);            // 0=Inactive 1=Active 2=Expired
                bool sys = ctl.IsSystemSoundsSession() == 0;
                string pn = "?"; try { using var p = Process.GetProcessById((int)pid); pn = p.ProcessName; } catch { }
                names.Add($"{pn}({pid}) 状态{sst}{(sys ? " 系统声音" : "")}");
                _ = tail;
            }
            lines.Add($"[{i}] state={st} 会话数={n}: {(names.Count > 0 ? string.Join(" | ", names) : "无")}");
            lines.Add($"     {id}");
        }
        return lines;
    }

    private static void CollectFrom(IMMDevice device, List<AudioApp> result, HashSet<uint> seenPids)
    {
        if (device.Activate(AudioGuids.IID_IAudioSessionManager2, CLSCTX_ALL, IntPtr.Zero, out object mgrObj) < 0
            || mgrObj == null) return;
        var mgr = (IAudioSessionManager2)mgrObj;

        if (mgr.GetSessionEnumerator(out IAudioSessionEnumerator sessions) < 0 || sessions == null) return;
        if (sessions.GetCount(out int n) < 0) return;

        for (int i = 0; i < n; i++)
        {
            if (sessions.GetSession(i, out object sessionObj) < 0 || sessionObj == null) continue;

            IAudioSessionControl2 ctl;
            try { ctl = (IAudioSessionControl2)sessionObj; }
            catch { continue; }

            if (ctl.IsSystemSoundsSession() == 0) continue;          // 跳过"系统声音"
            if (ctl.GetProcessId(out uint pid) < 0 || pid == 0) continue;
            if (!seenPids.Add(pid)) continue;                        // 同进程可能在多个设备上有会话

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
    }
}
