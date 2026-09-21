using System.Globalization;
using System.IO;
using MediaCraft.Media;

namespace MediaCraft.Ffmpeg;

/// <summary>预检问题的严重程度。</summary>
public enum IssueSeverity
{
    Info = 0,
    Warning,
    Error,
}

/// <summary>一条预检结论。</summary>
public sealed class PreflightIssue
{
    public required IssueSeverity Severity { get; init; }

    public required string Title { get; init; }

    public required string Detail { get; init; }

    /// <summary>已自动应用的修正说明；null 表示需要用户自己处理。</summary>
    public string? AppliedFix { get; init; }

    /// <summary>是否为「已自动处理」的提示。</summary>
    public bool WasFixed => !string.IsNullOrEmpty(AppliedFix);

    public string DisplayText => WasFixed
        ? $"{Title}：{Detail}（已自动处理：{AppliedFix}）"
        : $"{Title}：{Detail}";

    public string SeverityText => Severity switch
    {
        IssueSeverity.Error => "错误",
        IssueSeverity.Warning => "警告",
        _ => "提示",
    };
}

/// <summary>预检结果：修正后的参数 + 问题清单。</summary>
public sealed class PreflightResult
{
    /// <summary>修正后的参数副本（原始参数不被修改）。</summary>
    public required TranscodeParams Effective { get; init; }

    /// <summary>实际生效的硬解方式。</summary>
    public HwAccelKind EffectiveAccel { get; set; }

    public List<PreflightIssue> Issues { get; } = [];

    /// <summary>是否存在无法自动修复、会阻断执行的问题。</summary>
    public bool HasBlockingError =>
        Issues.Any(i => i.Severity == IssueSeverity.Error && !i.WasFixed);

    public int ErrorCount => Issues.Count(i => i.Severity == IssueSeverity.Error);

    public int WarningCount => Issues.Count(i => i.Severity == IssueSeverity.Warning);

    /// <summary>一行摘要，用于列表/状态栏。</summary>
    public string Summary => Issues.Count == 0
        ? "预检通过"
        : $"{ErrorCount} 错误 / {WarningCount} 警告 / {Issues.Count - ErrorCount - WarningCount} 提示";
}

