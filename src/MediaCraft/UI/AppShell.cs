using System.Windows;
using MediaCraft.Ffmpeg;
using MediaCraft.Logging;
using MediaCraft.Settings;

namespace MediaCraft.UI;

/// <summary>
/// 应用壳层：持有 ffmpeg 上下文、队列与主窗口，是各组件唯一的装配点。
/// </summary>
public sealed class AppShell
{
    private readonly SettingsService _settings;
    private MainWindow? _window;

    public AppShell(SettingsService settings)
    {
        _settings = settings;
        Ffmpeg = new FfmpegContext(settings);
        Queue = new Queue.TranscodeQueue(Ffmpeg, settings, System.Windows.Application.Current.Dispatcher);
        Main = new ViewModels.MainViewModel(Ffmpeg, settings, Queue);

        Queue.LoadPersisted();
    }

    /// <summary>ffmpeg 上下文（路径与能力探测）。</summary>
    public FfmpegContext Ffmpeg { get; }

    /// <summary>转码队列。</summary>
    public Queue.TranscodeQueue Queue { get; }

    /// <summary>主视图模型。</summary>
    public ViewModels.MainViewModel Main { get; }

    /// <summary>设置服务。</summary>
    public SettingsService Settings => _settings;

    public void ShowMainWindow()
    {
        try
        {
            if (_window is null)
            {
                _window = new MainWindow(Main);
                _window.ConfirmClose = OnConfirmClose;

                // 窗口先显示出来，再异步探测 ffmpeg（探测要跑几十个子进程，不能卡住启动）
                _ = InitializeFfmpegAsync();
                _ = AddStartupFilesAsync();
            }

            _window.BringToFront();
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "AppShell.ShowMainWindow");
        }
    }

    /// <summary>退出前的收尾。</summary>
    public void Shutdown()
    {
        try
        {
            Queue.SaveNow();
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "AppShell.Shutdown.Queue");
        }

        _settings.FlushOnExit();
    }

    /// <summary>初始化 ffmpeg：定位 → 能力探测 →（可选）编码器功能探测。</summary>
    public async Task InitializeFfmpegAsync()
    {
        Ffmpeg.InitializationFailed += OnInitializationFailed;
        try
        {
            await Ffmpeg.InitializeAsync(_settings.Current.ProbeEncodersOnStartup).ConfigureAwait(true);
        }
        finally
        {
            Ffmpeg.InitializationFailed -= OnInitializationFailed;
        }
    }

    /// <summary>命令行 / 「打开方式」传入的文件：等 ffmpeg 就绪后加入列表。</summary>
    private async Task AddStartupFilesAsync()
    {
        var paths = Program.StartupPaths;
        if (paths.Length == 0)
        {
            return;
        }

        try
        {
            // 探测是异步的，等它就绪再分析文件，否则 ffprobe 路径还不存在
            for (var i = 0; i < 60 && !Ffmpeg.IsReady; i++)
            {
                await Task.Delay(250).ConfigureAwait(true);
            }

            if (Ffmpeg.IsReady)
            {
                await Main.Transcode.AddPathsAsync(paths).ConfigureAwait(true);
            }
            else
            {
                AppLog.Warn("ffmpeg 未就绪，命令行传入的文件未加入列表", "AppShell");
            }
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "AppShell.AddStartupFiles");
        }
    }

    private void OnInitializationFailed(string? message)
    {
        if (message is null)
        {
            return;
        }

        MessageBox.Show(message, "MediaCraft", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>关窗时的二次确认：有任务在跑就问一句（关窗即退出，没有托盘）。</summary>
    private bool OnConfirmClose()
    {
        if (!Queue.HasActiveJobs)
        {
            return true;
        }

        var result = MessageBox.Show(
            $"还有 {Queue.ActiveCount} 个任务正在执行，关闭窗口会中断它们。\n\n确定要退出吗？",
            "MediaCraft",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        return result == MessageBoxResult.OK;
    }
}
