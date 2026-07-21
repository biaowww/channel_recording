using System.Diagnostics;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ChannelRecording;

internal sealed class MainForm : Form
{
    private enum SrcKind { Monitor, Window, Region, PickRegion }
    private sealed class Source
    {
        public SrcKind Kind;
        public Rectangle Rect;    // Monitor=显示器边界；Region=框选矩形
        public IntPtr Hwnd;       // Window
        public IntPtr Hmon;       // Monitor
        public uint Pid;          // Window 所属进程（用于和录音目标比对）
        public string Proc;
        public string Label;
    }

    private readonly ComboBox _cmbTarget = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Button _btnRefresh = new() { Text = "刷新" };
    private readonly CheckBox _chkTree = new() { Text = "含子进程", Checked = true };
    private readonly CheckBox _chkMic = new() { Text = "录麦克风" };
    private readonly CheckBox _chkAac = new() { Text = "音频转 AAC(小)", Checked = true };
    private readonly CheckBox _chkSlides = new() { Text = "抓投屏 PPT（自动换页存图 → PDF/Word）" };
    private readonly ComboBox _cmbSource = new() { DropDownStyle = ComboBoxStyle.DropDownList, Enabled = false };
    private readonly ComboBox _cmbDoc = new() { DropDownStyle = ComboBoxStyle.DropDownList, Enabled = false };
    private readonly NumericUpDown _numSilence = new() { Minimum = 0, Maximum = 36000, Value = 60 };
    private readonly Button _btnStart = new() { Text = "● 开始录制" };
    private readonly Button _btnStop = new() { Text = "■ 停止", Enabled = false };
    private readonly Button _btnOpen = new() { Text = "打开录音文件夹" };
    private readonly Label _lblStatus = new() { Text = "就绪。选择目标后点“开始录制”。", AutoSize = false };
    private readonly Label _lblStats = new() { AutoSize = false };

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };
    private List<AudioApp> _targets = new();
    private List<MonitorInfo> _monitors = new();
    private readonly List<Source> _sources = new();
    private Rectangle? _customRegion;
    private int _prevSourceIndex;
    private bool _suppressSource;
    private readonly int _ownPid = Environment.ProcessId;
    private RecordingSession _session;

    private bool _starting, _recording, _closePending, _allowClose;

    public MainForm()
    {
        Text = "ChannelRecorder · 定向录音";
        Font = new Font("Microsoft YaHei UI", 9f);
        ClientSize = new Size(480, 344);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        var lblT = new Label { Text = "录制目标:", Location = new Point(14, 18), AutoSize = true };
        _cmbTarget.SetBounds(86, 14, 290, 24);
        _btnRefresh.SetBounds(386, 13, 80, 26);

        _chkTree.SetBounds(86, 48, 84, 22);
        _chkMic.SetBounds(176, 48, 92, 22);
        _chkAac.SetBounds(276, 48, 150, 22);
        _chkSlides.SetBounds(86, 76, 380, 22);

        var lblSrc = new Label { Text = "投屏来源:", Location = new Point(14, 112), AutoSize = true };
        _cmbSource.SetBounds(86, 108, 250, 24);
        var lblDoc = new Label { Text = "导出:", Location = new Point(344, 112), AutoSize = true };
        _cmbDoc.SetBounds(386, 108, 80, 24);

        var lblSil = new Label { Text = "静音多少秒自动停 (0=关):", Location = new Point(14, 148), AutoSize = true };
        _numSilence.SetBounds(196, 144, 70, 24);
        var lblExit = new Label { Text = "（目标进程关闭也会自动停）", Location = new Point(276, 148), AutoSize = true, ForeColor = Color.Gray };

        _btnStart.SetBounds(14, 184, 130, 34);
        _btnStop.SetBounds(154, 184, 110, 34);
        _btnOpen.SetBounds(330, 184, 136, 34);

        _lblStatus.SetBounds(14, 234, 452, 24);
        _lblStatus.ForeColor = Color.FromArgb(0, 90, 160);
        _lblStats.SetBounds(14, 262, 452, 70);
        _lblStats.ForeColor = Color.DimGray;

        Controls.AddRange(new Control[]
        {
            lblT, _cmbTarget, _btnRefresh, _chkTree, _chkMic, _chkAac, _chkSlides,
            lblSrc, _cmbSource, lblDoc, _cmbDoc, lblSil, _numSilence, lblExit,
            _btnStart, _btnStop, _btnOpen, _lblStatus, _lblStats,
        });

        _cmbDoc.Items.AddRange(new object[] { "pdf", "docx", "both" });
        _cmbDoc.SelectedIndex = 0;

        _chkSlides.CheckedChanged += (_, _) =>
        {
            if (_recording || _starting) return;
            _cmbSource.Enabled = _cmbDoc.Enabled = _chkSlides.Checked;
            if (_chkSlides.Checked) RefreshSources();
        };
        _cmbSource.SelectedIndexChanged += OnSourceChanged;
        // 换了录制目标就重排投屏来源，让同一程序的窗口置顶标 ⭐
        _cmbTarget.SelectedIndexChanged += (_, _) => { if (!_recording && !_starting && _chkSlides.Checked) RefreshSources(); };
        _btnRefresh.Click += (_, _) => { RefreshTargets(); RefreshSources(); };
        _btnStart.Click += OnStart;
        _btnStop.Click += OnStopClicked;
        _btnOpen.Click += OnOpenFolder;
        _timer.Tick += OnTick;
        FormClosing += OnFormClosing;

        Load += (_, _) => { _monitors = ScreenCapture.Monitors(); RefreshTargets(); RefreshSources(); };
    }

    private void RefreshTargets()
    {
        _cmbTarget.Items.Clear();

        // 正在发声的（音频会话）——只是"发现列表"，程序一停出声 Windows 就回收会话
        List<AudioApp> audio;
        try { audio = AudioSessionLister.List(); } catch { audio = new(); }

        // 其它有窗口的运行中程序 —— 进程回环对任意 PID 都能录，不必等它出声（如刚打开还没播的 chrome）
        var others = new List<AudioApp>();
        try
        {
            var have = audio.Select(a => a.Pid).ToHashSet();
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id != _ownPid && p.MainWindowHandle != IntPtr.Zero &&
                        !have.Contains((uint)p.Id) && !string.IsNullOrWhiteSpace(p.MainWindowTitle))
                        others.Add(new AudioApp((uint)p.Id, p.ProcessName, p.MainWindowTitle));
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
        catch { }
        others = others.OrderBy(o => o.ProcessName, StringComparer.OrdinalIgnoreCase).ToList();

        _targets = new List<AudioApp>();
        _targets.AddRange(audio);
        _targets.AddRange(others);

        _cmbTarget.Items.Add("— 请选择要录的程序 —");   // 占位项(index 0)，真实目标从 index 1 开始
        foreach (var a in audio)
            _cmbTarget.Items.Add($"🔊 {a.ProcessName} ({a.Pid})" + (string.IsNullOrWhiteSpace(a.Title) ? "" : "  " + Trunc(a.Title, 26)));
        foreach (var a in others)
            _cmbTarget.Items.Add($"　 {a.ProcessName} ({a.Pid})  {Trunc(a.Title, 26)}");
        _cmbTarget.SelectedIndex = 0;   // 停在占位项：必须手动选，避免默认录成列表里第一个程序
        _lblStatus.Text = _targets.Count == 0
            ? "没列出任何程序，点“刷新”重试。"
            : "「录制目标」选录哪个程序的声音（🔊=正在出声）；下面的「投屏来源」只管画面。";
    }

    private void RefreshSources()
    {
        _suppressSource = true;
        _monitors = ScreenCapture.Monitors();   // 刷新时重扫显示器，支持中途插拔屏
        _sources.Clear();
        _cmbSource.Items.Clear();

        var pm = ScreenCapture.PrimaryMonitor();
        _sources.Add(new Source { Kind = SrcKind.Monitor, Rect = pm.Bounds, Hmon = pm.Hmon, Label = "主显示器" });
        foreach (var m in _monitors)
            _sources.Add(new Source { Kind = SrcKind.Monitor, Rect = m.Bounds, Hmon = m.Hmon, Label = $"显示器 {m.Index} ({m.Bounds.Width}x{m.Bounds.Height}){(m.Primary ? " 主" : "")}" });

        try
        {
            // 进程名放前面（标题长也不会把进程名挤没），并把"和录制目标同一个程序"的窗口置顶标 ⭐
            string targetProc = SelectedTargetProcName();
            var wins = ScreenCapture.Windows().Where(w => w.Pid != _ownPid).ToList();
            bool Same(WindowInfo w) => targetProc != null &&
                string.Equals(w.Process, targetProc, StringComparison.OrdinalIgnoreCase);

            foreach (var w in wins.Where(Same).Concat(wins.Where(x => !Same(x))))
                _sources.Add(new Source
                {
                    Kind = SrcKind.Window,
                    Hwnd = w.Hwnd,
                    Pid = w.Pid,
                    Proc = w.Process,
                    Label = (Same(w) ? "⭐ " : "🪟 ") + $"{w.Process} — {Trunc(w.Title, 24)}",
                });
        }
        catch { }

        if (_customRegion is Rectangle cr)
            _sources.Add(new Source { Kind = SrcKind.Region, Rect = cr, Label = $"✏️ 框选区域 {cr.Width}x{cr.Height}" });
        _sources.Add(new Source { Kind = SrcKind.PickRegion, Label = "✏️ 框选屏幕区域…" });

        foreach (var s in _sources) _cmbSource.Items.Add(s.Label);
        int sel = Math.Min(_prevSourceIndex, _sources.Count - 1);
        if (sel < 0) sel = 0;
        _cmbSource.SelectedIndex = sel;
        _prevSourceIndex = sel;
        _suppressSource = false;
    }

    private void OnSourceChanged(object sender, EventArgs e)
    {
        if (_suppressSource) return;
        int idx = _cmbSource.SelectedIndex;
        if (idx < 0 || idx >= _sources.Count) return;

        if (_sources[idx].Kind == SrcKind.PickRegion)
        {
            var picked = RegionSelector.Pick();
            if (picked is Rectangle r && r.Width >= 8 && r.Height >= 8)
            {
                _customRegion = r;
                RefreshSources();
                int ri = _sources.FindIndex(s => s.Kind == SrcKind.Region);
                if (ri >= 0) { _suppressSource = true; _cmbSource.SelectedIndex = ri; _suppressSource = false; _prevSourceIndex = ri; }
            }
            else   // 取消框选：退回上一个选择
            {
                _suppressSource = true; _cmbSource.SelectedIndex = _prevSourceIndex; _suppressSource = false;
            }
        }
        else _prevSourceIndex = idx;
    }

    /// <summary>当前「录制目标」的进程名（未选则 null），用于把同一程序的窗口置顶标 ⭐。</summary>
    private string SelectedTargetProcName()
    {
        int i = _cmbTarget.SelectedIndex;
        return (i > 0 && i <= _targets.Count) ? _targets[i - 1].ProcessName : null;
    }

    private SlideSource SelectedSlideSource()
    {
        int idx = _cmbSource.SelectedIndex;
        if (idx < 0 || idx >= _sources.Count)
        {
            var pm = ScreenCapture.PrimaryMonitor();
            return SlideSource.FromMonitor(pm.Hmon, pm.Bounds);
        }
        var src = _sources[idx];
        switch (src.Kind)
        {
            case SrcKind.Window: return SlideSource.FromWindow(src.Hwnd);
            case SrcKind.Region:
                var m = ScreenCapture.MonitorForRect(src.Rect);
                return SlideSource.FromRegion(src.Rect, m.Hmon, m.Bounds);
            default:
                return SlideSource.FromMonitor(src.Hmon, src.Rect);
        }
    }

    private async void OnStart(object sender, EventArgs e)
    {
        // index 0 是占位项；真实目标从 1 开始。必须显式选，杜绝"默认录成列表第一个"
        if (_cmbTarget.SelectedIndex <= 0 || _cmbTarget.SelectedIndex > _targets.Count)
        {
            MessageBox.Show(this,
                "请先在「录制目标」里选择要录哪个程序的声音。\n\n" +
                "注意：「录制目标」决定录音，下面的「投屏来源」只决定抓画面，两者是分开的。",
                "还没选录制目标", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var app = _targets[_cmbTarget.SelectedIndex - 1];

        // 录音目标与投屏来源不是同一个程序时提醒（上次就是录音选成 chrome、投屏选了企业微信）
        if (_chkSlides.Checked)
        {
            int si = _cmbSource.SelectedIndex;
            // 按进程名比，不按 PID —— chrome/企业微信 这类多进程程序，窗口 PID 和发声 PID 本来就不同
            if (si >= 0 && si < _sources.Count && _sources[si].Kind == SrcKind.Window &&
                !string.IsNullOrEmpty(_sources[si].Proc) &&
                !string.Equals(_sources[si].Proc, app.ProcessName, StringComparison.OrdinalIgnoreCase))
            {
                var ans = MessageBox.Show(this,
                    $"录音目标：{app.ProcessName} (PID {app.Pid})\n" +
                    $"投屏来源：{_sources[si].Proc} (PID {_sources[si].Pid})\n\n" +
                    "两者不是同一个程序 —— 会录前者的声音、抓后者的画面。确定要这样吗？",
                    "录音目标 与 投屏来源 不一致", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (ans != DialogResult.Yes) return;
            }
        }
        var session = new RecordingSession
        {
            Pid = app.Pid,
            IncludeTree = _chkTree.Checked,
            Mic = _chkMic.Checked,
            EncodeAac = _chkAac.Checked,
            SilenceSeconds = (int)_numSilence.Value,
            Slides = _chkSlides.Checked,
            SlideSource = _chkSlides.Checked ? SelectedSlideSource() : null,
            DocFormat = _cmbDoc.SelectedItem?.ToString() ?? "pdf",
            MeetingName = Program.GetMeetingName(app.Pid, app.ProcessName),
        };
        session.Stopped += OnSessionStopped;

        _starting = true;
        _recording = false;
        SetControls(canStart: false, canStop: false);
        _lblStatus.Text = "正在启动…";
        _lblStats.Text = "";
        _session = session;

        try { await Task.Run(session.Start); }
        catch (Exception ex)
        {
            _session = null;
            _starting = false;
            SetControls(canStart: true, canStop: false);
            _lblStatus.Text = "启动失败。";
            MessageBox.Show(this, ex.Message, "启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            if (_closePending) { _allowClose = true; Close(); }
            return;
        }

        _starting = false;
        _recording = true;
        SetControls(canStart: false, canStop: true);
        string micNote = session.MicRequested ? (session.MicActive ? "，含麦克风" : "，麦克风不可用") : "";
        _lblStatus.Text = $"● 正在录制{micNote}… 关会议或静音 {session.SilenceSeconds}s 会自动停。";
        _timer.Start();

        if (_closePending)
        {
            _lblStatus.Text = "正在停止并保存…";
            SetControls(canStart: false, canStop: false);
            session.Stop("窗口关闭");
        }
    }

    private void OnStopClicked(object sender, EventArgs e)
    {
        if (_session == null) return;
        SetControls(canStart: false, canStop: false);
        _lblStatus.Text = "正在停止并保存…";
        _session.Stop("手动停止");
    }

    private void OnTick(object sender, EventArgs e)
    {
        var s = _session;
        if (s == null || !_recording) return;
        int filled = Math.Max(0, Math.Min(10, s.Level / 10));
        string bar = new string('█', filled) + new string('·', 10 - filled);
        string warn = s.HasSound ? "" : "   ← 一直没听到声音，检查录制目标选对没";
        string line1 = $"时长 {s.Elapsed:hh\\:mm\\:ss}   {s.Bytes / 1024.0 / 1024.0:F1} MB   音量 [{bar}]{warn}";

        var lines = new List<string> { line1 };
        if (_chkSlides.Checked)
        {
            string cap = s.CaptureInfo != null ? $"   抓取 {s.CaptureInfo}" : "";
            lines.Add($"slide {s.SlideCount}{cap}");
        }
        if (s.SilenceSeconds > 0 && s.HasSound)
            lines.Add($"静音 {s.SecondsSinceSound:F0}/{s.SilenceSeconds}s 后自动停");
        _lblStats.Text = string.Join(Environment.NewLine, lines);
    }

    private void OnSessionStopped(string reason)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            BeginInvoke(() =>
            {
                _timer.Stop();
                _starting = false;
                _recording = false;
                SetControls(canStart: true, canStop: false);
                var s = _session;
                _lblStatus.Text = $"✔ 已停止（{reason}）";
                if (s != null)
                {
                    var lines = new List<string> { "音频: " + s.AudioPath };
                    if (s.Slides) lines.Add($"slide: {s.SlideCount} 张  →  {s.SlidesDir}");
                    foreach (var d in s.DocPaths) lines.Add("文档: " + d);
                    _lblStats.Text = string.Join(Environment.NewLine, lines);
                }
                if (_closePending) { _allowClose = true; Close(); }
            });
        }
        catch { }
    }

    private void OnFormClosing(object sender, FormClosingEventArgs e)
    {
        if (_allowClose) return;
        if (_recording || _starting)
        {
            e.Cancel = true;
            _closePending = true;
            if (_recording)
            {
                _lblStatus.Text = "正在停止并保存，请稍候…";
                SetControls(canStart: false, canStop: false);
                _session?.Stop("窗口关闭");
            }
            else _lblStatus.Text = "正在启动，完成后将自动停止并关闭…";
        }
    }

    private void OnOpenFolder(object sender, EventArgs e)
    {
        try
        {
            string dir = _session?.RecordingDir ?? PathUtil.EnsureRecordingDir();
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "打不开文件夹", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private void SetControls(bool canStart, bool canStop)
    {
        _btnStart.Enabled = canStart;
        _btnStop.Enabled = canStop;
        bool inputs = canStart;
        _cmbTarget.Enabled = _btnRefresh.Enabled = inputs;
        _chkTree.Enabled = _chkMic.Enabled = _chkAac.Enabled = _chkSlides.Enabled = inputs;
        _numSilence.Enabled = inputs;
        _cmbSource.Enabled = _cmbDoc.Enabled = inputs && _chkSlides.Checked;
    }

    private static string Trunc(string s, int max) => s.Length > max ? s[..(max - 1)] + "…" : s;
}
