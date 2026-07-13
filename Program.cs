using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ChannelRecording;

internal static class Program
{
    [DllImport("kernel32.dll")] private static extern bool FreeConsole();

    [STAThread]   // WinForms 需要 STA；进程回环的 MTA 要求由 LoopbackCapture 内部转线程满足
    private static int Main(string[] args)
    {
        ScreenCapture.EnableDpiAwareness();

        if (args.Length == 0)   // 无参数 = 图形界面
        {
            FreeConsole();      // 脱离控制台，双击时不残留黑窗
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
            return 0;
        }

        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (IsHelp(args[0])) { PrintUsage(); return 0; }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "list"   => CmdList(),
                "record" => CmdRecord(args[1..]),
                _        => Unknown(args[0]),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"✘ 出错: {ex.Message}");
            if (ex is DllNotFoundException || (uint)ex.HResult == 0x80070490)
                Console.Error.WriteLine("  提示: 进程回环需要 Windows 10 2004 (build 19041) 或更新版本。");
            return 1;
        }
    }

    private static int CmdList()
    {
        var apps = AudioSessionLister.List();
        if (apps.Count == 0)
        {
            Console.WriteLine("当前没有检测到正在发声的应用。先让目标 App 播放点声音，再运行一次 list。");
            return 0;
        }

        Console.WriteLine("当前有音频会话的应用（用下面的 PID 去录制）：\n");
        Console.WriteLine($"  {"PID",-8} {"进程名",-24} 窗口标题");
        Console.WriteLine($"  {new string('-', 8)} {new string('-', 24)} {new string('-', 30)}");
        foreach (var a in apps)
        {
            string title = a.Title.Length > 40 ? a.Title[..40] + "…" : a.Title;
            Console.WriteLine($"  {a.Pid,-8} {Trunc(a.ProcessName, 24),-24} {title}");
        }
        Console.WriteLine("\n示例: ChannelRecorder record --pid <PID> --mic --slides");
        Console.WriteLine("（直接双击 ChannelRecorder.exe 或运行 gui.bat 可打开图形界面）");
        return 0;
    }

    private static int CmdRecord(string[] args)
    {
        uint pid = 0;
        string name = null, outPath = null, dirOverride = null;
        bool includeTree = true, stopOnExit = true, slides = false, mic = false, encodeAac = true;
        int seconds = 0, silence = 60, slideInterval = 1000, monitor = 0;
        string region = null, docFmt = "pdf";

        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--pid":      pid = uint.Parse(args[++i]); break;
                    case "--name":     name = args[++i]; break;
                    case "--out":      outPath = args[++i]; break;
                    case "--dir":      dirOverride = args[++i]; break;
                    case "--mic":      mic = true; break;
                    case "--wav":      encodeAac = false; break;   // 默认转 AAC；--wav 保留无损
                    case "--seconds":  seconds = int.Parse(args[++i]); break;
                    case "--silence":  silence = int.Parse(args[++i]); break;
                    case "--no-exit-stop": stopOnExit = false; break;
                    case "--exclude":  includeTree = false; break;
                    case "--include":  includeTree = true; break;
                    case "--slides":   slides = true; break;
                    case "--region":   region = args[++i]; slides = true; break;
                    case "--monitor":  monitor = int.Parse(args[++i]); slides = true; break;
                    case "--slide-interval": slideInterval = int.Parse(args[++i]); break;
                    case "--doc":      docFmt = args[++i].ToLowerInvariant(); break;
                    default: Console.Error.WriteLine($"未知参数: {args[i]}"); return 1;
                }
            }
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or FormatException or OverflowException)
        {
            Console.Error.WriteLine("参数有误：数值格式不正确或缺少取值，请检查命令。"); return 1;
        }

        if (docFmt is not ("pdf" or "docx" or "both"))
        {
            Console.Error.WriteLine("--doc 只能是 pdf / docx / both。"); return 1;
        }

        if (pid == 0 && name != null) pid = ResolvePidByName(name);
        if (pid == 0)
        {
            Console.Error.WriteLine("请用 --pid <PID> 或 --name <进程名> 指定目标。先跑 `list` 查看可选项。");
            return 1;
        }

        var session = new RecordingSession
        {
            Pid = pid,
            IncludeTree = includeTree,
            Mic = mic,
            EncodeAac = encodeAac,
            SilenceSeconds = silence,
            StopOnExit = stopOnExit,
            Slides = slides,
            Region = slides ? ResolveRegion(region, monitor) : Rectangle.Empty,
            SlideIntervalMs = slideInterval,
            DocFormat = docFmt,
            MeetingName = GetMeetingName(pid, name),
            OutPathOverride = outPath,
            DirOverride = dirOverride,
        };

        var ended = new ManualResetEventSlim(false);
        string reason = null;
        session.Stopped += r => { reason = r; ended.Set(); };

        try { session.Start(); }
        catch (Exception ex) { Console.Error.WriteLine($"✘ 启动失败: {ex.Message}"); return 1; }

        Console.WriteLine($"目标: PID {pid} ({SafeProcName(pid)}){(includeTree ? " + 子进程" : " 排除模式")}");
        Console.WriteLine($"会议: {session.MeetingName}");
        Console.WriteLine($"输出: {session.WavPath}");
        Console.WriteLine($"音频格式: {(encodeAac ? "AAC .m4a (96kbps，收尾自动转码压体积)" : "WAV 无损")}");
        if (mic) Console.WriteLine($"麦克风: {(session.MicActive ? "已混入" : "不可用，仅录应用声音")}");
        if (silence > 0) Console.WriteLine($"自动停止: 静音 {silence}s 或目标进程退出");
        if (slides) Console.WriteLine($"投屏抓帧: {session.Region.Width}x{session.Region.Height} @({session.Region.X},{session.Region.Y})，导出 {docFmt}");

        Console.CancelKeyPress += (_, e) => { e.Cancel = true; session.Stop("手动停止 (Ctrl+C)"); };
        if (!Console.IsInputRedirected)
            new Thread(() => { Console.ReadLine(); session.Stop("手动停止 (Enter)"); }) { IsBackground = true }.Start();
        if (seconds > 0)
            new Thread(() => { Thread.Sleep(seconds * 1000); session.Stop($"到达指定时长 {seconds}s"); }) { IsBackground = true }.Start();

        Console.WriteLine("\n● 正在录制… 按 Enter 或 Ctrl+C 停止。");
        while (!ended.IsSet)
        {
            ended.Wait(500);
            string extra = slides ? $"  slide {session.SlideCount}" : "";
            string sil = (silence > 0 && session.HasSound) ? $"  静音 {session.SecondsSinceSound,4:F0}/{silence}s" : "";
            Console.Write($"\r  时长 {session.Elapsed:hh\\:mm\\:ss}  {session.Bytes / 1024.0 / 1024.0:F1} MB{extra}{sil}        ");
        }

        Console.WriteLine($"\n✔ 已停止（{reason}）");
        Console.WriteLine($"  音频: {session.AudioPath}");
        if (slides)
        {
            Console.WriteLine($"  slide: {session.SlideCount} 张 → {session.SlidesDir}");
            foreach (var d in session.DocPaths) Console.WriteLine($"  文档: {d}");
        }
        return 0;
    }

    private static Rectangle ResolveRegion(string region, int monitor)
    {
        if (!string.IsNullOrEmpty(region))
        {
            var p = region.Split(',', StringSplitOptions.TrimEntries);
            if (p.Length == 4 &&
                int.TryParse(p[0], out int x) && int.TryParse(p[1], out int y) &&
                int.TryParse(p[2], out int w) && int.TryParse(p[3], out int h) && w > 0 && h > 0)
            {
                var req = new Rectangle(x, y, w, h);
                var clamped = Rectangle.Intersect(req, ScreenCapture.VirtualBounds());
                if (clamped.Width <= 0 || clamped.Height <= 0)
                    Console.Error.WriteLine("--region 完全在屏幕之外，已退回主显示器。");
                else
                {
                    if (clamped != req)
                        Console.Error.WriteLine($"--region 超出屏幕，已裁剪为 {clamped.Width}x{clamped.Height} @({clamped.X},{clamped.Y})。");
                    return clamped;
                }
            }
            else Console.Error.WriteLine("--region 格式应为 x,y,宽,高，已退回主显示器。");
        }
        if (monitor > 0) return ScreenCapture.MonitorBounds(monitor);
        return ScreenCapture.PrimaryBounds();
    }

    private static uint ResolvePidByName(string name)
    {
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];

        var procs = Process.GetProcessesByName(name);
        if (procs.Length == 0)
        {
            Console.Error.WriteLine($"没找到名为 \"{name}\" 的进程。");
            return 0;
        }

        try
        {
            var audioPids = AudioSessionLister.List().Select(a => a.Pid).ToHashSet();
            var match = procs.FirstOrDefault(p => audioPids.Contains((uint)p.Id))
                     ?? procs.FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero)
                     ?? procs[0];

            if (procs.Length > 1)
                Console.WriteLine($"匹配到 {procs.Length} 个进程，已选用 PID {match.Id}。");
            return (uint)match.Id;
        }
        finally { foreach (var p in procs) p.Dispose(); }
    }

    /// <summary>会议名：优先目标进程主窗口标题，其次同名进程窗口标题，最后进程名。</summary>
    internal static string GetMeetingName(uint pid, string name)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            if (!string.IsNullOrWhiteSpace(p.MainWindowTitle)) return p.MainWindowTitle;
            string pn = p.ProcessName;
            var siblings = Process.GetProcessesByName(pn);
            try
            {
                var sibling = siblings.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.MainWindowTitle));
                return sibling?.MainWindowTitle ?? pn;
            }
            finally { foreach (var s in siblings) s.Dispose(); }
        }
        catch { return name ?? "recording"; }
    }

    internal static string SafeProcName(uint pid)
    {
        try { using var p = Process.GetProcessById((int)pid); return p.ProcessName; }
        catch { return "pid" + pid; }
    }

    private static string Trunc(string s, int max) => s.Length > max ? s[..(max - 1)] + "…" : s;
    private static bool IsHelp(string a) => a is "-h" or "--help" or "help" or "/?";
    private static int Unknown(string cmd) { Console.Error.WriteLine($"未知命令: {cmd}"); PrintUsage(); return 1; }

    private static void PrintUsage()
    {
        Console.WriteLine("""
        ChannelRecorder — 本地定向进程录音 + 投屏抓帧（双击 exe 或 gui.bat 打开图形界面）

        命令行用法:
          ChannelRecorder list
          ChannelRecorder record (--pid <PID> | --name <进程名>) [选项]

        目标:   --pid <N> / --name <名字>（如 wemeetapp）  --include(默认) / --exclude
        声音:   --mic 同时录麦克风并混入同一文件
        停止:   --silence <秒>(默认60,0关) / --no-exit-stop / --seconds <秒>
        投屏:   --slides  --region x,y,宽,高 / --monitor <N>  --slide-interval <ms>  --doc pdf|docx|both
        路径:   --out <wav路径>  --dir <归档目录>（默认 <项目根>\recording）

        例:
          ChannelRecorder record --name wemeetapp --mic
          ChannelRecorder record --name wemeetapp --mic --slides --doc both

        说明: 在系统音频层捕获目标 App 播出来的声音，与该 App 是否开放“录制权限”无关。
              请确保你有权录制相关音频并自行承担合规责任。
        """);
    }
}
