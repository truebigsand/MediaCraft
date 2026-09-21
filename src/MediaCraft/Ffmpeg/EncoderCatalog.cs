using MediaCraft.Media;

namespace MediaCraft.Ffmpeg;

/// <summary>文件类型。</summary>
public enum MediaFileKind
{
    Unknown = 0,
    Video,
    Audio,
    Subtitle,
}

/// <summary>编码器家族，决定参数风格。</summary>
public enum EncoderFamily
{
    Nvenc = 0,
    Qsv,
    X264,
    X265,
    SvtAv1,
    Aom,
}

/// <summary>一个视频编码器的定义与参数元数据。</summary>
public sealed class EncoderDefinition
{
    public required string Id { get; init; }

    /// <summary>界面显示名。</summary>
    public required string DisplayName { get; init; }

    /// <summary>目标编码格式：h264 / hevc / av1 / vp9。</summary>
    public required string Codec { get; init; }

    public required EncoderFamily Family { get; init; }

    /// <summary>使用该编码器时建议的硬解方式。</summary>
    public HwAccelKind PreferredAccel { get; init; } = HwAccelKind.None;

    /// <summary>质量参数名：-cq / -crf / -global_quality。</summary>
    public string QualityParam { get; init; } = "-crf";

    /// <summary>preset 参数名：-preset（libaom 用 -cpu-used）。</summary>
    public string PresetParam { get; init; } = "-preset";

    /// <summary>质量参数显示名（界面用）。</summary>
    public string QualityLabel { get; init; } = "质量值";

    public int QualityMin { get; init; }

    public int QualityMax { get; init; } = 51;

    public string[] Presets { get; init; } = [];

    public string DefaultPreset { get; init; } = string.Empty;

    public string[] Tunes { get; init; } = [];

    public string[] Profiles { get; init; } = [];

    public bool IsHardware => PreferredAccel != HwAccelKind.None;

    public bool SupportsTenBit { get; init; }

    public int MaxWidth { get; init; } = 4096;

    public int MaxHeight { get; init; } = 2304;

    /// <summary>界面分组名。</summary>
    public string GroupName => Family switch
    {
        EncoderFamily.Nvenc => "硬件编码 · NVIDIA NVENC",
        EncoderFamily.Qsv => "硬件编码 · Intel QSV",
        _ => "软件编码 · CPU",
    };

    public override string ToString() => DisplayName;
}

/// <summary>容器定义：决定可用的编码器与默认扩展名。</summary>
public sealed class ContainerDefinition
{
    public required string Extension { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>是否支持视频流（false = 纯音频容器）。</summary>
    public bool VideoCapable { get; init; } = true;

    public string[] VideoCodecs { get; init; } = [];

    public string[] AudioCodecs { get; init; } = [];

    /// <summary>该容器允许的字幕编码器；空数组表示不支持内封字幕。</summary>
    public string[] SubtitleCodecs { get; init; } = [];

    /// <summary>
    /// 音轨条数上限；0 = 不限。实测：mp3 / flac / wav 只接受单条音轨，多条会直接写入失败：
    /// 「Exactly one MP3 audio stream is required.」/「…FLAC audio stream is required.」/
    /// 「wav muxer does not support more than one stream of type audio」。
    /// </summary>
    public int MaxAudioStreams { get; init; }

    /// <summary>下拉框的可访问名称。</summary>
    public override string ToString() => DisplayName;
}

/// <summary>音频编码器定义。</summary>
public sealed class AudioCodecDefinition
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>
    /// 无损编码：码率由采样率/位深/声道决定（PCM）或取决于内容（FLAC/ALAC），
    /// 因此 `-b:a` 对它没有意义 —— 实测 ffmpeg 会静默忽略（同一输入加不加 -b:a 产出字节完全一致）。
    /// 界面对这类编码器隐藏码率输入。
    /// </summary>
    public bool IsLossless { get; init; }

    /// <summary>
    /// 固定码率档位；空数组 = 自由填。AC3 是固定档位集合，
    /// 实测填入非法值会被 ffmpeg **静默取整**（200k → 192k，1000k → 640k），所以界面只给合法档位。
    /// </summary>
    public int[] BitrateOptions { get; init; } = [];