/// <summary>
/// 开跑前预检：把「已知会翻车的组合」拦在跑之前，能自动修的直接修掉并留下记录。
///
/// 设计原则：规则基于**运行时探测到的本机能力**（ffmpeg 版本、编码器是否真的能跑），
/// 而不是硬编码文档结论 —— 例如「H.264 NVENC 默认输出 High 4:4:4 必须补 -profile:v main」
/// 在 ffmpeg 8.1.2 上已不成立（实测默认就是 Main/yuv420p），所以那条规则只在老版本上触发。
/// </summary>
public static class PreflightValidator
{
    /// <summary>执行预检。</summary>
    /// <param name="info">源文件信息。</param>
    /// <param name="parameters">用户设置的参数（不会被修改）。</param>
    /// <param name="capabilities">本机 ffmpeg 能力（可为 null = 未探测）。</param>
    /// <param name="outputPath">计划输出路径。</param>
    public static PreflightResult Validate(
        MediaInfo info,
        TranscodeParams parameters,
        FfmpegCapabilities? capabilities,
        string outputPath)
    {
        var effective = parameters.Clone();
        var encoder = effective.Encoder;
        var container = effective.ContainerDefinition;
        var result = new PreflightResult
        {
            Effective = effective,
            EffectiveAccel = HwAccelKind.None,
        };
        var issues = result.Issues;

        // ── 输入是纯字幕文件：只做格式转换，其余规则不适用 ──
        if (TranscodeCommandBuilder.IsSubtitleOnly(info))
        {
            result.EffectiveAccel = HwAccelKind.None;
            issues.Add(new PreflightIssue
            {
                Severity = IssueSeverity.Info,
                Title = "字幕文件",
                Detail = $"输入只含字幕流，将转换为 {TranscodeCommandBuilder.SubtitleCodecName(effective.SubtitleConvertFormat)} 格式",
            });
            return result;
        }

        var videoReencode = effective.VideoMode == VideoMode.Encode && info.HasVideo;

        // ── 1. 编码器可用性 ──
        if (effective.VideoMode == VideoMode.Encode && info.HasVideo && capabilities is not null)
        {
            if (capabilities.IsFunctionallyRejected(encoder.Id))
            {
                issues.Add(new PreflightIssue
                {
                    Severity = IssueSeverity.Error,
                    Title = "编码器不可用",
                    Detail = $"{encoder.DisplayName} 在功能探测中失败（可能是显卡不支持或驱动异常），请换一个编码器",
                });
            }
            else if (!capabilities.IsEncoderAvailable(encoder.Id))
            {
                issues.Add(new PreflightIssue
                {
                    Severity = IssueSeverity.Error,
                    Title = "编码器缺失",
                    Detail = $"当前 ffmpeg（{capabilities.Version}）不包含 {encoder.Id}",
                });
            }
        }

        // ── 2. 硬解方式解析 ──
        var accel = ResolveHwAccel(effective, encoder, info, capabilities, issues, videoReencode);
        result.EffectiveAccel = accel;

        // ── 3. 视频直通 / 丢弃与滤镜需求冲突 ──
        var wantsScale = effective.ScaleMode != ScaleMode.Keep;
        var burnTrack = effective.SubtitleTracks.FirstOrDefault(t => t.IsSelected && t.Action == SubtitleActionKind.Burn);
        var hasExternalSubtitle = !string.IsNullOrWhiteSpace(effective.ExternalSubtitlePath);
        var wantsBurn = hasExternalSubtitle || burnTrack is not null;

        if (videoReencode is false && effective.VideoMode == VideoMode.Copy && (wantsScale || wantsBurn))
        {
            effective.VideoMode = VideoMode.Encode;
            videoReencode = true;
            issues.Add(new PreflightIssue
            {
                Severity = IssueSeverity.Warning,
                Title = "已改为重新编码",
                Detail = "视频直通（-c:v copy）模式下无法缩放或烧字幕",
                AppliedFix = "视频处理方式：直通 → 重新编码",
            });
        }

        // ── 4. 烧字幕的可行性 ──
        if (wantsBurn && info.HasVideo)
        {
            if (hasExternalSubtitle)
            {
                if (!File.Exists(effective.ExternalSubtitlePath))
                {
                    issues.Add(new PreflightIssue
                    {
                        Severity = IssueSeverity.Error,
                        Title = "外挂字幕不存在",
                        Detail = effective.ExternalSubtitlePath,
                    });
                }
            }
            else if (burnTrack is { IsBitmap: true })
            {
                issues.Add(new PreflightIssue
                {
                    Severity = IssueSeverity.Error,
                    Title = "图形字幕无法烧入",
                    Detail = $"字幕 #{burnTrack.StreamIndex}（{burnTrack.SourceCodec}）是图形字幕，ffmpeg 无法直接烧入，请改用文本字幕轨",
                });
            }
        }

        // ── 5. 字幕提取的可行性 ──
        foreach (var track in effective.SubtitleTracks.Where(t => t.IsSelected && t.Action == SubtitleActionKind.Extract).ToArray())
        {
            if (track.IsBitmap)
            {
                track.Action = SubtitleActionKind.Drop;
                issues.Add(new PreflightIssue
                {
                    Severity = IssueSeverity.Warning,
                    Title = "图形字幕无法提取为文本",
                    Detail = $"字幕 #{track.StreamIndex} 是图形字幕（{track.SourceCodec}），提取成 srt/ass 需要 OCR",
                    AppliedFix = "该字幕轨已改为「丢弃」",
                });
            }
        }

        // ── 6. 缩放与编码器分辨率上限 ──
        if (videoReencode && effective.ScaleMode != ScaleMode.Keep)
        {
            var (targetWidth, targetHeight) = FilterBuilder.ComputeTargetSize(
                info.VideoStream?.Width ?? 0,
                info.VideoStream?.Height ?? 0,
                effective.ScaleMode,
                effective.ScaleWidth,
                effective.ScaleHeight);

            if (targetWidth > encoder.MaxWidth || targetHeight > encoder.MaxHeight)
            {
                var ratio = Math.Min(
                    (double)encoder.MaxWidth / Math.Max(1, targetWidth),
                    (double)encoder.MaxHeight / Math.Max(1, targetHeight));

                var clampedWidth = Even((int)Math.Round(targetWidth * ratio));
                var clampedHeight = Even((int)Math.Round(targetHeight * ratio));

                effective.ScaleMode = ScaleMode.Width;
                effective.ScaleWidth = clampedWidth;
                effective.ScaleHeight = clampedHeight;

                issues.Add(new PreflightIssue
                {
                    Severity = IssueSeverity.Warning,
                    Title = "超出编码器分辨率上限",
                    Detail = $"{encoder.Id} 最大支持 {encoder.MaxWidth}×{encoder.MaxHeight}",
                    AppliedFix = $"目标分辨率已收缩到 {clampedWidth}×{clampedHeight}",
                });
            }
        }

        // ── 7. 容器与编码格式兼容性 ──
        if (effective.VideoMode == VideoMode.Encode && info.HasVideo &&
            !EncoderCatalog.IsVideoCodecCompatible(encoder.Codec, container) && container.VideoCapable)
        {
            var previous = container.Extension;
            effective.Container = "mkv";
            container = effective.ContainerDefinition;
            issues.Add(new PreflightIssue
            {
                Severity = IssueSeverity.Warning,
                Title = "容器不支持该编码格式",
                Detail = $"{previous.ToUpperInvariant()} 容器装不下 {encoder.Codec}",
                AppliedFix = "输出容器已改为 MKV",
            });
        }

        if (!container.VideoCapable && effective.VideoMode != VideoMode.Drop && info.HasVideo)
        {
            effective.VideoMode = VideoMode.Drop;
            effective.Container = GuessAudioContainer(effective);
            container = effective.ContainerDefinition;
            issues.Add(new PreflightIssue
            {
                Severity = IssueSeverity.Warning,
                Title = "音频容器不能装视频",
                Detail = $"{container.Extension.ToUpperInvariant()} 是纯音频容器",
                AppliedFix = "视频流已丢弃（只输出音频）",
            });
        }

        if (effective.VideoMode == VideoMode.Drop && !info.HasAudio)
        {
            issues.Add(new PreflightIssue
            {
                Severity = IssueSeverity.Error,
                Title = "没有可输出的音频",
                Detail = "该文件没有音频流，无法执行「提取音频」",
            });
        }

        // ── 8. 音频编码兼容性 ──
        foreach (var track in effective.AudioTracks.Where(t => t.IsSelected).ToArray())
        {
            if (track.Action == AudioActionKind.Encode)
            {
                if (!EncoderCatalog.IsAudioCodecCompatible(track.CodecId, container))
                {
                    track.CodecId = container.AudioCodecs.Length > 0 ? container.AudioCodecs[0] : "aac";
                    issues.Add(new PreflightIssue
                    {
                        Severity = IssueSeverity.Warning,
                        Title = "音频编码与容器不兼容",
                        Detail = $"{container.Extension.ToUpperInvariant()} 不支持所选音频编码",
                        AppliedFix = $"音频 #{track.StreamIndex} 已改为 {track.CodecId}",
                    });
                }

                continue;
            }

            if (track.Action == AudioActionKind.Copy && !EncoderCatalog.IsAudioCodecCompatible(track.SourceCodec, container))
            {
                track.Action = AudioActionKind.Encode;
                track.CodecId = container.AudioCodecs.Contains("aac", StringComparer.OrdinalIgnoreCase)
                    ? "aac"
                    : container.AudioCodecs.FirstOrDefault() ?? "aac";

                issues.Add(new PreflightIssue
                {
                    Severity = IssueSeverity.Warning,
                    Title = "音频直通与容器不兼容",
                    Detail = $"{container.Extension.ToUpperInvariant()} 装不下 {track.SourceCodec}",
                    AppliedFix = $"音频 #{track.StreamIndex} 已改为重编码（{track.CodecId}）",
                });
            }
        }

        // ── 7b. 单音轨容器 + 多条音轨 ──
        // 实测：mp3 / flac / wav 只接受一条音轨，多条会让整个转码以 muxer 错误失败。
        // 这里**只拦不修**：自动丢掉用户勾选的音轨属于静默数据丢失，
        // 必须由人来决定保留哪一条（或换容器）。
        if (container.MaxAudioStreams > 0)
        {
            var keptAudio = effective.AudioTracks
                .Where(t => t.IsSelected && t.Action != AudioActionKind.Drop)
                .ToArray();

            if (keptAudio.Length > container.MaxAudioStreams)
            {
                issues.Add(new PreflightIssue
                {
                    Severity = IssueSeverity.Error,
                    Title = "该容器只支持单条音轨",
                    Detail = $"{container.DisplayName.Split('（')[0]} 只能写入 1 条音轨，" +
                             $"当前有 {keptAudio.Length} 条被保留" +
                             $"（音轨 #{string.Join("、#", keptAudio.Select(t => t.StreamIndex))}）。" +
                             "请只勾选一条，或把输出容器改成 MKV / MP4 / M4A",
                });
            }
        }

        // ── 8a. 固定码率档位取整（AC3 等）──
        // 实测：AC3 填 200k 实际得到 192k、填 1000k 得到 640k —— ffmpeg 静默取整，
        // 界面只给合法档位，但预设/导入的 JSON 仍可能带来非法值，这里兜底并告知。
        foreach (var track in effective.AudioTracks.Where(t => t.IsSelected && t.Action == AudioActionKind.Encode).ToArray())
        {
            var codec = EncoderCatalog.GetAudioCodec(track.CodecId);
            if (codec.IsLossless || codec.BitrateOptions.Length == 0 || track.BitRateKbps <= 0)
            {
                continue;
            }

            if (codec.BitrateOptions.Contains(track.BitRateKbps))
            {
                continue;
            }

            var nearest = EncoderCatalog.NearestBitrate(codec.BitrateOptions, track.BitRateKbps);
            var original = track.BitRateKbps;
            track.BitRateKbps = nearest;
            issues.Add(new PreflightIssue
            {
                Severity = IssueSeverity.Warning,
                Title = "码率不在合法档位",
                Detail = $"{codec.DisplayName.Split('（')[0]} 只支持固定码率档位，{original} kbps 不是其中之一",
                AppliedFix = $"音频 #{track.StreamIndex} 已取整到 {nearest} kbps",
            });
        }

        // ── 8b. PCM 直通到 MP4/MOV 的兼容性提示 ──
        // ffmpeg 实测允许这样写（muxer 接受），但相当多硬件播放器/电视不认 PCM-in-MP4。
        // 这属于兼容性提醒而不是硬限制，所以只提示、绝不改动用户的直通选择
        //（曾经因为表写错而把用户的 PCM 直通强行改成 AAC，那是不可接受的损失）。
        if (container.Extension is "mp4" or "mov")
        {
            var pcmTracks = effective.AudioTracks
                .Where(t => t.IsSelected && t.Action == AudioActionKind.Copy &&
                            t.SourceCodec.StartsWith("pcm_", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (pcmTracks.Length > 0)
            {
                issues.Add(new PreflightIssue
                {
                    Severity = IssueSeverity.Info,
                    Title = "PCM 直通装在 MP4/MOV 里兼容性有限",
                    Detail = $"{pcmTracks.Length} 条音轨直通的是 " +
                             string.Join("、", pcmTracks.Select(t => t.SourceCodec).Distinct()) +
                             "；ffmpeg 能写，但部分硬件播放器/电视不认。需要广泛兼容可用「手机友好 720p」预设，或改输出 MKV",
                });
            }
        }

        // ── 9. 字幕内封兼容性 ──
        foreach (var track in effective.SubtitleTracks.Where(t =>
                     t.IsSelected && t.Action == SubtitleActionKind.Copy).ToArray())
        {
            var compatible = container.SubtitleCodecs.Length > 0 &&
                             (container.SubtitleCodecs.Contains("copy", StringComparer.OrdinalIgnoreCase)
                              || IsTextSubtitle(track.SourceCodec));

            if (!compatible)
            {
                track.Action = SubtitleActionKind.Drop;
                issues.Add(new PreflightIssue
                {
                    Severity = IssueSeverity.Warning,
                    Title = "字幕无法内封到该容器",
                    Detail = $"{container.Extension.ToUpperInvariant()} 不支持 {track.SourceCodec} 字幕",
                    AppliedFix = $"字幕 #{track.StreamIndex} 已改为「丢弃」",
                });
            }
            else if (!container.SubtitleCodecs.Contains("copy", StringComparer.OrdinalIgnoreCase) &&
                     !string.Equals(track.SourceCodec, "mov_text", StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new PreflightIssue
                {
                    Severity = IssueSeverity.Info,
                    Title = "字幕将被转换",
                    Detail = $"{track.SourceCodec} → mov_text（{container.Extension.ToUpperInvariant()} 唯一支持的文本字幕编码）",
                });
            }
        }

        // ── 10. 位深与 HDR ──
        if (videoReencode && effective.VideoMode == VideoMode.Encode)
        {
            if (info.IsHighBitDepth && !encoder.SupportsTenBit)
            {
                issues.Add(new PreflightIssue
                {
                    Severity = IssueSeverity.Warning,
                    Title = "会被压成 8bit",
                    Detail = $"源是 {info.VideoStream?.PixelFormat}，而 {encoder.Id} 不支持 10bit 输出，位深信息会丢失",
                });
            }

            if (info.IsHdr)
            {
                issues.Add(new PreflightIssue
                {
                    Severity = IssueSeverity.Warning,
                    Title = "HDR 未做色调映射",
                    Detail = "源是 HDR（PQ/HLG），本工具不转色调映射，输出到 SDR 设备上画面可能发灰",
                });
            }
        }

        // ── 11. 帧率 ──
        if (!string.IsNullOrWhiteSpace(effective.FrameRate))
        {
            if (!double.TryParse(effective.FrameRate.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var frameRate) ||
                frameRate is < 1 or > 480)
            {
                issues.Add(new PreflightIssue
                {
                    Severity = IssueSeverity.Error,
                    Title = "帧率非法",
                    Detail = $"“{effective.FrameRate}” 不是有效帧率（1-480）",
                });
            }
            else
            {
                var sourceFps = info.VideoStream?.FrameRate ?? 0;
                if (sourceFps > 0 && Math.Abs(sourceFps - frameRate) > 0.01)
                {
                    var ratio = frameRate / sourceFps;
                    if (ratio is > 2 or < 0.5)
                    {
                        issues.Add(new PreflightIssue
                        {
                            Severity = IssueSeverity.Warning,
                            Title = "帧率变化较大",
                            Detail = $"{sourceFps:0.##} fps → {frameRate:0.##} fps，画面节奏会明显改变",
                        });
                    }
                }
            }
        }

        // ── 12. 质量值范围 ──
        if (effective.QualityMode == QualityMode.Advanced && effective.RateControl == RateControlKind.Quality)
        {
            var clamped = Math.Clamp(effective.QualityValue, encoder.QualityMin, encoder.QualityMax);
            if (clamped != effective.QualityValue)
            {
                var original = effective.QualityValue;
                effective.QualityValue = clamped;
                issues.Add(new PreflightIssue
                {
                    Severity = IssueSeverity.Warning,
                    Title = "质量值超出范围",
                    Detail = $"{encoder.Id} 的 {encoder.QualityLabel} 取值范围是 {encoder.QualityMin}-{encoder.QualityMax}",
                    AppliedFix = $"{original} → {clamped}",
                });
            }
        }

        // ── 13. 老版本 ffmpeg 的 NVENC profile 兼容性问题（按版本条件触发）──
        if (capabilities is not null && capabilities.MajorVersion is > 0 and < 7 &&
            encoder.Id == "h264_nvenc" && string.IsNullOrWhiteSpace(effective.Profile))
        {
            effective.Profile = "main";
            issues.Add(new PreflightIssue
            {
                Severity = IssueSeverity.Info,
                Title = "老版本 ffmpeg 的 NVENC profile 兜底",
                Detail = $"ffmpeg {capabilities.MajorVersion}.x 的 h264_nvenc 默认可能输出 High 4:4:4，部分播放器不认",
                AppliedFix = "已补 -profile:v main",
            });
        }

        // ── 14. 输出路径安全 ──
        if (PathsEqual(outputPath, info.Path))
        {
            issues.Add(new PreflightIssue
            {
                Severity = IssueSeverity.Error,
                Title = "输出会覆盖源文件",
                Detail = outputPath,
            });
        }

        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDirectory))
        {
            try
            {
                if (!Directory.Exists(outputDirectory))
                {
                    Directory.CreateDirectory(outputDirectory);
                    issues.Add(new PreflightIssue
                    {
                        Severity = IssueSeverity.Info,
                        Title = "已创建输出目录",
                        Detail = outputDirectory,
                    });
                }
            }
            catch (Exception ex)
            {
                issues.Add(new PreflightIssue
                {
                    Severity = IssueSeverity.Error,
                    Title = "输出目录不可用",
                    Detail = $"{outputDirectory}（{ex.Message}）",
                });
            }
        }

        // ── 15. 图形字幕却选了烧入时的兜底（前面已在 4 里报错，这里不再重复）──
        return result;
    }

