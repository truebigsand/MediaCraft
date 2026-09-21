# MediaCraft — FFmpeg 转码工具（WPF）

MediaCraft 是一个 Windows 桌面端 FFmpeg 图形前端：把文件或文件夹拖进来，选一套参数（或预设），
队列会自动跑完，并且**在开跑前就把已知会翻车的参数组合拦下来**。

它不是「FFmpeg 命令生成器」，而是把转码当成批量作业来管理：每文件独立参数、队列并发与恢复、
逐项进度与诊断、预设可导出分享。

---

## 1. 功能速览

**文件与参数**
- 拖拽文件 / 文件夹（递归扫描）；列表显示分辨率、时长、帧率、大小、视频/音频编码、字幕数、参数摘要
- **右键菜单**：文件列表（加入队列——只加右键的这一个 / 复制路径 / 在资源管理器中显示 / 用默认程序打开 / 移除 / 清空）、
  队列列表（取消 / 重试 / 上移下移 / 复制 ffmpeg 命令行 / 复制源路径 / 打开输出文件或位置 / 移除 / 清空已完成）
- **每个文件各自持有一套完整参数**（不是全局共享），可单独调，也可「应用到选中 / 全部」
- **参数作用域在界面上永远可见**（参数面板顶部「参数作用域」卡片）：
  - 默认勾选「改参数时同步到列表里所有文件」——批量转码的常态是一批素材统一规格，
    拖一下质量滑块，列表里所有文件的参数一起变（轨道选择仍各自独立，因为流索引因文件而异）
  - 关掉这个开关就变成逐文件精调：只改当前选中的文件
  - 添加文件后会自动选中第一个，避免「参数改到了『新文件默认参数』模板上、文件实际没变」这种误解
- 参数双模式：**简单模式**一个质量滑块（实时显示映射结果，如「滑块 75 → `-cq 23 -preset p5``」），
  **高级模式**直给原生参数（码率控制、CRF/CQ、preset/tune/profile/level/GOP/像素格式、附加参数）
- 分辨率四种模式（保持 / 指定宽 / 指定高 / 框内只缩不放）、帧率、硬解方式可选
- 音轨逐条处理：直通 / 重编码（AAC、OPUS、MP3、AC3、FLAC、Vorbis、ALAC、PCM 16/24/32bit）
  + 码率、声道、采样率；**码率控件会随编码器变形态**——有损自由填、AC3 只给合法档位（实测非法值会被
  ffmpeg 静默取整）、无损不显示码率框而是直接算出实际码率与体积（如「24bit · 2ch · 44100 Hz → 2116 kbps，约 15.5 MB/分钟」）；
  字幕轨：内封保留 / **烧入画面**（字体、字号、颜色、描边、位置）/ 提取为 srt·ass·vtt / 丢弃
- 外挂字幕文件可直接烧入；字幕文件本身（.srt/.ass/.vtt）可作为输入做格式转换
- 输出：命名模板（`{name}_{encoder}_{quality}` 等占位符）、输出目录、防覆盖自动加序号、`+faststart`

**队列**
- 并发数可调（1-8）；暂停 = 不再派发新任务（ffmpeg 无法安全中途挂起，界面文案如实说明）
- 排序、取消当前/全部、失败重试、清除已完成
- 逐项进度条 + 总进度 + 速度 + 剩余时间 + 用时；完整命令行可一键复制
- **队列持久化**：退出后重开自动恢复未完成任务（源文件已消失的不恢复）
- 成败按**进程退出码**判定，并清理失败残留文件（ffmpeg 失败时会留下 0 字节同名文件）

**诊断**
- 启动时逐个「真跑一帧」探测编码器可用性（编译进去 ≠ 能跑），设置页给出可用/不可用清单
- **开跑前预检**：能自动修的修掉并记录，不能修的拦下（详见 docs/spec.md 的规则清单）
- 完整日志写 `%AppData%\MediaCraft\logs`（按天切分、保留 7 天），界面可折叠日志面板
- `--selftest` 无头自检：把整条链路真跑一遍并出报告（见第 5 节）
- WPF 绑定错误也会写进日志（绑定失败默认静默，这类问题只有真点一下才暴露）

**编码器**（本机实测全部可用，取决于你的硬件）
NVIDIA NVENC：`h264_nvenc` `hevc_nvenc` `av1_nvenc`　|　Intel QSV：`h264_qsv` `hevc_qsv` `av1_qsv` `vp9_qsv`　|
CPU 软编：`libx264` `libx265` `libsvtav1` `libaom-av1`

**预设**
14 个内置预设（NVENC H.264/HEVC/AV1、QSV、x264/x265、SVT-AV1、转 1080p、手机友好 720p、
纯封装不重编码、提取音频、烧入第一条字幕、提取字幕），可另存自定义、重命名、删除、
**导入导出 JSON** 分享到别的机器。

---

## 2. 环境要求

| 项 | 要求 |
|---|---|
| 操作系统 | Windows 10 / 11（x64） |
| 运行（自包含版） | 无额外要求 |
| 运行（框架依赖版） | .NET 10 Desktop Runtime |
| 编译 | .NET 10 SDK |
| FFmpeg | **不内置**，运行时自动探测：设置里指定的路径 → `PATH` → 常见安装位置（Scoop / winget / Chocolatey / 常见目录）。找不到时启动会提示并在设置页可手动指定 |

推荐用带硬件的完整版 FFmpeg（例：`scoop install ffmpeg`，或 gyan.dev 的 full build）。
没有硬件编码器的机器上，11 个编码器里软编那几个仍然可用。

---

## 3. 构建与运行

```powershell
# 编译
dotnet build MediaCraft.slnx -c Debug

