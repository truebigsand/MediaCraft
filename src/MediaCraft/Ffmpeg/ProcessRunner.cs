using System.Diagnostics;
using System.Text;

namespace MediaCraft.Ffmpeg;

/// <summary>一次进程调用的结果。</summary>
public sealed class ProcessResult
{
    public int ExitCode { get; init; }

    public string StandardOutput { get; init; } = string.Empty;

    public string StandardError { get; init; } = string.Empty;

    public bool TimedOut { get; init; }

    public bool Canceled { get; init; }

    public bool Succeeded => !TimedOut && !Canceled && ExitCode == 0;

    /// <summary>stdout + stderr，用于诊断输出。</summary>
    public string CombinedOutput =>
        string.IsNullOrEmpty(StandardError) ? StandardOutput : StandardOutput + Environment.NewLine + StandardError;
}

/// <summary>
/// 进程调用工具：始终使用 ArgumentList 传参（不拼命令行字符串），
/// 因此路径中的空格、引号、中文都不需要手工转义。
/// </summary>
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken,
        int timeoutMs = 30000)
    {
        var psi = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
        {
            return new ProcessResult { ExitCode = -1, StandardError = "进程启动失败" };
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        var timedOut = false;
        try
        {
            using var timeoutCts = new CancellationTokenSource(timeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            if (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                timedOut = true;
            }
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            timedOut = !cancellationToken.IsCancellationRequested;
        }
        finally
        {
            if (!process.HasExited)
            {
                KillTree(process);
            }
        }

        string stdout;
        string stderr;
        try
        {
            stdout = await stdoutTask.ConfigureAwait(false);
            stderr = await stderrTask.ConfigureAwait(false);
        }
        catch (Exception)
        {
            stdout = string.Empty;
            stderr = string.Empty;
        }

        return new ProcessResult
        {
            ExitCode = SafeExitCode(process),
            StandardOutput = stdout,
            StandardError = stderr,
            TimedOut = timedOut,
            Canceled = cancellationToken.IsCancellationRequested,
        };
    }

    internal static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
        }
        catch (Exception)
        {
            // 进程可能已自行退出
        }
    }

    private static int SafeExitCode(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode : -1;
        }
        catch (Exception)
        {
            return -1;
        }
    }
}
