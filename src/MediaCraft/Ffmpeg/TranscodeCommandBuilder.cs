using System.Text;
using MediaCraft.Media;

namespace MediaCraft.Ffmpeg;

/// <summary>步骤类型。</summary>
public enum TranscodeStepKind
{
    /// <summary>准备步骤（例如把内封字幕抽到临时文件再烧入），不对用户产出文件。</summary>
    Prepare = 0,

    /// <summary>用户要求的字幕提取。</summary>
    ExtractSubtitle,

    /// <summary>主转码 / 封装。</summary>
    Transcode,

    /// <summary>字幕格式转换（输入本身就是字幕文件）。</summary>
    SubtitleConvert,
}

/// <summary>一个可独立执行的 ffmpeg 步骤。</summary>
public sealed class TranscodeStep
{
    public required TranscodeStepKind Kind { get; init; }

    /// <summary>界面显示名。</summary>
    public required string Label { get; init; }

    /// <summary>传给 ffmpeg 的参数（不含可执行文件本身）。</summary>
    public required IReadOnlyList<string> Arguments { get; init; }

    /// <summary>该步骤产出的文件（Prepare 步骤为临时文件）。</summary>
    public required string OutputPath { get; init; }

    /// <summary>进度分母（源时长）；null = 该步骤不显示进度。</summary>
    public TimeSpan? ExpectedDuration { get; init; }

    /// <summary>是否计入总进度。</summary>
    public bool TrackProgress => ExpectedDuration is { TotalSeconds: > 0 };

    /// <summary>是否为临时文件（任务结束后清理）。</summary>
    public bool IsTemporary { get; init; }

    /// <summary>该步骤是否会写文件（false = 仅用于探测）。</summary>
    public bool ProducesFile { get; init; } = true;

    /// <summary>预览用的命令行字符串（仅显示，不用于执行）。</summary>
    public string ToCommandLine(string ffmpegExe)
    {
        var builder = new StringBuilder(Quote(ffmpegExe));
        foreach (var argument in Arguments)
        {
            builder.Append(' ').Append(Quote(argument));
        }

        return builder.ToString();
    }

    private static string Quote(string value) =>
        value.Length > 0 && !value.Any(char.IsWhiteSpace) && !value.Contains('"')
            ? value
            : '"' + value.Replace("\"", "\\\"") + '"';
}

/// <summary>一个任务展开后的完整执行计划。</summary>
public sealed class TranscodePlan
{
    public List<TranscodeStep> Steps { get; } = [];

    public List<string> Notes { get; } = [];

    /// <summary>用户可见的产出文件（主转码 + 字幕提取）。</summary>
    public IEnumerable<TranscodeStep> VisibleSteps => Steps.Where(s => s.Kind != TranscodeStepKind.Prepare);

    /// <summary>主转码步骤。</summary>
    public TranscodeStep? MainStep =>
        Steps.LastOrDefault(s => s.Kind is TranscodeStepKind.Transcode or TranscodeStepKind.SubtitleConvert);

    /// <summary>执行时会写出的所有文件（用于失败清理）。</summary>
    public IEnumerable<string> AllOutputPaths => Steps.Where(s => s.ProducesFile).Select(s => s.OutputPath);
}

