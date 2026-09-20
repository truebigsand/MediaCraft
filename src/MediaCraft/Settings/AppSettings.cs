using MediaCraft.Logging;

namespace MediaCraft.Settings;

/// <summary>
/// 应用设置模型。新增字段时提升 <see cref="CurrentVersion"/> 并在 <see cref="Migrate"/> 中处理老数据。
/// </summary>
public sealed class AppSettings
{
    /// <summary>当前设置结构版本。</summary>
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>ffmpeg.exe 路径；空字符串表示自动探测。</summary>
    public string FfmpegPath { get; set; } = string.Empty;

    /// <summary>ffprobe.exe 路径；空字符串表示自动探测。</summary>
    public string FfprobePath { get; set; } = string.Empty;

    /// <summary>默认输出目录；空字符串表示输出到源文件所在目录。</summary>
    public string DefaultOutputDirectory { get; set; } = string.Empty;

    /// <summary>新增任务时使用的默认编码器。</summary>
    public string DefaultEncoderId { get; set; } = "h264_nvenc";

    /// <summary>默认容器。</summary>
    public string DefaultContainer { get; set; } = "mp4";

    /// <summary>默认输出文件名模板。</summary>
    public string NamingTemplate { get; set; } = "{name}_{encoder}_{quality}";

    /// <summary>默认并发任务数。</summary>
    public int DefaultConcurrency { get; set; } = 1;

    /// <summary>同名输出文件是否直接覆盖（默认关闭：自动加序号）。</summary>
    public bool AllowOverwrite { get; set; }

    /// <summary>MP4 输出是否添加 +faststart。</summary>
    public bool FastStart { get; set; } = true;

    /// <summary>启动时是否对编码器做一次功能探测。</summary>
    public bool ProbeEncodersOnStartup { get; set; } = true;

    /// <summary>是否持久化队列（关闭软件后保留未完成任务）。</summary>
    public bool PersistQueue { get; set; } = true;

    /// <summary>日志级别。</summary>
    public LogLevel LogLevel { get; set; } = LogLevel.Info;

    /// <summary>主窗口是否最大化启动。</summary>
    public bool StartMaximized { get; set; }

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
