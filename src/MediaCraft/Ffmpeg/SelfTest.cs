using System.Globalization;
using System.IO;
using System.Text;
using MediaCraft.Logging;
using MediaCraft.Media;

namespace MediaCraft.Ffmpeg;

/// <summary>
/// 无头自检：用**生产代码路径**把 FFmpeg 层真跑一遍。
///
/// 覆盖：ffmpeg 定位 → 能力探测 → 编码器功能探测 → ffprobe 解析 → 预检 → 命令构造 → 进程执行 → ffprobe 校验。
/// 用途：开发期阶段验证、交付期验收证据、用户排障（换一台机器跑一次就知道哪些编码器能用）。
/// </summary>
public static class SelfTest
{
    /// <summary>一项校验的预期。</summary>
    private sealed class Expectation
    {
        public string? CodecName { get; init; }

        public int? Width { get; init; }

        public int? Height { get; init; }

        public double? DurationSeconds { get; init; }

        public string? PixelFormat { get; init; }

        public string? Profile { get; init; }

        /// <summary>预期预检会拦下（不执行）。</summary>
        public bool ExpectPreflightBlocked { get; init; }

        /// <summary>预期产出的字幕文件。</summary>
        public bool ExpectSubtitleSidecar { get; init; }

        /// <summary>预期输出里有音频流。</summary>
        public bool? ExpectAudio { get; init; }

        /// <summary>预期音频编码（ffprobe 的 codec_name）。</summary>
        public string? ExpectAudioCodec { get; init; }

        /// <summary>预期音频采样率（Hz）。</summary>
        public int? ExpectAudioSampleRate { get; init; }

        /// <summary>预期音频声道数。</summary>
        public int? ExpectAudioChannels { get; init; }

        /// <summary>预期输出里的音轨条数（验证容器对多音轨的支持）。</summary>
        public int? ExpectAudioStreamCount { get; init; }

        /// <summary>预期计划里的可见步骤数（用于验证两遍编码确实拆成了两步）。</summary>
        public int? ExpectStepCount { get; init; }

        /// <summary>主步骤命令行里必须出现的参数片段。</summary>
        public string[] RequireArguments { get; init; } = [];

        /// <summary>主步骤命令行里必须**不**出现的参数片段。</summary>
        public string[] ForbidArguments { get; init; } = [];

        /// <summary>预期预检修正后的音频码率（校验修正结果，而不是产出文件的平均码率）。</summary>
        public int? ExpectEffectiveAudioBitrateKbps { get; init; }

        public string? ContainerExtension { get; init; }
    }

    private sealed class CaseResult
    {
        public required string Name { get; init; }

        public bool Passed { get; set; }

        public List<string> Details { get; } = [];

        public List<string> Failures { get; } = [];

        public CaseResult WithDetail(string detail)
        {
            Details.Add(detail);
            return this;
        }

        public CaseResult WithFailure(string failure)
        {
            Failures.Add(failure);
            Passed = false;
            return this;
        }
    }

    private sealed class Context
    {
        public required string WorkDirectory { get; init; }

        public required string OutputDirectory { get; init; }

        public required string TempDirectory { get; init; }

        public required string Mode { get; init; }

        public required string SamplePath { get; init; }

        public required string SampleWithSubtitlePath { get; init; }

        public required string ExternalSubtitlePath { get; init; }

        public required string SubtitleOnlyPath { get; init; }

        /// <summary>三音轨素材（用于验证容器对多音轨的支持差异）。</summary>
        public required string ThreeAudioPath { get; init; }

        /// <summary>opus 音轨素材（用于验证编码名归一化，避免把可直通的音轨误判为不兼容）。</summary>
        public required string OpusAudioPath { get; init; }

        public required FfmpegPaths Paths { get; init; }

        public required FfmpegCapabilities Capabilities { get; init; }
    }

