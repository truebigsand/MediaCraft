using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MediaCraft.Logging;
using MediaCraft.Queue;
using MediaCraft.ViewModels;

namespace MediaCraft.Views;

/// <summary>队列页的少量代码后台逻辑（剪贴板、打开目录）。</summary>
public partial class QueueView : UserControl
{
    /// <summary>右键菜单的目标（右键落在哪一行）。</summary>
    private TranscodeJob? _contextJob;

    public QueueView()
    {
        InitializeComponent();
    }

    private QueueViewModel? ViewModel => DataContext as QueueViewModel;

    private void OnCopyCommandClick(object sender, RoutedEventArgs e)
    {
        var command = ContextJob?.CommandLine;
        if (string.IsNullOrWhiteSpace(command))
        {
            return;
        }

        try
        {
            Clipboard.SetText(command);
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "复制命令行");
        }
    }

    // ── 右键菜单 ──

    /// <summary>右键落在哪一行就记下来并选中该行；菜单条目作用于它。</summary>
    private void OnJobListRightClick(object sender, MouseButtonEventArgs e)
    {
        var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (item?.DataContext is TranscodeJob job)
        {
            _contextJob = job;
            if (!item.IsSelected)
            {
                item.IsSelected = true;
            }
        }
        else
        {
            _contextJob = null;
        }
    }

    private TranscodeJob? ContextJob => _contextJob ?? ViewModel?.SelectedJob;

    private void OnCancelContextJobClick(object sender, RoutedEventArgs e)
    {
        var viewModel = ViewModel;
        var job = ContextJob;
        if (viewModel is not null && job is not null)
        {
            viewModel.CancelJob(job);
        }
    }

    private void OnRetryContextJobClick(object sender, RoutedEventArgs e)
    {
        var viewModel = ViewModel;
        var job = ContextJob;
        if (viewModel is not null && job is not null)
        {
            viewModel.RetryJob(job);
        }
    }

    private void OnMoveContextJobUpClick(object sender, RoutedEventArgs e)
    {
        var viewModel = ViewModel;
        var job = ContextJob;
        if (viewModel is not null && job is not null)
        {
            viewModel.MoveJob(job, -1);
        }
    }

    private void OnMoveContextJobDownClick(object sender, RoutedEventArgs e)
    {
        var viewModel = ViewModel;
        var job = ContextJob;
        if (viewModel is not null && job is not null)
        {
            viewModel.MoveJob(job, 1);
        }
    }

    private void OnRemoveContextJobClick(object sender, RoutedEventArgs e)
    {
        var viewModel = ViewModel;
        var job = ContextJob;
        if (viewModel is not null && job is not null)
        {
            viewModel.RemoveJob(job);
            _contextJob = null;
        }
    }

    private void OnClearFinishedJobsClick(object sender, RoutedEventArgs e) => ViewModel?.ClearFinishedJobs();

    private void OnCopySourcePathClick(object sender, RoutedEventArgs e)
    {
        var path = ContextJob?.SourcePath;
        if (!string.IsNullOrWhiteSpace(path))
        {
            try
            {
                Clipboard.SetText(path);
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "复制源文件路径");
            }
        }
    }

    private void OnOpenOutputFileClick(object sender, RoutedEventArgs e)
    {
        var output = ContextJob?.OutputPath;
        if (string.IsNullOrWhiteSpace(output) || !File.Exists(output))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(output) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "打开输出文件");
        }
    }

    /// <summary>向上找指定类型的可视祖先。</summary>
    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T match)
            {
                return match;
            }

            node = VisualTreeHelper.GetParent(node);
        }

        return null;
    }

    private void OnOpenOutputClick(object sender, RoutedEventArgs e)
    {
        var output = ContextJob?.OutputPath;
        if (string.IsNullOrWhiteSpace(output))
        {
            return;
        }

        try
        {
            if (File.Exists(output))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{output}\"") { UseShellExecute = false });
                return;
            }

            var directory = Path.GetDirectoryName(output);
            if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", directory) { UseShellExecute = false });
            }
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "打开输出位置");
        }
    }
}
