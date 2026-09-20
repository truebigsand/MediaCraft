using System.Windows;
using System.Windows.Controls;
using MediaCraft.ViewModels;

namespace MediaCraft.Views;

/// <summary>预设页：文件对话框相关的代码后台逻辑。</summary>
public partial class PresetView : UserControl
{
    public PresetView()
    {
        InitializeComponent();
    }

    private PresetViewModel? ViewModel => DataContext as PresetViewModel;

    private void OnExportSelectedClick(object sender, RoutedEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel?.SelectedPreset is null)
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出预设",
            FileName = viewModel.SelectedPreset.Name + ".json",
            Filter = "预设文件|*.json|所有文件|*.*",
            DefaultExt = "json",
        };

        if (dialog.ShowDialog() == true)
        {
            viewModel.ExportSelected(dialog.FileName);
        }
    }

    private void OnExportAllClick(object sender, RoutedEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel is null)
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出全部自定义预设",
            FileName = "MediaCraft-预设.json",
            Filter = "预设文件|*.json|所有文件|*.*",
            DefaultExt = "json",
        };

        if (dialog.ShowDialog() == true)
        {
            viewModel.ExportAll(dialog.FileName);
        }
    }

    private void OnImportClick(object sender, RoutedEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel is null)
        {
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入预设",
            Filter = "预设文件|*.json|所有文件|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() == true)
        {
            viewModel.Import(dialog.FileName);
        }
    }
}
