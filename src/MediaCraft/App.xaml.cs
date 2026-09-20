using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using MediaCraft.Logging;
using MediaCraft.Settings;
using MediaCraft.UI;

namespace MediaCraft;

/// <summary>
/// 应用壳：负责装配日志、设置、全局异常处理与主窗口。
/// </summary>
public partial class App : Application
{
    private AppShell? _shell;

    /// <summary>
    /// 无参构造：WPF 会根据 App.xaml 生成一个 Main 并调用它（本项目入口点是 <see cref="Program"/>，
    /// 那条路径不会被使用，但生成的代码必须能编译）。
    /// </summary>
    public App()
    {
        // 入口点是 Program（自定义 Main），生成的 App.Main 不会被调用，
        // 因此必须在这里手动加载 App.xaml —— 否则 Application.Resources 里的主题字典是空的。
        InitializeComponent();
    }

    /// <summary>第二个实例发来的「显示主窗口」事件句柄，由 <see cref="Program"/> 在 Run 之前赋值。</summary>
    internal static EventWaitHandle? ShowRequest { get; set; }

    /// <summary>供视图与视图模型访问的全局壳层实例。</summary>
    public static AppShell? Shell { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // 绑定失败默认是静默的（例如把命令名写错，按钮点了没反应），
        // 这里全部转进日志，排障时一眼能看到是哪条绑定不对。
        AttachBindingErrorListener();

        var settingsService = new SettingsService();
        settingsService.Load();
        AppLog.MinimumLevel = settingsService.Current.LogLevel;
        AppLog.Info($"MediaCraft 启动 v{typeof(App).Assembly.GetName().Version}", "App");

        _shell = new AppShell(settingsService);
        Shell = _shell;

        WatchShowRequest();
        _shell.ShowMainWindow();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _shell?.Shutdown();
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "App.OnExit");
        }

        AppLog.Info("MediaCraft 退出", "App");
        base.OnExit(e);
    }

    /// <summary>监听第二个实例发来的「显示主窗口」请求。</summary>
    private void WatchShowRequest()
    {
        var handle = ShowRequest;
        if (handle is null)
        {
            return;
        }

        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    handle.WaitOne();
                }
                catch (Exception)
                {
                    return;
                }

                Dispatcher.BeginInvoke(() => _shell?.ShowMainWindow());
            }
        })
        {
            IsBackground = true,
            Name = "MediaCraft.ShowRequest",
        };
        thread.Start();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error(e.Exception, "UI 线程未处理异常");
        MessageBox.Show(
            $"界面出现未处理的异常，已记录到日志：\n\n{e.Exception.Message}\n\n日志目录：{AppLog.LogDirectory}",
            "MediaCraft",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            AppLog.Error(ex, "非 UI 线程未处理异常");
        }
        else
        {
            AppLog.Error($"非 UI 线程未处理异常：{e.ExceptionObject}", "App");
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        AppLog.Error(e.Exception, "未观察的任务异常");
        e.SetObserved();
    }

    /// <summary>把 WPF 的数据绑定错误接进日志。</summary>
    private static void AttachBindingErrorListener()
    {
        try
        {
            PresentationTraceSources.Refresh();
            var source = PresentationTraceSources.DataBindingSource;
            source.Listeners.Add(new BindingErrorListener());
            source.Switch.Level = SourceLevels.Warning;
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "挂载绑定错误监听");
        }
    }

    /// <summary>把 WPF 绑定诊断输出写成日志（默认这些信息只在调试器输出窗口里）。</summary>
    private sealed class BindingErrorListener : TraceListener
    {
        private readonly StringBuilder _buffer = new();

        public override void Write(string? message) => _buffer.Append(message);

        public override void WriteLine(string? message)
        {
            _buffer.Append(message);
            var text = _buffer.ToString().Trim();
            _buffer.Clear();

            if (text.Length == 0)
            {
                return;
            }

            // 只记绑定失败的条目，忽略纯信息性输出
            if (text.Contains("BindingExpression", StringComparison.Ordinal) ||
                text.Contains("Cannot find", StringComparison.Ordinal) ||
                text.Contains("path error", StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Warn("绑定问题：" + text, "Binding");
            }
        }
    }
}
