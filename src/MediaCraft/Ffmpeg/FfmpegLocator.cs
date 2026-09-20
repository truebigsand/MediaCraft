using System.IO;
using MediaCraft.Logging;

namespace MediaCraft.Ffmpeg;

/// <summary>已解析出的 ffmpeg / ffprobe 路径及来源说明。</summary>
public sealed class FfmpegPaths
{
    public FfmpegPaths(string ffmpeg, string ffprobe, string source, string version)
    {
        Ffmpeg = ffmpeg;
        Ffprobe = ffprobe;
        Source = source;
        Version = version;
    }

    /// <summary>ffmpeg.exe 完整路径。</summary>
    public string Ffmpeg { get; }

    /// <summary>ffprobe.exe 完整路径。</summary>
    public string Ffprobe { get; }

    /// <summary>来源说明（设置 / PATH / 常见安装位置）。</summary>
    public string Source { get; }

    /// <summary>`ffmpeg -version` 首行。</summary>
    public string Version { get; }

    public override string ToString() => $"{Ffmpeg}（{Source}）";
}

/// <summary>
/// 定位 ffmpeg / ffprobe：优先用户配置，其次 PATH，最后扫常见安装位置。
/// </summary>
public static class FfmpegLocator
{
    private const string FfmpegExe = "ffmpeg.exe";
    private const string FfprobeExe = "ffprobe.exe";

    /// <summary>
    /// 解析可用的 ffmpeg / ffprobe。解析失败返回 null。
    /// </summary>
    public static async Task<FfmpegPaths?> ResolveAsync(
        string? configuredFfmpeg,
        string? configuredFfprobe,
        CancellationToken cancellationToken = default)
    {
        // 1) 用户配置
        if (!string.IsNullOrWhiteSpace(configuredFfmpeg) && File.Exists(configuredFfmpeg))
        {
            var probe = ResolveFfprobeBeside(configuredFfmpeg, configuredFfprobe);
            if (probe is not null)
            {
                var version = await ReadVersionAsync(configuredFfmpeg, cancellationToken).ConfigureAwait(false);
                if (version is not null)
                {
                    return new FfmpegPaths(configuredFfmpeg, probe, "设置中指定", version);
                }
            }
        }

        // 2) PATH
        var fromPath = FindOnPath(FfmpegExe);
        if (fromPath is not null)
        {
            var probe = ResolveFfprobeBeside(fromPath, configuredFfprobe);
            if (probe is not null)
            {
                var version = await ReadVersionAsync(fromPath, cancellationToken).ConfigureAwait(false);
                if (version is not null)
                {
                    return new FfmpegPaths(fromPath, probe, "PATH 环境变量", version);
                }
            }
        }

        // 3) 常见安装位置
        foreach (var candidate in EnumerateCommonCandidates())
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            var probe = ResolveFfprobeBeside(candidate, configuredFfprobe);
            if (probe is null)
            {
                continue;
            }

            var version = await ReadVersionAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (version is not null)
            {
                return new FfmpegPaths(candidate, probe, "常见安装位置", version);
            }
        }

        return null;
    }

    /// <summary>读取 ffmpeg -version 首行；失败返回 null。</summary>
    public static async Task<string?> ReadVersionAsync(string ffmpegPath, CancellationToken cancellationToken)
    {
        try
        {
            var result = await ProcessRunner
                .RunAsync(ffmpegPath, ["-hide_banner", "-version"], cancellationToken, 15000)
                .ConfigureAwait(false);
            if (!result.Succeeded)
            {
                return null;
            }

            var firstLine = result.StandardOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()?.Trim();
            return string.IsNullOrWhiteSpace(firstLine) ? null : firstLine;
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "FfmpegLocator.ReadVersion");
            return null;
        }
    }

    private static string? ResolveFfprobeBeside(string ffmpegPath, string? configuredFfprobe)
    {
        if (!string.IsNullOrWhiteSpace(configuredFfprobe) && File.Exists(configuredFfprobe))
        {
            return configuredFfprobe;
        }

        var directory = Path.GetDirectoryName(ffmpegPath);
        if (string.IsNullOrEmpty(directory))
        {
            return null;
        }

        var sibling = Path.Combine(directory, FfprobeExe);
        return File.Exists(sibling) ? sibling : null;
    }

    private static string? FindOnPath(string exeName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var directory in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var full = Path.Combine(directory.Trim(), exeName);
                if (File.Exists(full))
                {
                    return full;
                }
            }
            catch (Exception)
            {
                // 非法的 PATH 项忽略
            }
        }

        return null;
    }

    /// <summary>
    /// 常见安装位置：Scoop（用户级/全盘 Software\Scoop）、winget、Chocolatey、手工解压目录。
    /// </summary>
    private static IEnumerable<string> EnumerateCommonCandidates()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        yield return Path.Combine(userProfile, "scoop", "shims", FfmpegExe);
        yield return Path.Combine(localAppData, "Microsoft", "WinGet", "Links", FfmpegExe);
        yield return Path.Combine(programData, "chocolatey", "bin", FfmpegExe);
        yield return Path.Combine(userProfile, ".local", "bin", FfmpegExe);
        yield return @"C:\ffmpeg\bin\" + FfmpegExe;

        foreach (var drive in GetReadyDriveRoots())
        {
            yield return Path.Combine(drive, "Software", "Scoop", "shims", FfmpegExe);
            yield return Path.Combine(drive, "scoop", "shims", FfmpegExe);
            yield return Path.Combine(drive, "ffmpeg", "bin", FfmpegExe);
            yield return Path.Combine(drive, "Program Files", "ffmpeg", "bin", FfmpegExe);
        }
    }

    private static IEnumerable<string> GetReadyDriveRoots()
    {
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (Exception)
        {
            yield break;
        }

        foreach (var drive in drives)
        {
            var root = string.Empty;
            try
            {
                if (drive.IsReady && drive.DriveType is DriveType.Fixed or DriveType.Removable && drive.DriveFormat is not "CDFS")
                {
                    root = drive.RootDirectory.FullName;
                }
            }
            catch (Exception)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(root))
            {
                yield return root;
            }
        }
    }
}
