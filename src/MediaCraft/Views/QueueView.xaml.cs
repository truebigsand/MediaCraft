using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using MediaCraft.Logging;
using MediaCraft.ViewModels;

namespace MediaCraft.Views;

/// <summary>队列页的少量代码后台逻辑（剪贴板、打开目录）。</summary>
public partial class QueueView : UserControl
{
    public QueueView()
    {
        InitializeComponent();
    }

    private QueueViewModel? ViewModel => DataContext as QueueViewModel;

    private void OnCopyCommandClick(object sender, RoutedEventArgs e)
    {
        var command = ViewModel?.SelectedJob?.CommandLine;
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

    private void OnOpenOutputClick(object sender, RoutedEventArgs e)
    {
        var output = ViewModel?.SelectedJob?.OutputPath;
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
