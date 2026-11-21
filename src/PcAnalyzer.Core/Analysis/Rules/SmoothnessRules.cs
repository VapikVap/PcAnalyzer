using PcAnalyzer.Core.Models;
using static PcAnalyzer.Core.Analysis.AnalysisContext;

namespace PcAnalyzer.Core.Analysis.Rules;

public sealed class StutterRule : IAnalysisRule
{
    public IEnumerable<Finding> Evaluate(AnalysisContext ctx)
    {
        if (ctx.FrameStats is not { } fs) yield break;
        double minutes = Math.Max(ctx.DurationSec / 60.0, 0.5);
        double perMin = ctx.Spikes.Count / minutes;
        int freezes = ctx.Freezes.Count;

        Severity sev =
            freezes >= 10 || perMin >= 20 ? Severity.Critical :
            freezes >= 3 || perMin >= 6 ? Severity.Major :
            freezes >= 1 || perMin >= 1.5 ? Severity.Minor : Severity.None;
        if (sev == Severity.None) yield break;

        var f = new Finding
        {
            Code = "stutter",
            Severity = sev,
            Confidence = Confidence.High,
            Title = freezes > 0 ? "Фризы и скачки времени кадра" : "Микрофризы (скачки времени кадра)",
            Description = $"Зафиксировано {ctx.Spikes.Count} скачков времени кадра (≈{F1(perMin)} в минуту)" +
                          (freezes > 0 ? $", из них {freezes} фризов дольше {F0(ctx.T.FreezeMs)} мс (худший — {F0(ctx.Freezes.Max(x => x.Ms))} мс)." : ".") +
                          " Даже при хорошем среднем FPS такие скачки ощущаются как подёргивания.",
            When = ctx.Ranges(i => ctx.SpikeAt[i], 1, 6),
            Impact = $"Плавность ниже, чем можно ожидать по среднему FPS: 1% low {F0(fs.P1LowFps)} при среднем {F0(fs.AvgFps)}.",
        };
        f.Evidence.Add($"Медианное время кадра {F1(fs.MedianMs)} мс; скачком считается кадр дольше {F1(Math.Max(fs.MedianMs * ctx.T.StutterMultiplier, fs.MedianMs + ctx.T.StutterMinDeltaMs))} мс");
        f.Evidence.Add($"Максимальное время кадра: {F0(fs.MaxMs)} мс");

        // Correlate spikes with other conditions so the user sees what coincided.
        var spikeIdx = ctx.Where(i => ctx.SpikeAt[i]).ToList();
        if (spikeIdx.Count > 0)
        {
            var co = new List<string>();
            void Check(string name, Func<TelemetrySample, bool> cond)
            {
                int n = spikeIdx.Count(i => cond(ctx.Samples[i]));
                if (n >= Math.Max(2, spikeIdx.Count * 0.3)) co.Add($"{name} — в {P(n / (double)spikeIdx.Count)} случаев");
            }
            Check("перегрузка ядра CPU", s => s.CpuMaxCoreLoad >= ctx.T.CpuBoundCoreLoad);
            Check("заполненная видеопамять", s => s.VramPercent >= ctx.T.VramFullPercent);
            Check("высокая активность диска", s => s.DiskActivePercent >= ctx.T.DiskBusyPercent);
            Check("чтение из файла подкачки", s => s.PageInputsPerSec >= 100);
            if (co.Count > 0) f.Evidence.Add("Совпадения со скачками: " + string.Join("; ", co));
            else f.Evidence.Add("Явных совпадений скачков с нагрузкой на CPU, VRAM, диск или память не найдено.");
        }

        f.Recommendations.Add("Ограничить FPS чуть ниже стабильного минимума (лимитом в игре или драйвере) — это выравнивает время кадра.");
        if (ctx.RefreshHz.HasValue)
            f.Recommendations.Add("Если монитор поддерживает G-Sync / FreeSync — включить его вместе с лимитом FPS на 3–5 кадров ниже частоты монитора.");
        f.Recommendations.Add("Ознакомиться с другими находками отчёта — они указывают, что совпадало со скачками.");
        yield return f;
    }
}

