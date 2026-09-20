using CommunityToolkit.Mvvm.ComponentModel;
using MediaCraft.Ffmpeg;
using MediaCraft.Queue;
using MediaCraft.Settings;

namespace MediaCraft.ViewModels;

/// <summary>
/// 主视图模型：持有各标签页的视图模型，并把全局状态（ffmpeg / 队列）暴露给状态栏。
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly FfmpegContext _ffmpeg;
    private readonly TranscodeQueue _queue;

    public MainViewModel(FfmpegContext ffmpeg, SettingsService settings, TranscodeQueue queue)
    {
        _ffmpeg = ffmpeg;
        _queue = queue;

        Transcode = new TranscodeViewModel(ffmpeg, settings, queue);

        _ffmpeg.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(FfmpegStatus));
            OnPropertyChanged(nameof(ProbeStatus));
        };

        _queue.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(QueueStatus));
            OnPropertyChanged(nameof(QueueBusy));
        };
    }

    /// <summary>转码页。</summary>
    public TranscodeViewModel Transcode { get; }

    /// <summary>状态栏：ffmpeg 状态。</summary>
    public string FfmpegStatus => _ffmpeg.StatusText;

    /// <summary>状态栏：编码器探测进度。</summary>
    public string ProbeStatus => _ffmpeg.ProbeText;

    public bool HasProbeStatus => !string.IsNullOrEmpty(_ffmpeg.ProbeText);

    /// <summary>状态栏：队列状态。</summary>
    public string QueueStatus => _queue.SummaryText;

    /// <summary>是否有任务在跑（关窗确认用）。</summary>
    public bool QueueBusy => _queue.HasActiveJobs;

    /// <summary>队列引用（供壳层与后续页面使用）。</summary>
    public TranscodeQueue Queue => _queue;
}
