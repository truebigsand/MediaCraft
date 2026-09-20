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
}
