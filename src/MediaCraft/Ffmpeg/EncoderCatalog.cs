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

/// <summary>多遍编码的实现方式。</summary>
public enum TwoPassKind
{
    /// <summary>没有可用的多遍分析。</summary>
    None = 0,

    /// <summary>ffmpeg 的两遍编码（分两次调用 + 统计文件）。</summary>
    ExternalPass,

    /// <summary>编码器内部多遍（单次调用）。</summary>
    EncoderInternal,
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

    /// <summary>
    /// 多遍编码的实现方式。三种情况都由实测确定：
    ///
    /// - <see cref="TwoPassKind.ExternalPass"/>：ffmpeg 的两遍编码（-pass 1/2 + -passlogfile 统计文件），
    ///   分两次调用。实测仅软件编码器支持；硬件编码器命令返回成功但统计文件为 0 字节（静默忽略）。
    /// - <see cref="TwoPassKind.EncoderInternal"/>：编码器在单次调用内做多遍分析
    ///   （nvenc 的 -multipass、qsv 的 -extbrc），不产生统计文件。
    /// - <see cref="TwoPassKind.None"/>：没有可用的多遍分析。
    /// </summary>
    public TwoPassKind TwoPassKind { get; init; } = TwoPassKind.None;

    /// <summary>编码器内部多遍所需的参数（仅 <see cref="TwoPassKind.EncoderInternal"/> 使用）。</summary>
    public string[] TwoPassArguments { get; init; } = [];

    /// <summary>
    /// 该编码器是否只接受 4:2:0 输入（4:2:2 / 4:4:4 源必须先转成 4:2:0）。
    ///
    /// 实测：av1_nvenc 直接编码 10bit 4:2:2 或 8bit 4:4:4 源会失败
    ///（不带硬解时报 No capable devices found；带 -hwaccel cuda 时报
    /// Provided device doesn't support required NVENC features），加 -pix_fmt yuv420p 后正常；
    /// 10bit 4:2:0 源不需要转换。其余编码器（hevc/h264 nvenc、qsv 系列、软编）实测都能直接处理 4:2:2。
    /// </summary>
    public bool NeedsYuv420Input { get; init; }

    /// <summary>
    /// 该多遍机制是否只在目标码率模式下有效。
    /// 实测：ffmpeg 的两遍在质量优先模式下第二遍会直接失败；qsv 的 -extbrc 在 ICQ 模式下产出字节完全相同（无效果）。
    /// nvenc 的 -multipass 两种模式都有实测差别，故为 false。
    /// </summary>
    public bool TwoPassRequiresBitrate { get; init; } = true;

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

