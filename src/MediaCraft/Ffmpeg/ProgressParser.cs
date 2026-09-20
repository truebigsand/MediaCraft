using System.Globalization;

namespace MediaCraft.Ffmpeg;

/// <summary>一次转码的实时进度。</summary>
public sealed class TranscodeProgress
{
    public TimeSpan OutTime { get; init; }

    /// <summary>相对实时的速度倍率；0 表示未知。</summary>
    public double Speed { get; init; }

    public double Fps { get; init; }

    public long TotalSizeBytes { get; init; }

    public long Frame { get; init; }

    /// <summary>0-100；总时长未知时为 -1。</summary>
    public double Percent { get; init; }

    public TimeSpan? Eta { get; init; }

    public string PercentText => Percent < 0 ? "—" : Percent.ToString("0.0", CultureInfo.InvariantCulture) + "%";

    public string SpeedText => Speed > 0 ? Speed.ToString("0.##", CultureInfo.InvariantCulture) + "×" : "—";

    public string EtaText => Eta is null
        ? "—"
        : MediaFormat.FormatDuration(Eta.Value);

    public string FpsText => Fps > 0 ? Fps.ToString("0.#", CultureInfo.InvariantCulture) : "—";
}

/// <summary>
/// 解析 `ffmpeg -progress pipe:1` 的 key=value 输出。
/// 其中 out_time 是权威时间戳（out_time_ms 在 ffmpeg 里其实是微秒，不能按毫秒用）。
/// </summary>
public sealed class ProgressParser
{
    private readonly object _sync = new();
    private TimeSpan _outTime;
    private double _speed;
    private double _fps;
    private long _totalSize;
    private long _frame;

    public ProgressParser(TimeSpan? expectedDuration)
    {
        ExpectedDuration = expectedDuration;
    }

    /// <summary>进度分母（源文件时长）。</summary>
    public TimeSpan? ExpectedDuration { get; set; }

    public void Feed(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        var separator = line.IndexOf('=');
        if (separator <= 0)
        {
            return;
        }

        var key = line[..separator].Trim();
        var value = line[(separator + 1)..].Trim();

        lock (_sync)
        {
            switch (key)
            {
                case "out_time":
                    if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsedTime))
                    {
                        _outTime = parsedTime;
                    }

                    break;

                case "out_time_us":
                case "out_time_ms":
                    // 两者都是微秒（ffmpeg 的历史遗留命名）
                    if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var micros) && micros >= 0)
                    {
                        _outTime = TimeSpan.FromMicroseconds(micros);
                    }

                    break;

                case "speed":
                    _speed = ParseSpeed(value);
                    break;

                case "fps":
                    if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var fps) && fps > 0)
                    {
                        _fps = fps;
                    }

                    break;

                case "total_size":
                    if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) && size > 0)
                    {
                        _totalSize = size;
                    }

                    break;

                case "frame":
                    if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var frame) && frame > 0)
                    {
                        _frame = frame;
                    }

                    break;
            }
        }
    }

    public TranscodeProgress Snapshot()
    {
        lock (_sync)
        {
            var percent = -1.0;
            TimeSpan? eta = null;

            var duration = ExpectedDuration;
            if (duration is { TotalSeconds: > 0 })
            {
                percent = Math.Clamp(_outTime.TotalSeconds / duration.Value.TotalSeconds * 100.0, 0, 100);

                if (_speed > 0.01)
                {
                    var remaining = duration.Value - _outTime;
                    if (remaining > TimeSpan.Zero)
                    {
                        eta = TimeSpan.FromSeconds(remaining.TotalSeconds / _speed);
                    }
                    else
                    {
                        eta = TimeSpan.Zero;
                    }
                }
            }

            return new TranscodeProgress
            {
                OutTime = _outTime,
                Speed = _speed,
                Fps = _fps,
                TotalSizeBytes = _totalSize,
                Frame = _frame,
                Percent = percent,
                Eta = eta,
            };
        }
    }

    /// <summary>解析 "1.23x" / "N/A" 形式的速度。</summary>
    public static double ParseSpeed(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        var text = value.Trim().TrimEnd('x', 'X').Trim();
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed) && speed > 0
            ? speed
            : 0;
    }
}
