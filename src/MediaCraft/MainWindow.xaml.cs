using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using MediaCraft.Logging;
using MediaCraft.ViewModels;

namespace MediaCraft;

/// <summary>
/// 主窗口：四个标签页 + 可折叠日志面板 + 状态栏。
/// 关窗即退出；有任务在跑时由 <see cref="ConfirmClose"/> 决定是否二次确认。
/// </summary>
public partial class MainWindow : Window
{
    private const int MaxLogLines = 2000;

    private readonly ObservableCollection<LogEntry> _logEntries = [];

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();

        DataContext = viewModel;
        LogList.ItemsSource = _logEntries;
        StatusVersion.Text = "v" + (typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.3.0");

        foreach (var entry in AppLog.Snapshot())
        {
            _logEntries.Add(entry);
        }

        AppLog.LineWritten += OnLogLineWritten;
        Loaded += (_, _) => ScrollLogToEnd();
    }

    /// <summary>关窗时的二次确认钩子，返回 true 表示允许关闭。</summary>
    public Func<bool>? ConfirmClose { get; set; }

    /// <summary>让主窗口获得焦点（单实例唤起时使用）。</summary>
    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Show();
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (ConfirmClose is not null && !ConfirmClose())
        {
            e.Cancel = true;
            return;
        }

        AppLog.LineWritten -= OnLogLineWritten;
        base.OnClosing(e);
    }

    private void OnLogLineWritten(LogEntry entry)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnLogLineWritten(entry));
            return;
        }

        _logEntries.Add(entry);
        while (_logEntries.Count > MaxLogLines)
        {
            _logEntries.RemoveAt(0);
        }

        if (LogPanel.Visibility == Visibility.Visible)
        {
            ScrollLogToEnd();
        }

        if (entry.Level >= LogLevel.Warn)
        {
            LogSummary.Text = entry.Line;
            LogSummary.Foreground = entry.Level == LogLevel.Error
                ? (System.Windows.Media.Brush)FindResource("DangerBrush")
                : (System.Windows.Media.Brush)FindResource("WarnBrush");
        }
    }

    private void ScrollLogToEnd()
    {
        if (_logEntries.Count > 0)
        {
            LogList.ScrollIntoView(_logEntries[^1]);
        }
    }

    private void OnLogToggleChanged(object sender, RoutedEventArgs e)
    {
        var show = LogToggle.IsChecked == true;
        LogPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        LogToggle.Content = show ? "隐藏日志" : "显示日志";
        if (show)
        {
            ScrollLogToEnd();
        }
    }

    private void OnOpenLogDirectoryClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppLog.LogDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", AppLog.LogDirectory) { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "打开日志目录");
        }
    }
}