    /// <summary>选中该编码器时的默认码率（无损忽略）。</summary>
    public int DefaultBitrateKbps { get; init; } = 192;

    /// <summary>下拉框的可访问名称。</summary>
    public override string ToString() => DisplayName;

    /// <summary>PCM 的位深；非 PCM 返回 0。</summary>
    public static int PcmBitDepth(string codecId) => codecId switch
    {
        "pcm_s16le" => 16,
        "pcm_s24le" => 24,
        "pcm_s32le" => 32,
        _ => 0,
    };
}

/// <summary>
/// 编码器 / 容器目录，附带「简单模式」滑块到原生参数的映射。
/// 这里的取值都以本机 ffmpeg 实测为准（见 docs/spec.md 的探测记录）。
/// </summary>
public static class EncoderCatalog
{
    private static readonly string[] NvencPresets = ["p1", "p2", "p3", "p4", "p5", "p6", "p7"];
    private static readonly string[] NvencTunes = ["hq", "ll", "ull", "lossless"];
    private static readonly string[] QsvPresets =
        ["veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow"];
    private static readonly string[] X26xPresets =
        ["ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow", "placebo"];
    private static readonly string[] X26xTunes = ["film", "animation", "grain", "stillimage", "fastdecode", "zerolatency"];
    private static readonly string[] SvtAv1Presets =
        ["0", "1", "2", "3", "4", "5", "6", "7", "8", "9", "10", "11", "12", "13"];
    private static readonly string[] AomPresets = ["0", "1", "2", "3", "4", "5", "6", "7", "8"];

