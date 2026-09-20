using System.Windows;
using MediaCraft.Logging;
using MediaCraft.Settings;

namespace MediaCraft.UI;

/// <summary>
/// 应用壳层：持有主窗口与各视图模型，作为视图之间的装配点。
/// </summary>
public sealed class AppShell
{
    private readonly SettingsService _settings;
    private MainWindow? _window;

    public AppShell(SettingsService settings)
    {
        _settings = settings;
    }

    /// <summary>主窗口实例（尚未创建时为 null）。</summary>
    public MainWindow? Window => _window;

    public void ShowMainWindow()
    {
        try
        {
            if (_window is null)
            {
                _window = new MainWindow(_settings);
                _window.ConfirmClose = OnConfirmClose;
            }

            _window.BringToFront();
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "AppShell.ShowMainWindow");
        }
    }

    /// <summary>退出前的收尾：保存设置。</summary>
    public void Shutdown()
    {
        _settings.FlushOnExit();
    }

    /// <summary>关窗时的二次确认（有任务在跑时拦截）。</summary>
    private bool OnConfirmClose()
    {
        // 阶段 4 接入队列后：有正在运行的任务时弹确认框
        return true;
    }

    /// <summary>供窗口启动时读取的引用，避免编译告警。</summary>
    internal SettingsService Settings => _settings;
}