# 运行
dotnet run --project src/MediaCraft/MediaCraft.csproj

# 直接打开文件/文件夹（也支持资源管理器「打开方式」）
.\src\MediaCraft\bin\Debug\net10.0-windows\MediaCraft.exe "D:\videos" "E:\片子.mkv"
```

---

## 4. 发布

```powershell
# 自包含单文件（约 150 MB，目标机器无需装 .NET）→ 桌面\Claude Outputs\MediaCraft\standalone
powershell -ExecutionPolicy Bypass -File scripts\publish-standalone.ps1

# 框架依赖（体积小，需要 .NET 10 Desktop Runtime）→ 桌面\Claude Outputs\MediaCraft\framework-dependent
powershell -ExecutionPolicy Bypass -File scripts\publish-fd.ps1
```

脚本均为纯 ASCII（Windows PowerShell 5.1 会把无 BOM 的 UTF-8 当 GBK 读，中文会乱码）。
发布产物**不含 ffmpeg**，目标机器需要能访问到 ffmpeg（或在设置页指定路径）。

---

## 5. 自检

```powershell
# 完整自检：真跑 42 项（容器兼容性矩阵 135 个组合，含多音轨能力）、11 个编码器、硬解、缩放、烧字幕、提取、预检、预设逻辑…）
.\src\MediaCraft\bin\Debug\net10.0-windows\MediaCraft.exe --selftest all "$env:TEMP\report.txt"

# 快速自检：只做定位 → 能力探测 → 编码器功能探测 → 生成测试素材（约 15 秒）
.\src\MediaCraft\bin\Debug\net10.0-windows\MediaCraft.exe --selftest quick "$env:TEMP\report.txt"

