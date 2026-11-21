using PcAnalyzer.Core.Models;
using static PcAnalyzer.Core.Analysis.AnalysisContext;

namespace PcAnalyzer.Core.Analysis.Rules;

public sealed class GpuBoundRule : IAnalysisRule
{
    public IEnumerable<Finding> Evaluate(AnalysisContext ctx)
    {
        if (!ctx.HasGpuLoad) yield break;
        bool Valid(int i) => ctx.Samples[i].GpuLoad.HasValue;
        double share = ctx.ShareIdx(i => ctx.Limiter[i] == LimiterKind.Gpu, Valid);
        if (share < 0.5) yield break;

        var idx = ctx.Where(i => ctx.Limiter[i] == LimiterKind.Gpu).ToList();
        double? gpu = ctx.Avg(s => s.GpuLoad, idx);
        double? core = ctx.Avg(s => s.CpuMaxCoreLoad, idx);
        double? avgFps = ctx.FrameStats?.AvgFps;
        double target = RuleMath.Target(ctx);

        var sev = Severity.None;
        if (avgFps is { } f)
        {
            if (f < ctx.T.TargetFps) sev = Severity.Major;
            else if (f < target * 0.8) sev = Severity.Minor;
        }

        var finding = new Finding
        {
            Code = "gpu_bound",
            Severity = sev,
            Confidence = ctx.HasFps && share > 0.75 ? Confidence.High : Confidence.Medium,
            Title = "Основное ограничение — видеокарта",
            Description = $"Видеокарта {P(share)} времени работала с загрузкой {F0(ctx.T.GpuBoundLoad)}% и выше (в среднем {F0(gpu)}%). " +
                          "Значит, FPS в этом сценарии определялся прежде всего мощностью видеокарты, а процессор успевал готовить кадры.",
            Share = share,
            Impact = sev == Severity.None
                ? "Это нормальная ситуация для игр: видеокарта используется полностью."
                : $"Средний FPS {F0(avgFps)} ниже комфортного уровня ({F0(target)}), и упирается он в видеокарту.",
        };
        finding.Evidence.Add($"Загрузка GPU ≥ {F0(ctx.T.GpuBoundLoad)}%: {P(share)} времени, средняя {F0(gpu)}%");
        if (core.HasValue) finding.Evidence.Add($"Самое загруженное ядро CPU в эти моменты: в среднем {F0(core)}% — процессор не был пределом");
        if (avgFps.HasValue) finding.Evidence.Add($"Средний FPS: {F0(avgFps)}");

        if (sev == Severity.None)
        {
            finding.Recommendations.Add("Если FPS вас устраивает — ничего менять не нужно: полная загрузка видеокарты является нормой.");
        }
        else
        {
            finding.Recommendations.Add("Снизить разрешение рендеринга или включить апскейлинг (DLSS / FSR / XeSS) — самый сильный рычаг при ограничении видеокартой.");
            finding.Recommendations.Add("Снизить самые тяжёлые для GPU настройки: качество теней, отражения (SSR), объёмное освещение и туман, трассировку лучей, MSAA.");
            finding.Recommendations.Add("Проверить, вырастет ли FPS при снижении разрешения: если да — ограничение видеокартой подтверждено.");
        }
        yield return finding;
    }
}