    /// <summary>全部视频编码器（按界面分组顺序）。</summary>
    public static IReadOnlyList<EncoderDefinition> All { get; } =
    [
        new EncoderDefinition
        {
            Id = "h264_nvenc", DisplayName = "H.264 / NVENC（NVIDIA 硬编，兼容性最好）",
            Codec = "h264", Family = EncoderFamily.Nvenc, PreferredAccel = HwAccelKind.Cuda,
            QualityParam = "-cq", QualityLabel = "CQ", QualityMax = 51,
            Presets = NvencPresets, DefaultPreset = "p5", Tunes = NvencTunes,
            Profiles = ["baseline", "main", "high", "high444p"],
            MaxWidth = 4096, MaxHeight = 2304,
        },
        new EncoderDefinition
        {
            Id = "hevc_nvenc", DisplayName = "H.265 / HEVC / NVENC（NVIDIA 硬编，体积更小）",
            Codec = "hevc", Family = EncoderFamily.Nvenc, PreferredAccel = HwAccelKind.Cuda,
            QualityParam = "-cq", QualityLabel = "CQ", QualityMax = 51,
            Presets = NvencPresets, DefaultPreset = "p5", Tunes = NvencTunes,
            Profiles = ["main", "main10", "rext"],
            SupportsTenBit = true, MaxWidth = 7680, MaxHeight = 4320,
        },
        new EncoderDefinition
        {
            Id = "av1_nvenc", DisplayName = "AV1 / NVENC（NVIDIA 硬编，体积最小）",
            Codec = "av1", Family = EncoderFamily.Nvenc, PreferredAccel = HwAccelKind.Cuda,
            QualityParam = "-cq", QualityLabel = "CQ", QualityMax = 51,
            Presets = NvencPresets, DefaultPreset = "p5", Tunes = NvencTunes,
            Profiles = ["main"],
            SupportsTenBit = true, MaxWidth = 7680, MaxHeight = 4320,
        },
        new EncoderDefinition
        {
            Id = "h264_qsv", DisplayName = "H.264 / QSV（Intel 核显硬编）",
            Codec = "h264", Family = EncoderFamily.Qsv, PreferredAccel = HwAccelKind.Qsv,
            QualityParam = "-global_quality", QualityLabel = "全局质量", QualityMax = 51,
            Presets = QsvPresets, DefaultPreset = "medium",
            Profiles = ["baseline", "main", "high"],
            MaxWidth = 4096, MaxHeight = 2304,
        },
        new EncoderDefinition
        {
            Id = "hevc_qsv", DisplayName = "H.265 / HEVC / QSV（Intel 核显硬编）",
            Codec = "hevc", Family = EncoderFamily.Qsv, PreferredAccel = HwAccelKind.Qsv,
            QualityParam = "-global_quality", QualityLabel = "全局质量", QualityMax = 51,
            Presets = QsvPresets, DefaultPreset = "medium",
            Profiles = ["main", "main10"],
            SupportsTenBit = true, MaxWidth = 7680, MaxHeight = 4320,
        },
        new EncoderDefinition
        {
            Id = "av1_qsv", DisplayName = "AV1 / QSV（Intel Arc 硬编）",
            Codec = "av1", Family = EncoderFamily.Qsv, PreferredAccel = HwAccelKind.Qsv,
            QualityParam = "-global_quality", QualityLabel = "全局质量", QualityMax = 51,
            Presets = QsvPresets, DefaultPreset = "medium",
            Profiles = ["main"],
            SupportsTenBit = true, MaxWidth = 7680, MaxHeight = 4320,
        },
        new EncoderDefinition
        {
            Id = "vp9_qsv", DisplayName = "VP9 / QSV（Intel 核显硬编）",
            Codec = "vp9", Family = EncoderFamily.Qsv, PreferredAccel = HwAccelKind.Qsv,
            QualityParam = "-global_quality", QualityLabel = "全局质量", QualityMax = 51,
            Presets = QsvPresets, DefaultPreset = "medium",
            MaxWidth = 4096, MaxHeight = 2304,
        },
        new EncoderDefinition
        {
            Id = "libx264", DisplayName = "H.264 / x264（CPU 软编，兼容性优先）",
            Codec = "h264", Family = EncoderFamily.X264,
            QualityParam = "-crf", QualityLabel = "CRF", QualityMax = 51,
            Presets = X26xPresets, DefaultPreset = "medium", Tunes = X26xTunes,
            Profiles = ["baseline", "main", "high", "high10", "high422", "high444"],
            MaxWidth = 4096, MaxHeight = 2304,
        },
        new EncoderDefinition
        {
            Id = "libx265", DisplayName = "H.265 / HEVC / x265（CPU 软编）",
            Codec = "hevc", Family = EncoderFamily.X265,
            QualityParam = "-crf", QualityLabel = "CRF", QualityMax = 51,
            Presets = X26xPresets, DefaultPreset = "medium",
            Tunes = ["grain", "fastdecode", "zerolatency", "animation"],
            Profiles = ["main", "main10", "mainstillpicture"],
            SupportsTenBit = true, MaxWidth = 7680, MaxHeight = 4320,
        },
        new EncoderDefinition
        {
            Id = "libsvtav1", DisplayName = "AV1 / SVT-AV1（CPU 软编，速度与压缩比均衡）",
            Codec = "av1", Family = EncoderFamily.SvtAv1,
            QualityParam = "-crf", QualityLabel = "CRF", QualityMax = 63,
            Presets = SvtAv1Presets, DefaultPreset = "6",
            Profiles = ["main", "high", "professional"],
            SupportsTenBit = true, MaxWidth = 7680, MaxHeight = 4320,
        },
        new EncoderDefinition
        {
            Id = "libaom-av1", DisplayName = "AV1 / libaom（CPU 软编，压缩比最高但很慢）",
            Codec = "av1", Family = EncoderFamily.Aom,
            QualityParam = "-crf", QualityLabel = "CRF", QualityMax = 63,
            PresetParam = "-cpu-used",
            Presets = AomPresets, DefaultPreset = "6",
            Profiles = ["main", "high", "professional"],
            SupportsTenBit = true, MaxWidth = 7680, MaxHeight = 4320,
        },
    ];

