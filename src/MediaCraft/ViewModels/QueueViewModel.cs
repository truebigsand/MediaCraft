using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaCraft.Logging;
using MediaCraft.Queue;

namespace MediaCraft.ViewModels;

/// <summary>
/// 队列页：并发设置、暂停/取消/重试、逐项进度与诊断。
/// 队列状态本身由 <see cref="TranscodeQueue"/> 持有并在 UI 线程更新，这里只做投影。
/// </summary>
public sealed partial class QueueViewModel : ObservableObject
{
    private readonly TranscodeQueue _queue;

    public QueueViewModel(TranscodeQueue queue)
    {
        _queue = queue;
        _queue.Changed += OnQueueChanged;
        _queue.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsPaused));
            OnPropertyChanged(nameof(MaxConcurrency));
            OnPropertyChanged(nameof(PauseButtonText));
            RefreshSummary();
        };

        RefreshSummary();
    }

    /// <summary>任务列表（直接投影队列里的集合）。</summary>
    public ObservableCollection<TranscodeJob> Jobs => _queue.Jobs;

    /// <summary>并发数可选项。</summary>
    public IReadOnlyList<int> ConcurrencyOptions { get; } = [1, 2, 3, 4, 5, 6, 7, 8];

    [ObservableProperty]
    private TranscodeJob? _selectedJob;

    [ObservableProperty]
    private string _summaryText = "空闲";

    /// <summary>总体进度 0-100（把每个任务折算成等权进度）。</summary>
    [ObservableProperty]
    private double _overallPercent;

    [ObservableProperty]
    private string _overallText = "暂无任务";

    /// <summary>是否显示「硬件编码并发过多」的提示。</summary>
    [ObservableProperty]
    private bool _showConcurrencyHint;

    /// <summary>队列是否暂停。</summary>
    public bool IsPaused => _queue.IsPaused;

    public string PauseButtonText => _queue.IsPaused ? "继续" : "暂停";

    /// <summary>并发数（双向绑定到队列）。</summary>
    public int MaxConcurrency
    {
        get => _queue.MaxConcurrency;
        set
        {
            if (value == _queue.MaxConcurrency)
            {
                return;
            }

            _queue.MaxConcurrency = value;
            OnPropertyChanged();
            RefreshSummary();
        }
    }

    [RelayCommand]
    private void Start()
    {
        _queue.Start();
        RefreshSummary();
    }

    [RelayCommand]
    private void TogglePause()
    {
        if (_queue.IsPaused)
        {
            _queue.Start();
        }
        else
        {
            _queue.Pause();
        }

        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(PauseButtonText));
        RefreshSummary();
    }

    [RelayCommand]
    private void CancelSelected()
    {
        var target = SelectedJob ?? FirstActive();
        if (target is not null)
        {
            _queue.Cancel(target);
        }
    }

    [RelayCommand]
    private void CancelAll()
    {
        _queue.CancelAll();
        RefreshSummary();
    }

    [RelayCommand]
    private void RetryFailed()
    {
        _queue.RetryFailed();
        RefreshSummary();
    }

    [RelayCommand]
    private void ClearFinished()
    {
        _queue.ClearFinished();
        RefreshSummary();
    }

    [RelayCommand]
    private void RemoveSelected()
    {
        if (SelectedJob is not null)
        {
            _queue.Remove(SelectedJob);
            SelectedJob = null;
        }
    }

    [RelayCommand]
    private void MoveUp()
    {
        if (SelectedJob is not null)
        {
            _queue.MoveJob(SelectedJob, -1);
        }
    }

    [RelayCommand]
    private void MoveDown()
    {
        if (SelectedJob is not null)
        {
            _queue.MoveJob(SelectedJob, 1);
        }
    }

    /// <summary>清空整队（含持久化文件）。</summary>
    [RelayCommand]
    private void ClearAll()
    {
        if (Jobs.Count == 0)
        {
            return;
        }

        var choice = System.Windows.MessageBox.Show(
            $"确定要清空全部 {Jobs.Count} 项吗？正在执行的任务会被取消。",
            "MediaCraft",
            System.Windows.MessageBoxButton.OKCancel,
            System.Windows.MessageBoxImage.Warning);

        if (choice == System.Windows.MessageBoxResult.OK)
        {
            ClearAllCore();
        }
    }

    /// <summary>清空队列本身（不弹确认），给需要先确认再清空的调用方用。</summary>
    private void ClearAllCore()
    {
        _queue.ClearAll();
        SelectedJob = null;
        RefreshSummary();
    }

    /// <summary>
    /// 删除队列里所有任务的源文件（去重后送回收站），随后清空队列 —— 源文件没了，任务也就没意义。
    /// 删的是用户的原始素材而不是转码产物，所以确认框里列出要删的文件名并要求二次确认。
    /// </summary>
    [RelayCommand]
    private void DeleteAllSources()
    {
        var sources = Jobs
            .Select(j => j.SourcePath)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(File.Exists)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (sources.Length == 0)
        {
            System.Windows.MessageBox.Show(
                "队列里没有可以删除的源文件（可能文件已不在原位置）。",
                "删除全部源文件",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            return;
        }

        var preview = string.Join("\n", sources.Take(6).Select(Path.GetFileName));
        if (sources.Length > 6)
        {
            preview += $"\n……另有 {sources.Length - 6} 个";
        }

        var choice = System.Windows.MessageBox.Show(
            $"将删除 {sources.Length} 个源文件（移到回收站），并清空队列：\n\n{preview}\n\n" +
            "注意：删的是你的原始素材，不是转码产物。请先确认输出的视频没有问题。\n" +
            "删除后可以从回收站还原。",
            "删除全部源文件",
            System.Windows.MessageBoxButton.OKCancel,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.Cancel);

        if (choice != System.Windows.MessageBoxResult.OK)
        {
            return;
        }

        var deleted = 0;
        var failed = new List<string>();
        foreach (var path in sources)
        {
            try
            {
                // 送回收站而不是直接抹掉：点错了还能还原
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                    path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                deleted++;
            }
            catch (Exception ex)
            {
                failed.Add($"{Path.GetFileName(path)}：{ex.Message}");
                AppLog.Warn($"删除源文件失败：{path} —— {ex.Message}", "Queue");
            }
        }

        ClearAllCore();
        AppLog.Info($"删除源文件：成功 {deleted} 个、失败 {failed.Count} 个", "Queue");

        if (failed.Count > 0)
        {
            System.Windows.MessageBox.Show(
                $"已删除 {deleted} 个，{failed.Count} 个未能删除：\n\n" + string.Join("\n", failed.Take(5)),
                "删除全部源文件",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
        }
    }

    private TranscodeJob? FirstActive() => Jobs.FirstOrDefault(j => j.IsActive);

    // ── 右键菜单用的按任务操作 ──

    public void CancelJob(TranscodeJob job)
    {
        _queue.Cancel(job);
        RefreshSummary();
    }

    public void RetryJob(TranscodeJob job)
    {
        _queue.RetryJob(job);
        RefreshSummary();
    }

    public void MoveJob(TranscodeJob job, int offset)
    {
        _queue.MoveJob(job, offset);
        RefreshSummary();
    }

    public void RemoveJob(TranscodeJob job)
    {
        _queue.Remove(job);
        RefreshSummary();
    }


    private void OnQueueChanged()
    {
        RefreshSummary();
        OnPropertyChanged(nameof(Jobs));
    }

    /// <summary>重算总体进度与摘要。</summary>
    private void RefreshSummary()
    {
        if (Jobs.Count == 0)
        {
            OverallPercent = 0;
            OverallText = "暂无任务";
            SummaryText = "空闲";
            ShowConcurrencyHint = false;
            return;
        }

        double sum = 0;
        var completed = 0;
        foreach (var job in Jobs)
        {
            switch (job.State)
            {
                case JobState.Completed:
                    sum += 100;
                    completed++;
                    break;
                case JobState.Failed:
                case JobState.Canceled:
                    sum += 100;
                    completed++;
                    break;
                case JobState.Running:
                case JobState.Preparing:
                    sum += Math.Max(0, job.ProgressPercent);
                    break;
                default:
                    sum += 0;
                    break;
            }
        }

        OverallPercent = Math.Clamp(sum / Jobs.Count, 0, 100);
        OverallText = $"已完成 {completed}/{Jobs.Count}　·　总体进度 {OverallPercent:0.#}%";

        SummaryText = _queue.SummaryText;
        ShowConcurrencyHint = _queue.MaxConcurrency >= 3 && Jobs.Any(j =>
            j.IsActive || (j.State == JobState.Pending && Ffmpeg.EncoderCatalog.Get(j.Parameters.EncoderId).IsHardware));
    }
}
