using System.IO;
using MediaCraft.Ffmpeg;

namespace MediaCraft.Media;

/// <summary>
/// 输出路径计算：命名模板、输出目录、防覆盖（自动加序号）。
/// </summary>
public static class OutputPathBuilder
{
    /// <summary>命名模板占位符。</summary>
    public static readonly string[] TemplatePlaceholders =
    [
        "{name}", "{encoder}", "{quality}", "{date}", "{time}", "{index}",
    ];

    /// <summary>构造主输出路径。</summary>
    /// <param name="info">源文件信息。</param>
    /// <param name="parameters">任务参数。</param>
    /// <param name="defaultOutputDirectory">设置里的默认输出目录（可空 = 源目录）。</param>
    /// <param name="sequenceIndex">同批次内的序号（占位符 {index}）。</param>
    public static string Build(
        MediaInfo info,
        TranscodeParams parameters,
        string? defaultOutputDirectory,
        int sequenceIndex = 0)
    {
        var directory = ResolveOutputDirectory(info, parameters, defaultOutputDirectory);
        var fileName = BuildFileName(info, parameters, sequenceIndex);

        // 字幕文件输入的扩展名由目标字幕格式决定：容器概念对字幕不适用
        var extension = TranscodeCommandBuilder.IsSubtitleOnly(info)
            ? TranscodeCommandBuilder.SubtitleExtension(parameters.SubtitleConvertFormat).TrimStart('.')
            : EncoderCatalog.GetContainer(parameters.Container).Extension;

        var candidate = Path.Combine(directory, fileName + "." + extension);

        // 源文件本身也算「已占用」，绝不能被覆盖掉
        return parameters.AllowOverwrite
            ? candidate
            : EnsureUnique(candidate, info.Path);
    }

    /// <summary>构造无扩展名的文件名（套用命名模板）。</summary>
    public static string BuildFileName(MediaInfo info, TranscodeParams parameters, int sequenceIndex = 0)
    {
        var name = Path.GetFileNameWithoutExtension(info.Path);

        // 字幕转换时文件名模板没有意义（没有编码器/质量可言），固定用原名
        if (TranscodeCommandBuilder.IsSubtitleOnly(info))
        {
            return SanitizeFileName(name);
        }

        var template = string.IsNullOrWhiteSpace(parameters.NamingTemplate)
            ? "{name}_{encoder}_{quality}"
            : parameters.NamingTemplate;

        var encoder = EncoderCatalog.Get(parameters.EncoderId);

        var text = template
            .Replace("{name}", name, StringComparison.OrdinalIgnoreCase)
            .Replace("{encoder}", ShortEncoderToken(encoder), StringComparison.OrdinalIgnoreCase)
            .Replace("{quality}", QualityToken(parameters), StringComparison.OrdinalIgnoreCase)
            .Replace("{date}", DateTime.Now.ToString("yyyyMMdd"), StringComparison.OrdinalIgnoreCase)
            .Replace("{time}", DateTime.Now.ToString("HHmmss"), StringComparison.OrdinalIgnoreCase)
            .Replace("{index}", sequenceIndex.ToString(), StringComparison.OrdinalIgnoreCase);

        return SanitizeFileName(text);
    }

    /// <summary>字幕提取的附属文件路径（与主输出同目录同名前缀）。</summary>
    public static string BuildSidecarPath(string mainOutputPath, int streamIndex, SubtitleFormat format)
    {
        var directory = Path.GetDirectoryName(mainOutputPath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(mainOutputPath);
        var candidate = Path.Combine(directory, $"{name}.sub{streamIndex}{TranscodeCommandBuilder.SubtitleExtension(format)}");
        return EnsureUnique(candidate, mainOutputPath);
    }

    /// <summary>解析输出目录。</summary>
    public static string ResolveOutputDirectory(
        MediaInfo info,
        TranscodeParams parameters,
        string? defaultOutputDirectory)
    {
        if (!string.IsNullOrWhiteSpace(parameters.OutputDirectory))
        {
            return parameters.OutputDirectory;
        }

        if (!string.IsNullOrWhiteSpace(defaultOutputDirectory))
        {
            return defaultOutputDirectory;
        }

        var sourceDirectory = Path.GetDirectoryName(info.Path);
        return string.IsNullOrEmpty(sourceDirectory) ? Directory.GetCurrentDirectory() : sourceDirectory;
    }

    /// <summary>
    /// 保证路径不被占用：已存在（或被保留的源文件命中）时追加 (1)(2)…
    /// </summary>
    public static string EnsureUnique(string path, params string?[] reservedPaths)
    {
        if (!IsOccupied(path, reservedPaths))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);

        for (var index = 1; index < 10000; index++)
        {
            var candidate = Path.Combine(directory, $"{name} ({index}){extension}");
            if (!IsOccupied(candidate, reservedPaths))
            {
                return candidate;
            }
        }

        return Path.Combine(directory, $"{name} ({Guid.NewGuid():N}){extension}");
    }

    /// <summary>路径是否已被占用（存在，或命中保留路径）。</summary>
    public static bool IsOccupied(string path, params string?[] reservedPaths)
    {
        try
        {
            if (File.Exists(path))
            {
                return true;
            }
        }
        catch (Exception)
        {
            // 路径非法时按未占用处理，交给 ffmpeg 报错
        }

        foreach (var reserved in reservedPaths)
        {
            if (!string.IsNullOrWhiteSpace(reserved) &&
                string.Equals(Path.GetFullPath(path), Path.GetFullPath(reserved), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>编码器的短标记，用于文件名（避免出现下划线和 lib 前缀）。</summary>
    public static string ShortEncoderToken(EncoderDefinition encoder) => encoder.Id switch
    {
        "h264_nvenc" => "h264nvenc",
        "hevc_nvenc" => "hevcnvenc",
        "av1_nvenc" => "av1nvenc",
        "h264_qsv" => "h264qsv",
        "hevc_qsv" => "hevcqsv",
        "av1_qsv" => "av1qsv",
        "vp9_qsv" => "vp9qsv",
        "libx264" => "x264",
        "libx265" => "x265",
        "libsvtav1" => "svtav1",
        "libaom-av1" => "aomav1",
        _ => encoder.Id,
    };

    /// <summary>质量标记，用于文件名。</summary>
    public static string QualityToken(TranscodeParams parameters)
    {
        if (parameters.VideoMode == VideoMode.Drop)
        {
            return "audio";
        }

        if (parameters.VideoMode == VideoMode.Copy)
        {
            return "copy";
        }

        if (parameters.QualityMode == QualityMode.Simple)
        {
            return "q" + parameters.QualitySlider;
        }

        return parameters.RateControl == RateControlKind.Quality
            ? $"{EncoderCatalog.Get(parameters.EncoderId).QualityLabel.ToLowerInvariant()}{parameters.QualityValue}"
            : $"{parameters.BitrateKbps}k";
    }

    /// <summary>剔除文件名中的非法字符。</summary>
    public static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(name.Length);
        foreach (var ch in name)
        {
            builder.Append(invalid.Contains(ch) ? '_' : ch);
        }

        var result = builder.ToString().Trim().TrimEnd('.');
        return result.Length == 0 ? "output" : result;
    }
}
