using PcAnalyzer.Core.Abstractions;
using PcAnalyzer.Core.Analysis;
using PcAnalyzer.Core.Models;

namespace PcAnalyzer.Core.Services;

/// <summary>Lightweight per-tick checks during recording. Each kind of event is throttled.</summary>
public sealed class LiveEventDetector
{
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(30);
    private readonly Queue<double> _fps = new();
    private readonly Dictionary<string, DateTime> _last = new();

    public void Reset()
    {
        _fps.Clear();
        _last.Clear();
    }

    public IEnumerable<LiveEvent> Process(TelemetrySample s, float[] frames, DateTime sessionStart, AnalysisThresholds t)
    {
        var now = s.TimestampUtc;
        double off = (now - sessionStart).TotalSeconds;

        if (frames.Length > 0)
        {
            float worst = frames.Max();
            if (worst >= t.FreezeMs && Allow("freeze", now))
                yield return Ev("Фриз", $"Кадр длился {worst:0} мс", Severity.Major);
        }

        if (s.Fps is { } fps)
        {
            if (_fps.Count >= 10)
            {
                double med = FrameStatistics.Percentile(_fps, 50);
                if (med >= 20 && fps < med * (1 - t.FpsDropPercent / 100) && Allow("drop", now))
                    yield return Ev("Просадка FPS", $"FPS упал с ~{med:0} до {fps:0}", Severity.Minor);
            }
            _fps.Enqueue(fps);
            while (_fps.Count > 30) _fps.Dequeue();
        }

        if (s.CpuTempC >= t.CpuTempWarnC && Allow("cpu_temp", now))
            yield return Ev("Температура CPU", $"Процессор нагрелся до {s.CpuTempC:0} °C", Severity.Major);
        if (s.GpuTempC >= t.GpuTempWarnC && Allow("gpu_temp", now))
            yield return Ev("Температура GPU", $"Видеокарта нагрелась до {s.GpuTempC:0} °C", Severity.Major);
        if (s.VramPercent >= t.VramFullPercent && Allow("vram", now))
            yield return Ev("Видеопамять", $"VRAM заполнена на {s.VramPercent:0}%", Severity.Minor);
        if (s.RamPercent >= t.RamFullPercent && Allow("ram", now))
            yield return Ev("Оперативная память", $"RAM заполнена на {s.RamPercent:0}%", Severity.Minor);

        LiveEvent Ev(string kind, string msg, Severity sev) => new()
        {
            TimestampUtc = now, OffsetSec = off, Kind = kind, Message = msg, Severity = sev
        };
    }

    private bool Allow(string key, DateTime now)
    {
        if (_last.TryGetValue(key, out var t) && now - t < Cooldown) return false;
        _last[key] = now;
        return true;
    }
}