    public static async Task<int> RunAsync(string[] args)
    {
        var mode = args.Length > 1 && !args[1].StartsWith('-') ? args[1] : "all";
        var reportPath = args.Length > 2
            ? args[2]
            : Path.Combine(Path.GetTempPath(), "MediaCraft", $"selftest-{DateTime.Now:yyyyMMdd-HHmmss}.txt");

        var report = new StringBuilder();
        var results = new List<CaseResult>();
        var failures = new List<string>();

        void Write(string line)
        {
            report.AppendLine(line);
            Console.WriteLine(line);
        }

        Write("======================================================================");
        Write($"MediaCraft 自检报告   {DateTime.Now:yyyy-MM-dd HH:mm:ss}   模式：{mode}");
        Write("======================================================================");

        // ── 0. 纯逻辑（不依赖 ffmpeg，也不碰用户的预设/设置文件）──
        Write(string.Empty);
        Write("[0] 预设与参数逻辑（纯逻辑）");
        foreach (var result in RunPresetTests())
        {
            results.Add(result);
            Write($"  {(result.Passed ? "✓ PASS" : "✗ FAIL")}  {result.Name}");
            foreach (var detail in result.Details)
            {
                Write($"          {detail}");
            }

            foreach (var failure in result.Failures)
            {
                Write($"          ! {failure}");
            }
        }

        // logic 模式：只跑纯逻辑，用于 CI 这类没有 ffmpeg 的环境
        if (string.Equals(mode, "logic", StringComparison.OrdinalIgnoreCase))
        {
            var logicPassed = results.Count(r => r.Passed);
            Write(string.Empty);
            Write("======================================================================");
            Write($"结果：{logicPassed}/{results.Count} 通过（logic 模式：不检查 ffmpeg）");
            Write("======================================================================");
            WriteReport(reportPath, report);
            Console.WriteLine($"报告已写入：{reportPath}");
            return logicPassed == results.Count && results.Count > 0 ? 0 : 1;
        }

        try
        {
            // ── 1. 定位 ffmpeg ──
            Write(string.Empty);
            Write("[1] 定位 ffmpeg / ffprobe");
            var paths = await FfmpegLocator.ResolveAsync(null, null).ConfigureAwait(false);
            if (paths is null)
            {
                Write("  ✗ 未找到 ffmpeg。请安装 ffmpeg 并在设置里指定路径。");
                WriteReport(reportPath, report);
                return 1;
            }

            Write($"  ffmpeg : {paths.Ffmpeg}");
            Write($"  ffprobe: {paths.Ffprobe}");
            Write($"  来源   : {paths.Source}");
            Write($"  版本   : {paths.Version}");

            // ── 2. 能力探测 ──
            Write(string.Empty);
            Write("[2] 能力探测（-encoders / -hwaccels）");
            var capabilities = await FfmpegCapabilities.LoadAsync(paths, CancellationToken.None).ConfigureAwait(false);
            Write($"  主版本 : {capabilities.MajorVersion}");
            Write($"  编码器 : {capabilities.Encoders.Count} 个");
            Write($"  硬加速 : {string.Join(", ", capabilities.HwAccels)}");

            var locateCase = new CaseResult { Name = "ffmpeg 定位与能力探测" };
            locateCase.Details.Add($"路径：{paths.Ffmpeg}");
            locateCase.Details.Add($"来源：{paths.Source}");
            locateCase.Details.Add($"版本：{paths.Version}");
            locateCase.Details.Add($"编码器 {capabilities.Encoders.Count} 个 / 硬件加速 {capabilities.HwAccels.Count} 种 / 主版本 {capabilities.MajorVersion}");
            if (string.IsNullOrWhiteSpace(paths.Version) || capabilities.Encoders.Count == 0)
            {
                locateCase.Failures.Add("版本信息或编码器列表为空");
            }

            if (capabilities.HwAccels.Count == 0)
            {
                locateCase.Failures.Add("没有探测到任何硬件加速方式（不影响软编，但硬解/硬编会不可用）");
            }

            locateCase.Passed = locateCase.Failures.Count == 0;
            results.Add(locateCase);

            // ── 3. 编码器功能探测（真跑一帧）──
            Write(string.Empty);
            Write("[3] 编码器功能探测（真跑一帧，编译进去 ≠ 能跑）");
            await capabilities.ProbeFunctionalAsync(
                paths,
                EncoderCatalog.All.Select(e => e.Id),
                null,
                CancellationToken.None).ConfigureAwait(false);

            foreach (var encoder in EncoderCatalog.All)
            {
                var mark = capabilities.IsEncoderAvailable(encoder.Id) ? "✓" : "✗";
                Write($"  {mark} {encoder.Id,-14} {encoder.DisplayName}");
            }

            var encoderCase = new CaseResult { Name = "编码器功能探测" };
            var availableEncoders = EncoderCatalog.All.Where(e => capabilities.IsEncoderAvailable(e.Id)).ToArray();
            var rejectedEncoders = EncoderCatalog.All.Where(e => capabilities.IsFunctionallyRejected(e.Id)).ToArray();
            encoderCase.Details.Add($"可用 {availableEncoders.Length}/{EncoderCatalog.All.Count}：{string.Join("、", availableEncoders.Select(e => e.Id))}");
            if (rejectedEncoders.Length > 0)
            {
                encoderCase.Details.Add($"功能探测失败：{string.Join("、", rejectedEncoders.Select(e => e.Id))}");
            }

            if (availableEncoders.Length == 0)
            {
                encoderCase.Failures.Add("没有任何可用的视频编码器");
            }

            if (availableEncoders.All(e => e.IsHardware))
            {
                encoderCase.Failures.Add("只有硬件编码器可用，一个软件编码器都没探测到（ffmpeg 可能不完整）");
            }

            encoderCase.Passed = encoderCase.Failures.Count == 0;
            results.Add(encoderCase);

            // ── 4. 生成测试素材 ──
            Write(string.Empty);
            Write("[4] 生成测试素材");
            var workDirectory = Path.Combine(Path.GetTempPath(), "MediaCraft", "selftest");
            var outputDirectory = Path.Combine(workDirectory, "out");
            var tempDirectory = Path.Combine(workDirectory, "tmp");
            ResetDirectory(workDirectory);
            Directory.CreateDirectory(outputDirectory);
            Directory.CreateDirectory(tempDirectory);

            var samplePath = Path.Combine(workDirectory, "sample-720p.mp4");
            var chineseDirectory = Path.Combine(workDirectory, "带 空格 的中文 目录");
            Directory.CreateDirectory(chineseDirectory);
            var sampleWithSubtitlePath = Path.Combine(chineseDirectory, "有字幕.mkv");
            var externalSubtitlePath = Path.Combine(chineseDirectory, "外挂 字幕.srt");
            var subtitleOnlyPath = Path.Combine(workDirectory, "待转换.srt");
            var threeAudioPath = Path.Combine(workDirectory, "sample-3audio.mkv");
            var opusAudioPath = Path.Combine(workDirectory, "sample-opus.mkv");

            var sampleOk = await FfmpegAsync(paths,
                "-y", "-v", "error",
                "-f", "lavfi", "-i", "testsrc2=s=1280x720:r=30",
                "-f", "lavfi", "-i", "sine=f=440",
                "-t", "5",
                "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p",
                "-c:a", "aac", "-b:a", "128k",
                samplePath).ConfigureAwait(false);
            Write($"  sample-720p.mp4（1280x720 30fps 5s + AAC）: {(sampleOk.Ok ? "✓" : "✗ " + FirstLine(sampleOk.Output))}");

            var subtitleText =
                "1\n" +
                "00:00:00,500 --> 00:00:02,500\n" +
                "第一行字幕 · MediaCraft\n" +
                "\n" +
                "2\n" +
                "00:00:02,600 --> 00:00:04,800\n" +
                "Second line with 中文\n";
            await File.WriteAllTextAsync(externalSubtitlePath, subtitleText, new UTF8Encoding(false)).ConfigureAwait(false);
            await File.WriteAllTextAsync(subtitleOnlyPath, subtitleText, new UTF8Encoding(false)).ConfigureAwait(false);
            Write($"  {Path.GetFileName(externalSubtitlePath)}（中文+空格路径）: ✓");

            var muxOk = await FfmpegAsync(paths,
                "-y", "-v", "error",
                "-i", samplePath,
                "-i", externalSubtitlePath,
                "-map", "0", "-map", "1",
                "-c", "copy", "-c:s", "srt",
                sampleWithSubtitlePath).ConfigureAwait(false);
            Write($"  有字幕.mkv（内封 srt 字幕轨）: {(muxOk.Ok ? "✓" : "✗ " + FirstLine(muxOk.Output))}");

            var multiAudioOk = await FfmpegAsync(paths,
                "-y", "-v", "error",
                "-f", "lavfi", "-i", "testsrc2=s=640x360:r=30",
                "-f", "lavfi", "-i", "sine=f=440",
                "-f", "lavfi", "-i", "sine=f=550",
                "-f", "lavfi", "-i", "sine=f=660",
                "-t", "3",
                "-map", "0:v", "-map", "1:a", "-map", "2:a", "-map", "3:a",
                "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p",
                "-c:a", "aac",
                threeAudioPath).ConfigureAwait(false);
            Write($"  sample-3audio.mkv（1 视频 + 3 条音轨）: {(multiAudioOk.Ok ? "✓" : "✗ " + FirstLine(multiAudioOk.Output))}");

            var opusOk = await FfmpegAsync(paths,
                "-y", "-v", "error",
                "-f", "lavfi", "-i", "testsrc2=s=640x360:r=30",
                "-f", "lavfi", "-i", "sine=f=440",
                "-t", "3",
                "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p",
                "-c:a", "libopus",
                opusAudioPath).ConfigureAwait(false);
            Write($"  sample-opus.mkv（视频 + opus 音轨）: {(opusOk.Ok ? "✓" : "✗ " + FirstLine(opusOk.Output))}");

            if (!sampleOk.Ok || !muxOk.Ok)
            {
                Write("  ✗ 素材生成失败，终止。");
                results.Add(new CaseResult
                {
                    Name = "测试素材生成",
                    Passed = false,
                }.WithFailure($"测试片生成失败：{FirstLine(sampleOk.Ok ? muxOk.Output : sampleOk.Output)}"));
                WriteReport(reportPath, report);
                return 1;
            }

            results.Add(new CaseResult
            {
                Name = "测试素材生成",
                Passed = true,
            }.WithDetail("1280x720 30fps 5s + AAC 测试片与内封字幕 MKV 生成成功"));

            var context = new Context
            {
                WorkDirectory = workDirectory,
                OutputDirectory = outputDirectory,
                TempDirectory = tempDirectory,
                Mode = mode,
                SamplePath = samplePath,
                SampleWithSubtitlePath = sampleWithSubtitlePath,
                ExternalSubtitlePath = externalSubtitlePath,
                SubtitleOnlyPath = subtitleOnlyPath,
                ThreeAudioPath = threeAudioPath,
                OpusAudioPath = opusAudioPath,
                Paths = paths,
                Capabilities = capabilities,
            };

            // ── 5. 用例矩阵（快速模式跳过）──
            if (string.Equals(mode, "quick", StringComparison.OrdinalIgnoreCase))
            {
                Write(string.Empty);
                Write("[5] 转码矩阵：快速模式，已跳过（用 --selftest all 跑完整矩阵）");
            }
            else
            {
                // ── 4b. 容器兼容性矩阵：用真实 ffmpeg 逐个探测，校验 EncoderCatalog 里的表 ──
                Write(string.Empty);
                Write("[4b] 容器兼容性矩阵校验（逐个组合真跑一次）");
                var matrixCase = await VerifyContainerMatrixAsync(paths, workDirectory).ConfigureAwait(false);
                results.Add(matrixCase);
                Write($"  {(matrixCase.Passed ? "✓ PASS" : "✗ FAIL")}  {matrixCase.Name}");
                foreach (var detail in matrixCase.Details)
                {
                    Write($"          {detail}");
                }

                foreach (var failure in matrixCase.Failures)
                {
                    Write($"          ! {failure}");
                }

                Write(string.Empty);
                Write("[4c] 两遍编码支持情况校验（逐编码器看统计文件是否真有内容）");
                var twoPassCase = await VerifyTwoPassSupportAsync(paths, workDirectory).ConfigureAwait(false);
                results.Add(twoPassCase);
                Write($"  {(twoPassCase.Passed ? "✓ PASS" : "✗ FAIL")}  {twoPassCase.Name}");
                foreach (var detail in twoPassCase.Details)
                {
                    Write($"          {detail}");
                }

                foreach (var failure in twoPassCase.Failures)
                {
                    Write($"          ! {failure}");
                }

                Write(string.Empty);
                Write("[5] 转码矩阵（每项都真跑，并用 ffprobe 校验产出）");

                var definitions = BuildCases(context);
                foreach (var (name, sourcePath, parameters, expectation) in definitions)
                {
                    var result = await RunCaseAsync(name, sourcePath, parameters, expectation, context).ConfigureAwait(false);
                    results.Add(result);
                    var mark = result.Passed ? "✓ PASS" : "✗ FAIL";
                    Write($"  {mark}  {name}");
                    foreach (var detail in result.Details)
                    {
                        Write($"          {detail}");
                    }

                    foreach (var failure in result.Failures)
                    {
                        Write($"          ! {failure}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "SelfTest");
            Write($"  ✗ 自检异常：{ex}");
            failures.Add(ex.Message);
        }

        // ── 6. 汇总 ──
        var passed = results.Count(r => r.Passed);
        Write(string.Empty);
        Write("======================================================================");
        Write($"结果：{passed}/{results.Count} 通过");
        if (passed != results.Count || failures.Count > 0)
        {
            Write("失败项：");
            foreach (var result in results.Where(r => !r.Passed))
            {
                Write($"  - {result.Name}：{string.Join("；", result.Failures)}");
            }
        }

        Write("======================================================================");
        WriteReport(reportPath, report);
        Console.WriteLine($"报告已写入：{reportPath}");
        return passed == results.Count && results.Count > 0 ? 0 : 1;
    }

    /// <summary>
    /// 用真实 ffmpeg 逐个探测「容器 × 编码」组合，校验 <see cref="EncoderCatalog"/> 里的兼容性表。
    ///
    /// 为什么必须有这条：这张表曾经是手写的，把 `pcm_s24le` 在 MP4 里判成非法，
    /// 于是预检把用户的**无损 PCM 直通强行改成有损 AAC 192k**——用户既丢了画质又无法阻止，
    /// 比直接报错还糟。表必须由实测守住，而不是靠记忆和推测。
    /// </summary>
    private static async Task<CaseResult> VerifyContainerMatrixAsync(FfmpegPaths paths, string workDirectory)
    {
        var result = new CaseResult { Name = "容器兼容性矩阵与 ffmpeg 实测一致" };
        var probeDirectory = Path.Combine(workDirectory, "matrix");
        Directory.CreateDirectory(probeDirectory);

        var subtitleFile = Path.Combine(probeDirectory, "probe.srt");
        var subtitleText = "1" + Environment.NewLine +
            "00:00:00,100 --> 00:00:00,200" + Environment.NewLine +
            "test" + Environment.NewLine;
        await File.WriteAllTextAsync(subtitleFile, subtitleText).ConfigureAwait(false);

        // 兼容性表里写的是「编码格式」，探测要用具体编码器
        var videoEncoders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["h264"] = "libx264",
            ["hevc"] = "libx265",
            ["av1"] = "libsvtav1",
            ["vp9"] = "libvpx-vp9",
            ["mpeg4"] = "mpeg4",
        };

        // 注意两套命名：表里用 ffprobe 的 codec_name（运行时拿 track.SourceCodec 比对），
        // 探测要用 ffmpeg 的编码器名。srt 与 subrip 是同一个东西的两种叫法。
        var subtitleCodecs = new[]
        {
            (TableName: "subrip", Encoder: "srt"),
            (TableName: "ass", Encoder: "ass"),
            (TableName: "webvtt", Encoder: "webvtt"),
            (TableName: "mov_text", Encoder: "mov_text"),
        };

        var probes = new List<(string Label, bool Claimed, string[] Arguments)>();

        foreach (var container in EncoderCatalog.Containers)
        {
            var muxer = MuxerName(container.Extension);

            if (container.VideoCapable)
            {
                foreach (var (codec, encoder) in videoEncoders)
                {
                    probes.Add((
                        $"视频 {codec} → {container.Extension}",
                        EncoderCatalog.IsVideoCodecCompatible(codec, container),
                        [
                            "-y", "-v", "error",
                            "-f", "lavfi", "-i", "testsrc2=s=128x128:r=5", "-t", "0.2",
                            "-c:v", encoder,
                            "-f", muxer, Path.Combine(probeDirectory, $"v-{container.Extension}"),
                        ]));
                }
            }

            foreach (var codec in EncoderCatalog.AudioCodecs)
            {
                probes.Add((
                    $"音频 {codec.Id} → {container.Extension}",
                    EncoderCatalog.IsAudioCodecCompatible(codec.Id, container),
                    [
                        "-y", "-v", "error",
                        "-f", "lavfi", "-i", "anullsrc=r=44100:cl=stereo", "-t", "0.2",
                        "-c:a", codec.Id,
                        "-f", muxer, Path.Combine(probeDirectory, $"a-{container.Extension}"),
                    ]));
            }

            // 多音轨能力：实测 mp3 / flac / wav 只接受单条音轨，多条会直接写入失败。
            // 用容器自己支持的第一个音频编码器来探测，确保失败只可能来自「条数」而不是「编码器」。
            // 白名单里是规范名（ffprobe 的 codec_name），探测要换成 ffmpeg 的编码器名：
            // 直接传 "opus" 会命中实验性的原生编码器并报「experimental codecs are not enabled」。
            var multiCodec = EncoderCatalog.AudioEncoderIdFor(container.AudioCodecs.FirstOrDefault() ?? "aac");
            probes.Add((
                $"多音轨（3 条）→ {container.Extension}",
                container.MaxAudioStreams != 1,
                [
                    "-y", "-v", "error",
                    "-f", "lavfi", "-i", "sine=f=440",
                    "-f", "lavfi", "-i", "sine=f=550",
                    "-f", "lavfi", "-i", "sine=f=660",
                    "-t", "0.2",
                    "-map", "0:a", "-map", "1:a", "-map", "2:a",
                    "-c:a", multiCodec,
                    "-f", muxer, Path.Combine(probeDirectory, $"m-{container.Extension}"),
                ]));

            if (container.SubtitleCodecs.Length == 0)
            {
                continue;
            }

            foreach (var codec in subtitleCodecs)
            {
                // 断言只看**显式列出**的编码器；表里的 "copy" 是策略（原样内封）而不是某个编码器，无法逐个探测
                probes.Add((
                    $"字幕 {codec.TableName}（编码器 {codec.Encoder}）→ {container.Extension}",
                    container.SubtitleCodecs.Contains(codec.TableName, StringComparer.OrdinalIgnoreCase),
                    [
                        "-y", "-v", "error",
                        "-i", subtitleFile,
                        "-c:s", codec.Encoder,
                        "-f", muxer, Path.Combine(probeDirectory, $"s-{container.Extension}"),
                    ]));
            }
        }

        var mismatches = new List<string>();
        var gate = new SemaphoreSlim(4);
        var agreements = 0;

        var tasks = probes.Select(async probe =>
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var run = await ProcessRunner
                    .RunAsync(paths.Ffmpeg, probe.Arguments, CancellationToken.None, 60000)
                    .ConfigureAwait(false);

                var measured = run.Succeeded;
                if (measured == probe.Claimed)
                {
                    Interlocked.Increment(ref agreements);
                    return;
                }

                var reason = measured ? "ffmpeg 实际支持，但表里没有" : "表里说支持，但 ffmpeg 拒绝";
                var detail = measured ? string.Empty : FirstLine(run.StandardError);
                lock (mismatches)
                {
                    mismatches.Add($"{probe.Label}：{reason}" + (detail.Length > 0 ? $"（{detail}）" : string.Empty));
                }
            }
            finally
            {
                gate.Release();
                TryDeleteFirstOutput(probe.Arguments);
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);

        result.Details.Add($"共探测 {probes.Count} 个「容器 × 编码」组合，与表一致 {agreements} 个");
        if (mismatches.Count > 0)
        {
            foreach (var mismatch in mismatches.Take(20))
            {
                result.Failures.Add(mismatch);
            }

            if (mismatches.Count > 20)
            {
                result.Failures.Add($"……另有 {mismatches.Count - 20} 项不一致未列出");
            }
        }

        result.Passed = result.Failures.Count == 0;
        return result;
    }

    /// <summary>扩展名 → ffmpeg 复用器名。</summary>
    private static string MuxerName(string extension) => extension switch
    {
        "mp4" => "mp4",
        "mkv" => "matroska",
        "mov" => "mov",
        "webm" => "webm",
        "m4a" => "ipod",
        "mp3" => "mp3",
        "opus" => "ogg",
        "flac" => "flac",
        "wav" => "wav",
        _ => extension,
    };

    /// <summary>删掉探测输出的临时文件（参数数组的最后一项就是输出路径）。</summary>
    private static void TryDeleteFirstOutput(string[] arguments)
    {
        if (arguments.Length == 0)
        {
            return;
        }

        TryDelete(arguments[^1]);
    }

    /// <summary>
    /// 预设与参数的纯逻辑测试：不执行 ffmpeg，也不读写用户的预设文件。
    /// 覆盖：内置预设完整性、轨道意图归纳、意图套用、JSON 三种结构解析、导出解析往返一致。
    /// </summary>
    private static List<CaseResult> RunPresetTests()
    {
        var results = new List<CaseResult>();

        // ── 1. 内置预设完整性 ──
        var builtInCase = new CaseResult { Name = "预设 · 内置预设清单" };
        var builtIns = Presets.PresetStore.BuiltInPresets;
        builtInCase.Details.Add($"内置预设 {builtIns.Count} 个");

        if (builtIns.Count < 12)
        {
            builtInCase.Failures.Add($"内置预设数量偏少：{builtIns.Count}");
        }

        var duplicateNames = builtIns.GroupBy(p => p.Name).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
        if (duplicateNames.Length > 0)
        {
            builtInCase.Failures.Add("内置预设重名：" + string.Join("、", duplicateNames));
        }

        foreach (var preset in builtIns)
        {
            if (string.IsNullOrWhiteSpace(preset.Name) || string.IsNullOrWhiteSpace(preset.Summary))
            {
                builtInCase.Failures.Add($"预设「{preset.Name}」缺少名称或摘要");
            }

            // 预设引用的编码器必须真实存在（防手抖写错 id）
            if (!EncoderCatalog.All.Any(e => e.Id == preset.Parameters.EncoderId))
            {
                builtInCase.Failures.Add($"预设「{preset.Name}」引用了不存在的编码器 {preset.Parameters.EncoderId}");
            }

            if (!EncoderCatalog.Containers.Any(c => c.Extension == preset.Parameters.Container))
            {
                builtInCase.Failures.Add($"预设「{preset.Name}」引用了不存在的容器 {preset.Parameters.Container}");
            }
        }

        builtInCase.Details.Add("示例：" + string.Join(" / ", builtIns.Take(4).Select(p => $"{p.Name}（{p.Summary}）")));
        builtInCase.Passed = builtInCase.Failures.Count == 0;
        results.Add(builtInCase);

        // ── 2. 从参数归纳轨道意图 ──
        var intentCase = new CaseResult { Name = "预设 · 从参数归纳轨道意图" };
        var burnParams = new TranscodeParams();
        burnParams.SubtitleTracks.Add(new SubtitleTrackParams(2, "字幕 #2", "subrip", false) { Action = SubtitleActionKind.Copy });
        burnParams.SubtitleTracks.Add(new SubtitleTrackParams(3, "字幕 #3", "subrip", false) { Action = SubtitleActionKind.Burn });
        burnParams.AudioTracks.Add(new AudioTrackParams(1, "音频 #1", "aac", 2, "chi") { Action = AudioActionKind.Encode, CodecId = "aac", BitRateKbps = 192 });

        var burnIntent = Presets.PresetTrackIntent.FromParams(burnParams);
        intentCase.Details.Add($"烧入意图：{burnIntent.Description}");
        if (burnIntent.SubtitleAction != SubtitleActionKind.Burn || !burnIntent.SubtitleFirstOnly)
        {
            intentCase.Failures.Add("混合字幕动作未被归纳为「烧入第一条」");
        }

        if (burnIntent.AudioAction != AudioActionKind.Encode || burnIntent.AudioCodecId != "aac" || burnIntent.AudioBitRateKbps != 192)
        {
            intentCase.Failures.Add("音轨重编码意图没有正确带出编码器与码率");
        }

        var mixedParams = new TranscodeParams();
        mixedParams.AudioTracks.Add(new AudioTrackParams(1, "a", "aac", 2, "") { Action = AudioActionKind.Copy });
        mixedParams.AudioTracks.Add(new AudioTrackParams(2, "b", "aac", 2, "") { Action = AudioActionKind.Drop });
        var mixedIntent = Presets.PresetTrackIntent.FromParams(mixedParams);
        intentCase.Details.Add($"混合音轨动作：{mixedIntent.Description}");
        if (mixedIntent.AudioAction != AudioActionKind.Copy)
        {
            intentCase.Failures.Add("混合音轨动作应退化为「直通」");
        }

        intentCase.Passed = intentCase.Failures.Count == 0;
        results.Add(intentCase);

        // ── 3. 套用预设：标量 + 轨道意图 ──
        var applyCase = new CaseResult { Name = "预设 · 套用到目标参数（标量 + 轨道意图）" };
        var burnPreset = builtIns.FirstOrDefault(p => p.Name.Contains("烧入第一条字幕", StringComparison.Ordinal));
        if (burnPreset is null)
        {
            applyCase.Failures.Add("找不到「烧入第一条字幕」内置预设");
        }
        else
        {
            var target = new TranscodeParams();
            target.SubtitleTracks.Add(new SubtitleTrackParams(2, "字幕 #2", "subrip", false));
            target.SubtitleTracks.Add(new SubtitleTrackParams(5, "字幕 #5", "subrip", false));
            target.SubtitleTracks.Add(new SubtitleTrackParams(7, "字幕 #7", "subrip", false));
            target.AudioTracks.Add(new AudioTrackParams(1, "音频 #1", "aac", 2, ""));
            target.AudioTracks.Add(new AudioTrackParams(3, "音频 #3", "ac3", 6, ""));
            target.EncoderId = "libx265";
            target.Container = "webm";

            Presets.PresetStore.ApplyTo(burnPreset, target);

            applyCase.Details.Add($"编码器 {target.EncoderId} / 容器 {target.Container} / 质量 {target.QualitySlider}");
            applyCase.Details.Add("字幕动作：" + string.Join("、", target.SubtitleTracks.Select(t => $"{t.StreamIndex}→{t.Action}")));
            applyCase.Details.Add("音轨动作：" + string.Join("、", target.AudioTracks.Select(t => $"{t.StreamIndex}→{t.Action}")));

            if (target.EncoderId != "h264_nvenc" || target.Container != "mp4")
            {
                applyCase.Failures.Add("标量参数没有被预设覆盖");
            }

            if (target.SubtitleTracks[0].Action != SubtitleActionKind.Burn)
            {
                applyCase.Failures.Add("第一条字幕轨应设为烧入");
            }

            if (target.SubtitleTracks.Skip(1).Any(t => t.Action != SubtitleActionKind.Copy))
            {
                applyCase.Failures.Add("其余字幕轨应保持内封（FirstOnly 语义）");
            }

            if (target.AudioTracks.Any(t => t.Action != AudioActionKind.Copy))
            {
                applyCase.Failures.Add("音轨意图应为全部直通");
            }

            if (target.SubtitleTracks.Count != 3 || target.AudioTracks.Count != 2)
            {
                applyCase.Failures.Add("套用预设不应改动目标的轨道数量");
            }
        }

        applyCase.Passed = applyCase.Failures.Count == 0;
        results.Add(applyCase);

        // ── 4. JSON 三种结构解析 + 导出往返 ──
        var jsonCase = new CaseResult { Name = "预设 · JSON 解析与导出往返" };
        var sample = builtIns[0].Clone();
        sample.IsBuiltIn = false;
        sample.Name = "往返测试预设";

        var wrapper = System.Text.Json.JsonSerializer.Serialize(
            new Presets.PresetFile { Presets = [sample] },
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } });
        var arrayJson = System.Text.Json.JsonSerializer.Serialize(new[] { sample },
            new System.Text.Json.JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } });
        var singleJson = System.Text.Json.JsonSerializer.Serialize(sample,
            new System.Text.Json.JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } });

        foreach (var (label, json) in new[] { ("包装结构", wrapper), ("数组结构", arrayJson), ("单对象结构", singleJson) })
        {
            try
            {
                var parsed = Presets.PresetStore.Parse(json);
                jsonCase.Details.Add($"{label}：解析出 {parsed.Count} 个预设");
                if (parsed.Count != 1)
                {
                    jsonCase.Failures.Add($"{label} 应解析出 1 个预设，实际 {parsed.Count}");
                }
                else
                {
                    var roundTrip = parsed[0];
                    if (roundTrip.Name != sample.Name || roundTrip.Parameters.EncoderId != sample.Parameters.EncoderId ||
                        roundTrip.Parameters.QualitySlider != sample.Parameters.QualitySlider ||
                        roundTrip.Intent.AudioAction != sample.Intent.AudioAction)
                    {
                        jsonCase.Failures.Add($"{label} 往返后字段不一致");
                    }
                }
            }
            catch (Exception ex)
            {
                jsonCase.Failures.Add($"{label} 解析异常：{ex.Message}");
            }
        }

        try
        {
            var parsed = Presets.PresetStore.Parse("{ this is not json }");
            jsonCase.Details.Add($"非法内容解析出 {parsed.Count} 个预设（不抛异常）");
            if (parsed.Count != 0)
            {
                jsonCase.Failures.Add("非法 JSON 应返回 0 个预设");
            }
        }
        catch (Exception)
        {
            // 抛异常也算合理处理
            jsonCase.Details.Add("非法内容按预期抛出异常");
        }

        jsonCase.Passed = jsonCase.Failures.Count == 0;
        results.Add(jsonCase);

        // ── 5. 参数克隆独立性（每文件独立参数的基础）──
        var cloneCase = new CaseResult { Name = "参数 · 克隆独立性" };
        var original = new TranscodeParams { EncoderId = "libx264", QualitySlider = 60 };
        original.AudioTracks.Add(new AudioTrackParams(1, "音频 #1", "aac", 2, "chi"));
        original.SubtitleTracks.Add(new SubtitleTrackParams(2, "字幕 #2", "subrip", false));

        var clone = original.Clone();
        clone.EncoderId = "av1_nvenc";
        clone.QualitySlider = 90;
        clone.AudioTracks[0].Action = AudioActionKind.Encode;
        clone.AudioTracks[0].CodecId = "libopus";
        clone.SubtitleStyle.FontSize = 44;
        clone.AudioTracks.Add(new AudioTrackParams(9, "音频 #9", "ac3", 6, ""));

        cloneCase.Details.Add($"原对象：{original.EncoderId} / 质量 {original.QualitySlider} / 音轨 {original.AudioTracks.Count} 条 / 音轨动作 {original.AudioTracks[0].Action} / 字号 {original.SubtitleStyle.FontSize}");
        cloneCase.Details.Add($"克隆后：{clone.EncoderId} / 质量 {clone.QualitySlider} / 音轨 {clone.AudioTracks.Count} 条 / 音轨动作 {clone.AudioTracks[0].Action} / 字号 {clone.SubtitleStyle.FontSize}");

        if (original.EncoderId != "libx264" || original.QualitySlider != 60 ||
            original.AudioTracks.Count != 1 || original.AudioTracks[0].Action != AudioActionKind.Copy ||
            original.SubtitleStyle.FontSize != 24)
        {
            cloneCase.Failures.Add("克隆后修改影响了原对象（深拷贝不彻底）");
        }

        cloneCase.Passed = cloneCase.Failures.Count == 0;
        results.Add(cloneCase);

        // ── 6. 标量同步与轨道动作搬运（「改参数同步到全部文件」与「应用到全部」的底层逻辑）──
        // 这条锁的是一个真踩过的 bug：按目标文件重建轨道列表时，把当前文件调好的轨道动作
        // （例如「烧入字幕」）打回了默认「直通」。
        var syncCase = new CaseResult { Name = "参数 · 标量同步与轨道动作搬运" };

        var syncSource = new TranscodeParams { EncoderId = "hevc_nvenc", QualitySlider = 40, Container = "mkv" };
        syncSource.AudioTracks.Add(new AudioTrackParams(1, "源音轨1", "aac", 2, string.Empty)
        {
            Action = AudioActionKind.Encode,
            CodecId = "libopus",
            BitRateKbps = 96,
        });
        syncSource.SubtitleTracks.Add(new SubtitleTrackParams(2, "源字幕1", "subrip", false)
        {
            Action = SubtitleActionKind.Burn,
        });

        var syncTarget = new TranscodeParams { EncoderId = "libx264", QualitySlider = 90, Container = "mp4" };
        syncTarget.AudioTracks.Add(new AudioTrackParams(5, "目标音轨1", "ac3", 6, string.Empty));
        syncTarget.AudioTracks.Add(new AudioTrackParams(6, "目标音轨2", "aac", 2, string.Empty));
        syncTarget.SubtitleTracks.Add(new SubtitleTrackParams(9, "目标字幕1", "subrip", false));

        syncTarget.CopyScalarsFrom(syncSource);
        syncCase.Details.Add($"标量同步后：{syncTarget.EncoderId} / 质量 {syncTarget.QualitySlider} / 容器 {syncTarget.Container}");
        syncCase.Details.Add($"轨道数量与索引保持不变：音轨 {string.Join(",", syncTarget.AudioTracks.Select(t => t.StreamIndex))}、字幕 {string.Join(",", syncTarget.SubtitleTracks.Select(t => t.StreamIndex))}");

        if (syncTarget.EncoderId != "hevc_nvenc" || syncTarget.QualitySlider != 40 || syncTarget.Container != "mkv")
        {
            syncCase.Failures.Add("CopyScalarsFrom 没有正确覆盖标量参数");
        }

        if (syncTarget.AudioTracks.Count != 2 || syncTarget.AudioTracks[0].StreamIndex != 5 ||
            syncTarget.AudioTracks[0].Action != AudioActionKind.Copy ||
            syncTarget.SubtitleTracks[0].Action != SubtitleActionKind.Copy)
        {
            syncCase.Failures.Add("CopyScalarsFrom 不应改动目标的轨道列表与动作");
        }

        syncTarget.ApplyTracksFrom(syncSource);
        syncCase.Details.Add(
            $"搬运轨道动作后：音轨1→{syncTarget.AudioTracks[0].Action}（{syncTarget.AudioTracks[0].CodecId} {syncTarget.AudioTracks[0].BitRateKbps}k）、" +
            $"音轨2→{syncTarget.AudioTracks[1].Action}、字幕1→{syncTarget.SubtitleTracks[0].Action}");

        if (syncTarget.AudioTracks[0].Action != AudioActionKind.Encode ||
            syncTarget.AudioTracks[0].CodecId != "libopus" ||
            syncTarget.AudioTracks[0].BitRateKbps != 96)
        {
            syncCase.Failures.Add("ApplyTracksFrom 没有按位置搬来音轨动作");
        }

        if (syncTarget.AudioTracks[1].Action != AudioActionKind.Copy)
        {
            syncCase.Failures.Add("目标多出来的音轨应保持原样");
        }

        if (syncTarget.SubtitleTracks[0].Action != SubtitleActionKind.Burn)
        {
            syncCase.Failures.Add("ApplyTracksFrom 没有搬来字幕动作（「烧入字幕」被覆盖过一次，这里必须锁住）");
        }

        if (syncTarget.AudioTracks.Select(t => t.StreamIndex).ToArray() is not [5, 6])
        {
            syncCase.Failures.Add("ApplyTracksFrom 不应改动目标的流索引");
        }

        syncCase.Passed = syncCase.Failures.Count == 0;
        results.Add(syncCase);

        // ── 7. 音频编码器元数据（无损标记 / 固定档位 / PCM 码率公式）──
        var audioCase = new CaseResult { Name = "音频 · 编码器元数据与 PCM 码率公式" };

        foreach (var id in new[] { "flac", "alac", "pcm_s16le", "pcm_s24le", "pcm_s32le" })
        {
            if (!EncoderCatalog.GetAudioCodec(id).IsLossless)
            {
                audioCase.Failures.Add($"{id} 应标记为无损（实测 -b:a 对它无效）");
            }
        }

        foreach (var id in new[] { "aac", "libopus", "libmp3lame", "ac3", "libvorbis" })
        {
            if (EncoderCatalog.GetAudioCodec(id).IsLossless)
            {
                audioCase.Failures.Add($"{id} 不应标记为无损");
            }
        }

        var ac3 = EncoderCatalog.GetAudioCodec("ac3");
        if (ac3.BitrateOptions.Length == 0)
        {
            audioCase.Failures.Add("AC3 应有固定码率档位");
        }
        else
        {
            audioCase.Details.Add(
                $"AC3 固定档位 {ac3.BitrateOptions.Length} 个（{ac3.BitrateOptions[0]}-{ac3.BitrateOptions[^1]} kbps）");
            if (EncoderCatalog.NearestBitrate(ac3.BitrateOptions, 200) != 192)
            {
                audioCase.Failures.Add("200k 应取整到 192k（实测 ffmpeg 的行为）");
            }

            if (EncoderCatalog.NearestBitrate(ac3.BitrateOptions, 1000) != 640)
            {
                audioCase.Failures.Add("1000k 应取整到 640k（实测 ffmpeg 的行为）");
            }
        }

        // 无损 PCM 码率公式：采样率 × 位深 × 声道（实测 2116 / 6350 kbps 精确吻合）
        var pcmStereo = EncoderCatalog.ComputePcmBitrate(44100, 24, 2);
        var pcmSurround = EncoderCatalog.ComputePcmBitrate(44100, 24, 6);
        audioCase.Details.Add($"PCM 24bit 44.1kHz 立体声 → {pcmStereo} kbps（实测 ffprobe 报 2116）");
        audioCase.Details.Add($"PCM 24bit 44.1kHz 5.1 → {pcmSurround} kbps（实测 ffprobe 报 6350）");
        if (pcmStereo != 2116)
        {
            audioCase.Failures.Add($"PCM 立体声码率公式不符：得到 {pcmStereo}，应为 2116");
        }

        if (pcmSurround != 6350)
        {
            audioCase.Failures.Add($"PCM 5.1 码率公式不符：得到 {pcmSurround}，应为 6350");
        }

        audioCase.Passed = audioCase.Failures.Count == 0;
        results.Add(audioCase);

        // ── 8. 音频编码名归一化（曾因此把「MKV 里的 opus 直通」误判为不兼容并强行重编码）──
        var namingCase = new CaseResult { Name = "音频 · 编码名归一化与容器判定" };

        var pairs = new (string EncoderId, string ProbeName)[]
        {
            ("libopus", "opus"),
            ("libmp3lame", "mp3"),
            ("libvorbis", "vorbis"),
            ("aac", "aac"),
            ("flac", "flac"),
            ("ac3", "ac3"),
        };

        foreach (var (encoderId, probeName) in pairs)
        {
            if (EncoderCatalog.CanonicalAudioCodec(encoderId) != probeName)
            {
                namingCase.Failures.Add($"CanonicalAudioCodec({encoderId}) 应为 {probeName}，实际 {EncoderCatalog.CanonicalAudioCodec(encoderId)}");
            }

            if (EncoderCatalog.AudioEncoderIdFor(probeName) != encoderId)
            {
                namingCase.Failures.Add($"AudioEncoderIdFor({probeName}) 应为 {encoderId}，实际 {EncoderCatalog.AudioEncoderIdFor(probeName)}");
            }

            // 两种写法必须得到同一个判定结果（这是被误判的根源）
            var mkv = EncoderCatalog.GetContainer("mkv");
            var byId = EncoderCatalog.IsAudioCodecCompatible(encoderId, mkv);
            var byName = EncoderCatalog.IsAudioCodecCompatible(probeName, mkv);
            if (byId != byName)
            {
                namingCase.Failures.Add($"{encoderId} / {probeName} 在 MKV 上的判定不一致：{byId} vs {byName}");
            }
        }

        // 关键反例：opus 在 MKV 可直通、在 MOV 不行（两条都是实测结论）
        var mkvContainer = EncoderCatalog.GetContainer("mkv");
        var movContainer = EncoderCatalog.GetContainer("mov");
        namingCase.Details.Add(
            $"opus → MKV={EncoderCatalog.IsAudioCodecCompatible("opus", mkvContainer)}、" +
            $"opus → MOV={EncoderCatalog.IsAudioCodecCompatible("opus", movContainer)}");
        if (!EncoderCatalog.IsAudioCodecCompatible("opus", mkvContainer))
        {
            namingCase.Failures.Add("opus 在 MKV 上必须是兼容的（实测可直通）—— 误判会把它强行重编码");
        }

        if (EncoderCatalog.IsAudioCodecCompatible("opus", movContainer))
        {
            namingCase.Failures.Add("opus 在 MOV 上必须是不兼容的（实测 muxer 拒绝）");
        }

        namingCase.Passed = namingCase.Failures.Count == 0;
        results.Add(namingCase);

        return results;
    }

    /// <summary>
    /// 逐编码器实测两遍编码支持情况，与 <see cref="EncoderDefinition.SupportsTwoPass"/> 比对。
    ///
    /// 必须实测的原因：硬件编码器对 -pass **不报错也不写统计文件**（实测 0 字节），
    /// 命令返回成功 —— 靠「命令成功」判断会得出「支持」的错误结论，用户会以为做了两遍而实际没有。
    /// 判定标准是统计文件里真的有内容。
    /// </summary>
    private static async Task<CaseResult> VerifyTwoPassSupportAsync(FfmpegPaths paths, string workDirectory)
    {
        var result = new CaseResult { Name = "两遍编码支持情况与实测一致" };
        var probeDirectory = Path.Combine(workDirectory, "twopass");
        Directory.CreateDirectory(probeDirectory);

        // 小尺寸短素材：11 个编码器逐个探测也要够快
        var input = Path.Combine(probeDirectory, "probe.mp4");
        var makeInput = await ProcessRunner.RunAsync(
            paths.Ffmpeg,
            [
                "-y", "-v", "error",
                "-f", "lavfi", "-i", "testsrc2=s=160x120:r=30",
                "-f", "lavfi", "-i", "sine=f=440",
                "-t", "1",
                "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
                "-c:a", "aac",
                input,
            ],
            CancellationToken.None,
            60000).ConfigureAwait(false);

        if (!makeInput.Succeeded)
        {
            return result.WithFailure("探测素材生成失败：" + FirstLine(makeInput.StandardError));
        }

        var mismatches = new List<string>();
        var agreements = 0;

        foreach (var encoder in EncoderCatalog.All)
        {
            var prefix = Path.Combine(probeDirectory, "tp-" + encoder.Id);
            TryDelete(prefix + "-0.log");

            var extra = encoder.Family switch
            {
                EncoderFamily.SvtAv1 => new[] { "-preset", "10" },
                EncoderFamily.Aom => new[] { "-cpu-used", "8" },
                _ => [],
            };

            var probe = await ProcessRunner.RunAsync(
                paths.Ffmpeg,
                [
                    "-y", "-v", "error",
                    "-i", input,
                    "-c:v", encoder.Id,
                    "-b:v", "200k",
                    .. extra,
                    "-pass", "1", "-passlogfile", prefix,
                    "-an", "-f", "null", "-",
                ],
                CancellationToken.None,
                120000).ConfigureAwait(false);

            // 关键：看统计文件里有没有内容，而不是看退出码
            var statsPath = prefix + "-0.log";
            var statsBytes = File.Exists(statsPath) ? new FileInfo(statsPath).Length : 0;
            var measured = statsBytes > 0;

            if (measured == encoder.SupportsTwoPass)
            {
                agreements++;
            }
            else
            {
                mismatches.Add(
                    $"{encoder.Id}：表里写 {(encoder.SupportsTwoPass ? "支持" : "不支持")}，" +
                    $"实测统计文件 {statsBytes} 字节（退出码 {probe.ExitCode}）");
            }
        }

        result.Details.Add($"探测 {EncoderCatalog.All.Count} 个编码器，与表一致 {agreements} 个");
        result.Details.Add(
            "支持的：" + string.Join("、", EncoderCatalog.All.Where(e => e.SupportsTwoPass).Select(e => e.Id)));
        foreach (var mismatch in mismatches)
        {
            result.Failures.Add(mismatch);
        }

        TryDelete(input);
        result.Passed = result.Failures.Count == 0;
        return result;
    }

    /// <summary>构造全部用例。</summary>
    private static List<(string Name, string SourcePath, TranscodeParams Parameters, Expectation Expectation)> BuildCases(
        Context context)    {
        var cases = new List<(string, string, TranscodeParams, Expectation)>();
        var sampleInfo = MediaProbe.ProbeAsync(context.Paths.Ffprobe, context.SamplePath).GetAwaiter().GetResult();
        if (sampleInfo is null)
        {
            return cases;
        }

        // 让测试用例带标准轨道列表（与界面行为一致）
        TranscodeParams Base(Action<TranscodeParams>? configure = null)
        {
            var parameters = new TranscodeParams();
            parameters.InitializeTracksFrom(sampleInfo, resetExisting: true);
            configure?.Invoke(parameters);
            return parameters;
        }

        void Add(string name, TranscodeParams parameters, Expectation expectation, string? sourcePath = null) =>
            cases.Add((name, sourcePath ?? context.SamplePath, parameters, expectation));

        // ── 简单模式：每个编码器一个用例 ──
        foreach (var encoder in EncoderCatalog.All)
        {
            if (!context.Capabilities.IsEncoderAvailable(encoder.Id))
            {
                continue;
            }

            Add(
                $"简单模式 · {encoder.DisplayName}",
                Base(p => p.EncoderId = encoder.Id),
                new Expectation
                {
                    CodecName = encoder.Codec,
                    Width = 1280,
                    Height = 720,
                    DurationSeconds = 5,
                    ExpectAudio = true,
                    ContainerExtension = "mp4",
                });
        }

        // ── 硬解路径：NVENC + cuda（帧留在显存）──
        Add(
            "硬解 · NVENC + cuda 显存内转码",
            Base(p =>
            {
                p.EncoderId = "h264_nvenc";
                p.HwAccel = HwAccelKind.Cuda;
            }),
            new Expectation { CodecName = "h264", Width = 1280, Height = 720, ContainerExtension = "mp4" });

        // ── GPU 缩放路径（scale_cuda）──
        Add(
            "缩放 · 854x480 + NVENC（scale_cuda 路径）",
            Base(p =>
            {
                p.EncoderId = "h264_nvenc";
                p.ScaleMode = ScaleMode.Width;
                p.ScaleWidth = 854;
            }),
            new Expectation { CodecName = "h264", Width = 854, Height = 480, ContainerExtension = "mp4" });

        // ── 缩放 · 软件路径 ──
        Add(
            "缩放 · 640x360 + x264（软件缩放路径）",
            Base(p =>
            {
                p.EncoderId = "libx264";
                p.ScaleMode = ScaleMode.Height;
                p.ScaleHeight = 360;
            }),
            new Expectation { CodecName = "h264", Width = 640, Height = 360, ContainerExtension = "mp4" });

        // ── 成帧缩放（Fit，只缩不放）──
        Add(
            "缩放 · Fit 到 1920x1080 框（源更小，应保持原样）",
            Base(p =>
            {
                p.EncoderId = "h264_nvenc";
                p.ScaleMode = ScaleMode.Fit;
                p.ScaleWidth = 1920;
                p.ScaleHeight = 1080;
            }),
            new Expectation { CodecName = "h264", Width = 1280, Height = 720, ContainerExtension = "mp4" });

        // ── 烧字幕 · 内封轨（中文+空格目录，硬解+软件滤镜桥接）──
        var embeddedInfo = MediaProbe.ProbeAsync(context.Paths.Ffprobe, context.SampleWithSubtitlePath)
            .GetAwaiter().GetResult();
        if (embeddedInfo is not null && embeddedInfo.HasSubtitle)
        {
            var burnParameters = new TranscodeParams { EncoderId = "h264_nvenc" };
            burnParameters.InitializeTracksFrom(embeddedInfo, resetExisting: true);
            foreach (var track in burnParameters.SubtitleTracks)
            {
                track.Action = SubtitleActionKind.Burn;
            }

            cases.Add((
                "烧字幕 · 内封轨（中文+空格目录，硬解桥接）",
                context.SampleWithSubtitlePath,
                burnParameters,
                new Expectation { CodecName = "h264", Width = 1280, Height = 720, ContainerExtension = "mp4" }));

            // ── 字幕提取（附带产出 .srt）──
            var extractParameters = new TranscodeParams { EncoderId = "h264_nvenc" };
            extractParameters.InitializeTracksFrom(embeddedInfo, resetExisting: true);
            foreach (var track in extractParameters.SubtitleTracks)
            {
                track.IsSelected = true;
                track.Action = SubtitleActionKind.Extract;
                track.ExtractFormat = SubtitleFormat.Srt;
            }

            cases.Add((
                "字幕提取 · 内封轨 → 独立 srt 文件",
                context.SampleWithSubtitlePath,
                extractParameters,
                new Expectation
                {
                    CodecName = "h264",
                    ContainerExtension = "mp4",
                    ExpectSubtitleSidecar = true,
                }));
        }

        // ── 烧字幕 · 外挂字幕（中文+空格路径，验证滤镜路径转义）──
        Add(
            "烧字幕 · 外挂文件（中文+空格路径转义）",
            Base(p =>
            {
                p.EncoderId = "libx264";
                p.ExternalSubtitlePath = context.ExternalSubtitlePath;
                p.SubtitleStyle.FontName = "Microsoft YaHei";
                p.SubtitleStyle.FontSize = 28;
                p.SubtitleStyle.PrimaryColor = "#FFFF00";
                p.SubtitleStyle.OutlineWidth = 2;
            }),
            new Expectation { CodecName = "h264", Width = 1280, Height = 720, ContainerExtension = "mp4" });

        // ── 音频重编码 ──
        Add(
            "音频 · 重编码 AAC 128k 立体声",
            Base(p =>
            {
                p.EncoderId = "h264_nvenc";
                foreach (var track in p.AudioTracks)
                {
                    track.Action = AudioActionKind.Encode;
                    track.CodecId = "aac";
                    track.BitRateKbps = 128;
                }
            }),
            new Expectation { CodecName = "h264", ExpectAudio = true, ContainerExtension = "mp4" });

        // ── 纯封装（不重编码）──
        Add(
            "纯封装 · 视频+音频直通 → mkv",
            Base(p =>
            {
                p.VideoMode = VideoMode.Copy;
                p.Container = "mkv";
            }),
            new Expectation { CodecName = "h264", Width = 1280, Height = 720, ExpectAudio = true, ContainerExtension = "mkv" });

        // ── 无损 PCM：不传码率（ffmpeg 会静默忽略），采样率/声道覆盖生效 ──
        Add(
            "音频 · PCM 24bit（不传码率，采样率 48k、声道立体声）",
            Base(p =>
            {
                p.EncoderId = "h264_nvenc";
                foreach (var track in p.AudioTracks)
                {
                    track.Action = AudioActionKind.Encode;
                    track.CodecId = "pcm_s24le";
                    track.BitRateKbps = 192;   // 故意留着：验证它不会出现在命令行里
                    track.SampleRate = 48000;
                    track.TargetChannels = 2;
                }
            }),
            new Expectation
            {
                CodecName = "h264",
                ExpectAudio = true,
                ExpectAudioCodec = "pcm_s24le",
                ExpectAudioSampleRate = 48000,
                ExpectAudioChannels = 2,
                ContainerExtension = "mp4",
                RequireArguments = ["-ar:a:0", "48000", "-ac:a:0", "2"],
                ForbidArguments = ["-b:a:0"],
            });

        // ── 固定档位：非法码率由预检取整（实测 AC3 填 200k 会静默变成 192k）──
        Add(
            "音频 · AC3 非法码率（预检应取整到 192k）",
            Base(p =>
            {
                p.EncoderId = "h264_nvenc";
                p.Container = "mkv";
                foreach (var track in p.AudioTracks)
                {
                    track.Action = AudioActionKind.Encode;
                    track.CodecId = "ac3";
                    track.BitRateKbps = 200;
                }
            }),
            new Expectation
            {
                CodecName = "h264",
                ExpectAudio = true,
                ExpectAudioCodec = "ac3",
                ContainerExtension = "mkv",
                ExpectEffectiveAudioBitrateKbps = 192,
            });

        // ── 两遍编码：应拆成「第一遍分析 + 第二遍编码」两步 ──
        Add(
            "两遍编码 · x264 目标码率（两步且主步骤带 -pass 2）",
            Base(p =>
            {
                p.EncoderId = "libx264";
                p.QualityMode = QualityMode.Advanced;
                p.RateControl = RateControlKind.Bitrate;
                p.BitrateKbps = 800;
                p.Preset = "veryfast";
                p.TwoPass = true;
            }),
            new Expectation
            {
                CodecName = "h264",
                ContainerExtension = "mp4",
                ExpectStepCount = 2,
                RequireArguments = ["-pass", "2"],
            });

        // ── opus 直通：若编码名归一化出错，预检会强行重编码，输出音轨会变成 aac ──
        Add(
            "音频 · opus 直通 → MKV（不应被误判为不兼容）",
            Base(p =>
            {
                p.VideoMode = VideoMode.Copy;
                p.Container = "mkv";
            }),
            new Expectation { ContainerExtension = "mkv", ExpectAudio = true, ExpectAudioCodec = "opus" },
            context.OpusAudioPath);

        // ── 多音轨：容器差异（实测 mp3/flac/wav 只接受单条音轨）──
        var threeAudioInfo = MediaProbe
            .ProbeAsync(context.Paths.Ffprobe, context.ThreeAudioPath)
            .GetAwaiter().GetResult();
        if (threeAudioInfo is not null && threeAudioInfo.AudioStreams.Count == 3)
        {
            TranscodeParams ThreeAudio(string container) => new TranscodeParams
            {
                EncoderId = "h264_nvenc",
                Container = container,
            };

            cases.Add((
                "多音轨 · 3 条音轨 → MKV（应全部保留）",
                context.ThreeAudioPath,
                ThreeAudio("mkv"),
                new Expectation { CodecName = "h264", ExpectAudio = true, ExpectAudioStreamCount = 3, ContainerExtension = "mkv" }));

            cases.Add((
                "多音轨 · 3 条音轨 → MP4（应全部保留）",
                context.ThreeAudioPath,
                ThreeAudio("mp4"),
                new Expectation { CodecName = "h264", ExpectAudio = true, ExpectAudioStreamCount = 3, ContainerExtension = "mp4" }));

            // 注意必须显式丢弃视频：否则「纯音频容器装不下视频流」那条规则会先把容器
            // 自动改成 M4A（支持多音轨），单音轨规则就没机会触发 —— 顺序上两者是串联的。
            cases.Add((
                "多音轨 · 丢弃视频 + FLAC 容器 + 3 条音轨（单音轨容器，预检必须拦下）",
                context.ThreeAudioPath,
                new TranscodeParams { EncoderId = "h264_nvenc", Container = "flac", VideoMode = VideoMode.Drop },
                new Expectation { ExpectPreflightBlocked = true }));
        }

        // ── 纯音频提取 ──
        Add(
            "提取音频 · 丢弃视频 → m4a",
            Base(p =>
            {
                p.VideoMode = VideoMode.Drop;
                p.Container = "m4a";
            }),
            new Expectation { CodecName = null, ExpectAudio = true, ContainerExtension = "m4a" });

        // ── 高级模式 · CRF + preset + profile ──
        Add(
            "高级模式 · x264 CRF 28 + preset fast + profile high",
            Base(p =>
            {
                p.EncoderId = "libx264";
                p.QualityMode = QualityMode.Advanced;
                p.RateControl = RateControlKind.Quality;
                p.QualityValue = 28;
                p.Preset = "fast";
                p.Profile = "high";
                p.PixelFormat = "yuv420p";
            }),
            new Expectation { CodecName = "h264", Profile = "High", PixelFormat = "yuv420p", ContainerExtension = "mp4" });

        // ── 高级模式 · 目标码率 ──
        Add(
            "高级模式 · 目标码率 1200k + maxrate/bufsize",
            Base(p =>
            {
                p.EncoderId = "h264_nvenc";
                p.QualityMode = QualityMode.Advanced;
                p.RateControl = RateControlKind.Bitrate;
                p.BitrateKbps = 1200;
                p.MaxrateKbps = 1200;
                p.BufsizeKbps = 2400;
            }),
            new Expectation { CodecName = "h264", ContainerExtension = "mp4" });

        // ── 帧率转换 ──
        Add(
            "帧率 · 30 → 15 fps",
            Base(p =>
            {
                p.EncoderId = "h264_nvenc";
                p.FrameRate = "15";
            }),
            new Expectation { CodecName = "h264", ContainerExtension = "mp4" });

        // ── 字幕文件格式转换 ──
        Add(
            "字幕转换 · srt → ass",
            new TranscodeParams { SubtitleConvertFormat = SubtitleFormat.Ass },
            new Expectation { ContainerExtension = "ass" },
            context.SubtitleOnlyPath);

        // ── 预检拦截：输出会覆盖源文件 ──
        Add(
            "预检拦截 · 输出路径等于源文件（必须报错）",
            Base(p =>
            {
                p.EncoderId = "h264_nvenc";
                p.AllowOverwrite = true;
                p.NamingTemplate = "{name}";
                // 输出到源目录 + 只留原名 = 与源文件同名，预检必须拦下
                p.OutputDirectory = Path.GetDirectoryName(context.SamplePath) ?? string.Empty;
            }),
            new Expectation { ExpectPreflightBlocked = true });

        return cases;
    }

    /// <summary>执行单个用例：预检 → 构造 → 真跑 → 校验。</summary>
    private static async Task<CaseResult> RunCaseAsync(
        string name,
        string sourcePath,
        TranscodeParams parameters,
        Expectation expectation,
        Context context)
    {
        var result = new CaseResult { Name = name };

        var info = await MediaProbe.ProbeAsync(context.Paths.Ffprobe, sourcePath).ConfigureAwait(false);
        if (info is null)
        {
            result.Failures.Add("源文件 ffprobe 失败");
            return result;
        }

        parameters.InitializeTracksFrom(info);
        var outputPath = OutputPathBuilder.Build(info, parameters, context.OutputDirectory);
        result.Details.Add(
            $"源：{Path.GetFileName(sourcePath)}（{info.VideoStream?.ResolutionText ?? info.FormatName}） → 输出：{Path.GetFileName(outputPath)}");

        var preflight = PreflightValidator.Validate(info, parameters, context.Capabilities, outputPath);
        foreach (var issue in preflight.Issues)
        {
            result.Details.Add($"[预检·{issue.SeverityText}] {issue.DisplayText}");
        }

        if (expectation.ExpectEffectiveAudioBitrateKbps is not null)
        {
            var effectiveTrack = preflight.Effective.AudioTracks.FirstOrDefault();
            if (effectiveTrack?.BitRateKbps != expectation.ExpectEffectiveAudioBitrateKbps)
            {
                result.Failures.Add(
                    $"预检后音频码率不符：期望 {expectation.ExpectEffectiveAudioBitrateKbps}，实际 {effectiveTrack?.BitRateKbps}");
            }
        }

        if (expectation.ExpectPreflightBlocked)
        {
            result.Passed = preflight.HasBlockingError;
            if (!result.Passed)
            {
                result.Failures.Add("预期预检应阻断执行，但没有报错");
            }

            return result;
        }

        if (preflight.HasBlockingError)
        {
            result.Failures.Add("预检阻断了执行：" + string.Join("；",
                preflight.Issues.Where(i => i.Severity == IssueSeverity.Error).Select(i => i.DisplayText)));
            return result;
        }

        var encoder = EncoderCatalog.Get(preflight.Effective.EncoderId);
        var plan = TranscodeCommandBuilder.Build(
            info,
            preflight.Effective,
            encoder,
            preflight.EffectiveAccel,
            outputPath,
            context.TempDirectory);

        foreach (var step in plan.Steps)
        {
            result.Details.Add($"$ {step.ToCommandLine(context.Paths.Ffmpeg)}");
        }

        foreach (var note in plan.Notes)
        {
            result.Details.Add($"注：{note}");
        }

        if (expectation.ExpectStepCount is not null)
        {
            var visibleSteps = plan.VisibleSteps.Count();
            if (visibleSteps != expectation.ExpectStepCount)
            {
                result.Failures.Add($"步骤数不符：期望 {expectation.ExpectStepCount}，实际 {visibleSteps}");
            }
        }

        // 顺序执行（Prepare → 主转码 → 字幕提取）
        var sidecarPaths = new List<string>();
        foreach (var step in plan.Steps)
        {
            var run = await TranscodeRunner.RunAsync(
                context.Paths.Ffmpeg,
                step,
                null,
                null,
                CancellationToken.None).ConfigureAwait(false);

            if (!run.Success)
            {
                result.Failures.Add($"{step.Label} 失败（退出码 {run.ExitCode}）：{run.ErrorMessage}");
                return result;
            }

            if (step.Kind == TranscodeStepKind.ExtractSubtitle && step.ProducesFile)
            {
                sidecarPaths.Add(step.OutputPath);
            }
        }

        // 清理临时文件
        foreach (var step in plan.Steps.Where(s => s.IsTemporary))
        {
            TryDelete(step.OutputPath);
        }

        var mainStep = plan.MainStep;
        if (mainStep is null)
        {
            result.Failures.Add("没有主输出步骤");
            return result;
        }

        if (!File.Exists(mainStep.OutputPath))
        {
            result.Failures.Add($"主输出文件不存在：{mainStep.OutputPath}");
            return result;
        }

        // ── 校验产出 ──
        var produced = await MediaProbe.ProbeAsync(context.Paths.Ffprobe, mainStep.OutputPath).ConfigureAwait(false);
        if (produced is null)
        {
            result.Failures.Add("产出文件无法被 ffprobe 解析");
            return result;
        }

        var size = new FileInfo(mainStep.OutputPath).Length;
        result.Details.Add(
            $"产出：{produced.FormatName} {produced.DurationText} {MediaFormat.FormatSize(size)} " +
            $"{produced.VideoStream?.CodecName ?? "无视频"}/{produced.AudioStreams.Count} 音轨/{produced.SubtitleStreams.Count} 字幕");

        if (expectation.CodecName is not null)
        {
            var actual = produced.VideoStream?.CodecName;
            if (!string.Equals(actual, expectation.CodecName, StringComparison.OrdinalIgnoreCase))
            {
                result.Failures.Add($"视频编码不符：期望 {expectation.CodecName}，实际 {actual ?? "无"}");
            }
        }

        if (expectation.Width is not null && produced.VideoStream?.Width != expectation.Width)
        {
            result.Failures.Add($"宽度不符：期望 {expectation.Width}，实际 {produced.VideoStream?.Width}");
        }

        if (expectation.Height is not null && produced.VideoStream?.Height != expectation.Height)
        {
            result.Failures.Add($"高度不符：期望 {expectation.Height}，实际 {produced.VideoStream?.Height}");
        }

        if (expectation.DurationSeconds is not null &&
            Math.Abs(produced.Duration.TotalSeconds - expectation.DurationSeconds.Value) > 1.0)
        {
            result.Failures.Add($"时长不符：期望 {expectation.DurationSeconds:0.##}s，实际 {produced.Duration.TotalSeconds:0.##}s");
        }

        if (expectation.PixelFormat is not null &&
            !string.Equals(produced.VideoStream?.PixelFormat, expectation.PixelFormat, StringComparison.OrdinalIgnoreCase))
        {
            result.Failures.Add($"像素格式不符：期望 {expectation.PixelFormat}，实际 {produced.VideoStream?.PixelFormat}");
        }

        if (expectation.Profile is not null &&
            !string.Equals(produced.VideoStream?.Profile, expectation.Profile, StringComparison.OrdinalIgnoreCase))
        {
            result.Failures.Add($"profile 不符：期望 {expectation.Profile}，实际 {produced.VideoStream?.Profile}");
        }

        if (expectation.ExpectAudio is true && produced.AudioStreams.Count == 0)
        {
            result.Failures.Add("产出里没有音频流");
        }

        var producedAudio = produced.AudioStreams.FirstOrDefault();
        if (expectation.ExpectAudioCodec is not null &&
            !string.Equals(producedAudio?.CodecName, expectation.ExpectAudioCodec, StringComparison.OrdinalIgnoreCase))
        {
            result.Failures.Add($"音频编码不符：期望 {expectation.ExpectAudioCodec}，实际 {producedAudio?.CodecName ?? "无"}");
        }

        if (expectation.ExpectAudioSampleRate is not null && producedAudio?.SampleRate != expectation.ExpectAudioSampleRate)
        {
            result.Failures.Add($"音频采样率不符：期望 {expectation.ExpectAudioSampleRate}，实际 {producedAudio?.SampleRate}");
        }

        if (expectation.ExpectAudioChannels is not null && producedAudio?.Channels != expectation.ExpectAudioChannels)
        {
            result.Failures.Add($"音频声道数不符：期望 {expectation.ExpectAudioChannels}，实际 {producedAudio?.Channels}");
        }

        if (expectation.ExpectAudioStreamCount is not null && produced.AudioStreams.Count != expectation.ExpectAudioStreamCount)
        {
            result.Failures.Add($"音轨条数不符：期望 {expectation.ExpectAudioStreamCount}，实际 {produced.AudioStreams.Count}");
        }

        // 命令行层面的断言：有些行为（例如无损编码不传 -b:a）只能从参数看出来
        foreach (var required in expectation.RequireArguments)
        {
            if (!mainStep.Arguments.Contains(required))
            {
                result.Failures.Add($"命令行缺少必需参数：{required}");
            }
        }

        foreach (var forbidden in expectation.ForbidArguments)
        {
            if (mainStep.Arguments.Contains(forbidden))
            {
                result.Failures.Add($"命令行不应出现参数：{forbidden}");
            }
        }

        if (expectation.ContainerExtension is not null &&
            !string.Equals(Path.GetExtension(mainStep.OutputPath).TrimStart('.'), expectation.ContainerExtension,
                StringComparison.OrdinalIgnoreCase))
        {
            result.Failures.Add($"容器扩展名不符：期望 .{expectation.ContainerExtension}");
        }

        if (expectation.ExpectSubtitleSidecar)
        {
            if (sidecarPaths.Count == 0 || !File.Exists(sidecarPaths[0]))
            {
                result.Failures.Add("预期的字幕附属文件没有产出");
            }
            else
            {
                var sidecarText = await File.ReadAllTextAsync(sidecarPaths[0]).ConfigureAwait(false);
                result.Details.Add($"字幕附属文件：{Path.GetFileName(sidecarPaths[0])}（{sidecarText.Length} 字符）");
                if (sidecarText.Length < 10 || !sidecarText.Contains("-->", StringComparison.Ordinal))
                {
                    result.Failures.Add("字幕附属文件内容不像有效字幕");
                }
            }
        }

        result.Passed = result.Failures.Count == 0;
        return result;
    }

    private static async Task<(bool Ok, string Output)> FfmpegAsync(FfmpegPaths paths, params string[] arguments)
    {
        var result = await ProcessRunner.RunAsync(paths.Ffmpeg, arguments, CancellationToken.None, 120000)
            .ConfigureAwait(false);
        return (result.Succeeded, result.StandardError);
    }

    private static string FirstLine(string text)
    {
        var index = text.IndexOf('\n');
        return (index < 0 ? text : text[..index]).Trim();
    }

    private static void ResetDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"清理自检目录失败：{ex.Message}", "SelfTest");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // 临时文件删不掉不影响结论
        }
    }

    private static void WriteReport(string reportPath, StringBuilder report)
    {
        try
        {
            var directory = Path.GetDirectoryName(reportPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(reportPath, report.ToString(), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "SelfTest.WriteReport");
        }
    }
}
