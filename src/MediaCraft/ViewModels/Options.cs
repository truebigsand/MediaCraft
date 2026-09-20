using MediaCraft.Ffmpeg;
using MediaCraft.Media;

namespace MediaCraft.ViewModels;

/// <summary>带显示名的枚举选项。</summary>
public sealed class NamedOption<T>
{
    public required T Value { get; init; }

    public required string Name { get; init; }
}

/// <summary>
/// 界面上各类枚举下拉框的静态选项表（用 x:Static 直接绑定，省掉 RelativeSource 层层查找）。
/// </summary>
public static class Options
{
    public static IReadOnlyList<NamedOption<VideoMode>> VideoModes { get; } =
    [
        new NamedOption<VideoMode> { Value = VideoMode.Encode, Name = "重新编码" },
        new NamedOption<VideoMode> { Value = VideoMode.Copy, Name = "直接复制（纯封装）" },
        new NamedOption<VideoMode> { Value = VideoMode.Drop, Name = "丢弃视频（提取音频）" },
    ];

    public static IReadOnlyList<NamedOption<HwAccelKind>> HwAccelOptions { get; } =
    [
        new NamedOption<HwAccelKind> { Value = HwAccelKind.Auto, Name = "自动（跟随编码器）" },
        new NamedOption<HwAccelKind> { Value = HwAccelKind.None, Name = "不使用硬解（CPU 解码）" },
        new NamedOption<HwAccelKind> { Value = HwAccelKind.Cuda, Name = "CUDA（NVIDIA）" },
        new NamedOption<HwAccelKind> { Value = HwAccelKind.Qsv, Name = "QSV（Intel 核显）" },
        new NamedOption<HwAccelKind> { Value = HwAccelKind.D3d11va, Name = "D3D11VA" },
        new NamedOption<HwAccelKind> { Value = HwAccelKind.Dxva2, Name = "DXVA2" },
    ];

    public static IReadOnlyList<NamedOption<ScaleMode>> ScaleModes { get; } =
    [
        new NamedOption<ScaleMode> { Value = ScaleMode.Keep, Name = "保持原分辨率" },
        new NamedOption<ScaleMode> { Value = ScaleMode.Width, Name = "指定宽度（高度按比例）" },
        new NamedOption<ScaleMode> { Value = ScaleMode.Height, Name = "指定高度（宽度按比例）" },
        new NamedOption<ScaleMode> { Value = ScaleMode.Fit, Name = "缩放到框内（只缩不放）" },
    ];

    public static IReadOnlyList<NamedOption<VideoMode>> VideoModeRadioOptions => VideoModes;

    public static IReadOnlyList<NamedOption<RateControlKind>> RateControls { get; } =
    [
        new NamedOption<RateControlKind> { Value = RateControlKind.Quality, Name = "质量优先（CQ / CRF）" },
        new NamedOption<RateControlKind> { Value = RateControlKind.Bitrate, Name = "目标码率（-b:v）" },
    ];

    public static IReadOnlyList<NamedOption<AudioActionKind>> AudioActions { get; } =
    [
        new NamedOption<AudioActionKind> { Value = AudioActionKind.Copy, Name = "直通（不重编码）" },
        new NamedOption<AudioActionKind> { Value = AudioActionKind.Encode, Name = "重编码" },
        new NamedOption<AudioActionKind> { Value = AudioActionKind.Drop, Name = "丢弃" },
    ];

    public static IReadOnlyList<NamedOption<SubtitleActionKind>> SubtitleActions { get; } =
    [
        new NamedOption<SubtitleActionKind> { Value = SubtitleActionKind.Copy, Name = "内封保留" },
        new NamedOption<SubtitleActionKind> { Value = SubtitleActionKind.Burn, Name = "烧入画面" },
        new NamedOption<SubtitleActionKind> { Value = SubtitleActionKind.Extract, Name = "提取为文件" },
        new NamedOption<SubtitleActionKind> { Value = SubtitleActionKind.Drop, Name = "丢弃" },
    ];

    public static IReadOnlyList<NamedOption<SubtitleFormat>> SubtitleFormats { get; } =
    [
        new NamedOption<SubtitleFormat> { Value = SubtitleFormat.Srt, Name = "SRT" },
        new NamedOption<SubtitleFormat> { Value = SubtitleFormat.Ass, Name = "ASS" },
        new NamedOption<SubtitleFormat> { Value = SubtitleFormat.Vtt, Name = "VTT" },
    ];

    /// <summary>字幕对齐方式（ASS 小键盘编号）。</summary>
    public static IReadOnlyList<NamedOption<int>> SubtitleAlignments { get; } =
    [
        new NamedOption<int> { Value = 1, Name = "左下" },
        new NamedOption<int> { Value = 2, Name = "底部居中" },
        new NamedOption<int> { Value = 3, Name = "右下" },
        new NamedOption<int> { Value = 4, Name = "左侧居中" },
        new NamedOption<int> { Value = 5, Name = "画面正中" },
        new NamedOption<int> { Value = 6, Name = "右侧居中" },
        new NamedOption<int> { Value = 7, Name = "左上" },
        new NamedOption<int> { Value = 8, Name = "顶部居中" },
        new NamedOption<int> { Value = 9, Name = "右上" },
    ];

    /// <summary>编码器下拉（按硬件/软件分组）。</summary>
    public static IReadOnlyList<NamedOption<string>> EncoderGroups { get; } =
    [
        new NamedOption<string> { Value = "hardware", Name = "硬件编码（显卡）" },
        new NamedOption<string> { Value = "software", Name = "软件编码（CPU）" },
    ];

    /// <summary>常用字体（本机常见中文字体 + 通用无衬线）。</summary>
    public static IReadOnlyList<string> CommonFonts { get; } =
    [
        "Microsoft YaHei",
        "Microsoft YaHei UI",
        "SimHei",
        "SimSun",
        "KaiTi",
        "DengXian",
        "Arial",
        "Segoe UI",
        "Tahoma",
    ];

    /// <summary>命名模板占位符提示。</summary>
    public static string NamingTemplateHint =>
        "可用占位符：" + string.Join("  ", Media.OutputPathBuilder.TemplatePlaceholders);

    /// <summary>编码器短说明。</summary>
    public static string EncoderHint(EncoderDefinition encoder) =>
        encoder.IsHardware
            ? $"硬件编码，速度快、占用 CPU 低；最大 {encoder.MaxWidth}×{encoder.MaxHeight}" +
              (encoder.SupportsTenBit ? "，支持 10bit" : string.Empty)
            : $"软件编码，兼容性最好、压缩比高但速度慢；最大 {encoder.MaxWidth}×{encoder.MaxHeight}" +
              (encoder.SupportsTenBit ? "，支持 10bit" : string.Empty);
}
