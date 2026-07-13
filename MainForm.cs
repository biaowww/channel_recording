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
        try { _targets = AudioSessionLister.List(); } catch { _targets = new(); }
        foreach (var a in _targets)
        {
            string t = string.IsNullOrWhiteSpace(a.Title) ? "" : "  " + a.Title;
            _cmbTarget.Items.Add($"{a.ProcessName} ({a.Pid}){t}");
        }
        if (_cmbTarget.Items.Count > 0) _cmbTarget.SelectedIndex = 0;
        _lblStatus.Text = _targets.Count == 0
            ? "没检测到在发声的应用。先让会议/目标出点声音，再点“刷新”。"
            : "就绪。选择目标后点“开始录制”。";
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
            foreach (var w in ScreenCapture.Windows())
                if (w.Pid != _ownPid)
                    _sources.Add(new Source { Kind = SrcKind.Window, Hwnd = w.Hwnd, Label = $"🪟 {Trunc(w.Title, 30)} ({w.Process})" });
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
        if (_cmbTarget.SelectedIndex < 0 || _cmbTarget.SelectedIndex >= _targets.Count)
        {
            MessageBox.Show(this, "请选择要录制的应用。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var app = _targets[_cmbTarget.SelectedIndex];
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
        string slide = _chkSlides.Checked ? $"   slide {s.SlideCount}" : "";
        string sil = (s.SilenceSeconds > 0 && s.HasSound) ? $"   静音 {s.SecondsSinceSound:F0}/{s.SilenceSeconds}s" : "";
        _lblStats.Text = $"时长 {s.Elapsed:hh\\:mm\\:ss}   {s.Bytes / 1024.0 / 1024.0:F1} MB{slide}{sil}";
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
