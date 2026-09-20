using System.Text.Json.Serialization;
using MediaCraft.Media;

namespace MediaCraft.Presets;

/// <summary>
/// 预设对轨道动作的「意图」。
///
/// 为什么不直接存轨道列表：轨道选择是按流索引走的，而索引因文件而异，
/// 所以预设只记录「怎么做」（直通/重编码/烧第一条/丢弃），套用时再落到目标文件自己的轨道上。
/// </summary>
public sealed class PresetTrackIntent
{
    /// <summary>音轨动作；null = 不改变目标文件的音轨设置。</summary>
    public AudioActionKind? AudioAction { get; set; }

    public string? AudioCodecId { get; set; }

    public int? AudioBitRateKbps { get; set; }

    /// <summary>字幕动作；null = 不改变。</summary>
    public SubtitleActionKind? SubtitleAction { get; set; }

    /// <summary>提取格式（SubtitleAction = Extract 时有效）。</summary>
    public SubtitleFormat? SubtitleExtractFormat { get; set; }

    /// <summary>字幕动作只作用于第一条字幕轨（烧入通常只烧一条）。</summary>
    public bool SubtitleFirstOnly { get; set; }

    /// <summary>从一组轨道动作里归纳出意图（「另存为预设」时使用）。</summary>
    public static PresetTrackIntent FromParams(TranscodeParams parameters)
    {
        var intent = new PresetTrackIntent();

        var audioActions = parameters.AudioTracks.Select(t => t.Action).Distinct().ToArray();
        if (audioActions.Length == 1)
        {
            intent.AudioAction = audioActions[0];
        }
        else if (audioActions.Length > 1)
        {
            // 混合动作无法用一个意图表达：统一按「直通」处理，并在描述里说明
            intent.AudioAction = AudioActionKind.Copy;
        }

        var encoded = parameters.AudioTracks.FirstOrDefault(t => t.Action == AudioActionKind.Encode);
        if (encoded is not null)
        {
            intent.AudioCodecId = encoded.CodecId;
            intent.AudioBitRateKbps = encoded.BitRateKbps;
        }

        var subtitleActions = parameters.SubtitleTracks.Select(t => t.Action).Distinct().ToArray();
        if (subtitleActions.Length == 1)
        {
            intent.SubtitleAction = subtitleActions[0];
        }
        else if (parameters.SubtitleTracks.Any(t => t.Action == SubtitleActionKind.Burn))
        {
            intent.SubtitleAction = SubtitleActionKind.Burn;
            intent.SubtitleFirstOnly = true;
        }

        var extracted = parameters.SubtitleTracks.FirstOrDefault(t => t.Action == SubtitleActionKind.Extract);
        if (extracted is not null)
        {
            intent.SubtitleExtractFormat = extracted.ExtractFormat;
        }

        return intent;
    }

    public PresetTrackIntent Clone() => new()
    {
        AudioAction = AudioAction,
        AudioCodecId = AudioCodecId,
        AudioBitRateKbps = AudioBitRateKbps,
        SubtitleAction = SubtitleAction,
        SubtitleExtractFormat = SubtitleExtractFormat,
        SubtitleFirstOnly = SubtitleFirstOnly,
    };

    /// <summary>人类可读的意图描述。</summary>
    [JsonIgnore]
    public string Description
    {
        get
        {
            var parts = new List<string>();

            if (AudioAction is not null)
            {
                parts.Add(AudioAction switch
                {
                    AudioActionKind.Copy => "音轨直通",
                    AudioActionKind.Encode => $"音轨重编码 {AudioCodecId ?? "aac"}" +
                                              (AudioBitRateKbps is > 0 ? $" {AudioBitRateKbps}k" : string.Empty),
                    _ => "音轨丢弃",
                });
            }

            if (SubtitleAction is not null)
            {
                parts.Add(SubtitleAction switch
                {
                    SubtitleActionKind.Copy => "字幕内封",
                    SubtitleActionKind.Burn => SubtitleFirstOnly ? "烧入第一条字幕" : "烧入字幕",
                    SubtitleActionKind.Extract => $"提取字幕（{SubtitleExtractFormat?.ToString().ToUpperInvariant() ?? "SRT"}）",
                    _ => "字幕丢弃",
                });
            }

            return parts.Count == 0 ? "不改动轨道设置" : string.Join("、", parts);
        }
    }
}

/// <summary>一套可复用的转码预设。</summary>
public sealed class Preset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    /// <summary>内置预设（代码定义，不可删除，可「另存为」副本）。</summary>
    public bool IsBuiltIn { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>参数快照（不含轨道列表，轨道用 <see cref="Intent"/> 表达）。</summary>
    public TranscodeParams Parameters { get; set; } = new();

    public PresetTrackIntent Intent { get; set; } = new();

    /// <summary>列表里显示的一行摘要。</summary>
    [JsonIgnore]
    public string Summary => Parameters.Summary;

    /// <summary>轨道意图摘要。</summary>
    [JsonIgnore]
    public string IntentSummary => Intent.Description;

    /// <summary>完整说明（名称 + 描述 + 摘要），用于列表与提示。</summary>
    [JsonIgnore]
    public string FullDescription
    {
        get
        {
            var parts = new List<string> { Summary, IntentSummary };
            if (!string.IsNullOrWhiteSpace(Description))
            {
                parts.Add(Description);
            }

            return string.Join("　·　", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        }
    }

    public Preset Clone() => new()
    {
        Id = Id,
        Name = Name,
        Description = Description,
        IsBuiltIn = IsBuiltIn,
        CreatedAt = CreatedAt,
        Parameters = Parameters.Clone(),
        Intent = Intent.Clone(),
    };
}

/// <summary>预设文件的落盘结构（同时用于导入导出）。</summary>
public sealed class PresetFile
{
    public int Version { get; set; } = 1;

    public DateTime SavedAt { get; set; } = DateTime.Now;

    public List<Preset> Presets { get; set; } = [];
}
