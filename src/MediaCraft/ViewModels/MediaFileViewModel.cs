using CommunityToolkit.Mvvm.ComponentModel;
using MediaCraft.Ffmpeg;
using MediaCraft.Media;

namespace MediaCraft.ViewModels;

/// <summary>文件分析状态。</summary>
public enum AnalysisState
{
    Pending = 0,
    Analyzing,
    Done,
    Failed,
}

/// <summary>文件列表中的一行。</summary>
public sealed partial class MediaFileViewModel : ObservableObject
{
    public MediaFileViewModel(string path)
    {
        Path = path;
    }

    /// <summary>完整路径。</summary>
    public string Path { get; }

    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>
    /// 列表行的可访问名称。不重写的话屏幕阅读器（以及 UI 自动化）读到的会是
    /// `MediaCraft.ViewModels.MediaFileViewModel` 这种类名。
    /// </summary>
    public override string ToString() => FileName;

    public string DirectoryName => System.IO.Path.GetDirectoryName(Path) ?? string.Empty;

    [ObservableProperty]
    private AnalysisState _state = AnalysisState.Pending;

    [ObservableProperty]
    private string _resolution = "等待分析";

    [ObservableProperty]
    private string _duration = "—";

    [ObservableProperty]
    private string _frameRate = "—";

    [ObservableProperty]
    private string _sizeText = "—";

    [ObservableProperty]
    private string _videoCodec = "—";

    [ObservableProperty]
    private string _audioText = "—";

    [ObservableProperty]
    private string _subtitleText = "—";

    /// <summary>该文件自己的参数摘要。</summary>
    [ObservableProperty]
    private string _parametersSummary = string.Empty;

    /// <summary>该文件自己的参数（每文件独立）。</summary>
    [ObservableProperty]
    private TranscodeParams _parameters = new();

    /// <summary>ffprobe 结果。</summary>
    public MediaInfo? Info { get; private set; }

    /// <summary>分析失败原因。</summary>
    [ObservableProperty]
    private string _analysisError = string.Empty;

    /// <summary>是否可用于排队（分析成功）。</summary>
    public bool IsReady => State == AnalysisState.Done && Info is not null;

    public string KindText => MediaProbe.KindOf(Path) switch
    {
        MediaFileKind.Video => "视频",
        MediaFileKind.Audio => "音频",
        MediaFileKind.Subtitle => "字幕",
        _ => "未知",
    };

    /// <summary>分析完成：填充列表列与轨道默认值。</summary>
    public void ApplyInfo(MediaInfo info)
    {
        Info = info;
        State = AnalysisState.Done;
        AnalysisError = string.Empty;

        Resolution = info.VideoStream?.ResolutionText ?? (info.HasAudio ? "音频文件" : "—");
        Duration = info.DurationText;
        FrameRate = info.VideoStream?.FrameRateText ?? "—";
        SizeText = info.SizeText;
        VideoCodec = info.VideoStream is null
            ? "—"
            : info.VideoStream.CodecName + (string.IsNullOrWhiteSpace(info.VideoStream.Profile) ? string.Empty : $" ({info.VideoStream.Profile})");
        AudioText = info.AudioStreams.Count == 0
            ? "—"
            : info.AudioStreams.Count == 1
                ? $"{info.AudioStreams[0].CodecName} {info.AudioStreams[0].ChannelsText}"
                : $"{info.AudioStreams.Count} 条（{info.AudioStreams[0].CodecName}…）";
        SubtitleText = info.SubtitleStreams.Count == 0 ? "—" : $"{info.SubtitleStreams.Count} 条";

        // 轨道选择跟着文件走
        Parameters.InitializeTracksFrom(info, resetExisting: true);
        RefreshSummary();
    }

    /// <summary>分析失败。</summary>
    public void ApplyFailure(string message)
    {
        State = AnalysisState.Failed;
        AnalysisError = message;
        Resolution = "分析失败";
        Duration = "—";
        FrameRate = "—";
        SizeText = SafeSizeText();
    }

    /// <summary>刷新参数摘要显示。</summary>
    public void RefreshSummary()
    {
        // 字幕文件的参数摘要要按「字幕转换」描述：这时编码器/容器参数根本不参与执行
        ParametersSummary = Info is not null && Ffmpeg.TranscodeCommandBuilder.IsSubtitleOnly(Info)
            ? $"字幕转换 → {Parameters.SubtitleConvertFormat.ToString().ToUpperInvariant()}"
            : Parameters.Summary;

        OnPropertyChanged(nameof(IsReady));
    }

    partial void OnStateChanged(AnalysisState value) => OnPropertyChanged(nameof(IsReady));

    private string SafeSizeText()
    {
        try
        {
            return System.IO.File.Exists(Path) ? MediaFormat.FormatSize(new System.IO.FileInfo(Path).Length) : "—";
        }
        catch (Exception)
        {
            return "—";
        }
    }
}
