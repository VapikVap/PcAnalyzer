using PcAnalyzer.Core.Models;
using static PcAnalyzer.Core.Analysis.AnalysisContext;

namespace PcAnalyzer.Core.Analysis.Rules;

/// <summary>Shared logic for CPU and GPU: high temperature + clock drop under load = throttling.</summary>
internal static class ThermalLogic
{
    public static Finding? Evaluate(
        AnalysisContext ctx, string code, string device,
        Func<TelemetrySample, double?> temp, Func<TelemetrySample, double?> clock, Func<TelemetrySample, double?> load,
        double warnC, double loadForReference)
    {
        if (!ctx.Samples.Any(s => temp(s).HasValue)) return null;
        bool Hot(int i) => temp(ctx.Samples[i]) >= warnC;
        double shareHot = ctx.ShareIdx(Hot, i => temp(ctx.Samples[i]).HasValue);
        double? maxT = ctx.Max(temp);
        int hotCount = ctx.Where(Hot).Count();
        if (hotCount < 3) return null;

        var loaded = ctx.Where(i => load(ctx.Samples[i]) >= loadForReference).ToList();
        double? refClock = ctx.Pct(clock, 90, loaded);
        bool Throttled(int i)
        {
            var s = ctx.Samples[i];
            return Hot(i) && refClock.HasValue && clock(s) < refClock * 0.9 && load(s) >= loadForReference;
        }
        var thr = ctx.Where(Throttled).ToList();

        var f = new Finding { Code = code, When = ctx.Ranges(Hot) , Share = shareHot };
        f.Evidence.Add($"Максимальная температура {device}: {F0(maxT)} °C, выше {F0(warnC)} °C — {P(shareHot)} времени");

        if (thr.Count >= 3)
        {
            double? clockDuring = ctx.Avg(clock, thr);
            f.Severity = shareHot >= 0.2 ? Severity.Critical : Severity.Major;
            f.Confidence = Confidence.High;
            f.Title = $"Температурный троттлинг {device}";
            f.Description = $"{Cap(device)} нагревался до {F0(maxT)} °C, и под нагрузкой его частота падала примерно с {F0(refClock)} до {F0(clockDuring)} МГц. " +
                            "Так срабатывает защита от перегрева: устройство само снижает производительность.";
            f.Evidence.Add($"Частота при высокой температуре: {F0(clockDuring)} МГц против {F0(refClock)} МГц в нормальном режиме ({thr.Count} замеров)");
            f.Impact = "Производительность снижается именно в моменты перегрева — FPS падает тем сильнее, чем дольше идёт игра.";
        }
        else
        {
            f.Severity = shareHot >= 0.1 ? Severity.Major : Severity.Minor;
            f.Confidence = refClock.HasValue ? Confidence.Medium : Confidence.Low;
            f.Title = $"Высокая температура {device}";
            f.Description = refClock.HasValue
                ? $"{Cap(device)} нагревался до {F0(maxT)} °C. Явного падения частоты не зафиксировано, но запаса по температуре почти нет."
                : $"{Cap(device)} нагревался до {F0(maxT)} °C. Данных о частоте нет, поэтому проверить троттлинг невозможно.";
            f.Impact = "Пока без явной потери производительности, но при более жаркой погоде или долгой игре возможен троттлинг.";
        }
        return f;
    }

    private static string Cap(string s) => char.ToUpper(s[0]) + s[1..];
}

public sealed class CpuThermalRule : IAnalysisRule
{
    public IEnumerable<Finding> Evaluate(AnalysisContext ctx)
    {
        var f = ThermalLogic.Evaluate(ctx, "cpu_thermal", "процессора", s => s.CpuTempC, s => s.CpuClockMhz, s => s.CpuLoad, ctx.T.CpuTempWarnC, 40);
        if (f == null) yield break;
        f.Recommendations.Add("Проверить кулер процессора: вентилятор вращается, радиатор плотно прижат, нет пыли.");
        f.Recommendations.Add("Если ПК или ноутбук старше 2–3 лет — заменить термопасту.");
        f.Recommendations.Add("Улучшить продув корпуса: проверить корпусные вентиляторы и не перекрыты ли вентиляционные отверстия.");
        f.Recommendations.Add("Проверить кривую вентиляторов в BIOS / утилите материнской платы — при нагреве обороты должны расти.");
        yield return f;
    }
}

