using System.Text.RegularExpressions;
using MediaCraft.Logging;

namespace MediaCraft.Ffmpeg;

/// <summary>
/// ffmpeg 能力探测结果：可用编码器、硬件加速方式，以及「真实能不能跑」的功能探测结论。
/// 预检规则依赖这里的数据，而不是硬编码的文档结论。
/// </summary>
public sealed class FfmpegCapabilities
{
    private static readonly Regex EncoderLine = new(
        @"^\s*[VAS][\S.]{5}\s+(?<name>[A-Za-z0-9_\-]+)\s+(?<desc>.*)$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private readonly HashSet<string> _encoders = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _hwAccels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _functional = new(StringComparer.OrdinalIgnoreCase);

    private FfmpegCapabilities(string version, string configuration, string rawEncoders, string rawHwAccels)
    {
        Version = version;
        Configuration = configuration;
        RawEncoders = rawEncoders;
        RawHwAccels = rawHwAccels;
        MajorVersion = ParseMajorVersion(version);
    }

    /// <summary>`ffmpeg -version` 首行。</summary>
    public string Version { get; }

    /// <summary>主版本号（预检里用于按版本条件触发规则）；解析失败为 0。</summary>
    public int MajorVersion { get; }

    /// <summary>configure 参数行。</summary>
    public string Configuration { get; }

    /// <summary>-encoders 原始输出（诊断用）。</summary>
    public string RawEncoders { get; }

    /// <summary>-hwaccels 原始输出（诊断用）。</summary>
    public string RawHwAccels { get; }

    /// <summary>是否做过「真跑一次」的功能探测。</summary>
    public bool FunctionalProbeDone { get; private set; }

    /// <summary>ffmpeg 编译时包含的编码器名集合。</summary>
    public IReadOnlyCollection<string> Encoders => _encoders;

    /// <summary>可用的硬件加速方式集合。</summary>
    public IReadOnlyCollection<string> HwAccels => _hwAccels;

    /// <summary>功能探测明细（编码器 → 是否真的可用）。</summary>
    public IReadOnlyDictionary<string, bool> FunctionalResults => _functional;

    /// <summary>编码器在本次 ffmpeg 中是否可用（优先使用功能探测结果）。</summary>
    public bool IsEncoderAvailable(string encoderId)
    {
        if (string.IsNullOrWhiteSpace(encoderId))
        {
            return false;
        }

        if (_functional.TryGetValue(encoderId, out var works))
        {
            return works;
        }

        return _encoders.Contains(encoderId);
    }

    /// <summary>功能探测是否给出了「不可用」的结论。</summary>
    public bool IsFunctionallyRejected(string encoderId) =>
        _functional.TryGetValue(encoderId, out var works) && !works;

    public bool HasHwAccel(string name) => _hwAccels.Contains(name);

    /// <summary>读取 -version / -encoders / -hwaccels。</summary>
    public static async Task<FfmpegCapabilities> LoadAsync(FfmpegPaths paths, CancellationToken cancellationToken)
    {
        var versionResult = await ProcessRunner
            .RunAsync(paths.Ffmpeg, ["-hide_banner", "-version"], cancellationToken, 15000)
            .ConfigureAwait(false);

        var lines = versionResult.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var version = lines.FirstOrDefault()?.Trim() ?? paths.Version;
        var configuration = lines.FirstOrDefault(l => l.StartsWith("configuration:", StringComparison.OrdinalIgnoreCase))?.Trim()
            ?? string.Empty;

        var encodersResult = await ProcessRunner
            .RunAsync(paths.Ffmpeg, ["-hide_banner", "-encoders"], cancellationToken, 20000)
            .ConfigureAwait(false);

        var hwResult = await ProcessRunner
            .RunAsync(paths.Ffmpeg, ["-hide_banner", "-hwaccels"], cancellationToken, 15000)
            .ConfigureAwait(false);

        var capabilities = new FfmpegCapabilities(version, configuration, encodersResult.StandardOutput, hwResult.StandardOutput);

        foreach (Match match in EncoderLine.Matches(encodersResult.StandardOutput))
        {
            capabilities._encoders.Add(match.Groups["name"].Value);
        }

        foreach (var line in hwResult.StandardOutput.Split('\n'))
        {
            var name = line.Trim();
            if (name.Length > 0 && !name.StartsWith("Hardware", StringComparison.OrdinalIgnoreCase))
            {
                capabilities._hwAccels.Add(name);
            }
        }

        AppLog.Info(
            $"探测到 {capabilities._encoders.Count} 个编码器、{capabilities._hwAccels.Count} 种硬件加速方式；{version}",
            "FFmpeg");

        return capabilities;
    }

    /// <summary>
    /// 对候选编码器逐个做「真跑一帧」的功能探测。
    /// 编译进去不等于能跑（例如显卡不支持该编码器时调用会失败），所以这一步是必要的。
    /// </summary>
    public async Task ProbeFunctionalAsync(
        FfmpegPaths paths,
        IEnumerable<string> candidateEncoderIds,
        IProgress<(int Done, int Total, string EncoderId)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var candidates = candidateEncoderIds
            .Where(id => _encoders.Contains(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var total = candidates.Length;
        var done = 0;

        // 限制并发：每个探测都要起一个 ffmpeg 进程，同时跑太多会互相抢资源导致误判
        var throttler = new SemaphoreSlim(4);
        var tasks = candidates.Select(async encoderId =>
        {
            await throttler.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var result = await ProcessRunner.RunAsync(
                    paths.Ffmpeg,
                    [
                        "-hide_banner", "-v", "error",
                        "-f", "lavfi", "-i", "nullsrc=s=256x256",
                        "-frames:v", "1",
                        "-c:v", encoderId,
                        "-f", "null", "-",
                    ],
                    cancellationToken,
                    60000).ConfigureAwait(false);

                lock (_functional)
                {
                    _functional[encoderId] = result.Succeeded;
                }

                if (!result.Succeeded)
                {
                    AppLog.Warn($"编码器 {encoderId} 功能探测失败（退出码 {result.ExitCode}）：{FirstLine(result.StandardError)}", "FFmpeg");
                }
            }
            finally
            {
                throttler.Release();
                var current = Interlocked.Increment(ref done);
                progress?.Report((current, total, encoderId));
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        FunctionalProbeDone = true;
        AppLog.Info($"编码器功能探测完成：{_functional.Count(x => x.Value)}/{_functional.Count} 可用", "FFmpeg");
    }

    private static string FirstLine(string text)
    {
        var index = text.IndexOf('\n');
        return (index < 0 ? text : text[..index]).Trim();
    }

    /// <summary>从 "ffmpeg version 8.1.2-full_build-..." 里取主版本号。</summary>
    public static int ParseMajorVersion(string versionLine)
    {
        var match = Regex.Match(versionLine, @"version\s+n?(\d+)\.(\d+)", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out var major) ? major : 0;
    }
}
