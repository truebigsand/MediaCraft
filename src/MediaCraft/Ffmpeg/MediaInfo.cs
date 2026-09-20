using System.Globalization;

namespace MediaCraft.Ffmpeg;

/// <summary>媒体流信息（来自 ffprobe）。</summary>
public sealed class MediaStreamInfo
{
    public int Index { get; init; }

    /// <summary>video / audio / subtitle / data / attachment。</summary>
    public string CodecType { get; init; } = string.Empty;

    public string CodecName { get; init; } = string.Empty;

    public string CodecLongName { get; init; } = string.Empty;

    public string Profile { get; init; } = string.Empty;

    public int Width { get; init; }

    public int Height { get; init; }

    public string PixelFormat { get; init; } = string.Empty;

    /// <summary>颜色传递特性；smpte2084 / arib-std-b67 表示 HDR。</summary>
    public string ColorTransfer { get; init; } = string.Empty;

    public string FrameRateRaw { get; init; } = string.Empty;

    public double FrameRate { get; init; }

    public int Channels { get; init; }

    public string ChannelLayout { get; init; } = string.Empty;

    public int SampleRate { get; init; }

    public long BitRate { get; init; }

    public string Language { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public bool IsDefault { get; init; }

    public bool IsForced { get; init; }

    public bool IsAttachedPicture { get; init; }

    public TimeSpan? Duration { get; init; }

    /// <summary>真实视频流（排除封面图）。</summary>
    public bool IsVideo => CodecType == "video" && !IsAttachedPicture;

    public bool IsAudio => CodecType == "audio";

    public bool IsSubtitle => CodecType == "subtitle";

    /// <summary>10/12/16 bit 像素格式。</summary>
    public bool IsHighBitDepth =>
        PixelFormat.Contains("10", StringComparison.Ordinal) ||
        PixelFormat.Contains("12", StringComparison.Ordinal) ||
        PixelFormat.Contains("16", StringComparison.Ordinal);

    public bool IsHdr => ColorTransfer is "smpte2084" or "arib-std-b67";

    /// <summary>图形字幕无法转成文本字幕（需要 OCR）。</summary>
    public bool IsBitmapSubtitle => CodecName is "hdmv_pgs_subtitle" or "dvd_subtitle" or "dvb_subtitle" or "xsub";

    public string ResolutionText => Width > 0 && Height > 0 ? $"{Width}×{Height}" : "—";

    public string FrameRateText => FrameRate > 0
        ? FrameRate.ToString(FrameRate % 1 == 0 ? "0" : "0.###", CultureInfo.InvariantCulture) + " fps"
        : "—";

    public string BitRateText => MediaFormat.FormatBitRate(BitRate);

    public string ChannelsText
    {
        get
        {
            if (Channels <= 0)
            {
                return "—";
            }

            var layout = ChannelLayout switch
            {
                "mono" => "单声道",
                "stereo" => "立体声",
                "5.1" or "5.1(side)" => "5.1",
                "7.1" => "7.1",
                _ => ChannelLayout,
            };
            return $"{Channels}ch{(string.IsNullOrEmpty(layout) ? string.Empty : " " + layout)}";
        }
    }

    /// <summary>流列表中的显示名，例如 “音频 #1 · aac · 立体声 · 中文”。</summary>
    public string DisplayName
    {
        get
        {
            var kind = CodecType switch
            {
                "video" => "视频",
                "audio" => "音频",
                "subtitle" => "字幕",
                _ => CodecType,
            };

            var parts = new List<string> { $"{kind} #{Index}", CodecName };
            if (IsAudio)
            {
                parts.Add(ChannelsText);
                if (BitRate > 0)
                {
                    parts.Add(BitRateText);
                }
            }

            if (IsSubtitle)
            {
                parts.Add(IsForced ? "强制" : IsDefault ? "默认" : "可选");
            }

            if (!string.IsNullOrWhiteSpace(Language))
            {
                parts.Add(LanguageName(Language));
            }

            if (!string.IsNullOrWhiteSpace(Title))
            {
                parts.Add(Title);
            }

            return string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        }
    }

    /// <summary>常见语言代码转中文名，未收录则原样返回。</summary>
    public static string LanguageName(string code) => code.ToLowerInvariant() switch
    {
        "chi" or "zho" or "zh" or "chs" or "cht" => "中文",
        "eng" or "en" => "英语",
        "jpn" or "ja" => "日语",
        "kor" or "ko" => "韩语",
        "fra" or "fre" or "fr" => "法语",
        "deu" or "ger" or "de" => "德语",
        "spa" or "es" => "西班牙语",
        "rus" or "ru" => "俄语",
        _ => code,
    };
}

/// <summary>媒体文件信息（来自 ffprobe）。</summary>
public sealed class MediaInfo
{
    public string Path { get; init; } = string.Empty;

