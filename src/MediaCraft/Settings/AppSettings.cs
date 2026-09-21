using CommunityToolkit.Mvvm.ComponentModel;
using MediaCraft.Logging;

namespace MediaCraft.Settings;

/// <summary>
/// 应用设置模型。新增字段时提升 <see cref="CurrentVersion"/> 并在 <see cref="Migrate"/> 中处理老数据。
/// 做成可观察对象，这样设置页的改动能立即反映到其它界面（状态栏、队列等）。
/// </summary>
public sealed partial class AppSettings : ObservableObject
{
    /// <summary>当前设置结构版本。</summary>
    public const int CurrentVersion = 1;

    /// <summary>设置结构版本。</summary>
    [ObservableProperty]
    private int _version = CurrentVersion;

    /// <summary>ffmpeg.exe 路径；空字符串表示自动探测。</summary>
    [ObservableProperty]
    private string _ffmpegPath = string.Empty;

    /// <summary>ffprobe.exe 路径；空字符串表示自动探测。</summary>
    [ObservableProperty]
    private string _ffprobePath = string.Empty;

    /// <summary>默认输出目录；空字符串表示输出到源文件所在目录。</summary>
    [ObservableProperty]
    private string _defaultOutputDirectory = string.Empty;

    /// <summary>新增任务时使用的默认编码器。</summary>
    [ObservableProperty]
    private string _defaultEncoderId = "h264_nvenc";

    /// <summary>默认容器。</summary>
    [ObservableProperty]
    private string _defaultContainer = "mp4";

    /// <summary>默认输出文件名模板。</summary>
    [ObservableProperty]
    private string _namingTemplate = "{name}_{encoder}_{quality}";

    /// <summary>默认并发任务数。</summary>
    [ObservableProperty]
    private int _defaultConcurrency = 1;

    /// <summary>
    /// 改参数时是否同步到列表里所有文件。
    /// 默认开启：批量转码的常态是「一批素材统一规格」，每文件独立参数的差异主要体现在轨道选择上。
    /// </summary>
    [ObservableProperty]
    private bool _syncParamsToAllFiles = true;

    /// <summary>同名输出文件是否直接覆盖（默认关闭：自动加序号）。</summary>
    [ObservableProperty]
    private bool _allowOverwrite;

    /// <summary>MP4 输出是否添加 +faststart。</summary>
    [ObservableProperty]
    private bool _fastStart = true;

    /// <summary>启动时是否对编码器做一次功能探测。</summary>
    [ObservableProperty]
    private bool _probeEncodersOnStartup = true;

    /// <summary>是否持久化队列（关闭软件后保留未完成任务）。</summary>
    [ObservableProperty]
    private bool _persistQueue = true;

    /// <summary>日志级别。</summary>
    [ObservableProperty]
    private LogLevel _logLevel = LogLevel.Info;

    /// <summary>主窗口是否最大化启动。</summary>
    [ObservableProperty]
    private bool _startMaximized;

    /// <summary>首次加载的默认设置。</summary>
    public static AppSettings NewDefault() => new();

    /// <summary>旧版本设置的迁移钩子。</summary>
    public void Migrate()
    {
        if (Version < 1)
        {
            Version = 1;
        }

        Version = CurrentVersion;
    }

    /// <summary>非法值修正。</summary>
    public void Sanitize()
    {
        if (DefaultConcurrency < 1)
        {
            DefaultConcurrency = 1;
        }

        if (DefaultConcurrency > 8)
        {
            DefaultConcurrency = 8;
        }

        if (string.IsNullOrWhiteSpace(NamingTemplate))
        {
            NamingTemplate = "{name}_{encoder}_{quality}";
        }

        if (string.IsNullOrWhiteSpace(DefaultContainer))
        {
            DefaultContainer = "mp4";
        }

        if (string.IsNullOrWhiteSpace(DefaultEncoderId))
        {
            DefaultEncoderId = "h264_nvenc";
        }

        FfmpegPath ??= string.Empty;
        FfprobePath ??= string.Empty;
        DefaultOutputDirectory ??= string.Empty;
    }
}