/// <summary>
/// 把「参数 + 媒体信息」翻译成 ffmpeg 参数数组。
/// 全部使用参数数组（不是命令行字符串），因此不存在手工拼接引号导致的注入或转义问题。
/// </summary>
public static class TranscodeCommandBuilder
{
    /// <summary>构造执行计划。</summary>
    /// <param name="info">源文件信息。</param>
    /// <param name="parameters">任务参数（应为预检修正后的版本）。</param>
    /// <param name="encoder">目标视频编码器。</param>
    /// <param name="effectiveAccel">实际生效的硬解方式。</param>
    /// <param name="outputPath">主输出文件路径。</param>
    /// <param name="tempDirectory">临时文件目录（用于 Prepare 步骤）。</param>
    public static TranscodePlan Build(
        MediaInfo info,
        TranscodeParams parameters,
        EncoderDefinition encoder,
        HwAccelKind effectiveAccel,
        string outputPath,
        string tempDirectory)
    {
        var plan = new TranscodePlan();

        // ── 1. 输入本身就是字幕文件：只做格式转换 ──
        if (IsSubtitleOnly(info))
        {
            plan.Steps.Add(BuildSubtitleConvertStep(info, parameters, outputPath));
            plan.Notes.Add("输入是字幕文件，执行字幕格式转换");
            return plan;
        }

        // ── 2. 解析要烧入的字幕文件 ──
        var burnSource = ResolveBurnSubtitle(info, parameters, tempDirectory, plan);

        // ── 3. 转码步骤 ──
        plan.Steps.Add(BuildTranscodeStep(
            info,
            parameters,
            encoder,
            effectiveAccel,
            outputPath,
            burnSource.Path));

        if (burnSource.Note is not null)
        {
            plan.Notes.Add(burnSource.Note);
        }

        // ── 4. 用户要求的字幕提取（作为附加产出）──
        foreach (var track in parameters.SubtitleTracks.Where(t =>
                     t.IsSelected && t.Action == SubtitleActionKind.Extract && !t.IsBitmap))
        {
            var subtitlePath = OutputPathBuilder.BuildSidecarPath(outputPath, track.StreamIndex, track.ExtractFormat);
            plan.Steps.Add(new TranscodeStep
            {
                Kind = TranscodeStepKind.ExtractSubtitle,
                Label = $"提取字幕 #{track.StreamIndex}",
                OutputPath = subtitlePath,
                ExpectedDuration = null,
                Arguments =
                [
                    "-hide_banner", "-nostdin", "-loglevel", "error",
                    "-y",
                    "-i", info.Path,
                    "-map", "0:" + track.StreamIndex,
                    "-c:s", SubtitleCodecName(track.ExtractFormat),
                    subtitlePath,
                ],
            });
        }

        return plan;
    }

    /// <summary>输入是否只包含字幕流。</summary>
    public static bool IsSubtitleOnly(MediaInfo info) =>
        info.Streams.Count > 0 && info.Streams.All(s => s.IsSubtitle);

    /// <summary>字幕格式 → ffmpeg 编码器名。</summary>
    public static string SubtitleCodecName(SubtitleFormat format) => format switch
    {
        SubtitleFormat.Ass => "ass",
        SubtitleFormat.Vtt => "webvtt",
        _ => "srt",
    };

    /// <summary>字幕格式 → 文件扩展名。</summary>
    public static string SubtitleExtension(SubtitleFormat format) => format switch
    {
        SubtitleFormat.Ass => ".ass",
        SubtitleFormat.Vtt => ".vtt",
        _ => ".srt",
    };

    /// <summary>字幕格式 → 复用器名（显式指定，避免只靠扩展名猜）。</summary>
    public static string SubtitleMuxerName(SubtitleFormat format) => format switch
    {
        SubtitleFormat.Ass => "ass",
        SubtitleFormat.Vtt => "webvtt",
        _ => "srt",
    };

