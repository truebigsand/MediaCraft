using System.Text;
using MediaCraft.Media;

namespace MediaCraft.Ffmpeg;

/// <summary>滤镜链构造结果。</summary>
public sealed class FilterChainResult
{
    /// <summary>完整的 -vf 值；null 表示不需要滤镜。</summary>
    public string? Filter { get; init; }

    /// <summary>滤镜链是否完全在 GPU 内完成（scale_cuda）。</summary>
    public bool IsGpuOnly { get; init; }

    /// <summary>是否包含软件滤镜（决定了能不能把解码帧留在显存里）。</summary>
    public bool HasSoftwareFilter { get; init; }

    public List<string> Notes { get; } = [];
}

/// <summary>
/// 滤镜链构造与转义。
/// </summary>
public static class FilterBuilder
{
    /// <summary>
    /// 转义 subtitles/ass 滤镜的文件路径。
    ///
    /// 本机 ffmpeg 8.1.2 实测结论（见 docs/spec.md）：必须同时满足两条 ——
    /// 1) 整个路径用单引号包住；2) 盘符冒号写成 \:。
    /// 少了任何一条都会以 “Unable to parse "original_size" option value” 失败：
    /// 因为滤镜参数用 ':' 分隔选项，未转义的盘符会被当成选项分隔符。
    /// </summary>
    public static string EscapeFilterPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        var builder = new StringBuilder("'");
        foreach (var ch in normalized)
        {
            switch (ch)
            {
                case '\'':
                    builder.Append("\\'");
                    break;
                case ':':
                    builder.Append("\\:");
                    break;
                default:
                    builder.Append(ch);
                    break;
            }
        }