public sealed class GpuThermalRule : IAnalysisRule
{
    public IEnumerable<Finding> Evaluate(AnalysisContext ctx)
    {
        var f = ThermalLogic.Evaluate(ctx, "gpu_thermal", "видеокарты", s => s.GpuTempC, s => s.GpuCoreClockMhz, s => s.GpuLoad, ctx.T.GpuTempWarnC, 80);
        if (f == null) yield break;
        if (ctx.Max(s => s.GpuHotSpotC) is { } hs) f.Evidence.Add($"Максимальная температура hot spot: {F0(hs)} °C");
        if (ctx.Avg(s => s.GpuFanRpm) is { } rpm) f.Evidence.Add($"Средние обороты вентиляторов видеокарты: {F0(rpm)} об/мин");
        f.Recommendations.Add("Очистить видеокарту и корпус от пыли, проверить, что все вентиляторы видеокарты вращаются.");
        f.Recommendations.Add("Улучшить продув корпуса; не ставить видеокарту вплотную к другим платам.");
        f.Recommendations.Add("Ограничить FPS (на уровне частоты монитора) — видеокарта будет меньше греться при той же плавности.");
        f.Recommendations.Add("Если карта старая — обслуживание (замена термопасты/термопрокладок) в сервисе.");
        yield return f;
    }
}

/// <summary>Clocks fall under load while temperatures are normal → likely power limit or power plan.</summary>
public sealed class PowerLimitRule : IAnalysisRule
{
    public IEnumerable<Finding> Evaluate(AnalysisContext ctx)
    {
        // ---- GPU ----
        if (ctx.HasGpuClock && ctx.HasGpuLoad)
        {
            var loaded = ctx.Where(i => ctx.Samples[i].GpuLoad >= 90).ToList();
            double? refClock = ctx.Pct(s => s.GpuCoreClockMhz, 90, loaded);
            bool Limited(int i)
            {
                var s = ctx.Samples[i];
                bool cool = s.GpuTempC is not { } t || t < ctx.T.GpuTempWarnC - 5;
                return s.GpuLoad >= 90 && cool && refClock.HasValue && s.GpuCoreClockMhz < refClock * 0.88;
            }
            var idx = ctx.Where(Limited).ToList();
            if (loaded.Count >= 10 && idx.Count >= 5 && idx.Count >= loaded.Count * 0.1)
            {
                var f = new Finding
                {
                    Code = "gpu_power_limit",
                    Severity = Severity.Minor,
                    Confidence = ctx.HasGpuTemp ? Confidence.Medium : Confidence.Low,
                    Title = "Возможное ограничение видеокарты по питанию (Power Limit)",
                    Description = $"При полной загрузке частота видеокарты периодически опускалась примерно с {F0(refClock)} до {F0(ctx.Avg(s => s.GpuCoreClockMhz, idx))} МГц, " +
                                  "хотя температура была в норме. Чаще всего так работает ограничение по потреблению (Power Limit).",
                    When = ctx.Ranges(Limited),
                    Impact = "Небольшая потеря FPS в самых тяжёлых сценах.",
                };
                if (ctx.Avg(s => s.GpuPowerW, idx) is { } pw) f.Evidence.Add($"Потребление видеокарты в эти моменты: ~{F0(pw)} Вт (максимум за сессию {F0(ctx.Max(s => s.GpuPowerW))} Вт)");
                f.Evidence.Add($"Замеров с пониженной частотой при загрузке ≥90%: {idx.Count} из {loaded.Count}");
                f.Recommendations.Add("Проверить, что к видеокарте подключены все разъёмы дополнительного питания и блок питания соответствует её требованиям.");
                f.Recommendations.Add("Проверить, не снижен ли лимит мощности в фирменной утилите видеокарты (по умолчанию — 100%).");
                yield return f;
            }
        }

        // ---- CPU ----
        if (ctx.Samples.Any(s => s.CpuPerfPercent.HasValue))
        {
            bool Loaded(int i) => ctx.Samples[i].CpuLoad >= 60 || ctx.Samples[i].CpuMaxCoreLoad >= 90;
            bool Low(int i)
            {
                var s = ctx.Samples[i];
                bool cool = s.CpuTempC is not { } t || t < ctx.T.CpuTempWarnC - 5;
                return Loaded(i) && cool && s.CpuPerfPercent < 85;
            }
            int loadedCount = ctx.Where(Loaded).Count();
            var idx = ctx.Where(Low).ToList();
            if (loadedCount >= 10 && idx.Count >= 5 && idx.Count >= loadedCount * 0.15)
            {
                var f = new Finding
                {
                    Code = "cpu_clock_low",
                    Severity = Severity.Minor,
                    Confidence = ctx.HasCpuTemp ? Confidence.Medium : Confidence.Low,
                    Title = "Частота процессора под нагрузкой ниже номинальной",
                    Description = $"В {idx.Count} замерах под нагрузкой процессор работал лишь на {F0(ctx.Avg(s => s.CpuPerfPercent, idx))}% от номинальной частоты " +
                                  (ctx.HasCpuTemp ? "при нормальной температуре. " : "(температура CPU недоступна, перегрев исключить нельзя). ") +
                                  "Возможные причины: экономичный план электропитания, ограничение мощности в BIOS, работа ноутбука от батареи.",
                    When = ctx.Ranges(Low),
                    Impact = "Процессор отдаёт не всю свою производительность.",
                };
                f.Evidence.Add($"Средняя эффективная частота в эти моменты: {F0(ctx.Avg(s => s.CpuClockMhz, idx))} МГц");
                f.Recommendations.Add("Проверить план электропитания Windows: для игр — «Сбалансированный» или «Высокая производительность».");
                f.Recommendations.Add("Ноутбук: подключить зарядное устройство и выбрать режим «Производительность» в фирменной утилите.");
                yield return f;
            }
        }
    }
}

