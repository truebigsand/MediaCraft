using System.IO;
using System.Windows;
using System.Windows.Controls;
using MediaCraft.ViewModels;

namespace MediaCraft.Views;

/// <summary>
/// 转码页：文件拖拽/选择与「加入队列」的入口在代码后台，
/// 其余交互全部走绑定。多选文件时 ListBox.SelectedItems 无法直接绑定，
/// 因此在按钮回调里把选中项交给视图模型。
/// </summary>
public partial class TranscodeView : UserControl
{
    public TranscodeView()
    {
        InitializeComponent();
    }

    private TranscodeViewModel? ViewModel => DataContext as TranscodeViewModel;

    private async void OnAddFilesClick(object sender, RoutedEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel is null)
        {
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择媒体文件",
            Multiselect = true,
            Filter =
                "媒体文件|*.mp4;*.mkv;*.mov;*.avi;*.ts;*.m2ts;*.mts;*.flv;*.webm;*.wmv;*.mpg;*.mpeg;*.m4v;*.3gp;*.vob;*.rmvb;*.rm;*.ogv;*.mp3;*.m4a;*.aac;*.flac;*.wav;*.ogg;*.opus;*.wma;*.srt;*.ass;*.ssa;*.vtt|" +
                "视频|*.mp4;*.mkv;*.mov;*.avi;*.ts;*.flv;*.webm;*.wmv;*.mpg;*.mpeg;*.m4v|" +
                "音频|*.mp3;*.m4a;*.aac;*.flac;*.wav;*.ogg;*.opus;*.wma|" +
                "字幕|*.srt;*.ass;*.ssa;*.vtt|所有文件|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() == true)
        {
            await viewModel.AddPathsAsync(dialog.FileNames).ConfigureAwait(true);
        }
    }

    private async void OnAddFolderClick(object sender, RoutedEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel is null)
        {
            return;
        }

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择文件夹（会递归扫描其中的媒体文件）",
            Multiselect = false,
        };

        if (dialog.ShowDialog() == true)
        {
            await viewModel.AddPathsAsync([dialog.FolderName]).ConfigureAwait(true);
        }
    }

    private void OnPickOutputDirectoryClick(object sender, RoutedEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel is null)
        {
            return;
        }

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择输出目录",
            Multiselect = false,
        };

        var initial = viewModel.Params.OutputDirectory;
        if (!string.IsNullOrWhiteSpace(initial) && Directory.Exists(initial))
        {
            dialog.InitialDirectory = initial;
        }

        if (dialog.ShowDialog() == true)
        {
            viewModel.Params.OutputDirectory = dialog.FolderName;
        }
    }

    private void OnEnqueueSelectedClick(object sender, RoutedEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel is null)
        {
            return;
        }

        var selected = FileList.SelectedItems.OfType<MediaFileViewModel>().ToArray();
        if (selected.Length == 0)
        {
            viewModel.StatusText = "先在列表里选中文件（可按住 Ctrl / Shift 多选）";
            return;
        }

        viewModel.EnqueueFiles(selected);
    }

    private void OnEnqueueAllClick(object sender, RoutedEventArgs e) => ViewModel?.EnqueueFiles(null);

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel is null || e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0)
        {
            return;
        }

        await viewModel.AddPathsAsync(paths).ConfigureAwait(true);
    }
}
