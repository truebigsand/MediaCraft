using System.IO;
using System.Text;

namespace MediaCraft.Logging;

/// <summary>日志级别。</summary>
public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3,
}

/// <summary>一条日志记录。</summary>
public sealed class LogEntry
{
    public LogEntry(DateTime time, LogLevel level, string message, string context)
    {
        Time = time;
        Level = level;
        Message = message;
        Context = context;
    }

    public DateTime Time { get; }

    public LogLevel Level { get; }

    public string Message { get; }

    public string Context { get; }

    /// <summary>单行文本形式（用于日志面板显示与文件写入）。</summary>
    public string Line =>
        Context.Length == 0
            ? $"{Time:HH:mm:ss.fff} [{LevelText}] {Message}"
            : $"{Time:HH:mm:ss.fff} [{LevelText}] [{Context}] {Message}";

    private string LevelText => Level switch
    {
        LogLevel.Debug => "DBG",
        LogLevel.Warn => "WRN",
        LogLevel.Error => "ERR",
        _ => "INF",
    };
}

/// <summary>
/// 轻量文件日志：按天切分、保留若干天、UTF-8 无 BOM、任何异常静默降级。
/// 同时维护内存中的最近日志环形缓冲，供界面日志面板订阅。
/// </summary>
public static class AppLog
{
    private const int MemoryCapacity = 2000;
    private const int MaxMessageLength = 4000;

    private static readonly object Sync = new();
    private static readonly Queue<LogEntry> Memory = new(MemoryCapacity);
    private static string? _cachedDirectory;
    private static DateTime _lastCleanupDate = DateTime.MinValue;

    /// <summary>日志写入时触发（可能来自任意线程，订阅方自行切回 UI 线程）。</summary>
    public static event Action<LogEntry>? LineWritten;

    /// <summary>日志目录：%AppData%\MediaCraft\logs。</summary>
    public static string LogDirectory
    {
        get
        {
            if (_cachedDirectory is null)
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                _cachedDirectory = Path.Combine(appData, "MediaCraft", "logs");
            }

            return _cachedDirectory;
        }
    }

    /// <summary>日志保留天数。</summary>
    public static int RetentionDays { get; set; } = 7;

    /// <summary>低于该级别的日志不落盘。</summary>
    public static LogLevel MinimumLevel { get; set; } = LogLevel.Info;

    public static void Debug(string message, string context = "") => Write(LogLevel.Debug, message, context);

    public static void Info(string message, string context = "") => Write(LogLevel.Info, message, context);

    public static void Warn(string message, string context = "") => Write(LogLevel.Warn, message, context);

    public static void Error(string message, string context = "") => Write(LogLevel.Error, message, context);

    public static void Error(Exception exception, string context = "")
    {
        var text = exception.GetType().Name + ": " + exception.Message;
        if (!string.IsNullOrWhiteSpace(exception.StackTrace))
        {
            text += Environment.NewLine + exception.StackTrace;
        }

        Write(LogLevel.Error, text, context);
    }

    /// <summary>按指定级别写入（供需要动态决定级别的调用方使用）。</summary>
    public static void Log(LogLevel level, string message, string context = "") => Write(level, message, context);

    /// <summary>当前内存中的日志快照（最多 <see cref="MemoryCapacity"/> 条，按时间升序）。</summary>
    public static IReadOnlyList<LogEntry> Snapshot()
    {
        lock (Sync)
        {
            return Memory.ToArray();
        }
    }

    public static void ClearMemory()
    {
        lock (Sync)
        {
            Memory.Clear();
        }
    }

    private static void Write(LogLevel level, string message, string context)
    {
        if (level < MinimumLevel)
        {
            return;
        }

        if (message.Length > MaxMessageLength)
        {
            message = message[..MaxMessageLength] + " …（已截断）";
        }

        var entry = new LogEntry(DateTime.Now, level, message, context);

        lock (Sync)
        {
            Memory.Enqueue(entry);
            while (Memory.Count > MemoryCapacity)
            {
                Memory.Dequeue();
            }
        }

        try
        {
            WriteToFile(entry);
        }
        catch (Exception)
        {
            // 日志失败不得影响业务
        }

        try
        {
            LineWritten?.Invoke(entry);
        }
        catch (Exception)
        {
            // 订阅方异常不得影响业务
        }
    }

    private static void WriteToFile(LogEntry entry)
    {
        var directory = LogDirectory;
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        CleanupOldFiles(directory);

        var file = Path.Combine(directory, $"app-{entry.Time:yyyyMMdd}.log");
        File.AppendAllText(file, entry.Line + Environment.NewLine, new UTF8Encoding(false));
    }

    private static void CleanupOldFiles(string directory)
    {
        if (_lastCleanupDate.Date == DateTime.Today)
        {
            return;
        }

        _lastCleanupDate = DateTime.Today;
        var threshold = DateTime.Today.AddDays(-Math.Max(1, RetentionDays));
        foreach (var file in Directory.GetFiles(directory, "app-*.log"))
        {
            try
            {
                if (File.GetLastWriteTime(file) < threshold)
                {
                    File.Delete(file);
                }
            }
            catch (Exception)
            {
                // 单个文件删不掉不影响其它
            }
        }
    }
}
