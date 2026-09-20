using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using MediaCraft.Logging;
using MediaCraft.Settings;

namespace MediaCraft.Ffmpeg;

/// <summary>
/// 运行期共享上下文：ffmpeg 路径、能力探测结果、就绪状态。
/// 界面用它做预检和参数联动，队列用它执行任务。
/// </summary>
public sealed partial class FfmpegContext : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly SemaphoreSlim _initGate = new(1, 1);

    public FfmpegContext(SettingsService settings)
    {
        _settings = settings;
    }

    /// <summary>是否已完成初始化（找得到 ffmpeg 且能力探测完成）。</summary>
    [ObservableProperty]
    private bool _isReady;

    /// <summary>状态栏文案。</summary>
    [ObservableProperty]
    private string _statusText = "未检测";

    /// <summary>编码器功能探测进度文案。</summary>
    [ObservableProperty]
    private string _probeText = string.Empty;

    /// <summary>解析出的路径；未就绪时为 null。</summary>
    public FfmpegPaths? Paths { get; private set; }

    /// <summary>能力探测结果；未就绪时为 null。</summary>
    public FfmpegCapabilities? Capabilities { get; private set; }

    /// <summary>初始化状态变化（用于界面上报错误）。</summary>
    public event Action<string?>? InitializationFailed;

    /// <summary>
    /// 初始化：定位 ffmpeg → 探测能力 →（可选）逐个真跑一帧探测编码器。
    /// </summary>
    public async Task<bool> InitializeAsync(bool probeEncoders, CancellationToken cancellationToken = default)
    {
        await _initGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IsReady = false;
            ProbeText = "正在检测 ffmpeg…";
            StatusText = "检测中…";

            var settings = _settings.Current;
            var paths = await FfmpegLocator
                .ResolveAsync(settings.FfmpegPath, settings.FfprobePath, cancellationToken)
                .ConfigureAwait(false);

            if (paths is null)
            {
                Paths = null;
                Capabilities = null;
                StatusText = "未找到 ffmpeg";
                ProbeText = string.Empty;
                InitializationFailed?.Invoke(
                    "没有找到 ffmpeg / ffprobe。\n\n" +
                    "请安装 ffmpeg（例如 scoop install ffmpeg）或在本工具的「设置」页手动指定 ffmpeg.exe 路径。");
                return false;
            }

            Paths = paths;
            var capabilities = await FfmpegCapabilities.LoadAsync(paths, cancellationToken).ConfigureAwait(false);

            if (probeEncoders)
            {
                ProbeText = "正在逐个探测编码器可用性…";
                var progress = new Progress<(int Done, int Total, string EncoderId)>(p =>
                    ProbeText = $"编码器探测 {p.Done}/{p.Total}：{p.EncoderId}");

                await capabilities
                    .ProbeFunctionalAsync(paths, EncoderCatalog.All.Select(e => e.Id), progress, cancellationToken)
                    .ConfigureAwait(false);
            }

            Capabilities = capabilities;
            IsReady = true;
            ProbeText = string.Empty;

            var available = EncoderCatalog.All.Count(e => capabilities.IsEncoderAvailable(e.Id));
            StatusText = $"{ShortVersion(paths.Version)} · {available} 个编码器可用 · {paths.Source}";
            AppLog.Info($"ffmpeg 就绪：{StatusText}", "FFmpeg");
            InitializationFailed?.Invoke(null);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "FfmpegContext.Initialize");
            StatusText = "检测失败";
            ProbeText = string.Empty;
            InitializationFailed?.Invoke("检测 ffmpeg 时出错：" + ex.Message);
            return false;
        }
        finally
        {
            _initGate.Release();
        }
    }

    /// <summary>探测指定文件（供文件列表与队列共用）。</summary>
    public async Task<MediaInfo?> ProbeAsync(string file, CancellationToken cancellationToken = default)
    {
        var paths = Paths;
        if (paths is null)
        {
            return null;
        }

        return await MediaProbe.ProbeAsync(paths.Ffprobe, file, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>ffmpeg 是否位于该文件所在盘符之外（跨盘输出会慢，仅用于提示）。</summary>
    public static bool IsCrossVolume(string sourcePath, string outputPath)
    {
        try
        {
            var sourceRoot = Path.GetPathRoot(Path.GetFullPath(sourcePath));
            var outputRoot = Path.GetPathRoot(Path.GetFullPath(outputPath));
            return sourceRoot is not null && outputRoot is not null &&
                   !string.Equals(sourceRoot, outputRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string ShortVersion(string versionLine)
    {
        // "ffmpeg version 8.1.2-full_build-www.gyan.dev Copyright …" → "ffmpeg 8.1.2-full_build"
        var text = versionLine.Replace("ffmpeg version ", string.Empty, StringComparison.OrdinalIgnoreCase);
        var space = text.IndexOf(' ');
        if (space > 0)
        {
            text = text[..space];
        }

        return "ffmpeg " + text;
    }
}
