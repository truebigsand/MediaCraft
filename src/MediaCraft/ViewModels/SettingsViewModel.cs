using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaCraft.Ffmpeg;
using MediaCraft.Logging;
using MediaCraft.Settings;

namespace MediaCraft.ViewModels;

/// <summary>
/// 设置页：ffmpeg 路径与检测、输出默认值、队列默认值、日志、自检。
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;
    private readonly FfmpegContext _ffmpeg;
    private readonly Func<Task> _reinitialize;

    public SettingsViewModel(
        SettingsService settingsService,
        FfmpegContext ffmpeg,
        Func<Task> reinitialize)
    {
        _settingsService = settingsService;
        _ffmpeg = ffmpeg;
        _reinitialize = reinitialize;

        Settings = settingsService.Current;
        Settings.PropertyChanged += (_, _) => _settingsService.ScheduleSave();
        ffmpeg.PropertyChanged += (_, _) => RefreshStatus();
        RefreshStatus();
    }

    /// <summary>直接暴露设置模型给界面双向绑定。</summary>
    public AppSettings Settings { get; }

    /// <summary>全部编码器（用于选默认编码器）。</summary>
    public IReadOnlyList<EncoderDefinition> Encoders => EncoderCatalog.All;

    /// <summary>全部容器。</summary>
    public IReadOnlyList<ContainerDefinition> Containers => EncoderCatalog.Containers;

    /// <summary>日志级别可选项。</summary>
    public IReadOnlyList<string> LogLevels { get; } = ["Debug", "Info", "Warn", "Error"];

    /// <summary>并发数可选项。</summary>
    public IReadOnlyList<int> ConcurrencyOptions { get; } = [1, 2, 3, 4, 5, 6, 7, 8];

    [ObservableProperty]
    private string _detectedStatus = "未检测";

    [ObservableProperty]
    private string _encoderStatus = string.Empty;

    [ObservableProperty]
    private string _actionStatus = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>日志目录（显示与打开）。</summary>
    public string LogDirectory => AppLog.LogDirectory;

    /// <summary>设置文件路径。</summary>
    public string SettingsFilePath => _settingsService.FilePath;

    /// <summary>选日志级别（字符串形式便于绑定下拉框）。</summary>
    public string LogLevelText
    {
        get => Settings.LogLevel.ToString();
        set
        {
            if (Enum.TryParse<LogLevel>(value, ignoreCase: true, out var parsed) && parsed != Settings.LogLevel)
            {
                Settings.LogLevel = parsed;
                AppLog.MinimumLevel = parsed;
                AppLog.Info($"日志级别已切换为 {parsed}", "Settings");
                OnPropertyChanged();
            }
        }
    }

    /// <summary>选择 ffmpeg.exe。</summary>
    [RelayCommand]
    private void BrowseFfmpeg()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择 ffmpeg.exe",
            Filter = "ffmpeg|ffmpeg.exe|可执行文件|*.exe|所有文件|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() == true)
        {
            Settings.FfmpegPath = dialog.FileName;

            // 同目录下的 ffprobe 一并填上，省得用户再选一次
            var directory = Path.GetDirectoryName(dialog.FileName);
            if (!string.IsNullOrEmpty(directory))
            {
                var ffprobe = Path.Combine(directory, "ffprobe.exe");
                if (File.Exists(ffprobe))
                {
                    Settings.FfprobePath = ffprobe;
                }
            }

            Settings.ProbeEncodersOnStartup = true;
            ActionStatus = "已指定路径，点「重新检测」生效";
        }
    }

    /// <summary>选择 ffprobe.exe。</summary>
    [RelayCommand]
    private void BrowseFfprobe()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择 ffprobe.exe",
            Filter = "ffprobe|ffprobe.exe|可执行文件|*.exe|所有文件|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() == true)
        {
            Settings.FfprobePath = dialog.FileName;
            ActionStatus = "已指定路径，点「重新检测」生效";
        }
    }

    /// <summary>清空手动路径，回到自动探测。</summary>
    [RelayCommand]
    private void ClearPaths()
    {
        Settings.FfmpegPath = string.Empty;
        Settings.FfprobePath = string.Empty;
        ActionStatus = "已清除手动路径，点「重新检测」生效";
    }

    /// <summary>重新检测 ffmpeg。</summary>
    [RelayCommand]
    private async Task ReinitializeAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        ActionStatus = "正在重新检测…";
        try
        {
            _settingsService.Save();
            await _reinitialize().ConfigureAwait(true);
            ActionStatus = _ffmpeg.IsReady ? "检测完成" : "仍未找到可用的 ffmpeg";
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "重新检测 ffmpeg");
            ActionStatus = "检测失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
            RefreshStatus();
        }
    }

    /// <summary>选择默认输出目录。</summary>
    [RelayCommand]
    private void BrowseOutputDirectory()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择默认输出目录" };
        if (dialog.ShowDialog() == true)
        {
            Settings.DefaultOutputDirectory = dialog.FolderName;
        }
    }

    /// <summary>清空默认输出目录（回到源文件所在目录）。</summary>
    [RelayCommand]
    private void ClearOutputDirectory() => Settings.DefaultOutputDirectory = string.Empty;

    /// <summary>打开日志目录。</summary>
    [RelayCommand]
    private void OpenLogDirectory()
    {
        try
        {
            Directory.CreateDirectory(AppLog.LogDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", AppLog.LogDirectory) { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "打开日志目录");
        }
    }

    /// <summary>打开设置文件所在目录。</summary>
    [RelayCommand]
    private void OpenSettingsFile()
    {
        try
        {
            _settingsService.Save();
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_settingsService.FilePath}\"") { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "打开设置文件");
        }
    }

    /// <summary>运行快速自检（只做 ffmpeg 能力与编码器可用性探测，不跑转码矩阵）。</summary>
    [RelayCommand]
    private async Task RunQuickSelfTestAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        ActionStatus = "正在自检…";
        try
        {
            var reportPath = Path.Combine(
                Path.GetTempPath(), "MediaCraft", $"selftest-{DateTime.Now:yyyyMMdd-HHmmss}.txt");

            var exitCode = await Task.Run(() =>
                Ffmpeg.SelfTest.RunAsync(["--selftest", "quick", reportPath])).ConfigureAwait(true);

            ActionStatus = exitCode == 0 ? "自检通过" : "自检发现问题，详见报告";
            AppLog.Info($"快速自检完成（退出码 {exitCode}）：{reportPath}", "Settings");

            try
            {
                Process.Start(new ProcessStartInfo("notepad.exe", reportPath) { UseShellExecute = false });
            }
            catch (Exception ex)
            {
                AppLog.Warn($"打开自检报告失败：{ex.Message}", "Settings");
            }
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "运行自检");
            ActionStatus = "自检失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
            RefreshStatus();
        }
    }

    private void RefreshStatus()
    {
        var paths = _ffmpeg.Paths;
        var capabilities = _ffmpeg.Capabilities;

        DetectedStatus = paths is null
            ? "未找到 ffmpeg（可手动指定路径）"
            : $"{paths.Version}{Environment.NewLine}路径：{paths.Ffmpeg}{Environment.NewLine}来源：{paths.Source}";

        if (capabilities is null)
        {
            EncoderStatus = string.Empty;
            return;
        }

        var available = EncoderCatalog.All.Where(e => capabilities.IsEncoderAvailable(e.Id)).ToArray();
        var unavailable = EncoderCatalog.All.Where(e => !capabilities.IsEncoderAvailable(e.Id)).ToArray();

        var lines = new List<string>
        {
            capabilities.FunctionalProbeDone
                ? $"已做功能探测：{available.Length}/{EncoderCatalog.All.Count} 个编码器可用"
                : $"仅读取编码器列表（{available.Length} 个命中）：{available.Length}/{EncoderCatalog.All.Count}",
            "可用：" + (available.Length > 0 ? string.Join("、", available.Select(e => e.Id)) : "无"),
        };

        if (unavailable.Length > 0)
        {
            lines.Add("不可用：" + string.Join("、", unavailable.Select(e => e.Id)));
        }

        lines.Add("硬件加速方式：" + string.Join("、", capabilities.HwAccels));
        lines.Add("主版本：" + capabilities.MajorVersion);
        EncoderStatus = string.Join(Environment.NewLine, lines);
    }
}