        builder.Append('\'');
        return builder.ToString();
    }

    /// <summary>构造 force_style 参数（libass 样式覆盖）。</summary>
    public static string BuildSubtitleStyle(SubtitleStyleOptions style)
    {
        var parts = new List<string>
        {
            $"FontName={SanitizeFontName(style.FontName)}",
            $"FontSize={Math.Clamp(style.FontSize, 6, 200)}",
            $"PrimaryColour={ToAssColor(style.PrimaryColor)}",
            $"OutlineColour={ToAssColor(style.OutlineColor)}",
            $"Outline={Math.Clamp(style.OutlineWidth, 0, 10)}",
            $"Shadow={Math.Clamp(style.Shadow, 0, 10)}",
            $"MarginV={Math.Clamp(style.MarginVertical, 0, 500)}",
            $"Alignment={Math.Clamp(style.Alignment, 1, 9)}",
            $"Bold={-1 * (style.Bold ? 1 : 0)}",
        };

        return string.Join(",", parts);
    }

    /// <summary>
    /// 构造视频滤镜链。
    /// </summary>
    /// <param name="info">源媒体信息（用于计算缩放目标尺寸）。</param>
    /// <param name="parameters">任务参数。</param>
    /// <param name="encoder">目标编码器。</param>
    /// <param name="effectiveAccel">实际生效的硬解方式。</param>
    /// <param name="subtitlePath">需要烧入的字幕文件路径（内封提取出的临时文件或外挂字幕）；null = 不烧。</param>
    public static FilterChainResult Build(
        MediaInfo info,
        TranscodeParams parameters,
        EncoderDefinition encoder,
        HwAccelKind effectiveAccel,
        string? subtitlePath)
    {
        var result = new List<string>();
        var hasSoftwareFilter = false;
        var isGpuOnly = true;

        // 编码器只吃 4:2:0（av1_nvenc、QSV 家族）时，非 4:2:0 源要在链尾转一次。
        // 具体滤镜留到链尾再定：QSV 在帧留在显存时用 vpp_qsv 在显存内转（零拷贝），
        // 否则用软件 format —— 后者会关掉 -hwaccel_output_format 的显存帧路径。
        var needsPixelFormatConversion = EncoderCatalog.NeedsYuv420Conversion(
            encoder,
            info.VideoStream,
            parameters.PixelFormat);

        // ── 缩放 ──
        var scaleFilter = BuildScaleFilter(info, parameters, out var scaleNote);
        if (scaleNote is not null)
        {
            // 调用方通过 Notes 拿到提示
        }

        var useGpuScale = false;
        if (scaleFilter is not null)
        {
            useGpuScale = effectiveAccel == HwAccelKind.Cuda
                          && encoder.Family == EncoderFamily.Nvenc
                          && subtitlePath is null
                          && !needsPixelFormatConversion;

            if (useGpuScale)
            {
                result.Add(scaleFilter.Replace("scale=", "scale_cuda=", StringComparison.Ordinal));
            }
            else
            {
                result.Add(scaleFilter);
                hasSoftwareFilter = true;
                isGpuOnly = false;
            }
        }

        // ── 字幕烧入（软件滤镜，必须在缩放之后，这样字幕按输出分辨率渲染）──
        if (!string.IsNullOrWhiteSpace(subtitlePath))
        {
            var subtitleFilter = new StringBuilder("subtitles=");
            subtitleFilter.Append(EscapeFilterPath(subtitlePath));
            subtitleFilter.Append(":force_style=");
            subtitleFilter.Append('\'');
            subtitleFilter.Append(BuildSubtitleStyle(parameters.SubtitleStyle));
            subtitleFilter.Append('\'');
            result.Add(subtitleFilter.ToString());
            hasSoftwareFilter = true;
            isGpuOnly = false;
        }

        // ── 像素格式（链尾，与 ffmpeg 的 -pix_fmt 输出选项语义一致）──
        // vpp_qsv 是硬件滤镜，帧留在显存里完成转换，因此不关显存帧路径；
        // 软件 format 必须把帧取回内存，于是关掉它（显存帧做不了软件格式转换）。
        var pixelFormatFilter = needsPixelFormatConversion
            ? EncoderCatalog.BuildYuv420ConversionFilter(
                encoder,
                info.VideoStream,
                parameters.PixelFormat,
                useHardwareFilter: effectiveAccel == HwAccelKind.Qsv && !hasSoftwareFilter)
            : null;

        if (pixelFormatFilter is not null)
        {
            result.Add(pixelFormatFilter);

            if (!pixelFormatFilter.StartsWith("vpp_qsv", StringComparison.Ordinal))
            {
                hasSoftwareFilter = true;
                isGpuOnly = false;
            }
        }

        var chain = new FilterChainResult
        {
            Filter = result.Count == 0 ? null : string.Join(",", result),
            IsGpuOnly = isGpuOnly && result.Count > 0,
            HasSoftwareFilter = hasSoftwareFilter,
        };

        if (scaleNote is not null)
        {
            chain.Notes.Add(scaleNote);
        }

        if (useGpuScale)
        {
            chain.Notes.Add("缩放走 scale_cuda（全程在显存内完成）");
        }

        if (pixelFormatFilter is not null)
        {
            var target = pixelFormatFilter[(pixelFormatFilter.LastIndexOf('=') + 1)..];
            chain.Notes.Add(
                $"{encoder.DisplayName} 需要 4:2:0 输入，源是 {info.VideoStream?.PixelFormat}，已插入格式转换（{target}）");
        }

        return chain;
    }

    /// <summary>
    /// 构造缩放滤镜。目标尺寸这里直接算出来（而不是用 -2 / 表达式），
    /// 这样 GPU 缩放路径也能用，且避免滤镜表达式里的逗号转义问题。
    /// </summary>
    public static string? BuildScaleFilter(MediaInfo info, TranscodeParams parameters, out string? note)
    {
        note = null;
        if (parameters.ScaleMode == ScaleMode.Keep)
        {
            return null;
        }

        var sourceWidth = info.VideoStream?.Width ?? 0;
        var sourceHeight = info.VideoStream?.Height ?? 0;

        var (targetWidth, targetHeight) = ComputeTargetSize(
            sourceWidth,
            sourceHeight,
            parameters.ScaleMode,
            parameters.ScaleWidth,
            parameters.ScaleHeight);

        if (targetWidth <= 0 || targetHeight <= 0)
        {
            // 源分辨率未知：只能交给 ffmpeg 自己算
            return parameters.ScaleMode switch
            {
                ScaleMode.Width when parameters.ScaleWidth > 0 => $"scale={Even(parameters.ScaleWidth)}:-2",
                ScaleMode.Height when parameters.ScaleHeight > 0 => $"scale=-2:{Even(parameters.ScaleHeight)}",
                _ => null,
            };
        }

        if (sourceWidth > 0 && targetWidth == sourceWidth && targetHeight == sourceHeight)
        {
            note = "源分辨率与目标一致，已跳过缩放";
            return null;
        }

        if (sourceWidth > 0 && targetWidth > sourceWidth)
        {
            note = "目标分辨率大于源，已按原样输出（避免无意义的放大）";
            return null;
        }

        return $"scale={targetWidth}:{targetHeight}";
    }

    /// <summary>计算目标尺寸（保证宽高都是偶数，否则 yuv420p 编码会失败）。</summary>
    public static (int Width, int Height) ComputeTargetSize(
        int sourceWidth,
        int sourceHeight,
        ScaleMode mode,
        int requestedWidth,
        int requestedHeight)
    {
        var hasSource = sourceWidth > 0 && sourceHeight > 0;

        switch (mode)
        {
            case ScaleMode.Width when requestedWidth > 0:
            {
                var width = Even(requestedWidth);
                if (!hasSource)
                {
                    return (width, 0);
                }

                var height = Even((int)Math.Round(sourceHeight * (double)width / sourceWidth));
                return (width, Math.Max(2, height));
            }

            case ScaleMode.Height when requestedHeight > 0:
            {
                var height = Even(requestedHeight);
                if (!hasSource)
                {
                    return (0, height);
                }

                var width = Even((int)Math.Round(sourceWidth * (double)height / sourceHeight));
                return (Math.Max(2, width), height);
            }

            case ScaleMode.Fit when requestedWidth > 0 && requestedHeight > 0:
            {
                var boxWidth = Even(requestedWidth);
                var boxHeight = Even(requestedHeight);
                if (!hasSource)
                {
                    return (boxWidth, boxHeight);
                }

                var ratio = Math.Min((double)boxWidth / sourceWidth, (double)boxHeight / sourceHeight);
                var width = Even((int)Math.Round(sourceWidth * ratio));
                var height = Even((int)Math.Round(sourceHeight * ratio));
                return (Math.Max(2, width), Math.Max(2, height));
            }

            default:
                return (0, 0);
        }
    }

    private static int Even(int value) => value % 2 == 0 ? value : value - 1;

    /// <summary>#RRGGBB → ASS 的 &HAABBGGRR（AA=00 表示不透明）。</summary>
    public static string ToAssColor(string hex)
    {
        var text = (hex ?? string.Empty).Trim().TrimStart('#');
        if (text.Length != 6)
        {
            return "&H00FFFFFF";
        }

        var red = text[..2];
        var green = text.Substring(2, 2);
        var blue = text.Substring(4, 2);
        return $"&H00{blue}{green}{red}".ToUpperInvariant();
    }

    /// <summary>字体名里出现逗号或冒号会破坏 force_style，直接剔除。</summary>
    private static string SanitizeFontName(string fontName)
    {
        if (string.IsNullOrWhiteSpace(fontName))
        {
            return "Microsoft YaHei";
        }

        var cleaned = fontName.Replace(",", string.Empty).Replace(":", string.Empty).Trim();
        return cleaned.Length == 0 ? "Microsoft YaHei" : cleaned;
    }
}
