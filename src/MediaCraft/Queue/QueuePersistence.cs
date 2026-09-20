using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaCraft.Logging;
using MediaCraft.Media;

namespace MediaCraft.Queue;

/// <summary>队列持久化记录（只存恢复执行必需的信息）。</summary>
public sealed class QueueJobRecord
{
    public string SourcePath { get; set; } = string.Empty;

    public TranscodeParams? Parameters { get; set; }

    public string OutputPath { get; set; } = string.Empty;

    public JobState State { get; set; }

    public string ErrorMessage { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
}

/// <summary>队列文件结构。</summary>
public sealed class QueueSnapshot
{
    public int Version { get; set; } = 1;

    public DateTime SavedAt { get; set; }

    public List<QueueJobRecord> Jobs { get; set; } = [];
}

/// <summary>
/// 队列状态落盘：%AppData%\MediaCraft\queue.json。
/// 只保留「未完成 + 最近完成」的任务，避免文件无限膨胀。
/// </summary>
public static class QueuePersistence
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    private const int MaxFinishedKept = 50;

    public static string FilePath
    {
        get
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "MediaCraft", "queue.json");
        }
    }

    public static void Save(IEnumerable<TranscodeJob> jobs)
    {
        try
        {
            var snapshot = new QueueSnapshot
            {
                SavedAt = DateTime.Now,
                Jobs = jobs
                    .Where(j => !string.IsNullOrWhiteSpace(j.SourcePath))
                    .Take(2000)
                    .Select(j => new QueueJobRecord
                    {
                        SourcePath = j.SourcePath,
                        Parameters = j.Parameters,
                        OutputPath = j.OutputPath,
                        // 运行中的任务在下次启动时按「等待中」恢复
                        State = j.State is JobState.Running or JobState.Preparing ? JobState.Pending : j.State,
                        ErrorMessage = j.ErrorMessage,
                        CreatedAt = j.CreatedAt,
                    })
                    .ToList(),
            };

            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(FilePath, JsonSerializer.Serialize(snapshot, Options));
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "QueuePersistence.Save");
        }
    }

    public static List<TranscodeJob> Load()
    {
        var jobs = new List<TranscodeJob>();
        try
        {
            if (!File.Exists(FilePath))
            {
                return jobs;
            }

            var snapshot = JsonSerializer.Deserialize<QueueSnapshot>(File.ReadAllText(FilePath), Options);
            if (snapshot?.Jobs is null)
            {
                return jobs;
            }

            var keptFinished = 0;
            foreach (var record in snapshot.Jobs)
            {
                if (string.IsNullOrWhiteSpace(record.SourcePath) || !File.Exists(record.SourcePath))
                {
                    // 源文件已被移动/删除的任务不再恢复
                    continue;
                }

                var parameters = record.Parameters ?? new TranscodeParams();
                var job = new TranscodeJob(record.SourcePath, parameters)
                {
                    CreatedAt = record.CreatedAt == default ? DateTime.Now : record.CreatedAt,
                };

                job.OutputPath = record.OutputPath;
                job.ErrorMessage = record.ErrorMessage;

                if (record.State is JobState.Completed or JobState.Failed or JobState.Canceled)
                {
                    // 完成的任务只保留少量作为历史展示
                    if (keptFinished >= MaxFinishedKept)
                    {
                        continue;
                    }

                    keptFinished++;
                    job.State = record.State;
                    job.StatusText = job.StateText;
                }
                else
                {
                    job.State = JobState.Pending;
                    job.StatusText = "等待中";
                }

                job.ParametersSummary = job.Parameters.Summary;
                jobs.Add(job);
            }

            AppLog.Info($"队列已恢复：{jobs.Count} 项（来自 {FilePath}）", "Queue");
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "QueuePersistence.Load");
        }

        return jobs;
    }

    public static void Delete()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                File.Delete(FilePath);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "QueuePersistence.Delete");
        }
    }
}
