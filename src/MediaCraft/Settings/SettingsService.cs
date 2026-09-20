using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;
using MediaCraft.Logging;

namespace MediaCraft.Settings;

/// <summary>
/// 设置持久化：%AppData%\MediaCraft\settings.json。
/// 采用 500ms 防抖保存，退出时强制保存。
/// </summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly DispatcherTimer _saveTimer;
    private bool _dirty;

    public SettingsService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        FilePath = Path.Combine(appData, "MediaCraft", "settings.json");
        Current = AppSettings.NewDefault();

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            Save();
        };
    }

    /// <summary>设置变化通知（防抖后触发一次）。</summary>
    public event Action? Changed;

    /// <summary>设置文件完整路径。</summary>
    public string FilePath { get; }

    /// <summary>当前设置（进程内唯一实例）。</summary>
    public AppSettings Current { get; private set; }

    public void Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                Current = AppSettings.NewDefault();
                Save();
                return;
            }

            var json = File.ReadAllText(FilePath);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            if (loaded is null)
            {
                Current = AppSettings.NewDefault();
                return;
            }

            loaded.Migrate();
            loaded.Sanitize();
            Current = loaded;
            AppLog.Info($"设置已加载：{FilePath}", "Settings");
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "Settings.Load");
            Current = AppSettings.NewDefault();
        }
    }

    /// <summary>标记设置已变化，防抖 500ms 后写盘。</summary>
    public void ScheduleSave()
    {
        _dirty = true;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>立即写盘。</summary>
    public void Save()
    {
        try
        {
            Current.Sanitize();
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(Current, JsonOptions);
            File.WriteAllText(FilePath, json);
            _dirty = false;
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "Settings.Save");
        }
    }

    /// <summary>退出前调用：停掉防抖计时器并立即保存。</summary>
    public void FlushOnExit()
    {
        _saveTimer.Stop();
        if (_dirty)
        {
            Save();
        }
    }
}