/// <summary>
/// Spikes concentrated at the beginning of a session with no storage/memory explanation
/// are typical for shader compilation. This is only ever a low-confidence hypothesis.
/// </summary>
public sealed class ShaderCompilationRule : IAnalysisRule
{
    public IEnumerable<Finding> Evaluate(AnalysisContext ctx)
    {
        if (ctx.FrameStats == null || ctx.Spikes.Count < 10 || ctx.DurationSec < 300) yield break;
        const double window = 180;
        int early = ctx.Spikes.Count(s => s.OffsetSec <= window);
        double earlyShare = early / (double)ctx.Spikes.Count;
        double windowShare = window / ctx.DurationSec;
        if (earlyShare < 0.5 || windowShare > 0.3) yield break;

        var earlyIdx = ctx.Spikes.Where(s => s.OffsetSec <= window && s.SampleIndex >= 0).Select(s => s.SampleIndex).Distinct().ToList();
        int diskBusy = earlyIdx.Count(i => ctx.Samples[i].DiskActivePercent >= ctx.T.DiskBusyPercent || ctx.Samples[i].PageInputsPerSec >= 100);
        if (earlyIdx.Count > 0 && diskBusy > earlyIdx.Count * 0.5) yield break; // better explained by storage

        var f = new Finding
        {
            Code = "shader_compilation",
            Severity = Severity.Minor,
            Confidence = Confidence.Low,
            Title = "Возможные фризы из-за компиляции шейдеров",
            Description = $"{P(earlyShare)} всех скачков времени кадра пришлись на первые 3 минуты игры, после чего игра шла заметно ровнее. " +
                          "Накопитель и память в эти моменты не были перегружены. Такая картина характерна для компиляции шейдеров при первом запуске, после обновления игры или драйвера.",
            When = new() { $"00:00–{Clock(window)}" },
            Impact = "Подёргивания в начале игры, которые обычно исчезают при повторных запусках.",
        };
        f.Evidence.Add($"Скачков в первые 3 минуты: {early} из {ctx.Spikes.Count}");
        f.Evidence.Add("Это предположение: прямых данных о компиляции шейдеров у приложения нет.");
        f.Recommendations.Add("Если игра предлагает предварительную компиляцию шейдеров — дождаться её завершения в меню.");
        f.Recommendations.Add("Не уменьшать размер кэша шейдеров в настройках драйвера; сравнить со следующей сессией — фризов в начале должно стать меньше.");
        yield return f;
    }
}

/// <summary>Performance in the last quarter of a long session vs the first quarter.</summary>
public sealed class DegradationRule : IAnalysisRule
{
    public IEnumerable<Finding> Evaluate(AnalysisContext ctx)
    {
        if (!ctx.HasFps || ctx.DurationSec < 600 || ctx.Cap != null) yield break;
        int n = ctx.Samples.Count;
        var first = Enumerable.Range(0, n / 4).ToList();
        var last = Enumerable.Range(n - n / 4, n / 4).ToList();
        double? f1 = ctx.Pct(s => s.Fps, 50, first);
        double? f2 = ctx.Pct(s => s.Fps, 50, last);
        if (f1 is not > 0 || f2 is null) yield break;
        double drop = 1 - f2.Value / f1.Value;
        if (drop < 0.15) yield break;

        var reasons = new List<string>();
        void TempRise(string name, Func<TelemetrySample, double?> temp, Func<TelemetrySample, double?> clock)
        {
            var t1 = ctx.Avg(temp, first); var t2 = ctx.Avg(temp, last);
            var c1 = ctx.Avg(clock, first); var c2 = ctx.Avg(clock, last);
            if (t1.HasValue && t2 - t1 >= 8 && (c1 is null || c2 < c1 * 0.95))
                reasons.Add($"температура {name} выросла с {F0(t1)} до {F0(t2)} °C" + (c1.HasValue ? $", частота снизилась с {F0(c1)} до {F0(c2)} МГц" : ""));
        }
        TempRise("CPU", s => s.CpuTempC, s => s.CpuClockMhz);
        TempRise("GPU", s => s.GpuTempC, s => s.GpuCoreClockMhz);
        var r1 = ctx.Avg(s => s.GameRamMb, first); var r2 = ctx.Avg(s => s.GameRamMb, last);
        if (r1 > 0 && r2 > r1 * 1.3) reasons.Add($"потребление памяти игрой выросло с {F0(r1)} до {F0(r2)} МБ (возможна утечка памяти в игре)");

        var f = new Finding
        {
            Code = "degradation",
            Severity = drop >= 0.25 ? Severity.Major : Severity.Minor,
            Confidence = reasons.Count > 0 ? Confidence.Medium : Confidence.Low,
            Title = "Производительность снижалась по ходу сессии",
            Description = $"Медианный FPS в начале игры — {F0(f1)}, в конце — {F0(f2)} (−{P(drop)}). " +
                          (reasons.Count > 0 ? "Одновременно: " + string.Join("; ", reasons) + "."
                                             : "Явной причины по записанным данным не найдено — возможно, в конце сессии были более тяжёлые сцены."),
            Impact = "Чем дольше игра, тем ниже FPS.",
        };
        f.Evidence.Add($"FPS: первая четверть {F0(f1)}, последняя {F0(f2)}");
        foreach (var r in reasons) f.Evidence.Add(r);
        if (reasons.Any(r => r.Contains("температура")))
            f.Recommendations.Add("Проверить охлаждение — нагрев к концу сессии снижает частоты (см. находки о температуре).");
        if (reasons.Any(r => r.Contains("памяти")))
            f.Recommendations.Add("Перезапускать игру во время длительных сессий и проверить наличие обновлений игры.");
        if (reasons.Count == 0)
            f.Recommendations.Add("Сравнить с ещё одной сессией в той же локации, чтобы исключить разницу сцен.");
        yield return f;
    }
}
