using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MediaCraft.Media;

/// <summary>参数编辑模式。</summary>
public enum QualityMode
{
    /// <summary>简单模式：一个质量滑块 + 自动映射到原生参数。</summary>
    Simple = 0,

    /// <summary>高级模式：直接给原生参数（CRF/CQ/QP、preset、profile…）。</summary>
    Advanced,
}

/// <summary>码率控制方式（仅高级模式）。</summary>
public enum RateControlKind
{
    /// <summary>质量优先：NVENC 的 -cq / x264 的 -crf / QSV 的 -global_quality。</summary>
    Quality = 0,

    /// <summary>目标码率：-b:v（可选 -maxrate/-bufsize 走 CBR）。</summary>
    Bitrate,
}

/// <summary>硬件解码方式。</summary>
public enum HwAccelKind
{
    /// <summary>跟随编码器自动选择（选了硬件编码器就配对应硬解）。</summary>
    Auto = 0,

    None,

    Cuda,

    Qsv,

    D3d11va,

    Dxva2,
}

/// <summary>视频处理方式。</summary>
public enum VideoMode
{
    /// <summary>重新编码。</summary>
    Encode = 0,

    /// <summary>直接复制视频流（纯封装）。</summary>
    Copy,

    /// <summary>丢弃视频流（提取音频）。</summary>
    Drop,
}

/// <summary>分辨率处理方式。</summary>
public enum ScaleMode
{
    Keep = 0,

    /// <summary>指定宽度，高度按比例算。</summary>
    Width,

    /// <summary>指定高度，宽度按比例算。</summary>
    Height,

    /// <summary>缩放到指定框内（保持比例，只缩不放）。</summary>
    Fit,
}

/// <summary>音频轨处理方式。</summary>
public enum AudioActionKind
{
    Copy = 0,

    Encode,

    Drop,
}

/// <summary>字幕轨处理方式。</summary>
public enum SubtitleActionKind
{
    Copy = 0,

    /// <summary>烧入画面（需要重编码视频）。</summary>
    Burn,

    /// <summary>提取为独立字幕文件（作为该任务的附加步骤）。</summary>
    Extract,

    Drop,
}

/// <summary>字幕文件格式。</summary>
public enum SubtitleFormat
{
    Srt = 0,

    Ass,

    Vtt,
}

/// <summary>单条音频轨的处理参数。</summary>
public sealed partial class AudioTrackParams : ObservableObject
{
    public AudioTrackParams()
    {
    }

    public AudioTrackParams(int streamIndex, string displayName, string sourceCodec, int channels, string language)
    {
        StreamIndex = streamIndex;
        DisplayName = displayName;
        SourceCodec = sourceCodec;
        Channels = channels;
        Language = language;
    }

    public int StreamIndex { get; set; }

    /// <summary>界面显示名（来自 ffprobe）。</summary>
    public string DisplayName { get; set; } = string.Empty;

    public string SourceCodec { get; set; } = string.Empty;

    public int Channels { get; set; }

    public string Language { get; set; } = string.Empty;

    [ObservableProperty]
    private bool _isSelected = true;

    [ObservableProperty]
    private AudioActionKind _action = AudioActionKind.Copy;

    [ObservableProperty]
    private string _codecId = "aac";

    [ObservableProperty]
    private int _bitRateKbps = 192;

    /// <summary>目标声道数；0 = 保持源声道。</summary>
    [ObservableProperty]
    private int _targetChannels;

    public AudioTrackParams Clone() => new()
    {
        StreamIndex = StreamIndex,
        DisplayName = DisplayName,
        SourceCodec = SourceCodec,
        Channels = Channels,
        Language = Language,
        IsSelected = IsSelected,
        Action = Action,
        CodecId = CodecId,
        BitRateKbps = BitRateKbps,
        TargetChannels = TargetChannels,
    };
}

/// <summary>单条字幕轨的处理参数。</summary>
public sealed partial class SubtitleTrackParams : ObservableObject
{
    public SubtitleTrackParams()
    {
    }

    public SubtitleTrackParams(int streamIndex, string displayName, string sourceCodec, bool isBitmap)
    {
        StreamIndex = streamIndex;
        DisplayName = displayName;
        SourceCodec = sourceCodec;
        IsBitmap = isBitmap;
    }

    public int StreamIndex { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    public string SourceCodec { get; set; } = string.Empty;

    /// <summary>图形字幕（PGS/DVD）无法转成文本字幕。</summary>
    public bool IsBitmap { get; set; }

    [ObservableProperty]
    private bool _isSelected = true;

    [ObservableProperty]
    private SubtitleActionKind _action = SubtitleActionKind.Copy;

    [ObservableProperty]
    private SubtitleFormat _extractFormat = SubtitleFormat.Srt;

    public SubtitleTrackParams Clone() => new()
    {
        StreamIndex = StreamIndex,
        DisplayName = DisplayName,
        SourceCodec = SourceCodec,
        IsBitmap = IsBitmap,
        IsSelected = IsSelected,
        Action = Action,
        ExtractFormat = ExtractFormat,
    };
}

/// <summary>字幕烧入的样式选项（映射为 libass 的 force_style）。</summary>
public sealed partial class SubtitleStyleOptions : ObservableObject
{
    [ObservableProperty]
    private string _fontName = "Microsoft YaHei";