    /// <summary>
    /// 判断是否需要把内封字幕抽成临时文件再烧。
    /// 说明：subtitles 滤镜读的是「文件」，对多轨容器用 si 参数指定轨道的行为在不同版本上不一致，
    /// 因此这里统一先抽出临时文件（这条路径已实测可用）。
    /// </summary>
    private static (string? Path, string? Note) ResolveBurnSubtitle(
        MediaInfo info,
        TranscodeParams parameters,
        string tempDirectory,
        TranscodePlan plan)
    {
        // 外挂字幕优先
        if (!string.IsNullOrWhiteSpace(parameters.ExternalSubtitlePath))
        {
            return (parameters.ExternalSubtitlePath, "使用外挂字幕文件烧入");
        }

        var burnTrack = parameters.SubtitleTracks
            .FirstOrDefault(t => t.IsSelected && t.Action == SubtitleActionKind.Burn);

        if (burnTrack is null || burnTrack.IsBitmap)
        {
            return (null, null);
        }

        var format = burnTrack.SourceCodec.Contains("ass", StringComparison.OrdinalIgnoreCase)
            ? SubtitleFormat.Ass
            : SubtitleFormat.Srt;

        var temporaryPath = System.IO.Path.Combine(
            tempDirectory,
            $"burn-sub-{burnTrack.StreamIndex}{SubtitleExtension(format)}");

        plan.Steps.Add(new TranscodeStep
        {
            Kind = TranscodeStepKind.Prepare,
            Label = $"准备字幕 #{burnTrack.StreamIndex}",
            OutputPath = temporaryPath,
            ExpectedDuration = null,
            IsTemporary = true,
            Arguments =
            [
                "-hide_banner", "-nostdin", "-loglevel", "error",
                "-y",
                "-i", info.Path,
                "-map", "0:" + burnTrack.StreamIndex,
                "-c:s", SubtitleCodecName(format),
                temporaryPath,
            ],
        });

        return (temporaryPath, "内封字幕已抽出为临时文件后烧入");
    }

    private static TranscodeStep BuildSubtitleConvertStep(
        MediaInfo info,
        TranscodeParams parameters,
        string outputPath) => new()
    {
        Kind = TranscodeStepKind.SubtitleConvert,
        Label = "字幕格式转换",
        OutputPath = outputPath,
        ExpectedDuration = null,
        Arguments =
        [
            "-hide_banner", "-nostdin", "-loglevel", "error",
            parameters.AllowOverwrite ? "-y" : "-n",
            "-i", info.Path,
            "-c:s", SubtitleCodecName(parameters.SubtitleConvertFormat),
            "-f", SubtitleMuxerName(parameters.SubtitleConvertFormat),
            outputPath,
        ],
    };

