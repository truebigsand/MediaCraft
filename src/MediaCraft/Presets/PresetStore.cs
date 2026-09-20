using System.Collections.ObjectModel;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaCraft.Logging;
using MediaCraft.Media;

namespace MediaCraft.Presets;

/// <summary>
/// 预设仓库：内置预设（代码定义）+ 用户预设（%AppData%\MediaCraft\presets.json），
/// 支持把当前参数另存为预设、覆盖更新、重命名、删除、导入导出 JSON。
/// </summary>
public sealed class PresetStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public PresetStore()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        FilePath = Path.Combine(appData, "MediaCraft", "presets.json");
    }

    /// <summary>全部预设（内置在前，用户预设按创建时间）。</summary>
    public ObservableCollection<Preset> All { get; } = [];

    /// <summary>用户预设文件路径。</summary>
    public string FilePath { get; }

    /// <summary>预设集合变化通知。</summary>
    public event Action? Changed;

    /// <summary>加载：先放内置预设，再合并用户预设。</summary>
    public void Load()
    {
        All.Clear();
        foreach (var preset in BuiltInPresets)
        {
            All.Add(preset);
        }

        try
        {
            if (File.Exists(FilePath))
            {
                var file = JsonSerializer.Deserialize<PresetFile>(File.ReadAllText(FilePath), Options);
                foreach (var preset in file?.Presets ?? [])
                {
                    preset.IsBuiltIn = false;
                    All.Add(preset);
                }

                AppLog.Info($"预设已加载：内置 {All.Count(p => p.IsBuiltIn)} 个，自定义 {All.Count(p => !p.IsBuiltIn)} 个", "Presets");
            }
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "PresetStore.Load");
        }

        Changed?.Invoke();
    }

    /// <summary>保存用户预设（内置的不落盘）。</summary>
    public void Save()
    {
        try
        {
            var userPresets = All.Where(p => !p.IsBuiltIn).ToList();
            var file = new PresetFile { SavedAt = DateTime.Now, Presets = userPresets };

            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(FilePath, JsonSerializer.Serialize(file, Options));
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "PresetStore.Save");
        }
    }

    /// <summary>把当前参数另存为新预设。</summary>
    public Preset SaveAs(string name, string description, TranscodeParams source)
    {
        var preset = new Preset
        {
            Name = string.IsNullOrWhiteSpace(name) ? "未命名预设" : name.Trim(),
            Description = description?.Trim() ?? string.Empty,
            IsBuiltIn = false,
            Parameters = source.Clone(),
            Intent = PresetTrackIntent.FromParams(source),
        };

        // 同名用户预设：直接覆盖（避免一堆「XX (2)」）
        var existing = All.FirstOrDefault(p => !p.IsBuiltIn &&
                                              string.Equals(p.Name, preset.Name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            existing.Parameters = preset.Parameters;
            existing.Intent = preset.Intent;
            existing.Description = preset.Description;
            existing.CreatedAt = DateTime.Now;
            Save();
            Changed?.Invoke();
            AppLog.Info($"预设已更新：{existing.Name}", "Presets");
            return existing;
        }

        All.Add(preset);
        Save();
        Changed?.Invoke();
        AppLog.Info($"预设已保存：{preset.Name}", "Presets");
        return preset;
    }

    /// <summary>重命名 / 改描述。</summary>
    public bool Rename(Preset preset, string newName, string newDescription)
    {
        if (preset.IsBuiltIn || string.IsNullOrWhiteSpace(newName))
        {
            return false;
        }

        preset.Name = newName.Trim();
        preset.Description = newDescription?.Trim() ?? string.Empty;
        Save();
        Changed?.Invoke();
        return true;
    }

    /// <summary>删除用户预设。</summary>
    public bool Delete(Preset preset)
    {
        if (preset.IsBuiltIn)
        {
            return false;
        }

        var removed = All.Remove(preset);
        if (removed)
        {
            Save();
            Changed?.Invoke();
            AppLog.Info($"预设已删除：{preset.Name}", "Presets");
        }

        return removed;
    }

    /// <summary>把参数应用到一个预设（覆盖其内容）。</summary>
    public void UpdateFromParams(Preset preset, TranscodeParams source)
    {
        preset.Parameters = source.Clone();
        preset.Intent = PresetTrackIntent.FromParams(source);
        Save();
        Changed?.Invoke();
    }

    /// <summary>
    /// 把预设套用到目标参数：标量参数直接覆盖，轨道动作按意图落到目标文件自己的轨道上。
    /// </summary>
    public static void ApplyTo(Preset preset, TranscodeParams target)
    {
        var source = preset.Parameters;

        // ── 标量参数 ──
        target.EncoderId = source.EncoderId;
        target.QualityMode = source.QualityMode;
        target.VideoMode = source.VideoMode;
        target.QualitySlider = source.QualitySlider;
        target.RateControl = source.RateControl;
        target.QualityValue = source.QualityValue;
        target.BitrateKbps = source.BitrateKbps;
        target.MaxrateKbps = source.MaxrateKbps;
        target.BufsizeKbps = source.BufsizeKbps;
        target.Preset = source.Preset;
        target.Tune = source.Tune;
        target.Profile = source.Profile;
        target.Level = source.Level;
        target.Gop = source.Gop;
        target.PixelFormat = source.PixelFormat;
        target.HwAccel = source.HwAccel;
        target.ScaleMode = source.ScaleMode;
        target.ScaleWidth = source.ScaleWidth;
        target.ScaleHeight = source.ScaleHeight;
        target.FrameRate = source.FrameRate;
        target.Container = source.Container;
        target.NamingTemplate = source.NamingTemplate;
        target.AllowOverwrite = source.AllowOverwrite;
        target.FastStart = source.FastStart;
        target.ExtraArguments = source.ExtraArguments;

        // 字幕样式
        target.SubtitleStyle.FontName = source.SubtitleStyle.FontName;
        target.SubtitleStyle.FontSize = source.SubtitleStyle.FontSize;
        target.SubtitleStyle.PrimaryColor = source.SubtitleStyle.PrimaryColor;
        target.SubtitleStyle.OutlineColor = source.SubtitleStyle.OutlineColor;
        target.SubtitleStyle.OutlineWidth = source.SubtitleStyle.OutlineWidth;
        target.SubtitleStyle.Shadow = source.SubtitleStyle.Shadow;
        target.SubtitleStyle.MarginVertical = source.SubtitleStyle.MarginVertical;
        target.SubtitleStyle.Alignment = source.SubtitleStyle.Alignment;
        target.SubtitleStyle.Bold = source.SubtitleStyle.Bold;

        // 输出目录不跟着预设走（每台机器/每个批次差异太大，预设里留空表示沿用当前设置）

        // ── 轨道意图 ──
        var intent = preset.Intent;

        if (intent.AudioAction is not null)
        {
            foreach (var track in target.AudioTracks)
            {
                track.IsSelected = true;
                track.Action = intent.AudioAction.Value;

                if (intent.AudioAction == AudioActionKind.Encode)
                {
                    if (!string.IsNullOrWhiteSpace(intent.AudioCodecId))
                    {
                        track.CodecId = intent.AudioCodecId;
                    }

                    if (intent.AudioBitRateKbps is > 0)
                    {
                        track.BitRateKbps = intent.AudioBitRateKbps.Value;
                    }
                }
            }
        }

        if (intent.SubtitleAction is not null)
        {
            for (var index = 0; index < target.SubtitleTracks.Count; index++)
            {
                var track = target.SubtitleTracks[index];
                track.IsSelected = true;

                if (intent.SubtitleFirstOnly)
                {
                    // 只对第一条字幕轨生效，其余保持内封
                    track.Action = index == 0 ? intent.SubtitleAction.Value : SubtitleActionKind.Copy;
                }
                else
                {
                    track.Action = intent.SubtitleAction.Value;
                }

                if (intent.SubtitleExtractFormat is not null)
                {
                    track.ExtractFormat = intent.SubtitleExtractFormat.Value;
                }
            }
        }
    }

    // ── 导入导出 ──

    /// <summary>导出预设到 JSON 文件（可一次导出多个）。</summary>
    public int Export(IEnumerable<Preset> presets, string path)
    {
        var list = presets.ToList();
        var file = new PresetFile { SavedAt = DateTime.Now, Presets = list };
        File.WriteAllText(path, JsonSerializer.Serialize(file, Options));
        AppLog.Info($"已导出 {list.Count} 个预设：{path}", "Presets");
        return list.Count;
    }

    /// <summary>
    /// 从 JSON 文件导入预设。兼容三种结构：PresetFile 包装、预设数组、单个预设对象。
    /// 导入的预设一律标记为自定义，重名自动加序号。
    /// </summary>
    public int Import(string path)
    {
        var json = File.ReadAllText(path);
        var imported = Parse(json);
        if (imported.Count == 0)
        {
            throw new InvalidDataException("文件里没有识别到预设");
        }

        var count = 0;
        foreach (var preset in imported)
        {
            preset.IsBuiltIn = false;
            preset.Id = Guid.NewGuid().ToString("N");
            preset.Name = MakeUniqueName(preset.Name);
            All.Add(preset);
            count++;
        }

        Save();
        Changed?.Invoke();
        AppLog.Info($"已导入 {count} 个预设：{path}", "Presets");
        return count;
    }

    /// <summary>解析预设 JSON（容错三种结构）。</summary>
    public static List<Preset> Parse(string json)
    {
        var results = new List<Preset>();

        try
        {
            var file = JsonSerializer.Deserialize<PresetFile>(json, Options);
            if (file?.Presets is { Count: > 0 })
            {
                results.AddRange(file.Presets.Where(p => p is not null));
                return results;
            }
        }
        catch (JsonException)
        {
            // 继续尝试其它结构
        }

        try
        {
            var array = JsonSerializer.Deserialize<List<Preset>>(json, Options);
            if (array is { Count: > 0 })
            {
                results.AddRange(array.Where(p => p is not null));
                return results;
            }
        }
        catch (JsonException)
        {
            // 继续尝试其它结构
        }

        try
        {
            var single = JsonSerializer.Deserialize<Preset>(json, Options);
            if (single is not null && !string.IsNullOrWhiteSpace(single.Name))
            {
                results.Add(single);
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("不是有效的预设 JSON：" + ex.Message, ex);
        }

        return results;
    }

    private string MakeUniqueName(string baseName)
    {
        var name = string.IsNullOrWhiteSpace(baseName) ? "导入的预设" : baseName.Trim();
        if (All.All(p => !string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            return name;
        }

        for (var index = 2; index < 1000; index++)
        {
            var candidate = $"{name} ({index})";
            if (All.All(p => !string.Equals(p.Name, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }
        }

        return name + " (导入)";
    }

    /// <summary>内置预设：针对本机实测可用的 11 个编码器设计。</summary>
    public static IReadOnlyList<Preset> BuiltInPresets { get; } = BuildBuiltIns();

    private static IReadOnlyList<Preset> BuildBuiltIns() => BuiltIn().ToArray();

    private static IEnumerable<Preset> BuiltIn()
    {
        yield return Make(
            "NVENC H.264 高质量",
            "NVIDIA 硬编，兼容性最好，适合绝大多数场景",
            p =>
            {
                p.EncoderId = "h264_nvenc";
                p.QualityMode = QualityMode.Simple;
                p.QualitySlider = 75;
                p.Container = "mp4";
                p.FastStart = true;
            });

        yield return Make(
            "NVENC HEVC 小体积",
            "NVIDIA 硬编 H.265，同等画质体积约小 30-40%（老设备可能不支持）",
            p =>
            {
                p.EncoderId = "hevc_nvenc";
                p.QualityMode = QualityMode.Simple;
                p.QualitySlider = 70;
                p.Container = "mp4";
                p.FastStart = true;
            });

        yield return Make(
            "AV1 NVENC 最小体积",
            "NVIDIA 硬编 AV1，体积最小；需要支持 AV1 的播放器/硬件",
            p =>
            {
                p.EncoderId = "av1_nvenc";
                p.QualityMode = QualityMode.Simple;
                p.QualitySlider = 65;
                p.Container = "mp4";
                p.FastStart = true;
            });

        yield return Make(
            "QSV AV1（核显省电）",
            "Intel 核显硬编 AV1，几乎不占 CPU、功耗低",
            p =>
            {
                p.EncoderId = "av1_qsv";
                p.QualityMode = QualityMode.Simple;
                p.QualitySlider = 65;
                p.Container = "mp4";
                p.FastStart = true;
            });

        yield return Make(
            "QSV H.264（核显省电）",
            "Intel 核显硬编 H.264，适合标清/低码率批量任务",
            p =>
            {
                p.EncoderId = "h264_qsv";
                p.QualityMode = QualityMode.Simple;
                p.QualitySlider = 70;
                p.Container = "mp4";
                p.FastStart = true;
            });

        yield return Make(
            "x264 兼容性优先",
            "CPU 软编 H.264 + profile main，老旧设备/剪辑软件都能开",
            p =>
            {
                p.EncoderId = "libx264";
                p.QualityMode = QualityMode.Simple;
                p.QualitySlider = 75;
                p.Profile = "main";
                p.PixelFormat = "yuv420p";
                p.Container = "mp4";
                p.FastStart = true;
            });

        yield return Make(
            "x265 软编小体积",
            "CPU 软编 H.265，压缩比高但速度慢；适合留档",
            p =>
            {
                p.EncoderId = "libx265";
                p.QualityMode = QualityMode.Simple;
                p.QualitySlider = 70;
                p.Container = "mkv";
            });

        yield return Make(
            "SVT-AV1 软编",
            "CPU 软编 AV1，速度与压缩比均衡（preset 6）",
            p =>
            {
                p.EncoderId = "libsvtav1";
                p.QualityMode = QualityMode.Simple;
                p.QualitySlider = 60;
                p.Container = "mkv";
            });

        yield return Make(
            "转 1080p（NVENC H.264）",
            "统一压到 1920×1080（按比例，只缩不放），适合混分辨率素材批量归档",
            p =>
            {
                p.EncoderId = "h264_nvenc";
                p.QualityMode = QualityMode.Simple;
                p.QualitySlider = 75;
                p.ScaleMode = ScaleMode.Width;
                p.ScaleWidth = 1920;
                p.Container = "mp4";
                p.FastStart = true;
            });

        yield return Make(
            "手机友好 720p",
            "压到 1280×720 并重编码音频为 AAC 128k，体积小、兼容性广",
            p =>
            {
                p.EncoderId = "h264_nvenc";
                p.QualityMode = QualityMode.Simple;
                p.QualitySlider = 70;
                p.ScaleMode = ScaleMode.Width;
                p.ScaleWidth = 1280;
                p.Container = "mp4";
                p.FastStart = true;
            },
            intent: new PresetTrackIntent
            {
                AudioAction = AudioActionKind.Encode,
                AudioCodecId = "aac",
                AudioBitRateKbps = 128,
                SubtitleAction = SubtitleActionKind.Copy,
            });

        yield return Make(
            "纯封装不重编码",
            "视频音频全部直通，只换容器（秒级完成，画质零损失）",
            p =>
            {
                p.VideoMode = VideoMode.Copy;
                p.Container = "mkv";
            },
            intent: new PresetTrackIntent { AudioAction = AudioActionKind.Copy, SubtitleAction = SubtitleActionKind.Copy });

        yield return Make(
            "提取音频（m4a）",
            "丢弃视频，只把音轨转成 AAC 输出 .m4a",
            p =>
            {
                p.VideoMode = VideoMode.Drop;
                p.Container = "m4a";
                p.NamingTemplate = "{name}_audio";
            },
            intent: new PresetTrackIntent
            {
                AudioAction = AudioActionKind.Encode,
                AudioCodecId = "aac",
                AudioBitRateKbps = 192,
                SubtitleAction = SubtitleActionKind.Drop,
            });

        yield return Make(
            "烧入第一条字幕",
            "NVENC H.264 重编码并把第一条字幕烧进画面（需视频重编码）",
            p =>
            {
                p.EncoderId = "h264_nvenc";
                p.QualityMode = QualityMode.Simple;
                p.QualitySlider = 78;
                p.Container = "mp4";
                p.FastStart = true;
            },
            intent: new PresetTrackIntent
            {
                AudioAction = AudioActionKind.Copy,
                SubtitleAction = SubtitleActionKind.Burn,
                SubtitleFirstOnly = true,
            });

        yield return Make(
            "提取字幕（srt）",
            "视频音频直通，同时把内封字幕导出为 .srt 文件",
            p =>
            {
                p.VideoMode = VideoMode.Copy;
                p.Container = "mkv";
            },
            intent: new PresetTrackIntent
            {
                AudioAction = AudioActionKind.Copy,
                SubtitleAction = SubtitleActionKind.Extract,
                SubtitleExtractFormat = SubtitleFormat.Srt,
            });
    }

    private static Preset Make(
        string name,
        string description,
        Action<TranscodeParams> configure,
        PresetTrackIntent? intent = null)
    {
        var parameters = new TranscodeParams();
        configure(parameters);

        return new Preset
        {
            Id = "builtin-" + name.GetHashCode().ToString("x8"),
            Name = name,
            Description = description,
            IsBuiltIn = true,
            Parameters = parameters,
            Intent = intent ?? new PresetTrackIntent
            {
                AudioAction = AudioActionKind.Copy,
                SubtitleAction = SubtitleActionKind.Copy,
            },
        };
    }
}