# 纯逻辑自检：只跑预设/参数逻辑，不需要 ffmpeg（CI 用这个）
.\src\MediaCraft\bin\Debug\net10.0-windows\MediaCraft.exe --selftest logic "$env:TEMP\report.txt"
```

退出码 0 = 全部通过。图形界面里也有「设置 → 运行快速自检」按钮，跑完自动打开报告。

其中「容器兼容性矩阵」用例会把 117 个「容器 × 编码」组合逐个真跑一遍，校验代码里的兼容性表与 ffmpeg 实际行为一致——这张表曾经写错，把合法的 PCM-in-MP4 判成非法并因此强制重编码，代价是用户丢画质，所以现在由自检守住。
**换一台机器想确认哪些编码器能用，跑一次快速自检即可。**

---

## 6. 架构速览

```
src/MediaCraft/
├─ Program.cs              入口：单实例（Mutex + EventWaitHandle）、--selftest 分支、命令行文件参数
├─ App.xaml(.cs)           应用壳：手工 InitializeComponent、全局异常、绑定错误监听
├─ MainWindow.xaml(.cs)    四个标签页 + 可折叠日志面板 + 状态栏（关窗即退出，有任务时二次确认）
├─ Ffmpeg/                 FFmpeg 层（不认识界面）
│  ├─ FfmpegLocator        定位 ffmpeg / ffprobe（设置 → PATH → 常见位置）
│  ├─ FfmpegCapabilities   -encoders/-hwaccels 解析 + 「真跑一帧」功能探测
│  ├─ FfmpegContext        路径与能力的共享上下文 + 就绪状态
│  ├─ MediaProbe            ffprobe JSON 解析 → MediaInfo / MediaStreamInfo
│  ├─ EncoderCatalog       11 个编码器 + 9 种容器的参数元数据 + 滑块到原生值的映射
│  ├─ FilterBuilder        滤镜链与字幕滤镜路径转义（含实测结论）
│  ├─ TranscodeCommandBuilder  参数 → ffmpeg 参数数组（含硬解三态分流、多轨映射）
│  ├─ PreflightValidator   开跑前预检规则
│  ├─ TranscodeRunner      进程执行：进度解析、取消、残留清理
│  ├─ ProgressParser       -progress pipe:1 解析
│  └─ SelfTest             无头自检
├─ Media/                  领域模型：TranscodeParams、输出路径、文件夹扫描
├─ Queue/                  队列引擎与持久化（不含界面）
├─ Presets/                预设模型与仓库（内置 + 用户 + 导入导出）
├─ Settings/               设置模型与持久化
├─ ViewModels/             界面视图模型（CommunityToolkit.Mvvm）
└─ Views/                  四个页面（XAML）
```

依赖方向是单向的：`Views → ViewModels → (Queue | Presets | Media) → Ffmpeg`，`Ffmpeg` 不引用任何界面类型。
唯一的 NuGet 依赖是 `CommunityToolkit.Mvvm`。

数据位置：

| 内容 | 位置 |
|---|---|
| 设置 | `%AppData%\MediaCraft\settings.json` |
| 预设 | `%AppData%\MediaCraft\presets.json` |
| 队列 | `%AppData%\MediaCraft\queue.json` |
| 日志 | `%AppData%\MediaCraft\logs\app-yyyyMMdd.log`（保留 7 天） |

---

## 7. 已知限制（v1）

- **只驱动本机 ffmpeg**：不支持远程/SSH 执行（执行层已抽象，后续可加）
- **暂停不会挂起正在跑的 ffmpeg**：暂停仅停止派发新任务；取消会杀进程树并清理残留
- 不做色调映射：HDR 源转 SDR 会发灰（预检会提示）
- 图形字幕（PGS/DVD）不能烧入、也不能提取为文本（需要 OCR，预检会拦下）
- 不做两遍编码、不做画面滤镜（降噪/去隔行等）、不做剪辑合并
- 无托盘常驻：关窗即退出

---

## 8. 工程纪律

1. 后台回调一律切回 UI 线程（队列可见状态只在 UI 线程修改），UI 线程不做慢调用
2. 锁内不调外部组件
3. 成败只看**进程退出码**，不按「文件是否存在」判断（ffmpeg 失败时会留下 0 字节同名文件）
4. 删除残留前先确认「运行前不存在」，绝不误删用户原有文件
5. 输出路径与源文件相同时预检直接拦下
6. `scripts/*.ps1` 必须纯 ASCII
7. 设置保存用 500ms 防抖 + 退出强制保存

设计细节与实测记录见 [docs/spec.md](docs/spec.md)。
