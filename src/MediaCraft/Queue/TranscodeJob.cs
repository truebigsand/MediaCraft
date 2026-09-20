using System.IO;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using MediaCraft.Media;

namespace MediaCraft.Queue;

/// <summary>任务状态。</summary>
public enum JobState
{
    /// <summary>等待执行。</summary>
    Pending = 0,

    /// <summary>分析 / 预检中。</summary>
    Preparing,

    /// <summary>执行中。</summary>
    Running,

    Completed,

    Failed,

    Canceled,
}

/// <summary>
/// 一个转码任务：一个源文件 + 它自己的一套参数 + 运行期状态。
/// </summary>
public sealed partial class TranscodeJob : ObservableObject
{
    public TranscodeJob(string sourcePath, TranscodeParams parameters)
    {
        SourcePath = sourcePath;
        Parameters = parameters;
    }

    /// <summary>任务标识（持久化与 UI 定位用）。</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public string SourcePath { get; init; }

    [JsonIgnore]
    public string SourceName => Path.GetFileName(SourcePath);

    /// <summary>该任务自己的参数（每文件独立）。</summary>
    public TranscodeParams Parameters { get; init; }

    /// <summary>加入队列的时间。</summary>
    public DateTime CreatedAt { get; init; } = DateTime.Now;

    // ── 运行期状态（不进持久化）──

    [ObservableProperty]
    [property: JsonIgnore]
    private JobState _state = JobState.Pending;

    /// <summary>总体进度 0-100；-1 表示不确定。</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private double _progressPercent;

    [ObservableProperty]
    [property: JsonIgnore]
    private string _statusText = "等待中";

    [ObservableProperty]
    [property: JsonIgnore]
    private string _outputPath = string.Empty;

    [ObservableProperty]
    [property: JsonIgnore]
    private string _speedText = string.Empty;

    [ObservableProperty]
    [property: JsonIgnore]
    private string _etaText = string.Empty;

    [ObservableProperty]
    [property: JsonIgnore]
    private string _elapsedText = string.Empty;

    [ObservableProperty]
    [property: JsonIgnore]
    private string _errorMessage = string.Empty;

    /// <summary>最近一次失败时的诊断行（ffmpeg stderr 尾部）。</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private string _errorDetail = string.Empty;

    /// <summary>正在执行的完整命令行（可复制）。</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private string _commandLine = string.Empty;

    /// <summary>参数摘要（列表里显示）。</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private string _parametersSummary = string.Empty;

    /// <summary>预检问题摘要（跑之前显示）。</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private string _preflightSummary = string.Empty;

    /// <summary>预检是否有阻断性错误。</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private bool _hasBlockingError;

    [JsonIgnore]
    public bool IsFinished => State is JobState.Completed or JobState.Failed or JobState.Canceled;

    [JsonIgnore]
    public bool IsActive => State is JobState.Preparing or JobState.Running;

    /// <summary>状态文字（中文）。</summary>
    [JsonIgnore]
    public string StateText => State switch
    {
        JobState.Pending => "等待中",
        JobState.Preparing => "分析中",
        JobState.Running => "执行中",
        JobState.Completed => "已完成",
        JobState.Failed => "失败",
        JobState.Canceled => "已取消",
        _ => "未知",
    };

    /// <summary>进度条是否应该显示为不确定状态。</summary>
    [JsonIgnore]
    public bool IsProgressIndeterminate => State == JobState.Preparing || (State == JobState.Running && ProgressPercent < 0);

    partial void OnStateChanged(JobState value)
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(IsFinished));
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(IsProgressIndeterminate));
    }

    partial void OnProgressPercentChanged(double value) => OnPropertyChanged(nameof(IsProgressIndeterminate));

    /// <summary>标记为待执行（重试用）。</summary>
    public void ResetForRetry()
    {
        State = JobState.Pending;
        ProgressPercent = 0;
        StatusText = "等待中";
        ErrorMessage = string.Empty;
        ErrorDetail = string.Empty;
        SpeedText = string.Empty;
        EtaText = string.Empty;
        ElapsedText = string.Empty;
    }
}
