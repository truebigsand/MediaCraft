using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaCraft.Ffmpeg;
using MediaCraft.Logging;
using MediaCraft.Media;
using MediaCraft.Presets;
using MediaCraft.Queue;
using MediaCraft.Settings;

namespace MediaCraft.ViewModels;

/// <summary>
/// 转码页：文件列表 + 参数区（简单/高级双模式）+ 轨道选择 + 预检。
///
/// 参数作用域：每个文件持有自己完整的一套参数；参数区编辑的是「当前选中的文件」，
/// 没有选中文件时编辑的是「新文件模板」。
/// </summary>
public sealed partial class TranscodeViewModel : ObservableObject
{
    private readonly FfmpegContext _ffmpeg;
    private readonly SettingsService _settings;
    private readonly TranscodeQueue _queue;
    private readonly PresetStore _presets;
    private readonly DispatcherTimer _revalidateTimer;

    /// <summary>新文件的参数模板。</summary>
    private readonly TranscodeParams _templateParams = new();

    private bool _suppressRevalidate;

    /// <summary>正在把预检修正写回参数（防止写回触发的属性变更再次进入预检）。</summary>
    private bool _applyingPreflightFix;

    public TranscodeViewModel(FfmpegContext ffmpeg, SettingsService settings, TranscodeQueue queue, PresetStore presets)
    {
        _ffmpeg = ffmpeg;
        _settings = settings;
        _queue = queue;
        _presets = presets;

        _templateParams.NamingTemplate = settings.Current.NamingTemplate;
        _templateParams.Container = settings.Current.DefaultContainer;
        _templateParams.EncoderId = settings.Current.DefaultEncoderId;
        _templateParams.FastStart = settings.Current.FastStart;

        _revalidateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _revalidateTimer.Tick += (_, _) =>
        {
            _revalidateTimer.Stop();
            Revalidate();
            PropagateToAllIfNeeded();
        };

        Params = _templateParams;
        HookParams(_templateParams);
        UpdateEditTargetText();
        SyncToAllFiles = settings.Current.SyncParamsToAllFiles;

        // 列表为空时的提示语要跟着集合变化刷新：
        // Files 是只读属性，集合增减不会自动触发绑定重算，这里手动通知。
        Files.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Files));
            OnPropertyChanged(nameof(ScopeText));
            FilesChanged?.Invoke();
        };
    }

    /// <summary>列表数据源。</summary>
    public ObservableCollection<MediaFileViewModel> Files { get; } = [];

    /// <summary>当前正在编辑的参数对象。</summary>
    [ObservableProperty]
    private TranscodeParams _params;

    [ObservableProperty]
    private MediaFileViewModel? _selectedFile;

    /// <summary>参数区标题（编辑谁）。</summary>
    [ObservableProperty]
    private string _editTargetText = "新文件默认参数";

    [ObservableProperty]
    private bool _isAnalyzing;

    [ObservableProperty]
    private string _statusText = "添加文件后即可开始";

    /// <summary>输出路径预览。</summary>
    [ObservableProperty]
    private string _outputPreview = "—";

    /// <summary>预检问题列表。</summary>
    [ObservableProperty]
    private string _preflightSummary = "预检通过";

    [ObservableProperty]
    private bool _hasBlockingError;

    public ObservableCollection<PreflightIssue> PreflightIssues { get; } = [];

    /// <summary>当前参数的简单模式预览说明（滑块值对应的原生参数）。</summary>
    [ObservableProperty]
    private string _qualityHint = string.Empty;

    /// <summary>高级模式里质量参数的字段说明。</summary>
    [ObservableProperty]
    private string _qualityLabelText = string.Empty;

    /// <summary>当前编码器的能力说明。</summary>
    [ObservableProperty]
    private string _encoderHint = string.Empty;

    /// <summary>
    /// 改参数时是否同步到列表里所有文件。
    /// 默认开启：批量转码的常态是「一批素材统一规格」；关掉后才做纯粹的逐文件精调。
    /// </summary>
    [ObservableProperty]
    private bool _syncToAllFiles = true;

    /// <summary>当前参数到底作用在谁身上（界面上必须一眼可见，否则拖参数会以为没生效）。</summary>
    public string ScopeText
    {
        get
        {
            if (Files.Count == 0)
            {
                return "列表为空：当前参数会作为新加入文件的默认值";
            }

            if (SelectedFile is null)
            {
                return $"当前编辑的是「新文件默认参数」，列表里已有 {Files.Count} 个文件不受影响——" +
                       "选中一个文件或勾上同步开关才能改到它们。";
            }

            return SyncToAllFiles
                ? $"当前参数作用于全部 {Files.Count} 个文件（轨道选择各自独立）"
                : $"当前参数只作用于「{SelectedFile.FileName}」，其余 {Files.Count - 1} 个文件不受影响";
        }
    }

    // ── 下拉数据源 ──

    /// <summary>预设列表（内置 + 自定义）。</summary>
    public ObservableCollection<Preset> Presets => _presets.All;

    [ObservableProperty]
    private Preset? _selectedPreset;

    /// <summary>另存为预设时的名称 / 描述。</summary>
    [ObservableProperty]
    private string _newPresetName = string.Empty;

    [ObservableProperty]
    private string _newPresetDescription = string.Empty;

    /// <summary>把选中的预设套用到当前编辑目标。</summary>
    [RelayCommand]
    private void ApplyPreset()
    {
        if (SelectedPreset is null)
        {
            return;
        }

        PresetStore.ApplyTo(SelectedPreset, Params);
        StatusText = $"已应用预设「{SelectedPreset.Name}」";
        Revalidate();
    }

    /// <summary>把当前参数另存为预设。</summary>
    [RelayCommand]
    private void SaveCurrentAsPreset()
    {
        if (string.IsNullOrWhiteSpace(NewPresetName))
        {
            StatusText = "请先填预设名称";
            return;
        }

        var preset = _presets.SaveAs(NewPresetName, NewPresetDescription, Params);
        SelectedPreset = preset;
        NewPresetName = string.Empty;
        NewPresetDescription = string.Empty;
        StatusText = $"已保存预设「{preset.Name}」（同名预设会被覆盖）";
    }

    public IReadOnlyList<EncoderDefinition> Encoders => EncoderCatalog.All;

    public IReadOnlyList<ContainerDefinition> Containers => EncoderCatalog.Containers;

    public IReadOnlyList<AudioCodecDefinition> AudioCodecs => EncoderCatalog.AudioCodecs;

    /// <summary>常用分辨率（宽度模式，高度自动）。</summary>
    public IReadOnlyList<int> CommonWidths { get; } = [3840, 2560, 1920, 1600, 1280, 1024, 854, 640];

    public IReadOnlyList<int> CommonHeights { get; } = [2160, 1440, 1080, 900, 720, 576, 480, 360];

    public IReadOnlyList<string> FrameRatePresets { get; } = ["", "24", "25", "30", "50", "60", "120"];

    /// <summary>当前编码器可选的 preset。</summary>
    public IReadOnlyList<string> AvailablePresets =>
        new[] { string.Empty }.Concat(Params.Encoder.Presets).ToArray();

    public IReadOnlyList<string> AvailableTunes =>
        new[] { string.Empty }.Concat(Params.Encoder.Tunes).ToArray();

    public IReadOnlyList<string> AvailableProfiles =>
        new[] { string.Empty }.Concat(Params.Encoder.Profiles).ToArray();

    public bool IsSimpleMode => Params.QualityMode == QualityMode.Simple;

    public bool IsAdvancedMode => Params.QualityMode == QualityMode.Advanced;

    public bool IsQualityRateControl => Params.RateControl == RateControlKind.Quality;

    public bool HasSelectedFile => SelectedFile is not null;

    /// <summary>文件列表变化通知（用于统计与按钮可用性）。</summary>
    public event Action? FilesChanged;

    // ── 文件操作 ──

    /// <summary>添加文件与文件夹（拖拽与按钮共用入口）。</summary>
    public async Task AddPathsAsync(IEnumerable<string> paths, bool recursiveFolderScan = true)
    {
        var collected = new List<string>();
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                collected.AddRange(FolderScanner.Scan(path, recursiveFolderScan));
            }
            else if (File.Exists(path) && MediaProbe.IsMediaFile(path))
            {
                collected.Add(path);
            }
        }

        // 去重：既排除列表里已有的，也排除本批次内重复的
        var existing = new HashSet<string>(Files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
        var added = new List<MediaFileViewModel>();
        foreach (var path in collected)
        {
            if (existing.Add(path))
            {
                added.Add(new MediaFileViewModel(path));
            }
        }

        if (added.Count == 0)
        {
            StatusText = "没有新的可用文件（可能重复或格式不支持）";
            return;
        }

        foreach (var file in added)
        {
            // 新文件复制「当前面板参数」，再按自己的流列表重建轨道选择
            file.Parameters = Params.Clone();
            Files.Add(file);
            HookParams(file.Parameters);
        }

        StatusText = $"已添加 {added.Count} 个文件，正在分析…";

        // 自动选中刚加入的第一个文件。
        // 否则参数面板编辑的是「新文件默认参数」模板，用户拖质量滑块会看不到任何效果
        //（参数没落到文件上），这是最容易踩的坑。
        if (SelectedFile is null)
        {
            SelectedFile = added[0];
        }

        await AnalyzeAsync(added).ConfigureAwait(true);
    }

    /// <summary>并发分析文件信息（最多 4 个 ffprobe 同时跑）。</summary>
    private async Task AnalyzeAsync(IReadOnlyList<MediaFileViewModel> files)
    {
        if (files.Count == 0)
        {
            return;
        }

        IsAnalyzing = true;
        var gate = new SemaphoreSlim(4);
        var done = 0;

        var tasks = files.Select(async file =>
        {
            await gate.WaitAsync().ConfigureAwait(true);
            try
            {
                file.State = AnalysisState.Analyzing;
                file.Resolution = "分析中…";

                var info = await _ffmpeg.ProbeAsync(file.Path).ConfigureAwait(true);
                if (info is null)
                {
                    file.ApplyFailure("ffprobe 无法读取该文件");
                    AppLog.Warn($"分析失败：{file.Path}", "Transcode");
                }
                else
                {
                    file.ApplyInfo(info);
                }
            }
            catch (Exception ex)
            {
                file.ApplyFailure(ex.Message);
                AppLog.Error(ex, $"分析异常：{file.Path}");
            }
            finally
            {
                gate.Release();
                done++;
                StatusText = done < files.Count
                    ? $"正在分析… {done}/{files.Count}"
                    : $"就绪：{Files.Count} 个文件";
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(true);
        IsAnalyzing = false;

        Revalidate();
    }

    [RelayCommand]
    private void RemoveSelected(MediaFileViewModel? file)
    {
        var target = file ?? SelectedFile;
        if (target is null)
        {
            return;
        }

        UnhookParams(target.Parameters);
        Files.Remove(target);
        if (ReferenceEquals(SelectedFile, target))
        {
            SelectedFile = null;
        }

        StatusText = $"已移除，剩余 {Files.Count} 个文件";

    }

    [RelayCommand]
    private void ClearFiles()
    {
        foreach (var file in Files)
        {
            UnhookParams(file.Parameters);
        }

        Files.Clear();
        SelectedFile = null;
        StatusText = "列表已清空";

    }

    /// <summary>把当前参数应用到指定文件（或全部）。</summary>
    public void ApplyToFiles(IEnumerable<MediaFileViewModel> targets)
    {
        var count = 0;
        foreach (var file in targets)
        {
            if (ReferenceEquals(file.Parameters, Params))
            {
                continue;
            }

            // 标量参数直接覆盖
            file.Parameters.CopyScalarsFrom(Params);

            // 轨道：先按目标文件自己的流列表重建（流索引因文件而异），
            // 再按位置把当前文件的轨道动作搬过来——否则重建会把动作打回默认「直通」
            if (file.Info is not null)
            {
                file.Parameters.InitializeTracksFrom(file.Info, resetExisting: true);
            }

            file.Parameters.ApplyTracksFrom(Params);

            file.RefreshSummary();
            count++;
        }

        StatusText = count > 0 ? $"参数已应用到 {count} 个文件" : "没有需要应用的文件";
        OnPropertyChanged(nameof(ScopeText));
    }

    /// <summary>
    /// 同步开关打开时，把当前面板的标量参数铺到列表里所有文件。
    /// 只复制标量（便宜，可以跟着拖动实时跑）；轨道选择保持各文件独立。
    /// </summary>
    private void PropagateToAllIfNeeded()
    {
        if (!SyncToAllFiles || Files.Count == 0)
        {
            return;
        }

        var changed = 0;
        foreach (var file in Files)
        {
            if (ReferenceEquals(file.Parameters, Params))
            {
                continue;
            }

            file.Parameters.CopyScalarsFrom(Params);
            file.RefreshSummary();
            changed++;
        }

        if (changed > 0)
        {
            OnPropertyChanged(nameof(ScopeText));
        }
    }

    partial void OnSyncToAllFilesChanged(bool value)
    {
        _settings.Current.SyncParamsToAllFiles = value;
        _settings.ScheduleSave();
        OnPropertyChanged(nameof(ScopeText));
        PropagateToAllIfNeeded();
    }

    [RelayCommand]
    private void ApplyToAll() => ApplyToFiles(Files.ToArray());

    /// <summary>把参数重置为内置默认值。</summary>
    [RelayCommand]
    private void ResetParams()
    {
        _suppressRevalidate = true;
        try
        {
            var settings = _settings.Current;
            Params.EncoderId = settings.DefaultEncoderId;
            Params.QualityMode = QualityMode.Simple;
            Params.QualitySlider = 75;
            Params.VideoMode = VideoMode.Encode;
            Params.HwAccel = HwAccelKind.Auto;
            Params.ScaleMode = ScaleMode.Keep;
            Params.ScaleWidth = 0;
            Params.ScaleHeight = 0;
            Params.FrameRate = string.Empty;
            Params.Preset = string.Empty;
            Params.Tune = string.Empty;
            Params.Profile = string.Empty;
            Params.Level = string.Empty;
            Params.Gop = 0;
            Params.PixelFormat = string.Empty;
            Params.Container = settings.DefaultContainer;
            Params.NamingTemplate = settings.NamingTemplate;
            Params.FastStart = settings.FastStart;
            Params.AllowOverwrite = false;
            Params.ExtraArguments = string.Empty;
            Params.ExternalSubtitlePath = string.Empty;
            foreach (var track in Params.AudioTracks)
            {
                track.IsSelected = true;
                track.Action = AudioActionKind.Copy;
            }

            foreach (var track in Params.SubtitleTracks)
            {
                track.IsSelected = true;
                track.Action = SubtitleActionKind.Copy;
            }
        }
        finally
        {
            _suppressRevalidate = false;
        }

        StatusText = "参数已重置";
        Revalidate();
    }

    // ── 排队 ──

    /// <summary>把指定文件加入队列（空集合 = 全部可用的）。</summary>
    public void EnqueueFiles(IReadOnlyList<MediaFileViewModel>? files)
    {
        var candidates = (files is { Count: > 0 } ? files : Files.ToArray())
            .Where(f => f.IsReady)
            .ToArray();

        if (candidates.Length == 0)
        {
            StatusText = "没有可加入队列的文件（需要先分析成功）";
            return;
        }

        // 预检拦截：先把有阻断性问题的文件挑出来，让用户决定
        var blocked = new List<(MediaFileViewModel File, PreflightResult Result)>();
        foreach (var file in candidates)
        {
            var info = file.Info!;
            var outputPath = OutputPathBuilder.Build(info, file.Parameters, _settings.Current.DefaultOutputDirectory);
            var result = PreflightValidator.Validate(info, file.Parameters, _ffmpeg.Capabilities, outputPath);
            if (result.HasBlockingError)
            {
                blocked.Add((file, result));
            }
        }

        if (blocked.Count > 0)
        {
            var detail = string.Join(
                Environment.NewLine,
                blocked.Take(6).Select(b =>
                    $"· {b.File.FileName}：{string.Join("；", b.Result.Issues.Where(i => i.Severity == IssueSeverity.Error && !i.WasFixed).Select(i => $"{i.Title}"))}"));

            var message = blocked.Count == candidates.Length
                ? $"这 {blocked.Count} 个文件预检都没通过，无法加入队列：{Environment.NewLine}{Environment.NewLine}{detail}"
                : $"{blocked.Count} 个文件预检未通过，是否跳过它们、把其余 {candidates.Length - blocked.Count} 个加入队列？{Environment.NewLine}{Environment.NewLine}{detail}";

            if (blocked.Count == candidates.Length)
            {
                System.Windows.MessageBox.Show(message, "MediaCraft", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                StatusText = "预检未通过，未加入队列";
                return;
            }

            var choice = System.Windows.MessageBox.Show(
                message, "MediaCraft", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning);
            if (choice != System.Windows.MessageBoxResult.OK)
            {
                StatusText = "已取消加入队列";
                return;
            }

            var blockedSet = blocked.Select(b => b.File).ToHashSet();
            candidates = candidates.Where(f => !blockedSet.Contains(f)).ToArray();
        }

        var jobs = candidates
            .Select(file => new TranscodeJob(file.Path, file.Parameters.Clone()) { ParametersSummary = file.ParametersSummary })
            .ToArray();

        _queue.Enqueue(jobs);
        StatusText = $"已加入队列：{jobs.Length} 个任务（队列共 {_queue.Jobs.Count} 项）";
    }

    // ── 参数联动 ──

    /// <summary>切换编辑目标时更新派生状态。</summary>
    partial void OnSelectedFileChanged(MediaFileViewModel? value)
    {
        if (value is not null)
        {
            Params = value.Parameters;
        }
        else
        {
            Params = _templateParams;
        }

        UpdateEditTargetText();
        Revalidate();
    }

    partial void OnParamsChanged(TranscodeParams value)
    {
        OnPropertyChanged(nameof(IsSimpleMode));
        OnPropertyChanged(nameof(IsAdvancedMode));
        OnPropertyChanged(nameof(IsQualityRateControl));
        OnPropertyChanged(nameof(AvailablePresets));
        OnPropertyChanged(nameof(AvailableTunes));
        OnPropertyChanged(nameof(AvailableProfiles));
        UpdateQualityHint();
    }

    /// <summary>切换简单/高级模式（顺带同步两边的值，避免跳变）。</summary>
    [RelayCommand]
    private void SetQualityMode(string? mode)
    {
        if (!Enum.TryParse<QualityMode>(mode, ignoreCase: true, out var parsed))
        {
            return;
        }

        Params.SyncModeValues();
        Params.QualityMode = parsed;
        OnPropertyChanged(nameof(IsSimpleMode));
        OnPropertyChanged(nameof(IsAdvancedMode));
        Revalidate();
    }

    [RelayCommand]
    private void RefreshQualityHint() => UpdateQualityHint();

    /// <summary>刷新质量提示与编码器说明（拖动滑块时必须实时更新，否则用户没有任何反馈）。</summary>
    private void UpdateQualityHint()
    {
        var encoder = Params.Encoder;
        EncoderHint = Options.EncoderHint(encoder);
        QualityLabelText = $"{encoder.QualityLabel}（范围 {encoder.QualityMin}-{encoder.QualityMax}，越小越清晰）";

        if (Params.VideoMode != VideoMode.Encode)
        {
            QualityHint = "当前不做视频重编码，质量参数不生效";
            return;
        }

        if (Params.QualityMode == QualityMode.Simple)
        {
            var quality = EncoderCatalog.MapSliderToQuality(encoder, Params.QualitySlider);
            var preset = EncoderCatalog.MapSliderToPreset(encoder, Params.QualitySlider);
            QualityHint =
                $"{EncoderCatalog.DescribeQuality(Params.QualitySlider)}　→　" +
                $"{encoder.QualityParam} {quality}" + (string.IsNullOrEmpty(preset) ? string.Empty : $" -preset {preset}");
        }
        else
        {
            QualityHint = Params.RateControl == RateControlKind.Quality
                ? $"直接指定 {encoder.QualityLabel}（范围 {encoder.QualityMin}-{encoder.QualityMax}，越小越清晰）"
                : "按目标码率编码，适合需要控制文件体积的场景";
        }
    }

    /// <summary>选择外挂字幕文件。</summary>
    [RelayCommand]
    private void PickExternalSubtitle()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择字幕文件",
            Filter = "字幕文件|*.srt;*.ass;*.ssa;*.vtt|所有文件|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() == true)
        {
            Params.ExternalSubtitlePath = dialog.FileName;
            Revalidate();
        }
    }

    [RelayCommand]
    private void ClearExternalSubtitle()
    {
        Params.ExternalSubtitlePath = string.Empty;
        Revalidate();
    }

    // ── 预检与预览 ──

    /// <summary>重新计算输出预览与预检结果。</summary>
    public void Revalidate()
    {
        var file = SelectedFile;
        var info = file?.Info;

        if (info is null)
        {
            OutputPreview = Files.Count == 0 ? "—" : "选中一个文件后显示输出路径";
            PreflightIssues.Clear();
            PreflightSummary = "选中文件后执行预检";
            HasBlockingError = false;
            return;
        }

        try
        {
            // 轨道上的「预检已调整…」说明只在刚发生修正后有意义，
            // 每次预检先清空，避免改了容器之后留下过期提示。
            ClearTrackPreflightNotes();

            var outputPath = OutputPathBuilder.Build(info, Params, _settings.Current.DefaultOutputDirectory);
            var result = PreflightValidator.Validate(info, Params, _ffmpeg.Capabilities, outputPath);

            // 把预检的自动修正**写回正在编辑的参数**。
            // 否则界面会自相矛盾：预检卡片写「已自动处理：音频 #1 已改为重编码 aac」，
            // 而上面的音频板块仍显示「直通」——执行时用的是修正后的版本，用户看到的却不是它。
            // 不变式：参数面板显示的东西 == 实际会执行的东西。
            if (!_applyingPreflightFix && !Params.ContentEquals(result.Effective))
            {
                ApplyPreflightFixes(result);
                outputPath = OutputPathBuilder.Build(info, Params, _settings.Current.DefaultOutputDirectory);
                result = PreflightValidator.Validate(info, Params, _ffmpeg.Capabilities, outputPath);
            }

            OutputPreview = outputPath;
            PreflightIssues.Clear();
            foreach (var issue in result.Issues)
            {
                PreflightIssues.Add(issue);
            }

            PreflightSummary = result.Summary;
            HasBlockingError = result.HasBlockingError;
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "TranscodeViewModel.Revalidate");
            PreflightSummary = "预检异常：" + ex.Message;
            HasBlockingError = true;
        }
    }

    /// <summary>
    /// 把预检的修正搬回参数，并在被改动的轨道上留下原因说明。
    /// 轨道按位置搬运：Effective 与当前参数是同源副本，轨道数量与顺序一致。
    /// </summary>
    private void ApplyPreflightFixes(PreflightResult result)
    {
        _applyingPreflightFix = true;
        try
        {
            var container = Params.ContainerDefinition;

            // 先逐条比对，写清楚「哪条轨被改成了什么、为什么」
            for (var index = 0; index < Params.AudioTracks.Count && index < result.Effective.AudioTracks.Count; index++)
            {
                var before = Params.AudioTracks[index];
                var after = result.Effective.AudioTracks[index];

                if (before.Action == after.Action &&
                    string.Equals(before.CodecId, after.CodecId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var reason = after.Action == AudioActionKind.Encode &&
                             !EncoderCatalog.IsAudioCodecCompatible(before.SourceCodec, container)
                    ? $"{container.Extension.ToUpperInvariant()} 装不下 {before.SourceCodec}"
                    : "与容器不兼容";

                before.PreflightNote =
                    $"预检已调整：{DescribeAudioAction(before)} → {DescribeAudioAction(after)}（{reason}）";
            }

            for (var index = 0; index < Params.SubtitleTracks.Count && index < result.Effective.SubtitleTracks.Count; index++)
            {
                var before = Params.SubtitleTracks[index];
                var after = result.Effective.SubtitleTracks[index];

                if (before.Action == after.Action)
                {
                    continue;
                }

                var reason = after.Action == SubtitleActionKind.Drop
                    ? $"{container.Extension.ToUpperInvariant()} 不支持 {before.SourceCodec} 字幕"
                    : "与容器不兼容";

                before.PreflightNote =
                    $"预检已调整：{DescribeSubtitleAction(before)} → {DescribeSubtitleAction(after)}（{reason}）";
            }

            Params.CopyScalarsFrom(result.Effective);
            Params.ApplyTracksFrom(result.Effective);
            Params.OutputDirectory = result.Effective.OutputDirectory;

            SelectedFile?.RefreshSummary();
            PropagateToAllIfNeeded();

            AppLog.Info(
                $"预检自动调整了参数并已同步到参数面板（{result.Issues.Count(i => i.WasFixed)} 处修正）",
                "Transcode");
        }
        finally
        {
            _applyingPreflightFix = false;
        }
    }

    /// <summary>清空所有轨道上的预检说明。</summary>
    private void ClearTrackPreflightNotes()
    {
        foreach (var track in Params.AudioTracks)
        {
            track.PreflightNote = string.Empty;
        }

        foreach (var track in Params.SubtitleTracks)
        {
            track.PreflightNote = string.Empty;
        }
    }

    private static string DescribeAudioAction(AudioTrackParams track) => track.Action switch
    {
        AudioActionKind.Copy => "直通",
        AudioActionKind.Encode => $"重编码 {track.CodecId}",
        _ => "丢弃",
    };

    private static string DescribeSubtitleAction(SubtitleTrackParams track) => track.Action switch
    {
        SubtitleActionKind.Copy => "内封保留",
        SubtitleActionKind.Burn => "烧入画面",
        SubtitleActionKind.Extract => "提取为文件",
        _ => "丢弃",
    };

    private void ScheduleRevalidate()
    {
        if (_suppressRevalidate)
        {
            return;
        }

        _revalidateTimer.Stop();
        _revalidateTimer.Start();
    }

    private void UpdateEditTargetText()
    {
        EditTargetText = SelectedFile is null
            ? "新文件默认参数（添加文件时复制这套设置）"
            : $"正在编辑：{SelectedFile.FileName}";
        OnPropertyChanged(nameof(HasSelectedFile));
        OnPropertyChanged(nameof(ScopeText));
    }

    /// <summary>
    /// 订阅参数对象的所有变化（含轨道与字幕样式），用于实时更新预览与预检。
    /// </summary>
    private void HookParams(TranscodeParams parameters)
    {
        parameters.PropertyChanged += OnParamsPropertyChanged;
        parameters.SubtitleStyle.PropertyChanged += OnNestedPropertyChanged;
        parameters.AudioTracks.CollectionChanged += OnTracksChanged;
        parameters.SubtitleTracks.CollectionChanged += OnTracksChanged;

        foreach (var track in parameters.AudioTracks)
        {
            track.PropertyChanged += OnNestedPropertyChanged;
        }

        foreach (var track in parameters.SubtitleTracks)
        {
            track.PropertyChanged += OnNestedPropertyChanged;
        }
    }

    private void UnhookParams(TranscodeParams parameters)
    {
        parameters.PropertyChanged -= OnParamsPropertyChanged;
        parameters.SubtitleStyle.PropertyChanged -= OnNestedPropertyChanged;
        parameters.AudioTracks.CollectionChanged -= OnTracksChanged;
        parameters.SubtitleTracks.CollectionChanged -= OnTracksChanged;

        foreach (var track in parameters.AudioTracks)
        {
            track.PropertyChanged -= OnNestedPropertyChanged;
        }

        foreach (var track in parameters.SubtitleTracks)
        {
            track.PropertyChanged -= OnNestedPropertyChanged;
        }
    }

    private void OnTracksChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (var item in e.NewItems.OfType<INotifyPropertyChanged>())
            {
                item.PropertyChanged += OnNestedPropertyChanged;
            }
        }

        if (e.OldItems is not null)
        {
            foreach (var item in e.OldItems.OfType<INotifyPropertyChanged>())
            {
                item.PropertyChanged -= OnNestedPropertyChanged;
            }
        }

        OnNestedPropertyChanged(sender, new PropertyChangedEventArgs("Tracks"));

        // 轨道集合变了要让 Params.* 的绑定重新求值（例如提示语显隐、轨道行数量）
        OnPropertyChanged(nameof(Params));
    }

    private void OnNestedPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 任何参数变化都刷新提示：之前只在「编辑模板」分支里刷新，
        // 导致选中文件拖滑块时提示文本不动，用户以为滑块没生效。
        UpdateQualityHint();

        SelectedFile?.RefreshSummary();
        ScheduleRevalidate();
    }

    private void OnParamsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnNestedPropertyChanged(sender, e);

        switch (e.PropertyName)
        {
            case nameof(TranscodeParams.QualityMode):
                OnPropertyChanged(nameof(IsSimpleMode));
                OnPropertyChanged(nameof(IsAdvancedMode));
                break;
            case nameof(TranscodeParams.RateControl):
                OnPropertyChanged(nameof(IsQualityRateControl));
                break;
            case nameof(TranscodeParams.EncoderId):
                OnPropertyChanged(nameof(AvailablePresets));
                OnPropertyChanged(nameof(AvailableTunes));
                OnPropertyChanged(nameof(AvailableProfiles));
                UpdateQualityHint();
                break;
        }
    }
}