    /// <summary>
    /// 支持的输出容器。
    ///
    /// ⚠ 下面的「容器 × 编码」列表**全部来自本机 ffmpeg 实测**，不是按经验写的。
    /// 曾经手写的表把 `pcm_s24le` 在 MP4 里判成非法，于是预检把用户的无损 PCM 直通
    /// 强行改成有损 AAC 192k —— 用户既丢了画质又没法阻止，比报错还糟。
    /// 现在这张表由自检里的「容器兼容性矩阵」用例用真实 ffmpeg 逐个探测守住
    /// （见 SelfTest.VerifyContainerMatrixAsync），表与实测不一致就会失败。
    /// </summary>
    public static IReadOnlyList<ContainerDefinition> Containers { get; } =
    [
        new ContainerDefinition
        {
            Extension = "mp4", DisplayName = "MP4（兼容性最好）",
            VideoCodecs = ["h264", "hevc", "av1", "vp9", "mpeg4"],
            AudioCodecs =
            [
                "aac", "libopus", "libmp3lame", "ac3", "flac", "libvorbis", "alac",
                "pcm_s16le", "pcm_s24le", "pcm_s32le",
            ],
            // 实测：MP4/MOV 只认 mov_text（srt/ass/webvtt 都会 "not supported"）
            SubtitleCodecs = ["mov_text"],
        },
        new ContainerDefinition
        {
            Extension = "mkv", DisplayName = "MKV（什么都能装）",
            VideoCodecs = ["h264", "hevc", "av1", "vp9", "mpeg4"],
            AudioCodecs =
            [
                "aac", "libopus", "libmp3lame", "ac3", "flac", "libvorbis", "alac",
                "pcm_s16le", "pcm_s24le", "pcm_s32le",
            ],
            // 实测：matroska 不支持 mov_text，所以这里没有它；copy 表示原样内封（PGS 等图形字幕也能装）
            SubtitleCodecs = ["subrip", "ass", "webvtt", "copy"],
        },
        new ContainerDefinition
        {
            Extension = "mov", DisplayName = "MOV（苹果生态）",
            // 实测报错：「av1 only supported in MP4 and AVIF」
            VideoCodecs = ["h264", "hevc", "mpeg4"],
            // 实测：libopus 与 flac 装不进 mov；PCM 可以
            AudioCodecs =
            [
                "aac", "libmp3lame", "ac3", "libvorbis", "alac", "pcm_s16le", "pcm_s24le", "pcm_s32le",
            ],
            SubtitleCodecs = ["mov_text"],
        },
        new ContainerDefinition
        {
            Extension = "webm", DisplayName = "WebM（网页内嵌）",
            VideoCodecs = ["vp9", "av1"],
            // 实测报错：「Only VP8 or VP9 or AV1 video and Vorbis or Opus audio and WebVTT subtitles」
            AudioCodecs = ["libopus", "libvorbis"],
            SubtitleCodecs = ["webvtt"],
        },
        new ContainerDefinition
        {
            Extension = "m4a", DisplayName = "M4A（纯音频）", VideoCapable = false,
            AudioCodecs = ["aac", "ac3", "alac"],
        },
        new ContainerDefinition
        {
            Extension = "mp3", DisplayName = "MP3（纯音频）", VideoCapable = false,
            AudioCodecs = ["libmp3lame"],
            // 实测只接受单条音轨
            MaxAudioStreams = 1,
        },
        new ContainerDefinition
        {
            Extension = "opus", DisplayName = "OPUS（纯音频）", VideoCapable = false,
            AudioCodecs = ["libopus", "flac", "libvorbis"],
        },
        new ContainerDefinition
        {
            Extension = "flac", DisplayName = "FLAC（无损音频）", VideoCapable = false,
            AudioCodecs = ["flac"],
            // 实测只接受单条音轨
            MaxAudioStreams = 1,
        },
        new ContainerDefinition
        {
            Extension = "wav", DisplayName = "WAV（未压缩音频）", VideoCapable = false,
            // 实测只接受单条音轨
            MaxAudioStreams = 1,
            AudioCodecs =
            [
                "aac", "libmp3lame", "ac3", "flac", "libvorbis", "pcm_s16le", "pcm_s24le", "pcm_s32le",
            ],
        },
    ];

    /// <summary>AC3 的合法码率档位（实测非法值会被静默取整）。</summary>
    private static readonly int[] Ac3Bitrates =
        [32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384, 448, 512, 576, 640];

