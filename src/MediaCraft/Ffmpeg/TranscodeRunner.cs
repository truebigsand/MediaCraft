using System.Diagnostics;
using System.IO;
using System.Text;
using MediaCraft.Logging;

namespace MediaCraft.Ffmpeg;

/// <summary>一个步骤的执行结果。</summary>
public sealed class TranscodeRunResult
{
    public bool Success { get; init; }

    public bool Canceled { get; init; }

    public int ExitCode { get; init; }

    public TimeSpan Elapsed { get; init; }

    public string OutputPath { get; init; } = string.Empty;

    /// <summary>stderr 的最后若干行（失败诊断用）。</summary>
    public IReadOnlyList<string> LogTail { get; init; } = [];

    public string? ErrorMessage { get; init; }

    public string CommandLine { get; init; } = string.Empty;

    public TranscodeProgress? LastProgress { get; init; }
}

/// <summary>
/// 执行一个转码步骤：实时解析进度、收集日志、支持取消、失败后清理残留文件。
///
/// 关键点：成功与否只看**进程退出码**。
/// ffmpeg 失败时仍会留下一个 0 字节或残缺的同名输出文件（已实测），
/// 所以不能按「文件是否存在」判断成功，并且失败后必须删掉残留。
/// </summary>
public static class TranscodeRunner
{
    private const int MaxLogLines = 400;

    public static async Task<TranscodeRunResult> RunAsync(
        string ffmpegExe,
        TranscodeStep step,
        Action<TranscodeProgress>? onProgress,
        Action<string>? onLogLine,
        CancellationToken cancellationToken)
    {
        var commandLine = step.ToCommandLine(ffmpegExe);
        var parser = new ProgressParser(step.ExpectedDuration);
        var logLock = new object();
        var logLines = new Queue<string>();
        TranscodeProgress? lastProgress = null;

        // 运行前记录输出文件是否存在：只有「本来不存在」的残留才允许删除，
        // 否则会把用户原有的同名文件删掉。
        var outputExistedBefore = FileExists(step.OutputPath);
        EnsureOutputDirectory(step.OutputPath);

        var psi = new ProcessStartInfo(ffmpegExe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in step.Arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        var stopwatch = Stopwatch.StartNew();
        var exitCode = -1;
        var canceled = false;

        using (var process = new Process { StartInfo = psi, EnableRaisingEvents = true })
        {
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null)
                {
                    return;
                }

                parser.Feed(e.Data);
                if (e.Data.StartsWith("progress=", StringComparison.Ordinal))
                {
                    var snapshot = parser.Snapshot();
                    lastProgress = snapshot;
                    if (step.TrackProgress)
                    {
                        onProgress?.Invoke(snapshot);
                    }
                }
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null)
                {
                    return;
                }

                lock (logLock)
                {
                    logLines.Enqueue(e.Data);
                    while (logLines.Count > MaxLogLines)
                    {
                        logLines.Dequeue();
                    }
                }

                onLogLine?.Invoke(e.Data);
            };

            try
            {
                if (!process.Start())
                {
                    return new TranscodeRunResult
                    {
                        ExitCode = -1,
                        OutputPath = step.OutputPath,
                        ErrorMessage = "无法启动 ffmpeg 进程",
                        CommandLine = commandLine,
                        LogTail = [],
                    };
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                try
                {
                    await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    canceled = true;
                    ProcessRunner.KillTree(process);
                }

                // 等待异步读取把剩余缓冲区刷完，否则日志可能缺尾部
                try
                {
                    process.WaitForExit(2000);
                }
                catch (Exception)
                {
                    // 进程已被杀
                }

                exitCode = SafeExitCode(process);
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "TranscodeRunner");
                return new TranscodeRunResult
                {
                    ExitCode = -1,
                    OutputPath = step.OutputPath,
                    ErrorMessage = ex.Message,
                    CommandLine = commandLine,
                    LogTail = SnapshotLog(logLines, logLock),
                };
            }
        }

        stopwatch.Stop();

        var logTail = SnapshotLog(logLines, logLock);
        var success = !canceled && exitCode == 0;

        if (!success && !outputExistedBefore)
        {
            CleanupResidualOutput(step.OutputPath, commandLine);
        }

        return new TranscodeRunResult
        {
            Success = success,
            Canceled = canceled,
            ExitCode = exitCode,
            Elapsed = stopwatch.Elapsed,
            OutputPath = step.OutputPath,
            LogTail = logTail,
            ErrorMessage = success
                ? null
                : canceled
                    ? "已取消"
                    : DescribeFailure(exitCode, logTail),
            CommandLine = commandLine,
            LastProgress = lastProgress,
        };
    }

    /// <summary>从 ffmpeg 的 stderr 里提炼一句人类可读的失败原因。</summary>
    public static string DescribeFailure(int exitCode, IReadOnlyList<string> logTail)
    {
        var interesting = logTail
            .LastOrDefault(line =>
                line.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Invalid", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("No such file", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("not found", StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(interesting))
        {
            return interesting.Trim();
        }

        var last = logTail.LastOrDefault(line => !string.IsNullOrWhiteSpace(line));
        return string.IsNullOrWhiteSpace(last)
            ? $"ffmpeg 退出码 {exitCode}"
            : last.Trim();
    }

    private static void CleanupResidualOutput(string outputPath, string commandLine)
    {
        try
        {
            if (!File.Exists(outputPath))
            {
                return;
            }

            var size = new FileInfo(outputPath).Length;
            File.Delete(outputPath);
            AppLog.Warn($"已清理失败残留文件（{size} 字节）：{outputPath}", "TranscodeRunner");
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, $"清理残留文件失败：{outputPath} ← {commandLine}");
        }
    }

    private static void EnsureOutputDirectory(string outputPath)
    {
        try
        {
            var directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
                AppLog.Info($"已创建输出目录：{directory}", "TranscodeRunner");
            }
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "创建输出目录失败");
        }
    }

    private static bool FileExists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (Exception)
        {
            return false;
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

    private static IReadOnlyList<string> SnapshotLog(Queue<string> lines, object gate)
    {
        lock (gate)
        {
            return lines.ToArray();
        }
    }
}