public sealed class CpuBoundRule : IAnalysisRule
{
    public IEnumerable<Finding> Evaluate(AnalysisContext ctx)
    {
        if (!ctx.HasGpuLoad) yield break;
        bool Valid(int i) => ctx.Samples[i].GpuLoad.HasValue;
        double share = ctx.ShareIdx(i => ctx.Limiter[i] == LimiterKind.Cpu, Valid);
        if (share < 0.2) yield break;

        var idx = ctx.Where(i => ctx.Limiter[i] == LimiterKind.Cpu).ToList();
        double? gpu = ctx.Avg(s => s.GpuLoad, idx);
        double? cpu = ctx.Avg(s => s.CpuLoad, idx);
        double? core = ctx.Avg(s => s.CpuMaxCoreLoad, idx);
        double? game = ctx.Avg(s => s.GameCpuPercent, idx);
        bool singleThread = cpu is < 60 && core is >= 90;

        double fpsCov = ctx.HasFps ? RuleMath.CoefficientOfVariation(idx.Select(i => ctx.Samples[i].Fps).Where(v => v.HasValue).Select(v => v!.Value)) : 0;
        double? avgFps = ctx.FrameStats?.AvgFps;
        double target = RuleMath.Target(ctx);

        var sev = share >= 0.5 ? Severity.Major : Severity.Minor;
        if (avgFps is { } f && f >= target * 0.95 && fpsCov < 0.1) sev = Severity.Minor;

        var conf = ctx.HasFps && ctx.HasCoreLoads && share >= 0.4 ? Confidence.High
                 : ctx.HasCoreLoads ? Confidence.Medium : Confidence.Low;

        var finding = new Finding
        {
            Code = singleThread ? "cpu_single_thread" : "cpu_bound",
            Severity = sev,
            Confidence = conf,
            Title = singleThread ? "Ограничение процессором: перегружены один-два потока" : "Ограничение со стороны процессора",
            Description = singleThread
                ? $"Около {P(share)} времени видеокарта была загружена лишь на {F0(gpu)}%, а самое загруженное ядро процессора работало на {F0(core)}%, " +
                  $"хотя общая загрузка CPU выглядела умеренной ({F0(cpu)}%). Игра упирается в скорость одного-двух потоков процессора — видеокарта ждёт кадры."
                : $"Около {P(share)} времени видеокарта была загружена лишь на {F0(gpu)}%, а процессор работал на пределе " +
                  $"(общая загрузка {F0(cpu)}%, самое загруженное ядро {F0(core)}%). Видеокарта ждала процессор.",
            When = ctx.Ranges(i => ctx.Limiter[i] == LimiterKind.Cpu, 5),
            Share = share,
            Impact = fpsCov >= 0.15
                ? "FPS в эти моменты заметно колебался — это типичный признак упора в процессор."
                : "В эти моменты FPS ограничивался процессором; более мощная видеокарта здесь бы не помогла.",
        };
        finding.Evidence.Add($"Средняя загрузка GPU в эти периоды: {F0(gpu)}% (ниже {F0(ctx.T.GpuNotBoundLoad)}%)");
        finding.Evidence.Add($"Самое загруженное ядро CPU: {F0(core)}%, общая загрузка CPU: {F0(cpu)}%");
        if (game.HasValue) finding.Evidence.Add($"Процесс игры использовал {F0(game)}% всего процессора");
        if (ctx.HasFps) finding.Evidence.Add($"Разброс FPS в эти моменты: {F0(fpsCov * 100)}%");
        if (singleThread)
            finding.Evidence.Add("Windows переносит потоки между ядрами, поэтому реальная перегрузка одного потока может быть выше, чем видно по отдельным ядрам.");

        finding.Recommendations.Add("Снизить настройки, нагружающие процессор: дальность прорисовки, плотность толпы/NPC, качество физики, детализацию объектов и растительности.");
        finding.Recommendations.Add("Закрыть фоновые программы, нагружающие процессор (см. раздел о фоновых процессах, если он есть в отчёте).");
        finding.Recommendations.Add("Разрешение и «графические» настройки (сглаживание, текстуры) здесь почти не влияют на FPS — их можно повысить без потери кадров.");
        if (sev == Severity.Major && share >= 0.6)
        {
            var cpuName = ctx.Hardware?.Cpu.Name;
            finding.Recommendations.Add($"Если такая картина повторяется в разных играх, апгрейд процессора{(cpuName != null ? $" ({cpuName})" : "")} даст больше, чем апгрейд видеокарты.");
        }
        yield return finding;
    }
}

