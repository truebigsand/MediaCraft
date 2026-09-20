using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaCraft.Logging;
using MediaCraft.Media;
using MediaCraft.Presets;

namespace MediaCraft.ViewModels;

/// <summary>
/// 预设页：管理预设（应用、更新、重命名、另存副本、删除、导入导出）。
/// </summary>
public sealed partial class PresetViewModel : ObservableObject
{
    private readonly PresetStore _store;
    private readonly TranscodeViewModel _transcode;

    public PresetViewModel(PresetStore store, TranscodeViewModel transcode)
    {
        _store = store;
        _transcode = transcode;
        _store.Changed += () =>
        {
            OnPropertyChanged(nameof(Presets));
            RefreshCounts();
        };

        SelectedPreset = _store.All.FirstOrDefault();
        RefreshCounts();
    }

    /// <summary>全部预设（内置 + 自定义）。</summary>
    public ObservableCollection<Preset> Presets => _store.All;

    [ObservableProperty]
    private Preset? _selectedPreset;

    /// <summary>编辑用的名称。</summary>
    [ObservableProperty]
    private string _editName = string.Empty;

    /// <summary>编辑用的描述。</summary>
    [ObservableProperty]
    private string _editDescription = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _countText = string.Empty;

    /// <summary>选中项是内置预设（不可改名/删除）。</summary>
    public bool IsBuiltInSelected => SelectedPreset?.IsBuiltIn == true;

    /// <summary>选中项可以改名/删除。</summary>
    public bool CanEditSelected => SelectedPreset is { IsBuiltIn: false };

    partial void OnSelectedPresetChanged(Preset? value)
    {
        EditName = value?.Name ?? string.Empty;
        EditDescription = value?.Description ?? string.Empty;
        OnPropertyChanged(nameof(IsBuiltInSelected));
        OnPropertyChanged(nameof(CanEditSelected));
    }

    /// <summary>应用到当前编辑的文件（没有选中文件时应用到新文件模板）。</summary>
    [RelayCommand]
    private void ApplyToCurrent()
    {
        if (SelectedPreset is null)
        {
            return;
        }

        PresetStore.ApplyTo(SelectedPreset, _transcode.Params);
        _transcode.Revalidate();
        StatusText = $"已应用预设「{SelectedPreset.Name}」到{_transcode.EditTargetText}";
    }

    /// <summary>应用到列表里的全部文件。</summary>
    [RelayCommand]
    private void ApplyToAllFiles()
    {
        if (SelectedPreset is null)
        {
            return;
        }

        if (_transcode.Files.Count == 0)
        {
            StatusText = "文件列表是空的";
            return;
        }

        foreach (var file in _transcode.Files)
        {
            PresetStore.ApplyTo(SelectedPreset, file.Parameters);
            file.RefreshSummary();
        }

        // 当前编辑目标也一起更新，避免面板显示的还是旧参数
        PresetStore.ApplyTo(SelectedPreset, _transcode.Params);
        _transcode.Revalidate();

        StatusText = $"已应用预设「{SelectedPreset.Name}」到 {_transcode.Files.Count} 个文件";
    }

    /// <summary>用当前参数覆盖选中的自定义预设。</summary>
    [RelayCommand]
    private void UpdateFromCurrent()
    {
        if (SelectedPreset is null)
        {
            return;
        }

        if (SelectedPreset.IsBuiltIn)
        {
            StatusText = "内置预设不能覆盖，请用「另存为副本」";
            return;
        }

        _store.UpdateFromParams(SelectedPreset, _transcode.Params);
        StatusText = $"已用当前参数更新预设「{SelectedPreset.Name}」";
    }

    /// <summary>另存为副本（内置预设也能用）。</summary>
    [RelayCommand]
    private void Duplicate()
    {
        var baseName = SelectedPreset?.Name ?? "新预设";
        var clone = _store.SaveAs(baseName + " 副本", SelectedPreset?.Description ?? string.Empty, _transcode.Params);
        SelectedPreset = clone;
        StatusText = $"已另存为「{clone.Name}」";
    }

    /// <summary>把当前参数存为新预设（用页面上的名称/描述输入框）。</summary>
    [RelayCommand]
    private void SaveCurrentAsNew()
    {
        if (string.IsNullOrWhiteSpace(EditName))
        {
            StatusText = "请先填预设名称";
            return;
        }

        var preset = _store.SaveAs(EditName, EditDescription, _transcode.Params);
        SelectedPreset = preset;
        StatusText = $"已保存预设「{preset.Name}」";
    }

    /// <summary>重命名 / 改描述。</summary>
    [RelayCommand]
    private void ApplyRename()
    {
        if (SelectedPreset is null)
        {
            return;
        }

        if (_store.Rename(SelectedPreset, EditName, EditDescription))
        {
            StatusText = "已更新名称与描述";
            OnPropertyChanged(nameof(SelectedPreset));
        }
        else
        {
            StatusText = "内置预设不能改名，请用「另存为副本」";
        }
    }

    [RelayCommand]
    private void DeleteSelected()
    {
        if (SelectedPreset is null || SelectedPreset.IsBuiltIn)
        {
            StatusText = "内置预设不能删除";
            return;
        }

        var choice = System.Windows.MessageBox.Show(
            $"确定删除预设「{SelectedPreset.Name}」吗？",
            "MediaCraft",
            System.Windows.MessageBoxButton.OKCancel,
            System.Windows.MessageBoxImage.Question);

        if (choice != System.Windows.MessageBoxResult.OK)
        {
            return;
        }

        var name = SelectedPreset.Name;
        _store.Delete(SelectedPreset);
        SelectedPreset = _store.All.FirstOrDefault();
        StatusText = $"已删除预设「{name}」";
    }

    /// <summary>导出选中的预设。</summary>
    public void ExportSelected(string path)
    {
        if (SelectedPreset is null)
        {
            return;
        }

        try
        {
            _store.Export([SelectedPreset], path);
            StatusText = $"已导出预设「{SelectedPreset.Name}」到 {path}";
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "导出预设");
            StatusText = "导出失败：" + ex.Message;
        }
    }

    /// <summary>导出全部自定义预设。</summary>
    public void ExportAll(string path)
    {
        try
        {
            var presets = _store.All.Where(p => !p.IsBuiltIn).ToArray();
            if (presets.Length == 0)
            {
                StatusText = "还没有自定义预设可以导出";
                return;
            }

            _store.Export(presets, path);
            StatusText = $"已导出 {presets.Length} 个自定义预设到 {path}";
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "导出全部预设");
            StatusText = "导出失败：" + ex.Message;
        }
    }

    /// <summary>从 JSON 文件导入预设。</summary>
    public void Import(string path)
    {
        try
        {
            var count = _store.Import(path);
            SelectedPreset = _store.All.LastOrDefault(p => !p.IsBuiltIn);
            StatusText = $"已导入 {count} 个预设";
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "导入预设");
            StatusText = "导入失败：" + ex.Message;
            System.Windows.MessageBox.Show("导入失败：" + ex.Message, "MediaCraft",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    private void RefreshCounts()
    {
        var builtIn = _store.All.Count(p => p.IsBuiltIn);
        var custom = _store.All.Count(p => !p.IsBuiltIn);
        CountText = $"内置 {builtIn} 个 · 自定义 {custom} 个";
    }
}