    /// <summary>可选音频编码器。</summary>
    public static IReadOnlyList<AudioCodecDefinition> AudioCodecs { get; } =
    [
        new AudioCodecDefinition { Id = "aac", DisplayName = "AAC（通用，推荐）", DefaultBitrateKbps = 192 },
        new AudioCodecDefinition { Id = "libopus", DisplayName = "OPUS（体积小、延迟低）", DefaultBitrateKbps = 128 },
        new AudioCodecDefinition { Id = "libmp3lame", DisplayName = "MP3", DefaultBitrateKbps = 192 },
        new AudioCodecDefinition
        {
            Id = "ac3", DisplayName = "AC3（家庭影院，固定码率档位）",
            BitrateOptions = Ac3Bitrates, DefaultBitrateKbps = 192,
        },
        new AudioCodecDefinition { Id = "flac", DisplayName = "FLAC（无损压缩）", IsLossless = true },
        new AudioCodecDefinition { Id = "libvorbis", DisplayName = "Vorbis", DefaultBitrateKbps = 192 },
        new AudioCodecDefinition { Id = "alac", DisplayName = "ALAC（苹果无损压缩）", IsLossless = true },
        new AudioCodecDefinition { Id = "pcm_s16le", DisplayName = "PCM 16bit（无损未压缩）", IsLossless = true },
        new AudioCodecDefinition { Id = "pcm_s24le", DisplayName = "PCM 24bit（无损未压缩）", IsLossless = true },
        new AudioCodecDefinition { Id = "pcm_s32le", DisplayName = "PCM 32bit（无损未压缩）", IsLossless = true },
    ];

    /// <summary>
    /// 未压缩 PCM 的码率：采样率 × 位深 × 声道数。
    /// 实测本机 pcm_s24le 44.1kHz 立体声 = 2116 kbps，与该公式精确吻合。
    /// </summary>
    public static int ComputePcmBitrate(int sampleRate, int bitDepth, int channels)
    {
        if (sampleRate <= 0 || bitDepth <= 0 || channels <= 0)
        {
            return 0;
        }

        // 截断而不是四舍五入：ffprobe 报 2116 kbps 而 44100×24×2 = 2116.8 kbps，
        // 界面上的数字要和别的工具看到的一致。
        return (int)(sampleRate * (long)bitDepth * channels / 1000);
    }

    /// <summary>取离目标最近的合法档位。</summary>
    public static int NearestBitrate(int[] options, int target)
    {
        if (options.Length == 0)
        {
            return target;
        }

        var best = options[0];
        foreach (var option in options)
        {
            if (Math.Abs(option - target) < Math.Abs(best - target))
            {
                best = option;
            }
        }

        return best;
    }

    /// <summary>简单模式滑块 → 原生质量值的锚点（滑块值 → 占质量区间上限的比例）。</summary>
    private static readonly (int Slider, double Fraction)[] QualityAnchors =
    [
        (0, 1.000),    // 51/51：最差画质、最小体积
        (25, 0.745),   // 38/51
        (50, 0.588),   // 30/51
        (60, 0.529),   // 27/51
        (75, 0.451),   // 23/51 ← 默认：x264 的经典 CRF 23 / NVENC 的 CQ 23
        (90, 0.353),   // 18/51
        (100, 0.274),  // 14/51：接近视觉无损（再高会导致文件体积暴涨）
    ];

    public static EncoderDefinition Get(string? encoderId)
    {
        var match = All.FirstOrDefault(e => string.Equals(e.Id, encoderId, StringComparison.OrdinalIgnoreCase));
        return match ?? All[0];
    }

    public static EncoderDefinition? Find(string? encoderId) =>
        All.FirstOrDefault(e => string.Equals(e.Id, encoderId, StringComparison.OrdinalIgnoreCase));

    public static ContainerDefinition GetContainer(string? extension)
    {
        var match = Containers.FirstOrDefault(c => string.Equals(c.Extension, extension, StringComparison.OrdinalIgnoreCase));
        return match ?? Containers[0];
    }

    public static AudioCodecDefinition GetAudioCodec(string? id)
    {
        var match = AudioCodecs.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
        return match ?? AudioCodecs[0];
    }

    /// <summary>简单模式：滑块值映射为该编码器的原生质量值。</summary>
    public static int MapSliderToQuality(EncoderDefinition encoder, int sliderValue)
    {
        var slider = Math.Clamp(sliderValue, 0, 100);
        var fraction = InterpolateFraction(slider);
        var value = (int)Math.Round(fraction * encoder.QualityMax);
        return Math.Clamp(value, encoder.QualityMin, encoder.QualityMax);
    }