/// <summary>
/// The CPU is saturated most of the time (high total load) while the GPU also works hard, so neither
/// the pure GPU-bound nor the pure CPU-bound rule fires. Typical for older 4-core CPUs in modern games.
/// </summary>
public sealed class CpuSaturationRule : IAnalysisRule
{
    public IEnumerable<Finding> Evaluate(AnalysisContext ctx)
    {
        if (!ctx.Samples.Any(s => s.CpuLoad.HasValue)) yield break;
        bool High(int i) => ctx.Samples[i].CpuLoad >= ctx.T.CpuBoundTotalLoad;
        double share = ctx.ShareIdx(High, i => ctx.Samples[i].CpuLoad.HasValue);
        if (share < 0.4) yield break;
        // Already explained as a clear CPU bottleneck.
        if (ctx.HasGpuLoad && ctx.ShareIdx(i => ctx.Limiter[i] == LimiterKind.Cpu, i => ctx.Samples[i].GpuLoad.HasValue) >= 0.2) yield break;

        var idx = ctx.Where(High).ToList();
        double? cpu = ctx.Avg(s => s.CpuLoad, idx);
        double? gpu = ctx.Avg(s => s.GpuLoad, idx);
        double? game = ctx.Avg(s => s.GameCpuPercent, idx);
        double bothShare = ctx.HasGpuLoad ? ctx.ShareIdx(i => ctx.Limiter[i] == LimiterKind.Both, i => ctx.Samples[i].GpuLoad.HasValue) : 0;

        // Do frame spikes happen more often while the CPU is saturated?
        int spikeHigh = idx.Count(i => ctx.SpikeAt[i]);
        int lowCount = ctx.Samples.Count - idx.Count;
        int spikeLow = ctx.Where(i => !High(i) && ctx.SpikeAt[i]).Count();
        double rateHigh = idx.Count == 0 ? 0 : spikeHigh / (double)idx.Count;
        double rateLow = lowCount == 0 ? 0 : spikeLow / (double)lowCount;
        bool spikesCorrelate = ctx.FrameStats != null && spikeHigh >= 5 && rateHigh > rateLow * 1.5;

        var f = new Finding
        {
            Code = "cpu_saturated",
            Severity = share >= 0.6 ? Severity.Major : Severity.Minor,
            Confidence = spikesCorrelate || bothShare >= 0.3 ? Confidence.Medium : Confidence.Low,
            Title = bothShare >= 0.3 ? "Процессор и видеокарта работают на пределе одновременно" : "Процессор почти всё время загружен на пределе",
            Description = $"{P(share)} времени общая загрузка процессора была {F0(ctx.T.CpuBoundTotalLoad)}% и выше (в среднем {F0(cpu)}%)" +
                          (gpu.HasValue ? $", а видеокарта в эти моменты — {F0(gpu)}%. " : ". ") +
                          "У процессора не остаётся запаса: любая дополнительная нагрузка (сложная сцена, фоновая программа, подгрузка) сразу отражается на времени кадра. " +
                          (bothShare >= 0.3 ? "Видеокарта тоже близка к пределу, поэтому снижение только графики даст ограниченный эффект." : ""),
            When = ctx.Ranges(High, 5),
            Share = share,
            Impact = spikesCorrelate
                ? "Скачки времени кадра заметно чаще случались именно при перегрузке процессора."
                : "Мало запаса производительности; FPS чувствителен к фоновой нагрузке.",
        };
        f.Evidence.Add($"Загрузка CPU ≥ {F0(ctx.T.CpuBoundTotalLoad)}%: {P(share)} времени, в среднем {F0(cpu)}%");
        if (game.HasValue) f.Evidence.Add($"Сама игра использовала в среднем {F0(game)}% всего процессора");
        if (gpu.HasValue) f.Evidence.Add($"Загрузка GPU в эти моменты: {F0(gpu)}%");
        if (ctx.FrameStats != null)
            f.Evidence.Add($"Скачки времени кадра: {F1(rateHigh * 100)}% замеров при перегрузке CPU против {F1(rateLow * 100)}% в остальное время");
        var cpuName = ctx.Hardware?.Cpu.Name;
        f.Recommendations.Add("Снизить настройки, нагружающие процессор: дальность прорисовки, плотность объектов и NPC, качество физики и теней от динамических объектов.");
        f.Recommendations.Add("Закрыть перед игрой браузер, лаунчеры, оверлеи и другие фоновые программы — у процессора нет запаса.");
        f.Recommendations.Add("Ограничить FPS немного ниже среднего значения — это снимает пики нагрузки на процессор и выравнивает время кадра.");
        if (share >= 0.6)
            f.Recommendations.Add($"Если так во многих играх — процессор{(cpuName != null ? $" ({cpuName})" : "")} стал узким местом системы; апгрейд CPU даст наибольший эффект.");
        yield return f;
    }
}