    public string FileName => System.IO.Path.GetFileName(Path);

    public TimeSpan Duration { get; init; }

    public long SizeBytes { get; init; }

    public long BitRate { get; init; }

    public string FormatName { get; init; } = string.Empty;

    public string FormatLongName { get; init; } = string.Empty;

    public IReadOnlyList<MediaStreamInfo> Streams { get; init; } = [];

    public MediaStreamInfo? VideoStream => Streams.FirstOrDefault(s => s.IsVideo);

    public IReadOnlyList<MediaStreamInfo> AudioStreams => Streams.Where(s => s.IsAudio).ToArray();

    public IReadOnlyList<MediaStreamInfo> SubtitleStreams => Streams.Where(s => s.IsSubtitle).ToArray();

    public bool HasVideo => VideoStream is not null;

    public bool HasAudio => AudioStreams.Count > 0;

    public bool HasSubtitle => SubtitleStreams.Count > 0;

    /// <summary>主视频流是否 10bit 及以上。</summary>
    public bool IsHighBitDepth => VideoStream?.IsHighBitDepth == true;

    /// <summary>主视频流是否 HDR。</summary>
    public bool IsHdr => VideoStream?.IsHdr == true;

    public string DurationText => MediaFormat.FormatDuration(Duration);

    public string SizeText => MediaFormat.FormatSize(SizeBytes);

    public string BitRateText => MediaFormat.FormatBitRate(BitRate);

    /// <summary>音频/字幕轨的简短摘要，例如 “2 音轨 / 1 字幕”。</summary>
    public string TrackSummary
    {
        get
        {
            var parts = new List<string>();
            if (HasVideo)
            {
                parts.Add(VideoStream!.ResolutionText);
            }

            if (HasAudio)
            {
                parts.Add($"{AudioStreams.Count} 音轨");
            }

            if (HasSubtitle)
            {
                parts.Add($"{SubtitleStreams.Count} 字幕");
            }

            return parts.Count == 0 ? "—" : string.Join(" / ", parts);
        }
    }
}

/// <summary>时长、体积、码率的显示格式化。</summary>
public static class MediaFormat
{
    public static string FormatDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return "—";
        }

        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}:{duration.Minutes:D2}:{duration.Seconds:D2}"
            : $"{duration.Minutes:D2}:{duration.Seconds:D2}";
    }

    public static string FormatSize(long bytes)
    {
        if (bytes <= 0)
        {
            return "—";
        }

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return value.ToString(unit == 0 ? "0" : "0.##", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    public static string FormatBitRate(long bitsPerSecond)
    {
        if (bitsPerSecond <= 0)
        {
            return "—";
        }

        return bitsPerSecond >= 1_000_000
            ? (bitsPerSecond / 1_000_000.0).ToString("0.##", CultureInfo.InvariantCulture) + " Mbps"
            : (bitsPerSecond / 1000.0).ToString("0", CultureInfo.InvariantCulture) + " kbps";
    }

    /// <summary>把 "30/1" 这样的分数帧率解析为 double。</summary>
    public static double ParseFrameRate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return 0;
        }

        var parts = raw.Split('/');
        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) &&
            denominator > 0)
        {
            return numerator / denominator;
        }

        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }
}
