using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MediaCraft.Ffmpeg;
using MediaCraft.Logging;
using MediaCraft.Media;
using MediaCraft.Settings;

namespace MediaCraft.Queue;

/// <summary>
/// 转码队列引擎：并发调度、进度聚合、暂停/取消、失败重试、状态持久化。
///
/// 线程纪律：<see cref="Jobs"/> 与所有任务的可见状态只在 UI 线程上修改，
/// 后台流程通过 <see cref="Ui"/> 转发；耗时操作（ffprobe / ffmpeg）全部在后台。
/// </summary>
public sealed partial class TranscodeQueue : ObservableObject
{
    private readonly FfmpegContext _ffmpeg;
    private readonly SettingsService _settings;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _saveTimer;
    private readonly Dictionary<string, CancellationTokenSource> _cancellations = new(StringComparer.Ordinal);

    private int _activeCount;
    private bool _loaded;

    public TranscodeQueue(FfmpegContext ffmpeg, SettingsService settings, Dispatcher dispatcher)
    {
        _ffmpeg = ffmpeg;
        _settings = settings;
        _dispatcher = dispatcher;

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            QueuePersistence.Save(Jobs);
        };

        MaxConcurrency = Math.Clamp(settings.Current.DefaultConcurrency, 1, 8);
    }

    /// <summary>队列内容变化（UI 用于刷新统计）。</summary>
    public event Action? Changed;

    /// <summary>队列全局状态文案变化。</summary>
    public ObservableCollection<TranscodeJob> Jobs { get; } = [];

    /// <summary>是否已暂停（暂停 = 当前任务跑完，但不再派发新任务）。</summary>
    [ObservableProperty]
    private bool _isPaused;

    /// <summary>并发任务数（1-8）。</summary>
    [ObservableProperty]
    private int _maxConcurrency = 1;

    /// <summary>队列统计文案。</summary>
    [ObservableProperty]
    private string _summaryText = "空闲";

    /// <summary>是否有任务在跑。</summary>
    public bool HasActiveJobs => Jobs.Any(j => j.IsActive);

    /// <summary>正在运行的数量。</summary>
    public int ActiveCount => _activeCount;

    /// <summary>等待中的数量。</summary>
    public int PendingCount => Jobs.Count(j => j.State == JobState.Pending);

    /// <summary>失败的数量。</summary>
    public int FailedCount => Jobs.Count(j => j.State == JobState.Failed);

    /// <summary>从磁盘恢复队列（仅一次）。</summary>
    public void LoadPersisted()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        if (!_settings.Current.PersistQueue)
        {
            return;
        }

        foreach (var job in QueuePersistence.Load())
        {
            job.ParametersSummary = job.Parameters.Summary;
            Jobs.Add(job);
        }

        UpdateSummary();
    }

    /// <summary>加入任务并开始调度。</summary>
    public void Enqueue(IEnumerable<TranscodeJob> jobs)
    {
        var added = 0;
        foreach (var job in jobs)
        {
            job.ParametersSummary = job.Parameters.Summary;
            job.StatusText = "等待中";
            Jobs.Add(job);
            added++;
        }

        if (added == 0)
        {
            return;
        }

        AppLog.Info($"已加入 {added} 个任务，队列共 {Jobs.Count} 项", "Queue");
        IsPaused = false;
        SaveDebounced();
        Dispatch();
    }

    /// <summary>开始 / 继续执行。</summary>
    public void Start()
    {
        IsPaused = false;
        Dispatch();
    }

    /// <summary>暂停：不再派发新任务（正在跑的会跑完，ffmpeg 无法安全地中途挂起）。</summary>
    public void Pause()
    {
        IsPaused = true;
        UpdateSummary();
        AppLog.Info("队列已暂停（正在执行的任务会跑完）", "Queue");
    }

    /// <summary>取消单个任务（等待中直接标记，执行中则杀进程并清理残留）。</summary>
    public void Cancel(TranscodeJob job)
    {
        if (job.IsFinished)
        {
            return;
        }

        if (_cancellations.TryGetValue(job.Id, out var cts))
        {
            job.StatusText = "正在取消…";
            cts.Cancel();
            return;
        }

        job.State = JobState.Canceled;
        job.StatusText = "已取消";
        SaveDebounced();
        Dispatch();
    }

    /// <summary>取消所有未完成任务并停止派发。</summary>
    public void CancelAll()
    {
        IsPaused = true;
        foreach (var job in Jobs.Where(j => !j.IsFinished).ToArray())
        {
            Cancel(job);
        }
    }

    /// <summary>重试所有失败任务。</summary>
    public void RetryFailed()
    {
        var retried = 0;
        foreach (var job in Jobs.Where(j => j.State == JobState.Failed).ToArray())
        {
            job.ResetForRetry();
            retried++;
        }

        if (retried > 0)
        {
            AppLog.Info($"重试 {retried} 个失败任务", "Queue");
            IsPaused = false;
            SaveDebounced();
            Dispatch();
        }
    }

    /// <summary>从队列移除一项（执行中的需先取消）。</summary>
    public void Remove(TranscodeJob job)
    {
        if (job.IsActive)
        {
            return;
        }

        Jobs.Remove(job);
        SaveDebounced();
        UpdateSummary();
    }

    /// <summary>清除已完成/已取消/失败的任务。</summary>
    public void ClearFinished()
    {
        foreach (var job in Jobs.Where(j => j.IsFinished).ToArray())
        {
            Jobs.Remove(job);
        }

        SaveDebounced();
        UpdateSummary();
    }

    /// <summary>调整任务顺序（等待中的任务才能移动）。</summary>
    public void MoveJob(TranscodeJob job, int offset)
    {
        var index = Jobs.IndexOf(job);
        if (index < 0)
        {
            return;
        }

        var target = index + offset;
        if (target < 0 || target >= Jobs.Count)
        {
            return;
        }

        Jobs.Move(index, target);
        SaveDebounced();
    }

    /// <summary>立即落盘（退出前调用）。</summary>
    public void SaveNow()
    {
        _saveTimer.Stop();
        if (_settings.Current.PersistQueue)
        {
            QueuePersistence.Save(Jobs);
        }
    }

    /// <summary>清空全部队列内容并删除持久化文件。</summary>
    public void ClearAll()
    {
        CancelAll();
        Jobs.Clear();
        QueuePersistence.Delete();
        UpdateSummary();
    }

    partial void OnMaxConcurrencyChanged(int value)
    {
        var clamped = Math.Clamp(value, 1, 8);
        if (clamped != value)
        {
            MaxConcurrency = clamped;
            return;
        }

        _settings.Current.DefaultConcurrency = clamped;
        _settings.ScheduleSave();
        Dispatch();
    }

    partial void OnIsPausedChanged(bool value) => UpdateSummary();

    /// <summary>派发任务：在并发上限内把等待中的任务逐个启动。</summary>
    private void Dispatch()
    {
        Ui(() =>
        {
            if (!_ffmpeg.IsReady || IsPaused)
            {
                UpdateSummary();
                return;
            }

            while (_activeCount < MaxConcurrency)
            {
                var next = Jobs.FirstOrDefault(j => j.State == JobState.Pending);
                if (next is null)
                {
                    break;
                }

                _activeCount++;
                next.State = JobState.Preparing;
                next.StatusText = "准备中…";
                next.ProgressPercent = 0;

                var cts = new CancellationTokenSource();
                _cancellations[next.Id] = cts;
                _ = Task.Run(() => RunJobAsync(next, cts.Token));
            }

            UpdateSummary();
        });
    }

    private async Task RunJobAsync(TranscodeJob job, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var paths = _ffmpeg.Paths;
        var capabilities = _ffmpeg.Capabilities;

        try
        {
            if (paths is null)
            {
                Fail(job, "ffmpeg 未就绪", string.Empty);
                return;
            }

            var info = await _ffmpeg.ProbeAsync(job.SourcePath, cancellationToken).ConfigureAwait(false);
            if (info is null)
            {
                Fail(job, "无法读取源文件信息（ffprobe 失败或文件已损坏）", string.Empty);
                return;
            }

            // 恢复的队列任务可能没有轨道列表，这里按当前文件补齐
            job.Parameters.InitializeTracksFrom(info);

            var outputPath = OutputPathBuilder.Build(info, job.Parameters, _settings.Current.DefaultOutputDirectory);
            var preflight = PreflightValidator.Validate(info, job.Parameters, capabilities, outputPath);

            Ui(() =>
            {
                job.OutputPath = outputPath;
                job.PreflightSummary = preflight.Summary;
                job.HasBlockingError = preflight.HasBlockingError;
            });

            foreach (var issue in preflight.Issues)
            {
                var method = issue.Severity switch
                {
                    IssueSeverity.Error => LogLevel.Error,
                    IssueSeverity.Warning => LogLevel.Warn,
                    _ => LogLevel.Info,
                };
                AppLog.Log(method, $"[预检][{job.SourceName}] {issue.DisplayText}", "Queue");
            }

            if (preflight.HasBlockingError)
            {
                var detail = string.Join("；", preflight.Issues
                    .Where(i => i.Severity == IssueSeverity.Error && !i.WasFixed)
                    .Select(i => i.DisplayText));
                Fail(job, "预检未通过", detail);
                return;
            }

            var encoder = EncoderCatalog.Get(preflight.Effective.EncoderId);
            var tempDirectory = Path.Combine(Path.GetTempPath(), "MediaCraft", job.Id);
            var plan = TranscodeCommandBuilder.Build(
                info,
                preflight.Effective,
                encoder,
                preflight.EffectiveAccel,
                outputPath,
                tempDirectory);

            var visibleSteps = plan.VisibleSteps.ToArray();
            var trackedSteps = visibleSteps.Count(s => s.TrackProgress);

            Ui(() =>
            {
                job.State = JobState.Running;
                job.StatusText = "执行中";
                job.CommandLine = plan.MainStep?.ToCommandLine(paths.Ffmpeg) ?? string.Empty;
            });

            AppLog.Info(
                $"[开始] {job.SourceName} → {Path.GetFileName(outputPath)}（{preflight.Effective.Summary}）",
                "Queue");

            var completed = 0;
            foreach (var step in plan.Steps)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (step.Kind != TranscodeStepKind.Prepare)
                {
                    var label = step.Label;
                    var index = completed + 1;
                    Ui(() => job.StatusText = visibleSteps.Length > 1 ? $"{label}（{index}/{visibleSteps.Length}）" : label);
                }

                var baseline = completed;
                var run = await TranscodeRunner.RunAsync(
                    paths.Ffmpeg,
                    step,
                    progress => Ui(() => ApplyProgress(job, progress, baseline, trackedSteps)),
                    null,
                    cancellationToken).ConfigureAwait(false);

                if (run.Canceled)
                {
                    Ui(() =>
                    {
                        job.State = JobState.Canceled;
                        job.StatusText = "已取消";
                        job.SpeedText = string.Empty;
                        job.EtaText = string.Empty;
                    });
                    AppLog.Warn($"[取消] {job.SourceName}", "Queue");
                    return;
                }

                if (!run.Success)
                {
                    Fail(job, run.ErrorMessage ?? $"ffmpeg 退出码 {run.ExitCode}",
                        string.Join(Environment.NewLine, run.LogTail.TakeLast(12)));
                    return;
                }

                completed++;
                Ui(() =>
                {
                    job.ProgressPercent = trackedSteps > 0
                        ? Math.Clamp(completed / (double)trackedSteps * 100, 0, 100)
                        : 0;
                });
            }

            CleanupTemp(tempDirectory);

            var size = SafeSize(outputPath);
            Ui(() =>
            {
                job.State = JobState.Completed;
                job.ProgressPercent = 100;
                job.StatusText = "已完成";
                job.SpeedText = string.Empty;
                job.EtaText = string.Empty;
                job.OutputPath = outputPath;
            });

            AppLog.Info($"[完成] {job.SourceName} → {outputPath}（{MediaFormat.FormatSize(size)}，耗时 {stopwatch.Elapsed:mm\\:ss}）", "Queue");
        }
        catch (OperationCanceledException)
        {
            Ui(() =>
            {
                job.State = JobState.Canceled;
                job.StatusText = "已取消";
            });
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, $"队列执行异常：{job.SourceName}");
            Fail(job, ex.Message, ex.ToString());
        }
        finally
        {
            stopwatch.Stop();
            Ui(() =>
            {
                job.ElapsedText = stopwatch.Elapsed.TotalMinutes >= 1
                    ? $"{(int)stopwatch.Elapsed.TotalMinutes}分{stopwatch.Elapsed.Seconds}秒"
                    : $"{stopwatch.Elapsed.TotalSeconds:0.#}秒";

                _cancellations.Remove(job.Id);
                _activeCount = Math.Max(0, _activeCount - 1);
                SaveDebounced();
                UpdateSummary();
                Dispatch();
            });
        }
    }

    /// <summary>把当前步骤的进度折算为总进度。</summary>
    private static void ApplyProgress(TranscodeJob job, TranscodeProgress progress, int completedSteps, int trackedSteps)
    {
        if (trackedSteps <= 0)
        {
            job.ProgressPercent = -1;
            job.SpeedText = progress.SpeedText;
            job.EtaText = progress.EtaText;
            return;
        }

        var stepFraction = progress.Percent < 0 ? 0 : progress.Percent / 100.0;
        job.ProgressPercent = Math.Clamp((completedSteps + stepFraction) / trackedSteps * 100.0, 0, 100);
        job.SpeedText = progress.SpeedText;
        job.EtaText = progress.EtaText;
    }

    private void Fail(TranscodeJob job, string message, string detail)
    {
        Ui(() =>
        {
            job.State = JobState.Failed;
            job.StatusText = "失败";
            job.ErrorMessage = message;
            job.ErrorDetail = detail;
            job.SpeedText = string.Empty;
            job.EtaText = string.Empty;
        });

        AppLog.Error($"[失败] {job.SourceName}：{message}", "Queue");
    }

    private static void CleanupTemp(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"清理临时目录失败：{directory}（{ex.Message}）", "Queue");
        }
    }

    private static long SafeSize(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private void SaveDebounced()
    {
        if (!_settings.Current.PersistQueue)
        {
            return;
        }

        Ui(() =>
        {
            _saveTimer.Stop();
            _saveTimer.Start();
        });
    }

    private void UpdateSummary()
    {
        var running = _activeCount;
        var pending = PendingCount;
        var failed = FailedCount;

        var parts = new List<string>();
        if (running > 0)
        {
            parts.Add($"执行中 {running}");
        }

        if (pending > 0)
        {
            parts.Add($"等待 {pending}");
        }

        if (failed > 0)
        {
            parts.Add($"失败 {failed}");
        }

        if (parts.Count == 0)
        {
            SummaryText = Jobs.Count > 0 ? $"空闲（共 {Jobs.Count} 项）" : "空闲";
        }
        else
        {
            SummaryText = (IsPaused ? "已暂停 · " : string.Empty) + string.Join(" · ", parts);
        }

        OnPropertyChanged(nameof(HasActiveJobs));
        OnPropertyChanged(nameof(ActiveCount));
        OnPropertyChanged(nameof(PendingCount));
        OnPropertyChanged(nameof(FailedCount));
        Changed?.Invoke();
    }

    /// <summary>切到 UI 线程执行（队列状态只允许在 UI 线程修改）。</summary>
    private void Ui(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _dispatcher.BeginInvoke(action);
        }
    }
}