    /// <summary>ASS 画布（默认 288 高）中的字号；24 约等于画面高度的 8%。</summary>
    [ObservableProperty]
    private int _fontSize = 24;

    [ObservableProperty]
    private string _primaryColor = "#FFFFFF";

    [ObservableProperty]
    private string _outlineColor = "#000000";

    [ObservableProperty]
    private int _outlineWidth = 2;

    [ObservableProperty]
    private int _shadow;

    /// <summary>底边距（ASS 单位）。</summary>
    [ObservableProperty]
    private int _marginVertical = 20;

    /// <summary>对齐方式，ASS 小键盘编号，2 = 底部居中。</summary>
    [ObservableProperty]
    private int _alignment = 2;

    [ObservableProperty]
    private bool _bold;

    public SubtitleStyleOptions Clone() => new()
    {
        FontName = FontName,
        FontSize = FontSize,
        PrimaryColor = PrimaryColor,
        OutlineColor = OutlineColor,
        OutlineWidth = OutlineWidth,
        Shadow = Shadow,
        MarginVertical = MarginVertical,
        Alignment = Alignment,
        Bold = Bold,
    };
}

/// <summary>
/// 一个任务的完整参数。每个文件各自持有一份（含各自的轨道选择），
/// 因此可以针对不同素材用不同规格。
/// </summary>
public sealed partial class TranscodeParams : ObservableObject
{
    private static readonly JsonSerializerOptions CloneOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    // ── 视频 ──

    [ObservableProperty]
    private string _encoderId = "h264_nvenc";

    [ObservableProperty]
    private QualityMode _qualityMode = QualityMode.Simple;

    [ObservableProperty]
    private VideoMode _videoMode = VideoMode.Encode;

    /// <summary>简单模式质量滑块（0-100，越大越清晰）。</summary>
    [ObservableProperty]
    private int _qualitySlider = 75;

    [ObservableProperty]
    private RateControlKind _rateControl = RateControlKind.Quality;

    /// <summary>高级模式：原生质量值（CQ / CRF / 全局质量）。</summary>
    [ObservableProperty]
    private int _qualityValue = 23;

    [ObservableProperty]
    private int _bitrateKbps = 4000;

    /// <summary>0 = 不设置 maxrate。</summary>
    [ObservableProperty]
    private int _maxrateKbps;

    /// <summary>0 = 不设置 bufsize。</summary>
    [ObservableProperty]
    private int _bufsizeKbps;

    [ObservableProperty]
    private string _preset = string.Empty;

    [ObservableProperty]
    private string _tune = string.Empty;

    [ObservableProperty]
    private string _profile = string.Empty;

    [ObservableProperty]
    private string _level = string.Empty;

    /// <summary>关键帧间隔；0 = 自动。</summary>
    [ObservableProperty]
    private int _gop;

    [ObservableProperty]
    private string _pixelFormat = string.Empty;

    [ObservableProperty]
    private HwAccelKind _hwAccel = HwAccelKind.Auto;

    [ObservableProperty]
    private ScaleMode _scaleMode = ScaleMode.Keep;

    [ObservableProperty]
    private int _scaleWidth;

    [ObservableProperty]
    private int _scaleHeight;

    /// <summary>目标帧率；空 = 保持源帧率。</summary>
    [ObservableProperty]
    private string _frameRate = string.Empty;

    // ── 轨道 ──

    public ObservableCollection<AudioTrackParams> AudioTracks { get; set; } = [];

    public ObservableCollection<SubtitleTrackParams> SubtitleTracks { get; set; } = [];

    [ObservableProperty]
    private SubtitleStyleOptions _subtitleStyle = new();

    /// <summary>外挂字幕文件路径（设置后优先烧入它，忽略内封字幕轨）。</summary>
    [ObservableProperty]
    private string _externalSubtitlePath = string.Empty;

    /// <summary>字幕文件输入时的目标格式。</summary>
    [ObservableProperty]
    private SubtitleFormat _subtitleConvertFormat = SubtitleFormat.Ass;

    // ── 输出 ──

    [ObservableProperty]
    private string _container = "mp4";

    /// <summary>文件名模板，占位符：{name} {encoder} {quality} {date} {index} {ext}。</summary>
    [ObservableProperty]
    private string _namingTemplate = "{name}_{encoder}_{quality}";

    /// <summary>输出目录；空 = 源文件所在目录。</summary>
    [ObservableProperty]
    private string _outputDirectory = string.Empty;

    [ObservableProperty]
    private bool _allowOverwrite;

    [ObservableProperty]
    private bool _fastStart = true;

