using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaCraft.Logging;

namespace MediaCraft.Ffmpeg;

/// <summary>
/// ffprobe 封装：读取媒体信息，并提供媒体文件类型判定（供文件列表与递归扫描使用）。
/// </summary>
public static class MediaProbe
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>可参与转码的视频容器扩展名。</summary>
    public static readonly string[] VideoExtensions =
    [
        ".mp4", ".mkv", ".mov", ".avi", ".ts", ".m2ts", ".mts", ".flv", ".webm", ".wmv",
        ".mpg", ".mpeg", ".m4v", ".3gp", ".vob", ".rmvb", ".rm", ".ogv", ".divx", ".asf", ".f4v",
    ];

    /// <summary>纯音频扩展名。</summary>
    public static readonly string[] AudioExtensions =
    [
        ".mp3", ".m4a", ".aac", ".flac", ".wav", ".ogg", ".oga", ".opus", ".wma", ".ape", ".alac", ".ac3", ".dts",
    ];

    /// <summary>字幕扩展名（可作为独立任务做格式转换，或作为外挂字幕烧入）。</summary>
    public static readonly string[] SubtitleExtensions =
    [
        ".srt", ".ass", ".ssa", ".vtt", ".sub",
    ];

    /// <summary>文件列表可接受的扩展名（全部）。</summary>
    public static IEnumerable<string> AllExtensions =>
        VideoExtensions.Concat(AudioExtensions).Concat(SubtitleExtensions);

    public static bool IsMediaFile(string path) => KindOf(path) != MediaFileKind.Unknown;

    public static bool IsSubtitleFile(string path) =>
        SubtitleExtensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>按扩展名判定文件类型。</summary>
    public static MediaFileKind KindOf(string path)
    {
        var extension = System.IO.Path.GetExtension(path);
        if (VideoExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return MediaFileKind.Video;
        }

        if (AudioExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return MediaFileKind.Audio;
        }

        if (SubtitleExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return MediaFileKind.Subtitle;
        }

        return MediaFileKind.Unknown;
    }

    /// <summary>调用 ffprobe 读取媒体信息，失败返回 null。</summary>
    public static async Task<MediaInfo?> ProbeAsync(
        string ffprobePath,
        string file,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await ProcessRunner.RunAsync(
                ffprobePath,
                [
                    "-v", "error",
                    "-print_format", "json",
                    "-show_format",
                    "-show_streams",
                    "-show_error",
                    file,
                ],
                cancellationToken,
                60000).ConfigureAwait(false);

            if (!result.Succeeded || string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                var message = result.StandardError.Trim();
                AppLog.Warn($"ffprobe 读取失败：{System.IO.Path.GetFileName(file)} → {FirstLine(message)}", "FFprobe");
                return null;
            }

            return Parse(result.StandardOutput, file);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "MediaProbe.Probe");
            return null;
        }
    }

    /// <summary>解析 ffprobe 的 JSON 输出。</summary>
    public static MediaInfo? Parse(string json, string file)
    {
        ProbeRoot? root;
        try
        {
            root = JsonSerializer.Deserialize<ProbeRoot>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            AppLog.Error(ex, "MediaProbe.Parse");
            return null;
        }

        if (root?.Format is null && root?.Streams is null)
        {
            return null;
        }

        var streams = (root?.Streams ?? [])
            .Select(ToStreamInfo)
            .OrderBy(s => s.Index)
            .ToArray();

        var duration = ParseSeconds(root?.Format?.Duration);
        if (duration <= 0)
        {
            // 部分格式（如裸流、图片）在 format 层没有时长，退回到视频流时长
            var streamDuration = streams
                .Select(s => s.Duration?.TotalSeconds ?? 0)
                .DefaultIfEmpty(0)
                .Max();
            duration = streamDuration;
        }

        return new MediaInfo
        {
            Path = file,
            Duration = TimeSpan.FromSeconds(duration),
            SizeBytes = ParseLong(root?.Format?.Size) ?? SafeFileSize(file),
            BitRate = ParseLong(root?.Format?.BitRate) ?? 0,
            FormatName = root?.Format?.FormatName ?? string.Empty,
            FormatLongName = root?.Format?.FormatLongName ?? string.Empty,
            Streams = streams,
        };
    }

    private static MediaStreamInfo ToStreamInfo(ProbeStream stream)
    {
        var frameRateRaw = string.IsNullOrWhiteSpace(stream.AvgFrameRate) || stream.AvgFrameRate == "0/0"
            ? stream.RFrameRate
            : stream.AvgFrameRate;

        var codecType = stream.CodecType ?? string.Empty;
        var isAttachedPicture = stream.Disposition?.AttachedPic == 1;

        return new MediaStreamInfo
        {
            Index = stream.Index,
            CodecType = codecType,
            CodecName = stream.CodecName ?? string.Empty,
            CodecLongName = stream.CodecLongName ?? string.Empty,
            Profile = stream.Profile ?? string.Empty,
            Width = stream.Width,
            Height = stream.Height,
            PixelFormat = stream.PixelFormat ?? string.Empty,
            ColorTransfer = stream.ColorTransfer ?? string.Empty,
            FrameRateRaw = frameRateRaw ?? string.Empty,
            FrameRate = MediaFormat.ParseFrameRate(frameRateRaw),
            Channels = stream.Channels,
            ChannelLayout = stream.ChannelLayout ?? string.Empty,
            SampleRate = (int)(ParseLong(stream.SampleRate) ?? 0),
            BitRate = ParseLong(stream.BitRate) ?? 0,
            Language = TagValue(stream.Tags, "language"),
            Title = TagValue(stream.Tags, "title"),
            IsDefault = stream.Disposition?.Default == 1,
            IsForced = stream.Disposition?.Forced == 1,
            IsAttachedPicture = isAttachedPicture,
            Duration = ParseSeconds(stream.Duration) is var seconds && seconds > 0
                ? TimeSpan.FromSeconds(seconds)
                : null,
        };
    }

    private static string TagValue(Dictionary<string, string>? tags, string key) =>
        tags is not null && tags.TryGetValue(key, out var value) ? value : string.Empty;

    private static double ParseSeconds(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static long? ParseLong(string? text) =>
        long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static long SafeFileSize(string file)
    {
        try
        {
            return new FileInfo(file).Exists ? new FileInfo(file).Length : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static string FirstLine(string text)
    {
        var index = text.IndexOf('\n');
        return (index < 0 ? text : text[..index]).Trim();
    }

    // ── ffprobe JSON DTO（字段名与 ffprobe 输出一致，数值可能以字符串形式出现）──

    private sealed class ProbeRoot
    {
        [JsonPropertyName("streams")] public List<ProbeStream>? Streams { get; set; }

        [JsonPropertyName("format")] public ProbeFormat? Format { get; set; }
    }

    private sealed class ProbeFormat
    {
        [JsonPropertyName("format_name")] public string? FormatName { get; set; }

        [JsonPropertyName("format_long_name")] public string? FormatLongName { get; set; }

        [JsonPropertyName("duration")] public string? Duration { get; set; }

        [JsonPropertyName("size")] public string? Size { get; set; }

        [JsonPropertyName("bit_rate")] public string? BitRate { get; set; }
    }

    private sealed class ProbeStream
    {
        [JsonPropertyName("index")] public int Index { get; set; }

        [JsonPropertyName("codec_name")] public string? CodecName { get; set; }

        [JsonPropertyName("codec_long_name")] public string? CodecLongName { get; set; }

        [JsonPropertyName("codec_type")] public string? CodecType { get; set; }

        [JsonPropertyName("profile")] public string? Profile { get; set; }

        [JsonPropertyName("width")] public int Width { get; set; }

        [JsonPropertyName("height")] public int Height { get; set; }

        [JsonPropertyName("pix_fmt")] public string? PixelFormat { get; set; }

        [JsonPropertyName("color_transfer")] public string? ColorTransfer { get; set; }

        [JsonPropertyName("r_frame_rate")] public string? RFrameRate { get; set; }

        [JsonPropertyName("avg_frame_rate")] public string? AvgFrameRate { get; set; }

        [JsonPropertyName("channels")] public int Channels { get; set; }

        [JsonPropertyName("channel_layout")] public string? ChannelLayout { get; set; }

        [JsonPropertyName("sample_rate")] public string? SampleRate { get; set; }

        [JsonPropertyName("bit_rate")] public string? BitRate { get; set; }

        [JsonPropertyName("duration")] public string? Duration { get; set; }

        [JsonPropertyName("tags")] public Dictionary<string, string>? Tags { get; set; }

        [JsonPropertyName("disposition")] public ProbeDisposition? Disposition { get; set; }
    }

    private sealed class ProbeDisposition
    {
        [JsonPropertyName("default")] public int Default { get; set; }

        [JsonPropertyName("forced")] public int Forced { get; set; }

        [JsonPropertyName("attached_pic")] public int AttachedPic { get; set; }
    }
}