    private static TranscodeStep BuildTranscodeStep(
        MediaInfo info,
        TranscodeParams parameters,
        EncoderDefinition encoder,
        HwAccelKind effectiveAccel,
        string outputPath,
        string? burnSubtitlePath)
    {
        var container = parameters.ContainerDefinition;
        var videoReencode = parameters.VideoMode == VideoMode.Encode && info.HasVideo;

        // 滤镜链先算出来：它决定了能不能把解码帧留在显存里
        var filterChain = videoReencode
            ? FilterBuilder.Build(info, parameters, encoder, effectiveAccel, burnSubtitlePath)
            : new FilterChainResult { Filter = null, HasSoftwareFilter = false, IsGpuOnly = false };

        var useGpuFrames = videoReencode
            && effectiveAccel is HwAccelKind.Cuda or HwAccelKind.Qsv
            && !filterChain.HasSoftwareFilter
            && string.IsNullOrWhiteSpace(parameters.PixelFormat)
            && (!string.IsNullOrWhiteSpace(filterChain.Filter) ? filterChain.IsGpuOnly : true);

        var arguments = new List<string>
        {
            "-hide_banner", "-nostdin", "-loglevel", "info",
            parameters.AllowOverwrite ? "-y" : "-n",
        };

        // 输入选项必须在 -i 之前
        if (videoReencode)
        {
            switch (effectiveAccel)
            {
                case HwAccelKind.Cuda:
                    arguments.AddRange(["-hwaccel", "cuda"]);
                    break;
                case HwAccelKind.Qsv:
                    arguments.AddRange(["-hwaccel", "qsv"]);
                    break;
                case HwAccelKind.D3d11va:
                    arguments.AddRange(["-hwaccel", "d3d11va"]);
                    break;
                case HwAccelKind.Dxva2:
                    arguments.AddRange(["-hwaccel", "dxva2"]);
                    break;
            }

            if (useGpuFrames)
            {
                arguments.AddRange(["-hwaccel_output_format", effectiveAccel == HwAccelKind.Cuda ? "cuda" : "qsv"]);
            }
        }

        arguments.AddRange(["-i", info.Path]);

        // ── 流映射 ──
        var videoStreamIndex = info.VideoStream?.Index ?? -1;
        if (videoStreamIndex >= 0 && parameters.VideoMode != VideoMode.Drop)
        {
            arguments.AddRange(["-map", "0:" + videoStreamIndex]);
        }

        var selectedAudio = parameters.AudioTracks
            .Where(t => t.IsSelected && t.Action != AudioActionKind.Drop)
            .OrderBy(t => t.StreamIndex)
            .ToArray();

        foreach (var track in selectedAudio)
        {
            arguments.AddRange(["-map", "0:" + track.StreamIndex]);
        }

        // 烧入的字幕不参与映射；其余保留的字幕轨道按顺序映射
        var keptSubtitles = parameters.SubtitleTracks
            .Where(t => t.IsSelected && t.Action == SubtitleActionKind.Copy)
            .OrderBy(t => t.StreamIndex)
            .ToArray();

        foreach (var track in keptSubtitles)
        {
            arguments.AddRange(["-map", "0:" + track.StreamIndex]);
        }

        // ── 视频编码 ──
        if (videoStreamIndex >= 0 && parameters.VideoMode != VideoMode.Drop)
        {
            if (parameters.VideoMode == VideoMode.Copy)
            {
                arguments.AddRange(["-c:v", "copy"]);
            }
            else
            {
                arguments.AddRange(["-c:v", encoder.Id]);
                AppendVideoQualityArguments(arguments, parameters, encoder, useGpuFrames);

                if (!string.IsNullOrWhiteSpace(parameters.FrameRate))
                {
                    arguments.AddRange(["-r", parameters.FrameRate.Trim()]);
                }

                if (!string.IsNullOrWhiteSpace(filterChain.Filter))
                {
                    arguments.AddRange(["-vf", filterChain.Filter]);
                }
            }
        }

        // ── 音频编码（按输出序号，逐个指定，避免多轨互相干扰）──
        for (var index = 0; index < selectedAudio.Length; index++)
        {
            var track = selectedAudio[index];
            if (track.Action == AudioActionKind.Copy)
            {
                arguments.AddRange([$"-c:a:{index}", "copy"]);
                continue;
            }

            var codecId = string.IsNullOrWhiteSpace(track.CodecId) ? "aac" : track.CodecId;
            arguments.AddRange([$"-c:a:{index}", codecId]);
            if (track.BitRateKbps > 0)
            {
                arguments.AddRange([$"-b:a:{index}", track.BitRateKbps + "k"]);
            }

            if (track.TargetChannels > 0)
            {
                arguments.AddRange([$"-ac:{index}", track.TargetChannels.ToString()]);
            }
        }

        // ── 字幕编码 ──
        for (var index = 0; index < keptSubtitles.Length; index++)
        {
            var codec = ResolveCopySubtitleCodec(keptSubtitles[index].SourceCodec, container);
            if (codec is null)
            {
                continue;
            }

            arguments.AddRange([$"-c:s:{index}", codec]);
        }

        // ── 容器相关 ──
        if (parameters.FastStart && container.Extension is "mp4" or "mov")
        {
            arguments.AddRange(["-movflags", "+faststart"]);
        }

        // ── 附加参数（用户手填，放在最后以便覆盖前面的设置）──
        var extra = SplitArguments(parameters.ExtraArguments);
        if (extra.Count > 0)
        {
            arguments.AddRange(extra);
        }

        // ── 进度输出 ──
        arguments.AddRange(["-progress", "pipe:1", "-nostats"]);

        // 输出路径必须是最后一个参数
        arguments.Add(outputPath);

        return new TranscodeStep
        {
            Kind = TranscodeStepKind.Transcode,
            Label = parameters.VideoMode switch
            {
                VideoMode.Drop => "提取音频",
                VideoMode.Copy => "封装",
                _ => "转码",
            },
            OutputPath = outputPath,
            ExpectedDuration = info.Duration,
            Arguments = arguments,
        };
    }