public sealed class PowerPlanRule : IAnalysisRule
{
    private static readonly Guid PowerSaver = new("a1841308-3541-4fab-bc81-f71556f20b4a");

    public IEnumerable<Finding> Evaluate(AnalysisContext ctx)
    {
        var hw = ctx.Hardware;
        if (hw?.PowerPlanGuid != PowerSaver) yield break;
        var f = new Finding
        {
            Code = "power_saver_plan",
            Severity = Severity.Major,
            Confidence = Confidence.High,
            Title = "Включён план электропитания «Экономия энергии»",
            Description = "Во время сессии в Windows был активен план «Экономия энергии». Он ограничивает частоту процессора и снижает производительность в играх.",
            Impact = "Заметно ниже FPS в играх, зависящих от процессора.",
        };
        f.Evidence.Add($"Активный план электропитания: {hw.PowerPlanName ?? PowerSaver.ToString()}");
        f.Recommendations.Add("Переключить план электропитания на «Сбалансированный» или «Высокая производительность» (Параметры → Система → Питание).");
        yield return f;
    }
}

public sealed class ClockStabilityRule : IAnalysisRule
{
    public IEnumerable<Finding> Evaluate(AnalysisContext ctx)
    {
        if (ctx.HasGpuClock && ctx.HasGpuLoad)
        {
            var idx = ctx.Where(i => ctx.Samples[i].GpuLoad >= 90 && ctx.Samples[i].GpuCoreClockMhz.HasValue).ToList();
            double cov = RuleMath.CoefficientOfVariation(idx.Select(i => ctx.Samples[i].GpuCoreClockMhz!.Value));
            if (idx.Count >= 30 && cov > 0.08)
            {
                var f = new Finding
                {
                    Code = "gpu_clock_unstable",
                    Severity = Severity.Minor,
                    Confidence = Confidence.Low,
                    Title = "Нестабильная частота видеокарты под нагрузкой",
                    Description = $"При загрузке GPU ≥90% частота ядра колебалась на {F0(cov * 100)}% (от {F0(ctx.Pct(s => s.GpuCoreClockMhz, 5, idx))} до {F0(ctx.Pct(s => s.GpuCoreClockMhz, 95, idx))} МГц). " +
                                  "Причиной могут быть температура, лимит питания или настройки разгона.",
                    Impact = "Может давать неровный FPS в тяжёлых сценах.",
                };
                f.Evidence.Add($"Разброс частоты GPU: {F0(cov * 100)}% на {idx.Count} замерах под нагрузкой");
                f.Recommendations.Add("Если видеокарта разогнана или андервольтнута — вернуть заводские настройки и сравнить сессии.");
                f.Recommendations.Add("Проверить температуру и питание видеокарты (см. другие находки отчёта).");
                yield return f;
            }
        }
        if (ctx.HasCpuClock)
        {
            var idx = ctx.Where(i => ctx.Samples[i].CpuLoad >= 50 && ctx.Samples[i].CpuClockMhz.HasValue).ToList();
            double cov = RuleMath.CoefficientOfVariation(idx.Select(i => ctx.Samples[i].CpuClockMhz!.Value));
            if (idx.Count >= 30 && cov > 0.12)
            {
                var f = new Finding
                {
                    Code = "cpu_clock_unstable",
                    Severity = Severity.Minor,
                    Confidence = Confidence.Low,
                    Title = "Нестабильная частота процессора под нагрузкой",
                    Description = $"При нагрузке CPU ≥50% эффективная частота колебалась на {F0(cov * 100)}% " +
                                  $"(от {F0(ctx.Pct(s => s.CpuClockMhz, 5, idx))} до {F0(ctx.Pct(s => s.CpuClockMhz, 95, idx))} МГц).",
                    Impact = "Неравномерная производительность процессора может давать рваный FPS.",
                };
                f.Recommendations.Add("Проверить план электропитания и температуру процессора.");
                yield return f;
            }
        }
    }
}
