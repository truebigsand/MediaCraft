using System.Threading;

namespace MediaCraft;

/// <summary>
/// 自定义入口：单实例控制。第二个实例通过命名事件唤起已运行窗口后立即退出。
/// </summary>
internal static class Program
{
    private const string MutexName = "MediaCraft_SingleInstance";
    private const string ShowEventName = "MediaCraft_ShowRequest";

    /// <summary>命令行传入的文件 / 文件夹（启动后自动加入列表）。</summary>
    public static string[] StartupPaths { get; private set; } = [];

    [STAThread]
    private static int Main(string[] args)
    {
        // 无头自检模式：不启窗口，直接验证 FFmpeg 层（阶段验证与用户排障都用它）
        if (args.Length > 0 && args[0] is "--selftest" or "-selftest")
        {
            UseUtf8OutputInCi();
            return Ffmpeg.SelfTest.RunAsync(args).GetAwaiter().GetResult();
        }

        // 其余参数按「文件/文件夹路径」处理，实现「打开方式」与命令行直接添加
        StartupPaths = args.Where(a => !a.StartsWith('-')).ToArray();

        // initiallyOwned: false —— 不能请求所有权，否则第二个实例会阻塞在构造上
        using var mutex = new Mutex(initiallyOwned: false, MutexName, out bool createdNew);
        if (!createdNew)
        {
            try
            {
                using var showEvent = EventWaitHandle.OpenExisting(ShowEventName);
                showEvent.Set();
            }
            catch (Exception)
            {
                // 已运行实例正在退出，或事件尚未创建：忽略
            }

            return 0;
        }

        using var showEventHandle = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        App.ShowRequest = showEventHandle;
        var app = new App();
        return app.Run();
    }

    /// <summary>
    /// GitHub Actions 的日志按 UTF-8 解码，而 Windows 控制台默认用 OEM 代码页（中文系统是 GBK），
    /// 直接输出中文会变成一串问号。CI 里把标准输出切成 UTF-8；本机保持系统默认，免得本地终端反而乱码。
    /// </summary>
    private static void UseUtf8OutputInCi()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
        }
        catch (System.IO.IOException)
        {
            // 没有真实控制台句柄时设置会失败，不影响自检本身
        }
    }
}