    /// <summary>解析实际生效的硬解方式，并在不可用时降级。</summary>
    private static HwAccelKind ResolveHwAccel(
        TranscodeParams parameters,
        EncoderDefinition encoder,
        MediaInfo info,
        FfmpegCapabilities? capabilities,
        List<PreflightIssue> issues,
        bool videoReencode)
    {
        if (!videoReencode)
        {
            return HwAccelKind.None;
        }

        var requested = parameters.HwAccel;
        if (requested == HwAccelKind.Auto)
        {
            requested = encoder.PreferredAccel;
        }

        if (requested == HwAccelKind.None)
        {
            return HwAccelKind.None;
        }

        if (capabilities is null)
        {
            return requested;
        }

        var accelName = requested switch
        {
            HwAccelKind.Cuda => "cuda",
            HwAccelKind.Qsv => "qsv",
            HwAccelKind.D3d11va => "d3d11va",
            HwAccelKind.Dxva2 => "dxva2",
            _ => null,
        };

        if (accelName is not null && !capabilities.HasHwAccel(accelName))
        {
            issues.Add(new PreflightIssue
            {
                Severity = IssueSeverity.Warning,
                Title = "硬解不可用",
                Detail = $"本机 ffmpeg 不支持 {accelName} 硬解",
                AppliedFix = "已改为 CPU 软解",
            });
            return HwAccelKind.None;
        }

        // 烧字幕等软件滤镜与「帧留在显存」互斥：
        // 这里显式降级为「硬解 + 自动下载到内存」，这是实测可用的安全路径。
        // 注意：NVENC + cuda 下的缩放走 scale_cuda（全程在显存内），不算软件滤镜，
        // 判定条件必须与 FilterBuilder 里选择 scale_cuda 的条件保持一致，否则会给出误导性提示。
        var scaleStaysOnGpu = parameters.ScaleMode != ScaleMode.Keep
                              && requested == HwAccelKind.Cuda
                              && encoder.Family == EncoderFamily.Nvenc;

        var hasSoftwareFilter = !scaleStaysOnGpu &&
                                (parameters.ScaleMode != ScaleMode.Keep ||
                                 !string.IsNullOrWhiteSpace(parameters.ExternalSubtitlePath) ||
                                 parameters.SubtitleTracks.Any(t => t.IsSelected && t.Action == SubtitleActionKind.Burn));

        if (hasSoftwareFilter)
        {
            issues.Add(new PreflightIssue
            {
                Severity = IssueSeverity.Info,
                Title = "硬解搭配软件滤镜",
                Detail = "解码仍走硬件，但帧会回读到内存后再做滤镜（否则滤镜链会因显存格式报错）",
            });
        }

        return requested;
    }

    /// <summary>纯音频容器被选中时，按音频编码猜测合适的容器。</summary>
    private static string GuessAudioContainer(TranscodeParams parameters)
    {
        var codec = parameters.AudioTracks
            .Where(t => t.IsSelected && t.Action == AudioActionKind.Encode)
            .Select(t => t.CodecId)
            .FirstOrDefault();

        return codec switch
        {
            "libmp3lame" => "mp3",
            "libopus" => "opus",
            "flac" => "flac",
            "pcm_s16le" or "pcm_s24le" => "wav",
            _ => "m4a",
        };
    }

    private static bool IsTextSubtitle(string codec) => codec is
        "subrip" or "srt" or "ass" or "ssa" or "mov_text" or "webvtt" or "text" or "sami" or "microdvd";

    private static bool PathsEqual(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static int Even(int value) => value % 2 == 0 ? value : value - 1;
}
