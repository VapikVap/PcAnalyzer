using PcAnalyzer.Core.Models;

namespace PcAnalyzer.Core.Analysis;

public enum DeltaVerdict { Better, Worse, Same, NotComparable }

public sealed record MetricDelta(string Name, string Unit, double? Before, double? After, bool HigherIsBetter)
{
    public double? Delta => Before.HasValue && After.HasValue ? After - Before : null;
    public double? DeltaPercent => Before is > 0 && After.HasValue ? (After - Before) / Before * 100 : null;

    public DeltaVerdict Verdict
    {
        get
        {
            if (DeltaPercent is not { } p) return DeltaVerdict.NotComparable;
            if (Math.Abs(p) < SessionComparer.NoisePercent) return DeltaVerdict.Same;
            return (p > 0) == HigherIsBetter ? DeltaVerdict.Better : DeltaVerdict.Worse;
        }
    }
}

public sealed class ComparisonResult
{
    public List<MetricDelta> Metrics { get; } = new();
    public string Verdict { get; set; } = "";
    public List<string> Warnings { get; } = new();
}

/// <summary>Compares two sessions ("before" vs "after" a settings change).</summary>
public static class SessionComparer
{
    /// <summary>Differences smaller than this are treated as measurement noise.</summary>
    public const double NoisePercent = 3;

    public static ComparisonResult Compare(GameSession before, GameSession after)
    {
        var r = new ComparisonResult();
        var a = before.Summary ?? new SessionSummary();
        var b = after.Summary ?? new SessionSummary();

        r.Metrics.Add(new("Средний FPS", "", a.AvgFps, b.AvgFps, true));
        r.Metrics.Add(new("1% Low", "FPS", a.P1LowFps, b.P1LowFps, true));
        r.Metrics.Add(new("0.1% Low", "FPS", a.P01LowFps, b.P01LowFps, true));
        r.Metrics.Add(new("Среднее время кадра", "мс", a.AvgFrameTimeMs, b.AvgFrameTimeMs, false));
        r.Metrics.Add(new("Фризы", "шт", a.FreezeCount, b.FreezeCount, false));
        r.Metrics.Add(new("Плавность", "/100", a.SmoothnessScore, b.SmoothnessScore, true));
        r.Metrics.Add(new("Загрузка CPU", "%", a.AvgCpu, b.AvgCpu, false));
        r.Metrics.Add(new("Самое загруженное ядро", "%", a.AvgMaxCore, b.AvgMaxCore, false));
        r.Metrics.Add(new("Загрузка GPU", "%", a.AvgGpu, b.AvgGpu, true));
        r.Metrics.Add(new("Макс. температура CPU", "°C", a.MaxCpuTemp, b.MaxCpuTemp, false));
        r.Metrics.Add(new("Макс. температура GPU", "°C", a.MaxGpuTemp, b.MaxGpuTemp, false));
        r.Metrics.Add(new("Пик VRAM", "МБ", a.MaxVramMb, b.MaxVramMb, false));
        r.Metrics.Add(new("Пик RAM", "МБ", a.MaxRamUsedMb, b.MaxRamUsedMb, false));

        if (!string.Equals(before.GameName, after.GameName, StringComparison.OrdinalIgnoreCase))
            r.Warnings.Add("Сравниваются разные игры — выводы условны.");
        if (before.Hardware?.Cpu.Name != after.Hardware?.Cpu.Name || before.Hardware?.PrimaryGpu?.Name != after.Hardware?.PrimaryGpu?.Name)
            r.Warnings.Add("Между сессиями изменилось железо.");
        if (before.Hardware?.PrimaryGpu?.DriverVersion is { } d1 && after.Hardware?.PrimaryGpu?.DriverVersion is { } d2 && d1 != d2)
            r.Warnings.Add($"Между сессиями обновился драйвер видеокарты ({d1} → {d2}).");
        if (Math.Abs(before.Duration.TotalMinutes - after.Duration.TotalMinutes) > Math.Max(before.Duration.TotalMinutes, after.Duration.TotalMinutes) * 0.5)
            r.Warnings.Add("Сессии сильно отличаются по длительности — сравнивайте похожие сценарии.");

        var fps = r.Metrics[0];
        var low = r.Metrics[1];
        if (fps.Verdict == DeltaVerdict.NotComparable)
        {
            r.Verdict = "Нет данных FPS в одной из сессий — оценить результат изменений нельзя. Сравните загрузку и температуры ниже.";
        }
        else if (fps.Verdict == DeltaVerdict.Same && low.Verdict is DeltaVerdict.Same or DeltaVerdict.NotComparable)
        {
            r.Verdict = $"Изменения не дали заметного эффекта: разница в пределах погрешности (±{NoisePercent}%).";
        }
        else
        {
            static string Word(MetricDelta m) => m.Verdict switch
            {
                DeltaVerdict.Better => $"вырос на {Math.Abs(m.DeltaPercent!.Value):0.#}%",
                DeltaVerdict.Worse => $"снизился на {Math.Abs(m.DeltaPercent!.Value):0.#}%",
                DeltaVerdict.Same => "почти не изменился",
                _ => "не сравнивается"
            };
            bool better = fps.Verdict != DeltaVerdict.Worse && low.Verdict != DeltaVerdict.Worse;
            bool worse = fps.Verdict != DeltaVerdict.Better && low.Verdict != DeltaVerdict.Better;
            string head = better ? "Стало лучше" : worse ? "Стало хуже" : "Результат смешанный";
            r.Verdict = $"{head}: средний FPS {Word(fps)}, 1% low {Word(low)}.";
        }
        return r;
    }
}
