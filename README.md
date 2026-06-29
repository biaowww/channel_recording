# ChannelRecorder — 本地定向进程录音 + 投屏抓帧

把**指定某个应用**（腾讯会议 / 企业微信 / 浏览器某标签所在进程等）输出的声音单独录成 WAV，
并可选地把会议里**投屏的 PPT** 按翻页自动抓成图片序列，合成 PDF / Word。

## 原理：为什么能绕过会议软件“没有录制权限”

会议软件里那个“你没有录制会议的权限”是**它应用内部**的开关。但任何 App 要让你听到声音，
都得把音频送进 Windows 音频引擎再播出来（即“音量合成器”里每个 App 那一条）。本工具用 WASAPI 的
**进程级回环捕获 (Process Loopback)**，在**系统音频层**单独抓目标进程（及其子进程）渲染的声音 ——
与目标 App 自己给不给“录制权限”**无关**。

> 需要 **Windows 10 2004（build 19041）或更新**。本机已实测通过。

## ⚠️ 合规提醒

录制会议/通话音频可能涉及他人隐私与“同意”要求。请确保你**有权**录制并仅作个人用途，责任自负。

## 图形界面（推荐，双击即用）

双击 **`gui.bat`**（或 `bin\Release\net8.0-windows\ChannelRecorder.exe`）打开窗口：
选录制目标 → 勾选「含子进程 / 同时录我的麦克风 / 抓投屏PPT」→ 设静音自动停秒数 →
点「● 开始录制」，实时显示时长/大小/slide数/静音倒计时，点「■ 停止」或直接关窗即收尾。

## 命令行

```powershell
cd E:\claude_project\channel_recording

# 1) 看当前哪些 App 在发声、它们的 PID
.\rec.bat list

# 2) 只录音（自动归档 + 自动停止）
.\rec.bat record --name wemeetapp

# 3) 录音 + 把我的麦克风也混进去
.\rec.bat record --name wemeetapp --mic

# 4) 录音 + 抓投屏 PPT，并导出 PDF 和 Word
.\rec.bat record --name wemeetapp --mic --slides --doc both
```

录制中按 **Enter** 或 **Ctrl+C** 手动停止。产物默认存到 `recording\` 目录，
文件名 = **会议名_时间戳**（如 `腾讯会议_20260629_161148.wav`）。

## 麦克风混音

加 `--mic`（GUI 勾「同时录我的麦克风」）会把**默认麦克风/录音设备**实时混入同一个 WAV
（逐样本相加、限幅，统一 44100/16bit/立体声，起点与应用声音对齐）。
注意：**静音自动停止只看会议(应用)的声音**，不把你的麦克风算进去 —— 这样“会议结束→静音→停”才符合预期。

## 自动停止

默认开启，满足任一条件即停：
- **目标进程退出**（会议软件被关）
- **检测到声音后，静音满 N 秒**（默认 60s；只在“出现过声音”之后才计时，避免会议没开始就被停）
- 指定了 `--seconds` 且录满时长

控制：`--silence 90`（改静音时长）、`--silence 0`（关静音停止）、`--no-exit-stop`（关进程退出停止）。

## 投屏 PPT 抓帧（可选）

开启后定时抓投屏画面 → 感知哈希识别换页（带去抖，滤掉切换动画）→ 每张不同 slide 存为 JPEG → 合成 PDF / Word。

**抓哪块画面 —— 三选一（GUI「投屏来源」下拉）：**
- **显示器**：整个主显示器或指定显示器；
- **窗口**：选某个程序的窗口（如会议窗口），**跟随窗口移动/缩放**，不必全屏；
- **框选区域**：拖动框选屏幕任意一块（比如会议放在右上角时只框那一块）。

命令行对应：默认主显示器；`--monitor N` 指定显示器；`--region x,y,宽,高` 框定区域（越界自动裁剪/回退）。
通用：`--slide-interval <ms>` 采样间隔（默认 1000）；`--doc pdf|docx|both` 导出格式（默认 pdf）。
（按窗口跟随是 GUI 专属；命令行用 `--region` 框定。）

## 参数总览

| 参数 | 说明 |
| --- | --- |
| `--pid <N>` / `--name <名字>` | 目标进程（名字多匹配时优先选在发声的那个） |
| `--mic` | 同时录默认麦克风并混入同一文件 |
| `--include` / `--exclude` | 含子进程（默认）/ 录“整机声音里扣掉该进程树” |
| `--silence <秒>` | 有声后静音满 N 秒自动停（默认 60，0=关） |
| `--no-exit-stop` | 关闭“目标进程退出即停” |
| `--seconds <秒>` | 录满 N 秒自动停 |
| `--slides` | 开启投屏抓帧 |
| `--region x,y,宽,高` / `--monitor <N>` | 抓取区域 / 指定显示器 |
| `--slide-interval <ms>` | 抓帧间隔（默认 1000） |
| `--doc pdf\|docx\|both` | slide 导出格式（默认 pdf） |
| `--out <路径>` | 自定义 WAV 完整路径（覆盖默认归档命名） |
| `--dir <路径>` | 自定义归档根目录（默认 `<项目根>\recording`） |

常见会议进程名：腾讯会议 `wemeetapp`、企业微信 `wwmapp`、钉钉 `DingTalk`、Zoom `Zoom`、飞书 `Feishu`/`Lark`。

## 输出

- 音频：WAV / PCM **44100Hz / 16-bit / 立体声**，约 10 MB/分钟。
- slide：`recording\<会议名_时间>_slides\slide_001.jpg…` + `<会议名_时间>.pdf` / `.docx`。
- 转 MP3：`ffmpeg -i xxx.wav -b:a 128k xxx.mp3`。

## 构建 / 打包

```powershell
dotnet build -c Release
# 打包单 exe（依赖已装的 .NET 8 运行时）：
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

## 代码结构

| 文件 | 作用 |
| --- | --- |
| `Interop.cs` | WASAPI / Core Audio COM 互操作（进程回环、会话枚举） |
| `LoopbackCapture.cs` | 进程级回环捕获 + 音量/静音追踪 + 麦克风混音 |
| `MicCapture.cs` / `ByteRing.cs` | 麦克风采集 + 线程安全环形缓冲 |
| `RecordingSession.cs` | 会话编排（声音+麦克风+抓帧+自动停止+收尾，GUI/CLI 共用） |
| `MainForm.cs` | WinForms 图形界面 |
| `Mta.cs` | 把音频 COM 激活统一切到 MTA 线程（GUI 是 STA） |
| `AudioSessionLister.cs` | 枚举正在发声的进程 + PID（`list`） |
| `WavWriter.cs` | 16-bit PCM WAV 写入 |
| `ScreenCapture.cs` | 抓屏（整屏/显示器/区域）+ DPI 感知 |
| `SlideCapturer.cs` | 感知哈希换页识别，存 slide 图片 |
| `PdfBuilder.cs` / `DocxBuilder.cs` | 图片序列合成 PDF / Word（零依赖手写） |
| `PathUtil.cs` | 归档目录、会议名清洗、会话命名 |
| `Program.cs` | 入口：无参开 GUI，带参走命令行 |

## 已知限制

- 麦克风录的是**默认录音设备**；要指定具体麦克风需另加选择（可扩展）。
- 同一页内大幅滚动 / 播放视频可能多抓几张 slide，属正常，可后期删。
- 投屏抓帧抓的是屏幕可见内容；演示窗口被完全遮挡时抓到的是遮挡物。
