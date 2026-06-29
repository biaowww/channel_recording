# ChannelRecorder · 本地定向录音

> 把**某一个程序**正在播放的声音单独录下来 —— 哪怕会议软件不让你录。
> 还能把**你的麦克风**一起录进去，并把会议里**投屏的 PPT** 自动抓成图片、导出 PDF / Word。

![platform](https://img.shields.io/badge/Windows-10%202004%2B%20%2F%2011-0078D6)
![dotnet](https://img.shields.io/badge/.NET-8-512BD4)
![license](https://img.shields.io/badge/License-MIT-green)

---

## 能干嘛

- 🎯 **只录指定程序的声音** —— 比如只录腾讯会议 / 企业微信 / 某个浏览器标签，不会混进别的程序杂音。
- 🎤 **可加上自己的麦克风** —— 对方的声音 + 你说的话，录进同一个文件。
- ⏱ **自动停止** —— 会议软件关掉、或安静超过 N 秒，自动停。
- 🖼 **抓投屏 PPT** —— 自动识别翻页，每页存成图片，再合成一个 PDF / Word。
- 🪟 **想录哪块自己定** —— 整个屏幕 / 某个显示器 / 某个窗口（跟着窗口走）/ 鼠标框一块。
- 🖱 **图形界面**，点点鼠标就行；也有命令行。

> ❓ **会议软件"不让录"为什么它还能录？**
> 因为它录的是声音**播放到系统之后**的那一路（也就是你耳朵能听到的声音），不是去调用会议软件自己的录制功能 —— 所以跟会议软件给不给"录制权限"**没有关系**。

## ⚠️ 先读一句：合规与隐私

录制他人讲话 / 会议可能涉及隐私和"是否经过同意"的法律问题。**请确保你有权录制，并且仅用于自己。** 由此产生的责任由使用者自负。

---

## 一、准备（只做一次）

1. 系统：**Windows 10 版本 2004（build 19041）或更新**，或 Windows 11。
2. 安装 **.NET 8 桌面运行时**：打开 <https://dotnet.microsoft.com/download/dotnet/8.0> →
   找 **".NET Desktop Runtime 8.x"** 的 **Windows x64** 下载安装。
   - 如果你拿到的是别人打包好的"自包含版" exe，这一步可以跳过。

## 二、拿到程序（二选一）

**方式 A · 直接用打包好的 exe（最省事）**
如果 [Releases 页面](https://github.com/biaowww/channel_recording/releases) 有打包好的版本，下载解压后双击 `ChannelRecorder.exe` 即可。

**方式 B · 自己编译**（需要 .NET 8 **SDK**，不是只有运行时）
```powershell
git clone https://github.com/biaowww/channel_recording.git
cd channel_recording
dotnet build -c Release
```
编译好后，程序在：`bin\Release\net8.0-windows\ChannelRecorder.exe`。

---

## 三、怎么用 · 图形界面（傻瓜版，照着点）

1. **打开**：双击项目根目录的 **`gui.bat`**（或上面那个 `ChannelRecorder.exe`）。
2. **选「录制目标」**：最上面的下拉框，选你要录的程序。
   - 👉 下拉里**看不到**目标？先让它**出点声音**（会议里有人说话、放段音乐都行），再点右边 **刷新**。
   - 常见软件叫什么名字，见下面的表。
3. **（可选）勾选项**：
   - ☑ **含子进程**：默认勾上。会议软件的声音常在子进程里，建议保留。
   - ☑ **同时录我的麦克风**：把你自己说的话也录进去。
   - ☑ **抓投屏 PPT**：自动把投屏画面按翻页存图，并导出 PDF / Word。
4. **（勾了"抓投屏"才需要）选「投屏来源」** —— 你要录屏幕的哪一块：
   - **显示器**：录一整块屏幕（多显示器可选第几个）。
   - **某个窗口**：录某个程序的窗口，**会跟着窗口移动**，不用全屏。
   - **框选屏幕区域…**：弹出半透明遮罩，用鼠标**拖一个框**（比如会议放在右上角，就框右上角那块），松手即定。
   - 旁边「导出」选 `pdf` / `docx` / `both`。
5. **开始**：点 **● 开始录制**。下方会实时显示：时长、文件大小、抓到几页、静音倒计时。
6. **停止**：点 **■ 停止**；或者**直接关窗口**也行（它会先把文件存好再退出）。也可以设「静音 N 秒自动停」让它自己停。
7. **找文件**：点 **打开录音文件夹**。文件名是「会议名_时间」，例如 `腾讯会议_20260629_161148.wav`。

### 常见会议软件的"进程名"

| 软件 | 进程名 |
| --- | --- |
| 腾讯会议 | `wemeetapp` |
| 企业微信 | `wwmapp` |
| 钉钉 | `DingTalk` |
| Zoom | `Zoom` |
| 飞书 / Lark | `Feishu` / `Lark` |

> 图形界面里直接按名字选就行，不用记这些。

---

## 四、输出在哪 / 是什么

所有产物都在项目的 **`recording\`** 文件夹里：

- 录音：`会议名_时间.wav`（44100Hz / 16bit / 立体声，约 **10 MB/分钟**）。
- 投屏：`会议名_时间_slides\` 里一张张图片 ＋ `会议名_时间.pdf`（或 `.docx`）。
- 想要更小的 MP3：装了 [ffmpeg](https://ffmpeg.org/) 后执行 `ffmpeg -i 录音.wav -b:a 128k 录音.mp3`。

---

## 五、命令行用法（想脚本化 / 进阶的人看）

不带参数运行 = 打开图形界面；带参数 = 命令行模式。

```powershell
# 看现在哪些程序在出声、它们的 PID
.\rec.bat list

# 录腾讯会议 + 我的麦克风
.\rec.bat record --name wemeetapp --mic

# 再加抓投屏 PPT，导出 PDF 和 Word
.\rec.bat record --name wemeetapp --mic --slides --doc both

# 录满 60 秒自动停
.\rec.bat record --name wemeetapp --seconds 60
```

| 参数 | 作用 |
| --- | --- |
| `--name <名字>` / `--pid <N>` | 指定目标进程（名字多匹配时，优先选正在出声的那个） |
| `--mic` | 同时录默认麦克风并混入同一文件 |
| `--slides` | 抓投屏 PPT |
| `--region x,y,宽,高` / `--monitor <N>` | 抓某块区域 / 第 N 个显示器 |
| `--slide-interval <毫秒>` | 抓帧间隔（默认 1000） |
| `--doc pdf\|docx\|both` | 投屏导出格式（默认 pdf） |
| `--silence <秒>` | 有声之后静音满 N 秒自动停（默认 60，0 = 关闭） |
| `--seconds <秒>` | 录满 N 秒自动停 |
| `--no-exit-stop` | 关闭"目标进程退出即停" |
| `--include` / `--exclude` | 含子进程（默认） / 录"整机声音里扣掉该进程树" |
| `--out <路径>` / `--dir <目录>` | 自定义输出文件 / 归档目录 |

---

## 六、常见问题（FAQ）

- **双击没反应 / 提示要装 .NET？** → 装 **.NET 8 桌面运行时**（见"准备"）。
- **下拉里找不到要录的程序？** → 先让它**出声**，再点 **刷新**。
- **录出来是静音的？** → 录制期间目标程序得真的在出声；另外"静音自动停"的秒数别设太短。
- **投屏抓到的是别的画面？** → 它抓的是屏幕上**看得见**的内容，别让别的窗口盖住目标；最稳的办法是用 **框选区域** 只框 PPT 那一块。
- **麦克风没录进去？** → 录的是 Windows **默认**录音设备，先到「设置 → 系统 → 声音」把你要用的麦克风设为默认输入。
- **报错"需要 Windows 10 2004+"？** → 进程级录音是较新系统才有的特性，老系统不支持。

---

## 七、工作原理（一句话）

用 Windows 的 **WASAPI 进程级回环捕获（Process Loopback）**，在系统混音层单独抓目标进程（及其子进程）输出的音频；投屏抓帧用**感知哈希**判断"翻页"再存图。全程不依赖会议软件的录制接口，因此与其"录制权限"无关。需要 Windows 10 2004（build 19041）及以上。

## 八、给想分享给别人的作者

打包成**单个、对方不用装运行时**的 exe：
```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```
把 `publish\ChannelRecorder.exe` 发给别人即可（文件较大，因为内置了运行时）。也可以上传到本仓库的 **Releases**。

## 九、代码结构（开发者）

| 文件 | 作用 |
| --- | --- |
| `Interop.cs` | WASAPI / Core Audio 的 COM 互操作（进程回环、会话枚举） |
| `LoopbackCapture.cs` | 进程级回环捕获 + 音量/静音追踪 + 麦克风混音 |
| `MicCapture.cs` / `ByteRing.cs` | 麦克风采集 + 线程安全环形缓冲 |
| `RecordingSession.cs` | 会话编排（声音+麦克风+抓帧+自动停止+收尾，GUI/CLI 共用） |
| `MainForm.cs` | WinForms 图形界面 |
| `Mta.cs` | 把音频 COM 激活统一切到 MTA 线程（GUI 是 STA） |
| `AudioSessionLister.cs` | 枚举正在发声的进程 + PID（`list`） |
| `ScreenCapture.cs` | 抓屏（整屏 / 显示器 / 区域 / 窗口）+ DPI 感知 |
| `SlideCapturer.cs` | 感知哈希识别换页，存 slide 图片 |
| `PdfBuilder.cs` / `DocxBuilder.cs` | 图片序列合成 PDF / Word（零依赖手写） |
| `WavWriter.cs` | 16-bit PCM WAV 写入 |
| `PathUtil.cs` | 归档目录、会议名清洗、会话命名 |
| `RegionSelector.cs` | 框选屏幕区域的遮罩窗口 |
| `Program.cs` | 入口：无参开 GUI，带参走命令行 |

---

## 许可证

本项目以 **[MIT License](LICENSE)** 开源，© 2026 Biao Wang。
你可以自由地使用、修改、分发，甚至用于商业项目，只需在副本中保留版权与许可声明即可。软件按"原样"提供，不附带任何担保。

## 欢迎贡献

发现 Bug 或想加功能，欢迎在 [Issues](https://github.com/biaowww/channel_recording/issues) 提出，或直接发 Pull Request 🙌