    /// <summary>简单模式：滑块值映射为推荐的 preset。</summary>
    public static string MapSliderToPreset(EncoderDefinition encoder, int sliderValue)
    {
        if (encoder.Presets.Length == 0)
        {
            return string.Empty;
        }

        var slider = Math.Clamp(sliderValue, 0, 100);
        return encoder.Family switch
        {
            // p1 最快 / p7 最慢最好
            EncoderFamily.Nvenc => slider switch
            {
                >= 85 => "p6",
                >= 70 => "p5",
                >= 50 => "p4",
                >= 30 => "p3",
                _ => "p2",
            },
            // 数值越小越慢越好
            EncoderFamily.SvtAv1 => slider switch
            {
                >= 85 => "2",
                >= 70 => "4",
                >= 50 => "6",
                >= 30 => "8",
                _ => "10",
            },
            EncoderFamily.Aom => slider switch
            {
                >= 85 => "2",
                >= 70 => "3",
                >= 50 => "5",
                _ => "6",
            },
            // 名字越靠后越慢越好
            _ => slider switch
            {
                >= 85 => "slow",
                >= 60 => "medium",
                >= 35 => "fast",
                _ => "veryfast",
            },
        };
    }

    /// <summary>滑块值反推：给定原生质量值，估算最接近的滑块位置（高级模式切回简单模式时用）。</summary>
    public static int MapQualityToSlider(EncoderDefinition encoder, int qualityValue)
    {
        var fraction = encoder.QualityMax <= 0 ? 0.5 : (double)qualityValue / encoder.QualityMax;
        var best = 75;
        var bestDistance = double.MaxValue;
        for (var slider = 0; slider <= 100; slider++)
        {
            var distance = Math.Abs(InterpolateFraction(slider) - fraction);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = slider;
            }
        }

        return best;
    }

    /// <summary>质量值在界面上的定性描述。</summary>
    public static string DescribeQuality(int sliderValue) => sliderValue switch
    {
        >= 95 => "接近无损（体积很大）",
        >= 85 => "高画质",
        >= 70 => "画质与体积均衡",
        >= 50 => "体积优先",
        >= 30 => "高压缩（画质明显损失）",
        _ => "极限压缩（仅供预览）",
    };

    /// <summary>容器是否支持该视频编码格式。</summary>
    public static bool IsVideoCodecCompatible(string codec, ContainerDefinition container) =>
        !container.VideoCapable || container.VideoCodecs.Contains(codec, StringComparer.OrdinalIgnoreCase);

    /// <summary>容器是否支持该音频编码器。</summary>
    public static bool IsAudioCodecCompatible(string audioCodecId, ContainerDefinition container) =>
        container.AudioCodecs.Contains(audioCodecId, StringComparer.OrdinalIgnoreCase);

    /// <summary>容器是否支持内封该字幕编码。</summary>
    public static bool IsSubtitleCodecCompatible(string subtitleCodec, ContainerDefinition container) =>
        container.SubtitleCodecs.Contains(subtitleCodec, StringComparer.OrdinalIgnoreCase) ||
        container.SubtitleCodecs.Contains("copy", StringComparer.OrdinalIgnoreCase);

    /// <summary>该编码器是否支持指定的像素格式（判断 10bit 兼容性）。</summary>
    public static bool SupportsPixelFormat(EncoderDefinition encoder, string pixelFormat)
    {
        if (string.IsNullOrWhiteSpace(pixelFormat))
        {
            return true;
        }

        var isHighBitDepth =
            pixelFormat.Contains("10", StringComparison.Ordinal) ||
            pixelFormat.Contains("12", StringComparison.Ordinal) ||
            pixelFormat.Contains("16", StringComparison.Ordinal);

        return !isHighBitDepth || encoder.SupportsTenBit;
    }

    private static double InterpolateFraction(int slider)
    {
        if (slider <= QualityAnchors[0].Slider)
        {
            return QualityAnchors[0].Fraction;
        }

        for (var i = 1; i < QualityAnchors.Length; i++)
        {
            var (rightSlider, rightFraction) = QualityAnchors[i];
            if (slider > rightSlider)
            {
                continue;
            }

            var (leftSlider, leftFraction) = QualityAnchors[i - 1];
            var span = rightSlider - leftSlider;
            if (span <= 0)
            {
                return rightFraction;
            }

            var ratio = (double)(slider - leftSlider) / span;
            return leftFraction + (rightFraction - leftFraction) * ratio;
        }

        return QualityAnchors[^1].Fraction;
    }
}