    /// <summary>PCM 的位深；非 PCM 返回 0。浮点格式按其容器位宽算（f32=32、f64=64）。</summary>
    public static int PcmBitDepth(string codecId) => codecId switch
    {
        "pcm_s16le" or "pcm_s16be" => 16,
        "pcm_s24le" or "pcm_s24be" => 24,
        "pcm_s32le" or "pcm_s32be" or "pcm_f32le" or "pcm_f32be" => 32,
        "pcm_f64le" or "pcm_f64be" => 64,
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
            TwoPassKind = TwoPassKind.EncoderInternal,
            TwoPassArguments = ["-multipass", "2"],
            TwoPassRequiresBitrate = false,
            QualityParam = "-cq", QualityLabel = "CQ", QualityMax = 51,
            Presets = NvencPresets, DefaultPreset = "p5", Tunes = NvencTunes,
            Profiles = ["baseline", "main", "high", "high444p"],
            MaxWidth = 4096, MaxHeight = 2304,
        },
        new EncoderDefinition
        {
            Id = "hevc_nvenc", DisplayName = "H.265 / HEVC / NVENC（NVIDIA 硬编，体积更小）",
            Codec = "hevc", Family = EncoderFamily.Nvenc, PreferredAccel = HwAccelKind.Cuda,
            TwoPassKind = TwoPassKind.EncoderInternal,
            TwoPassArguments = ["-multipass", "2"],
            TwoPassRequiresBitrate = false,
            QualityParam = "-cq", QualityLabel = "CQ", QualityMax = 51,
            Presets = NvencPresets, DefaultPreset = "p5", Tunes = NvencTunes,
            Profiles = ["main", "main10", "rext"],
            SupportsTenBit = true, MaxWidth = 7680, MaxHeight = 4320,
        },
        new EncoderDefinition
        {
            Id = "av1_nvenc", DisplayName = "AV1 / NVENC（NVIDIA 硬编，体积最小）",
            Codec = "av1", Family = EncoderFamily.Nvenc, PreferredAccel = HwAccelKind.Cuda,
            TwoPassKind = TwoPassKind.EncoderInternal,
            TwoPassArguments = ["-multipass", "2"],
            NeedsYuv420Input = true,
            TwoPassRequiresBitrate = false,
            QualityParam = "-cq", QualityLabel = "CQ", QualityMax = 51,
            Presets = NvencPresets, DefaultPreset = "p5", Tunes = NvencTunes,
            // 实测：av1_nvenc 不接受 "main"（Unable to parse "profile" option value "main"），
            // 它认的是 main10 或数字 0/1 —— 这里只列真能用的值
            Profiles = ["main10"],
            SupportsTenBit = true, MaxWidth = 7680, MaxHeight = 4320,
        },
        new EncoderDefinition
        {
            Id = "h264_qsv", DisplayName = "H.264 / QSV（Intel 核显硬编）",
            Codec = "h264", Family = EncoderFamily.Qsv, PreferredAccel = HwAccelKind.Qsv,
            NeedsYuv420Input = true,
            TwoPassKind = TwoPassKind.EncoderInternal,
            TwoPassArguments = ["-extbrc", "1"],
            QualityParam = "-global_quality", QualityLabel = "全局质量", QualityMax = 51,
            Presets = QsvPresets, DefaultPreset = "medium",
            Profiles = ["baseline", "main", "high"],
            MaxWidth = 4096, MaxHeight = 2304,
        },
        new EncoderDefinition
        {
            Id = "hevc_qsv", DisplayName = "H.265 / HEVC / QSV（Intel 核显硬编）",
            Codec = "hevc", Family = EncoderFamily.Qsv, PreferredAccel = HwAccelKind.Qsv,
            NeedsYuv420Input = true,
            TwoPassKind = TwoPassKind.EncoderInternal,
            TwoPassArguments = ["-extbrc", "1"],
            QualityParam = "-global_quality", QualityLabel = "全局质量", QualityMax = 51,
            Presets = QsvPresets, DefaultPreset = "medium",
            // 实测：hevc_qsv 不接受 "main10"（10bit 输出本身没问题，它的 -profile 只认数字）
            Profiles = ["main"],
            SupportsTenBit = true, MaxWidth = 7680, MaxHeight = 4320,
        },
        new EncoderDefinition
        {
            Id = "av1_qsv", DisplayName = "AV1 / QSV（Intel Arc 硬编）",
            Codec = "av1", Family = EncoderFamily.Qsv, PreferredAccel = HwAccelKind.Qsv,
            NeedsYuv420Input = true,
            // 实测 -extbrc 被接受但产出字节与不传时完全相同（无效果），故不提供
            TwoPassKind = TwoPassKind.None,
            QualityParam = "-global_quality", QualityLabel = "全局质量", QualityMax = 51,
            Presets = QsvPresets, DefaultPreset = "medium",
            Profiles = ["main"],
            SupportsTenBit = true, MaxWidth = 7680, MaxHeight = 4320,
        },
        new EncoderDefinition
        {
            Id = "vp9_qsv", DisplayName = "VP9 / QSV（Intel 核显硬编）",
            Codec = "vp9", Family = EncoderFamily.Qsv, PreferredAccel = HwAccelKind.Qsv,
            NeedsYuv420Input = true,
            // 实测该编码器没有 look_ahead / extbrc 选项（传入会被标记为未使用）
            TwoPassKind = TwoPassKind.None,
            QualityParam = "-global_quality", QualityLabel = "全局质量", QualityMax = 51,
            Presets = QsvPresets, DefaultPreset = "medium",
            MaxWidth = 4096, MaxHeight = 2304,
        },
        new EncoderDefinition
        {
            Id = "libx264", DisplayName = "H.264 / x264（CPU 软编，兼容性优先）",
            Codec = "h264", Family = EncoderFamily.X264,
            TwoPassKind = TwoPassKind.ExternalPass,
            QualityParam = "-crf", QualityLabel = "CRF", QualityMax = 51,
            Presets = X26xPresets, DefaultPreset = "medium", Tunes = X26xTunes,
            Profiles = ["baseline", "main", "high", "high10", "high422", "high444"],
            MaxWidth = 4096, MaxHeight = 2304,
        },
        new EncoderDefinition
        {
            Id = "libx265", DisplayName = "H.265 / HEVC / x265（CPU 软编）",
            Codec = "hevc", Family = EncoderFamily.X265,
            TwoPassKind = TwoPassKind.ExternalPass,
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
            // SVT-AV1 只做 4:2:0：4:2:2 源若不显式转换，ffmpeg 会**静默**降成 4:2:0（用户看不到），
            // 显式指定 professional profile 则直接报 bad parameter。标上它，由我们插转换并提示。
            NeedsYuv420Input = true,
            TwoPassKind = TwoPassKind.ExternalPass,
            QualityParam = "-crf", QualityLabel = "CRF", QualityMax = 63,
            Presets = SvtAv1Presets, DefaultPreset = "6",
            // 实测：只认 main（high / professional 会被 libsvtav1 拒绝）
            Profiles = ["main"],
            SupportsTenBit = true, MaxWidth = 7680, MaxHeight = 4320,
        },
        new EncoderDefinition
        {
            Id = "libaom-av1", DisplayName = "AV1 / libaom（CPU 软编，压缩比最高但很慢）",
            Codec = "av1", Family = EncoderFamily.Aom,
            TwoPassKind = TwoPassKind.ExternalPass,
            QualityParam = "-crf", QualityLabel = "CRF", QualityMax = 63,
            PresetParam = "-cpu-used",
            Presets = AomPresets, DefaultPreset = "6",
            // 实测：只认 main（high / professional 会被 libaom 拒绝）
            Profiles = ["main"],
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
            // vorbis 不在列表里：mp4 里的 vorbis 是实验性特性，本机 ffmpeg 8.1.2 能写、
            // 但 CI 上的 full build 报 "Error submitting a packet to the muxer"，
            // 行为随版本变化且播放器支持极差 —— 不兼容时预检会转 AAC 或提示换 MKV
            AudioCodecs =
            [
                "aac", "opus", "mp3", "ac3", "flac", "alac",
                "pcm_s16le", "pcm_s24le", "pcm_s32le",
                "pcm_s16be", "pcm_s24be", "pcm_f32le", "pcm_f64le",
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
                "aac", "opus", "mp3", "ac3", "flac", "vorbis", "alac",
                "pcm_s16le", "pcm_s24le", "pcm_s32le",
                "pcm_s16be", "pcm_s24be", "pcm_f32le", "pcm_f64le",
            ],
            // 实测：matroska 不支持 mov_text，所以这里没有它；copy 表示原样内封（PGS 等图形字幕也能装）
            SubtitleCodecs = ["subrip", "ass", "webvtt", "copy"],
        },
        new ContainerDefinition
        {
            Extension = "mov", DisplayName = "MOV（苹果生态）",
            // 实测报错：「av1 only supported in MP4 and AVIF」
            VideoCodecs = ["h264", "hevc", "mpeg4"],
            // 实测：libopus 与 flac 装不进 mov；PCM 可以；vorbis 与 mp4 同理（版本相关，不进白名单）
            AudioCodecs =
            [
                "aac", "mp3", "ac3", "alac",
                "pcm_s16le", "pcm_s24le", "pcm_s32le", "pcm_s16be", "pcm_s24be", "pcm_f32le", "pcm_f64le",
            ],
            SubtitleCodecs = ["mov_text"],
        },
        new ContainerDefinition
        {
            Extension = "webm", DisplayName = "WebM（网页内嵌）",
            VideoCodecs = ["vp9", "av1"],
            // 实测报错：「Only VP8 or VP9 or AV1 video and Vorbis or Opus audio and WebVTT subtitles」
            AudioCodecs = ["opus", "vorbis"],
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
            AudioCodecs = ["mp3"],
            // 实测只接受单条音轨
            MaxAudioStreams = 1,
        },
        new ContainerDefinition
        {
            Extension = "opus", DisplayName = "OPUS（纯音频）", VideoCapable = false,
            AudioCodecs = ["opus", "flac", "vorbis"],
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
                "aac", "mp3", "ac3", "flac", "vorbis",
                // WAVE 规范只收小端：pcm_s16be / pcm_s24be 会被 muxer 拒绝
                //（Codec pcm_s16be not supported in WAVE format），浮点小端则可以
                "pcm_s16le", "pcm_s24le", "pcm_s32le", "pcm_f32le", "pcm_f64le",
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
        new AudioCodecDefinition { Id = "pcm_s16be", DisplayName = "PCM 16bit 大端（无损未压缩）", IsLossless = true },
        new AudioCodecDefinition { Id = "pcm_s24be", DisplayName = "PCM 24bit 大端（无损未压缩）", IsLossless = true },
        new AudioCodecDefinition { Id = "pcm_f32le", DisplayName = "PCM 32bit 浮点（无损未压缩）", IsLossless = true },
        new AudioCodecDefinition { Id = "pcm_f64le", DisplayName = "PCM 64bit 浮点（无损未压缩）", IsLossless = true },
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

    /// <summary>
    /// 音频编码的两套命名归一化。
    ///
    /// 容器白名单按 **ffprobe 的 codec_name** 书写（因为要跟 track.SourceCodec 比对），
    /// 而界面下拉与 -c:a 用的是 **ffmpeg 编码器 id**。三个编码器两边名字不同：
    /// opus/libopus、mp3/libmp3lame、vorbis/libvorbis。
    /// 混用会导致「MKV 装不下 opus」这类误判 —— 预检会把本可直通的音轨强行重编码（丢画质）。
    /// </summary>
    public static string CanonicalAudioCodec(string nameOrId) => nameOrId switch
    {
        "libopus" => "opus",
        "libmp3lame" => "mp3",
        "libvorbis" => "vorbis",
        _ => nameOrId,
    };

    /// <summary>规范名 → 可直接传给 -c:a 的编码器 id。</summary>
    public static string AudioEncoderIdFor(string canonical) => canonical switch
    {
        "opus" => "libopus",
        "mp3" => "libmp3lame",
        "vorbis" => "libvorbis",
        _ => canonical,
    };

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

    /// <summary>
    /// 容器是否支持该音频编码。传编码器 id（libopus）或 ffprobe 名（opus）都可以 —— 内部先归一化。
    /// </summary>
    public static bool IsAudioCodecCompatible(string audioCodecNameOrId, ContainerDefinition container) =>
        container.AudioCodecs.Contains(CanonicalAudioCodec(audioCodecNameOrId), StringComparer.OrdinalIgnoreCase);

    /// <summary>容器是否支持内封该字幕编码。</summary>
    public static bool IsSubtitleCodecCompatible(string subtitleCodec, ContainerDefinition container) =>
        container.SubtitleCodecs.Contains(subtitleCodec, StringComparer.OrdinalIgnoreCase) ||
        container.SubtitleCodecs.Contains("copy", StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 该容器能否原样内封这个字幕编码。
    /// 预检规则与界面提示共用这一个判定，避免两处逻辑不一致。
    /// </summary>
    public static bool CanKeepSubtitle(string subtitleCodec, ContainerDefinition container)
    {
        if (container.SubtitleCodecs.Length == 0)
        {
            return false;
        }

        if (container.SubtitleCodecs.Contains("copy", StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        var isText = subtitleCodec is
            "subrip" or "srt" or "ass" or "ssa" or "mov_text" or "webvtt" or "text" or "sami" or "microdvd";

        return isText && container.SubtitleCodecs.Contains("mov_text", StringComparer.OrdinalIgnoreCase);
    }

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

    /// <summary>把像素格式名换成界面用词（提示文案里不出现 nv12 / p010le 这类术语）。</summary>
    public static string DescribePixelFormat(string pixelFormat) => pixelFormat.Trim().ToLowerInvariant() switch
    {
        "yuv420p" or "yuvj420p" or "nv12" => "4:2:0 8bit",
        "yuv420p10le" or "p010le" => "4:2:0 10bit",
        var other => other,
    };

    /// <summary>色度采样是否为 4:2:0（格式名含 420，或硬件侧的 4:2:0 格式）。</summary>
    public static bool IsYuv420(string pixelFormat)    {
        var format = pixelFormat.Trim().ToLowerInvariant();
        if (format.Length == 0)
        {
            return false;
        }

        return format.Contains("420", StringComparison.Ordinal)
               || format is "nv12" or "nv21" or "p010le" or "p010be";
    }

    /// <summary>从像素格式名读位深：无位深后缀按 8bit，p10/p010 → 10，p12/p012 → 12，p16/p016 → 16。</summary>
    public static int PixelFormatBitDepth(string pixelFormat)
    {
        var format = pixelFormat.Trim().ToLowerInvariant();
        if (format.EndsWith("p16le", StringComparison.Ordinal) || format.EndsWith("p016le", StringComparison.Ordinal))
        {
            return 16;
        }

        if (format.EndsWith("p12le", StringComparison.Ordinal) || format.EndsWith("p012le", StringComparison.Ordinal))
        {
            return 12;
        }

        if (format.EndsWith("p10le", StringComparison.Ordinal) || format.EndsWith("p010le", StringComparison.Ordinal))
        {
            return 10;
        }

        return 8;
    }

    /// <summary>
    /// 源是否需要为「只收 4:2:0 的编码器」做一次格式转换。
    /// 4:2:0（含 nv12 / p010）且位深不超过 10bit 时不需要；探测不到像素格式时不擅自转换。
    /// </summary>
    public static bool NeedsYuv420Conversion(
        EncoderDefinition encoder,
        MediaStreamInfo? videoStream,
        string userPixelFormat)
    {
        if (!encoder.NeedsYuv420Input || videoStream is null)
        {
            return false;
        }

        // 用户在高级参数里手填了像素格式：以用户的为准，不插手
        if (!string.IsNullOrWhiteSpace(userPixelFormat))
        {
            return false;
        }

        var source = videoStream.PixelFormat;
        if (string.IsNullOrWhiteSpace(source))
        {
            // 探测不到像素格式：不擅自插转换，交给 ffmpeg 自己判断
            return false;
        }

        return !IsYuv420(source) || PixelFormatBitDepth(source) > 10;
    }

    /// <summary>
    /// 为「只收 4:2:0 的编码器」构造格式转换滤镜；不需要转换时返回 null。
    ///
    /// 目标格式按编码器与源位深选：
    /// - NVENC 用软件格式名：`format=yuv420p` / `format=yuv420p10le`
    ///   （NVENC 的 AV1 输出只到 10bit，更高位深降到 10bit）
    /// - QSV 家族用半平面格式：硬件路径 `vpp_qsv=format=nv12|p010le`（在显存内转换），
    ///   软件路径 `format=nv12|p010le`。两条路的目标格式一致，产出也一致（实测见 docs/spec.md）
    ///
    /// <paramref name="useHardwareFilter"/> 只在帧留在 QSV 表面时可为 true：
    /// 链上已经有软件滤镜（缩放 / 字幕）时必须走软件路径，否则滤镜图协商不上。
    /// </summary>
    public static string? BuildYuv420ConversionFilter(
        EncoderDefinition encoder,
        MediaStreamInfo? videoStream,
        string userPixelFormat,
        bool useHardwareFilter = false)
    {
        if (!NeedsYuv420Conversion(encoder, videoStream, userPixelFormat))
        {
            return null;
        }

        var bitDepth = PixelFormatBitDepth(videoStream!.PixelFormat);

        if (encoder.Family == EncoderFamily.Qsv)
        {
            var format = bitDepth > 8 && encoder.SupportsTenBit ? "p010le" : "nv12";
            return useHardwareFilter ? $"vpp_qsv=format={format}" : $"format={format}";
        }

        return bitDepth > 8 ? "format=yuv420p10le" : "format=yuv420p";
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