public sealed class FpsCapRule : IAnalysisRule
{
    public IEnumerable<Finding> Evaluate(AnalysisContext ctx)
    {
        if (ctx.Cap is not { } cap) yield break;
        double? gpu = ctx.Pct(s => s.GpuLoad, 50);
        var f = new Finding
        {
            Code = "fps_cap",
            Severity = Severity.None,
            Confidence = cap.MatchesRefreshRate ? Confidence.High : Confidence.Medium,
            Title = cap.MatchesRefreshRate ? "FPS ограничен V-Sync / частотой монитора" : "FPS ограничен лимитом кадров",
            Description = $"FPS {P(cap.Share)} времени держался около {F0(cap.Fps)}" +
                          (gpu.HasValue ? $", при этом видеокарта была загружена в среднем на {F0(gpu)}%" : "") +
                          (cap.MatchesRefreshRate ? $". Это совпадает с частотой монитора ({ctx.RefreshHz} Гц) — вероятно, включён V-Sync или лимит под монитор." : ". Похоже на ограничитель кадров в игре или драйвере."),
            Share = cap.Share,
            Impact = "Компьютер способен на большее — производительность ограничена настройкой, а не железом.",
        };
        f.Evidence.Add($"Медианный FPS {F0(cap.Fps)}, {P(cap.Share)} секунд в узком диапазоне ±4%");
        if (gpu.HasValue) f.Evidence.Add($"Медианная загрузка GPU: {F0(gpu)}%");
        f.Recommendations.Add("Если хотите больше FPS — отключите V-Sync или поднимите лимит кадров в игре / драйвере.");
        f.Recommendations.Add("Если цель — тишина, низкие температуры и ровный кадр, текущий лимит — правильная настройка.");
        yield return f;
    }
}

/// <summary>GPU load collapses at the same moments the CPU is saturated.</summary>
public sealed class GpuLoadDropRule : IAnalysisRule
{
    public IEnumerable<Finding> Evaluate(AnalysisContext ctx)
    {
        if (!ctx.HasGpuLoad || !ctx.HasCoreLoads) yield break;
        double? median = ctx.Pct(s => s.GpuLoad, 50);
        if (median is not >= 80) yield break;
        double limit = median.Value - 20;
        bool Drop(int i)
        {
            var s = ctx.Samples[i];
            return s.GpuLoad < limit && s.CpuMaxCoreLoad >= ctx.T.CpuBoundCoreLoad && !ctx.IsCappedSample(s);
        }
        var idx = ctx.Where(Drop).ToList();
        if (idx.Count < 5) yield break;
        // already explained by a dominant CPU bottleneck
        if (ctx.ShareIdx(i => ctx.Limiter[i] == LimiterKind.Cpu, i => ctx.Samples[i].GpuLoad.HasValue) >= 0.5) yield break;

        var f = new Finding
        {
            Code = "gpu_drop_cpu_peak",
            Severity = Severity.Minor,
            Confidence = ctx.HasFps ? Confidence.Medium : Confidence.Low,
            Title = "Провалы загрузки видеокарты при пиках нагрузки на процессор",
            Description = $"Обычно видеокарта была загружена на {F0(median)}%, но {idx.Count} раз(а) её загрузка падала ниже {F0(limit)}% одновременно с перегрузкой ядра процессора. " +
                          "В такие моменты видеокарта ждала процессор (например, при загрузке новых областей, взрывах, большом количестве объектов).",
            When = ctx.Ranges(Drop, 2),
            Impact = "Кратковременные просадки FPS в насыщенных сценах.",
        };
        f.Evidence.Add($"Загрузка GPU в эти моменты: {F0(ctx.Avg(s => s.GpuLoad, idx))}%, самое загруженное ядро CPU: {F0(ctx.Avg(s => s.CpuMaxCoreLoad, idx))}%");
        if (ctx.HasFps) f.Evidence.Add($"FPS в эти моменты: {F0(ctx.Avg(s => s.Fps, idx))} против {F0(ctx.FrameStats?.AvgFps)} в среднем");
        f.Recommendations.Add("Снизить настройки, нагружающие CPU: дальность прорисовки, количество объектов/NPC, физику.");
        f.Recommendations.Add("Закрыть фоновые программы, чтобы освободить процессор в пиковые моменты.");
        yield return f;
    }
}
