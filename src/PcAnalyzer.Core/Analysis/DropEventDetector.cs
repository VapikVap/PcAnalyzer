using PcAnalyzer.Core.Models;
using static PcAnalyzer.Core.Analysis.AnalysisContext;

namespace PcAnalyzer.Core.Analysis;

/// <summary>Finds FPS drops (vs a rolling median) and freezes, and lists what coincided with each.</summary>
public static class DropEventDetector
{
    public const int MaxEvents = 60;

    public static List<PerformanceEvent> Detect(AnalysisContext ctx)
    {
        var events = new List<PerformanceEvent>();
        var s = ctx.Samples;
        double factor = 1 - ctx.T.FpsDropPercent / 100.0;
        int window = Math.Max(10, (int)Math.Round(30 / ctx.IntervalSec));

        if (ctx.HasFps)
        {
            int i = 0;
            while (i < s.Count)
            {
                double? med = RollingMedian(s, i, window);
                if (s[i].Fps is { } fps && med is >= 20 && fps < med * factor)
                {
                    int start = i;
                    double worst = fps;
                    while (i + 1 < s.Count && s[i + 1].Fps is { } f2 && f2 < med * factor) { i++; worst = Math.Min(worst, f2); }
                    var ev = new PerformanceEvent
                    {
                        OffsetSec = ctx.Offset(start),
                        DurationSec = (i - start + 1) * ctx.IntervalSec,
                        Kind = "Просадка FPS",
                        FpsBefore = med,
                        FpsDuring = worst,
                        WorstFrameMs = Enumerable.Range(start, i - start + 1).Select(k => s[k].FrameTimeMaxMs).Max(),
                    };
                    ev.LikelyCauses.AddRange(Causes(ctx, start, i));
                    events.Add(ev);
                }
                i++;
            }
        }

        // Freezes that are not inside an already detected drop.
        foreach (var fz in ctx.Freezes)
        {
            if (events.Any(e => fz.OffsetSec >= e.OffsetSec - 1 && fz.OffsetSec <= e.OffsetSec + e.DurationSec + 1)) continue;
            var ev = new PerformanceEvent
            {
                OffsetSec = fz.OffsetSec,
                DurationSec = fz.Ms / 1000.0,
                Kind = "Фриз",
                WorstFrameMs = fz.Ms,
                FpsBefore = fz.SampleIndex > 0 ? RollingMedian(s, fz.SampleIndex, window) : null,
            };
            if (fz.SampleIndex >= 0) ev.LikelyCauses.AddRange(Causes(ctx, fz.SampleIndex, fz.SampleIndex));
            events.Add(ev);
        }

        return events.OrderBy(e => e.OffsetSec).Take(MaxEvents).ToList();
    }

    private static double? RollingMedian(IReadOnlyList<TelemetrySample> s, int i, int window)
    {
        var vals = new List<double>();
        for (int k = Math.Max(0, i - window); k < i; k++)
            if (s[k].Fps is { } f) vals.Add(f);
        return vals.Count >= 5 ? FrameStatistics.Percentile(vals, 50) : null;
    }

    private static IEnumerable<string> Causes(AnalysisContext ctx, int a, int b)
    {
        var range = Enumerable.Range(a, b - a + 1).Select(i => ctx.Samples[i]).ToList();
        var t = ctx.T;
        var list = new List<string>();
        double? gpu = range.Select(x => x.GpuLoad).Where(v => v.HasValue).DefaultIfEmpty().Average();
        double? core = range.Select(x => x.CpuMaxCoreLoad).Where(v => v.HasValue).DefaultIfEmpty().Max();

        if (core >= t.CpuBoundCoreLoad && gpu < t.GpuNotBoundLoad)
            list.Add($"Процессор: ядро загружено на {F0(core)}%, GPU ждал (загрузка {F0(gpu)}%)");
        else if (gpu >= t.GpuBoundLoad)
            list.Add($"Тяжёлая сцена: видеокарта на пределе ({F0(gpu)}%)");
        if (range.Any(x => x.VramPercent >= t.VramFullPercent)) list.Add("Видеопамять заполнена");
        if (range.Any(x => x.RamPercent >= t.RamFullPercent)) list.Add("Оперативная память почти заполнена");
        if (range.Any(x => x.PageInputsPerSec >= 100)) list.Add($"Чтение из файла подкачки ({F0(range.Max(x => x.PageInputsPerSec))} стр/с)");
        if (range.Any(x => x.DiskActivePercent >= t.DiskBusyPercent)) list.Add($"Накопитель занят на {F0(range.Max(x => x.DiskActivePercent))}%");
        if (range.Any(x => x.CpuTempC >= t.CpuTempWarnC)) list.Add($"Температура CPU {F0(range.Max(x => x.CpuTempC))} °C");
        if (range.Any(x => x.GpuTempC >= t.GpuTempWarnC)) list.Add($"Температура GPU {F0(range.Max(x => x.GpuTempC))} °C");

        // Background process activity: nearest snapshot within ±5 samples.
        string? game = Path.GetFileNameWithoutExtension(ctx.Session.ExeName ?? "");
        for (int k = Math.Max(0, a - 5); k <= Math.Min(ctx.Samples.Count - 1, b + 5); k++)
        {
            var top = ctx.Samples[k].TopProcesses;
            if (top == null) continue;
            var bg = top.FirstOrDefault(p => p.CpuPercent >= 10 && !p.Name.Equals(game, StringComparison.OrdinalIgnoreCase) && p.Name != "Idle");
            if (bg != null) { list.Add($"Фоновый процесс {bg.Name}: {F0(bg.CpuPercent)}% CPU"); break; }
        }

        if (list.Count == 0) list.Add("Причина по записанным данным не установлена");
        return list;
    }
}