    /// <summary>附加的原生参数（原样追加到命令行，仅高级模式建议使用）。</summary>
    [ObservableProperty]
    private string _extraArguments = string.Empty;

    /// <summary>参数摘要（队列与预设列表里显示）。</summary>
    [JsonIgnore]
    public string Summary
    {
        get
        {
            var encoder = Ffmpeg.EncoderCatalog.Get(EncoderId);
            var containerDef = Ffmpeg.EncoderCatalog.GetContainer(Container);

            if (VideoMode == VideoMode.Drop)
            {
                return $"仅音频 → {containerDef.Extension.ToUpperInvariant()}";
            }

            if (VideoMode == VideoMode.Copy)
            {
                return $"视频直通 → {containerDef.Extension.ToUpperInvariant()}";
            }

            var qualityText = QualityMode == QualityMode.Simple
                ? $"质量 {QualitySlider}"
                : RateControl == RateControlKind.Quality
                    ? $"{encoder.QualityLabel} {QualityValue}"
                    : $"{BitrateKbps} kbps";

            var scaleText = ScaleMode switch
            {
                ScaleMode.Width => $" {ScaleWidth}px宽",
                ScaleMode.Height => $" {ScaleHeight}px高",
                ScaleMode.Fit => $" {ScaleWidth}×{ScaleHeight}框",
                _ => string.Empty,
            };

            var burn = SubtitleTracks.Any(t => t.IsSelected && t.Action == SubtitleActionKind.Burn)
                       || !string.IsNullOrWhiteSpace(ExternalSubtitlePath);
            var burnText = burn ? " +烧字幕" : string.Empty;

            return $"{encoder.DisplayName.Split('（')[0].Trim()} · {qualityText}{scaleText}{burnText} → {containerDef.Extension.ToUpperInvariant()}";
        }
    }

    /// <summary>当前生效的编码器定义。</summary>
    [JsonIgnore]
    public Ffmpeg.EncoderDefinition Encoder => Ffmpeg.EncoderCatalog.Get(EncoderId);

    /// <summary>当前生效的容器定义。</summary>
    [JsonIgnore]
    public Ffmpeg.ContainerDefinition ContainerDefinition => Ffmpeg.EncoderCatalog.GetContainer(Container);

    /// <summary>深拷贝（预设套用、每文件独立参数都要用）。</summary>
    public TranscodeParams Clone()
    {
        var json = JsonSerializer.Serialize(this, CloneOptions);
        var clone = JsonSerializer.Deserialize<TranscodeParams>(json, CloneOptions) ?? new TranscodeParams();

        // ObservableCollection 的属性在反序列化时会换成新实例，轨道里的引用关系需要重新建立
        clone.AudioTracks = new ObservableCollection<AudioTrackParams>(AudioTracks.Select(t => t.Clone()));
        clone.SubtitleTracks = new ObservableCollection<SubtitleTrackParams>(SubtitleTracks.Select(t => t.Clone()));
        clone.SubtitleStyle = SubtitleStyle.Clone();
        return clone;
    }

    /// <summary>切换模式时把当前值同步过去，避免用户看到「跳变」。</summary>
    public void SyncModeValues()
    {
        var encoder = Encoder;
        if (QualityMode == QualityMode.Advanced)
        {
            // 简单 → 高级：把滑块换算成原生值
            QualityValue = Ffmpeg.EncoderCatalog.MapSliderToQuality(encoder, QualitySlider);
            if (string.IsNullOrEmpty(Preset))
            {
                Preset = Ffmpeg.EncoderCatalog.MapSliderToPreset(encoder, QualitySlider);
            }
        }
        else
        {
            // 高级 → 简单：把原生值反推回滑块位置
            QualitySlider = Ffmpeg.EncoderCatalog.MapQualityToSlider(encoder, QualityValue);
        }
    }

    /// <summary>新文件是否应跟随当前参数（模板模式）。</summary>
    [JsonIgnore]
    public bool IsTemplate => false;

    /// <summary>
    /// 按 ffprobe 结果生成轨道列表（默认：全部音轨直通、全部字幕直通）。
    /// 每文件独立参数的前提是轨道选择跟着文件走，所以这里按文件重建。
    /// </summary>
    public void InitializeTracksFrom(Ffmpeg.MediaInfo info, bool resetExisting = false)
    {
        if (resetExisting || AudioTracks.Count == 0)
        {
            AudioTracks.Clear();
            foreach (var stream in info.AudioStreams)
            {
                AudioTracks.Add(new AudioTrackParams(
                    stream.Index,
                    stream.DisplayName,
                    stream.CodecName,
                    stream.Channels,
                    stream.Language));
            }
        }

        if (resetExisting || SubtitleTracks.Count == 0)
        {
            SubtitleTracks.Clear();
            foreach (var stream in info.SubtitleStreams)
            {
                SubtitleTracks.Add(new SubtitleTrackParams(
                    stream.Index,
                    stream.DisplayName,
                    stream.CodecName,
                    stream.IsBitmapSubtitle));
            }
        }
    }
}