    private static void AppendVideoQualityArguments(
        List<string> arguments,
        TranscodeParams parameters,
        EncoderDefinition encoder,
        bool useGpuFrames)
    {
        if (parameters.QualityMode == QualityMode.Simple)
        {
            var quality = EncoderCatalog.MapSliderToQuality(encoder, parameters.QualitySlider);
            arguments.AddRange([encoder.QualityParam, quality.ToString()]);

            var preset = EncoderCatalog.MapSliderToPreset(encoder, parameters.QualitySlider);
            if (!string.IsNullOrWhiteSpace(preset))
            {
                arguments.AddRange([encoder.PresetParam, preset]);
            }

            return;
        }

        // 高级模式
        if (parameters.RateControl == RateControlKind.Quality)
        {
            var quality = Math.Clamp(parameters.QualityValue, encoder.QualityMin, encoder.QualityMax);
            arguments.AddRange([encoder.QualityParam, quality.ToString()]);
        }
        else
        {
            arguments.AddRange(["-b:v", Math.Max(1, parameters.BitrateKbps) + "k"]);

            if (parameters.MaxrateKbps > 0)
            {
                arguments.AddRange(["-maxrate", parameters.MaxrateKbps + "k"]);
                if (parameters.BufsizeKbps > 0)
                {
                    arguments.AddRange(["-bufsize", parameters.BufsizeKbps + "k"]);
                }
            }

            if (encoder.Family == EncoderFamily.Nvenc)
            {
                arguments.AddRange(["-rc", parameters.MaxrateKbps > 0 ? "cbr" : "vbr"]);
            }
        }

        if (!string.IsNullOrWhiteSpace(parameters.Preset))
        {
            arguments.AddRange([encoder.PresetParam, parameters.Preset.Trim()]);
        }

        if (!string.IsNullOrWhiteSpace(parameters.Tune))
        {
            arguments.AddRange(["-tune", parameters.Tune.Trim()]);
        }

        if (!string.IsNullOrWhiteSpace(parameters.Profile))
        {
            arguments.AddRange(["-profile:v", parameters.Profile.Trim()]);
        }

        if (!string.IsNullOrWhiteSpace(parameters.Level))
        {
            arguments.AddRange(["-level", parameters.Level.Trim()]);
        }

        if (parameters.Gop > 0)
        {
            arguments.AddRange(["-g", parameters.Gop.ToString()]);
        }

        // 显存里的帧不能同时指定软件像素格式
        if (!string.IsNullOrWhiteSpace(parameters.PixelFormat) && !useGpuFrames)
        {
            arguments.AddRange(["-pix_fmt", parameters.PixelFormat.Trim()]);
        }
    }

    /// <summary>容器允许直接 copy 就 copy，否则退回容器唯一支持的文本字幕编码。</summary>
    private static string? ResolveCopySubtitleCodec(string sourceCodec, ContainerDefinition container)
    {
        if (container.SubtitleCodecs.Length == 0)
        {
            return null;
        }

        if (container.SubtitleCodecs.Contains("copy", StringComparer.OrdinalIgnoreCase))
        {
            return "copy";
        }

        // mp4/mov 只认 mov_text，文本字幕可以直接转
        var isTextSource = sourceCodec is
            "subrip" or "srt" or "ass" or "ssa" or "mov_text" or "webvtt" or "text" or "sami" or "microdvd";

        return isTextSource && container.SubtitleCodecs.Contains("mov_text", StringComparer.OrdinalIgnoreCase)
            ? "mov_text"
            : null;
    }

    /// <summary>把用户手填的附加参数字符串切开（支持双引号包裹）。</summary>
    public static IReadOnlyList<string> SplitArguments(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var ch in text)
        {
            switch (ch)
            {
                case '"':
                    inQuotes = !inQuotes;
                    break;
                case ' ' or '\t' or '\r' or '\n' when !inQuotes:
                    if (current.Length > 0)
                    {
                        result.Add(current.ToString());
                        current.Clear();
                    }

                    break;
                default:
                    current.Append(ch);
                    break;
            }
        }

        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }

        return result;
    }
}
