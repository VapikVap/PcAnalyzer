using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PcAnalyzer.Core.Analysis.Rules;
using PcAnalyzer.Core.Models;

namespace PcAnalyzer.Core.Analysis;

/// <summary>
/// Bottleneck detection engine + report generator. Runs all rules over a recorded session and
/// produces the <see cref="SessionSummary"/> and <see cref="SessionReport"/>.
/// </summary>
public sealed class PerformanceAnalyzer
{
    public const int MinSamplesForAnalysis = 15;

    private readonly IReadOnlyList<IAnalysisRule> _rules;
    private readonly IDiagnosisNarrator _narrator;
    private readonly ILogger _log;

    public PerformanceAnalyzer(IDiagnosisNarrator? narrator = null, IEnumerable<IAnalysisRule>? rules = null, ILogger<PerformanceAnalyzer>? log = null)
    {
        _narrator = narrator ?? new LocalNarrator();
        _log = (ILogger?)log ?? NullLogger.Instance;
        _rules = (rules ?? DefaultRules()).ToList();
    }

    public static IEnumerable<IAnalysisRule> DefaultRules() => new IAnalysisRule[]
    {
        new GpuBoundRule(), new CpuBoundRule(), new CpuSaturationRule(), new FpsCapRule(), new GpuLoadDropRule(),
        new CpuThermalRule(), new GpuThermalRule(), new PowerLimitRule(), new PowerPlanRule(), new ClockStabilityRule(),
        new VramRule(), new RamRule(), new DiskRule(), new BackgroundProcessRule(),
        new StutterRule(), new ShaderCompilationRule(), new DegradationRule(),
    };

    public (SessionSummary Summary, SessionReport Report) Analyze(AnalysisInput input)
    {
        var ctx = new AnalysisContext(input);
        var summary = BuildSummary(ctx);
        var report = new SessionReport { GeneratedBy = _narrator.Name, GeneratedUtc = DateTime.UtcNow };
        report.DataNotes.AddRange(DataNotes(ctx));

        if (ctx.Samples.Count < MinSamplesForAnalysis)
        {
            report.Headline = "Недостаточно данных, чтобы точно определить причину";
            report.Narrative = $"Сессия слишком короткая: записано {ctx.Samples.Count} замеров (нужно минимум {MinSamplesForAnalysis}).";
            report.SmoothnessLabel = "Нет данных";
            summary.Diagnosis = report.Headline;
            return (summary, report);
        }

        var findings = new List<Finding>();
        foreach (var rule in _rules)
        {
            try { findings.AddRange(rule.Evaluate(ctx)); }
            catch (Exception ex) { _log.LogError(ex, "Rule {Rule} failed", rule.GetType().Name); }
        }
        findings = findings.OrderByDescending(f => f.Severity).ThenByDescending(f => f.Confidence).ToList();
        report.Findings = findings;

        try { report.Events = DropEventDetector.Detect(ctx); }
        catch (Exception ex) { _log.LogError(ex, "Drop detection failed"); }

        bool Valid(int i) => ctx.Samples[i].GpuLoad.HasValue;
        double gpuShare = ctx.ShareIdx(i => ctx.Limiter[i] == LimiterKind.Gpu, Valid);
        double cpuShare = ctx.ShareIdx(i => ctx.Limiter[i] == LimiterKind.Cpu, Valid);
        double capShare = ctx.ShareIdx(i => ctx.Limiter[i] == LimiterKind.Capped, Valid);
        double bothShare = ctx.ShareIdx(i => ctx.Limiter[i] == LimiterKind.Both, Valid);
        report.PrimaryLimiter =
            !ctx.HasGpuLoad ? null :
            capShare >= 0.5 ? "Лимит FPS / V-Sync" :
            gpuShare >= 0.5 ? "Видеокарта" :
            cpuShare >= 0.3 ? "Процессор" :
            bothShare + cpuShare >= 0.4 ? "Процессор и видеокарта (оба близки к пределу)" : "Нет единственного ограничителя";

        // Scores
        summary.SmoothnessScore = SmoothnessScore(ctx);
        summary.OverallScore = OverallScore(ctx, summary, findings);
        report.SmoothnessLabel = summary.SmoothnessScore switch
        {
            null => "Нет данных о кадрах",
            >= 85 => "Отличная",
            >= 70 => "Хорошая",
            >= 50 => "Удовлетворительная",
            _ => "Плохая"
        };

        var problems = findings.Where(f => f.Severity >= Severity.Minor).ToList();
        summary.IssueCount = problems.Count;
        summary.WorstSeverity = problems.Count > 0 ? problems.Max(f => f.Severity) : Severity.None;

        report.Headline = problems.Count > 0
            ? $"Основная проблема: {Lower(problems[0].Title)}"
            : report.PrimaryLimiter is { } lim && lim != "Нет единственного ограничителя"
                ? $"Проблем не обнаружено. Основное ограничение: {Lower(lim)}"
                : ctx.HasGpuLoad ? "Проблем не обнаружено" : "Недостаточно данных, чтобы точно определить причину";
        summary.Diagnosis = report.Headline;

        report.Recommendations = problems.SelectMany(f => f.Recommendations).Distinct().Take(8).ToList();
        if (report.Recommendations.Count == 0)
            report.Recommendations.AddRange(findings.SelectMany(f => f.Recommendations).Distinct().Take(3));

        try
        {
            report.Narrative = _narrator.Compose(new NarrationInput(
                input.Session.GameName, summary, findings, gpuShare, cpuShare, capShare, ctx.Cap, report.DataNotes) { BothShare = bothShare });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Narrator failed");
            report.Narrative = report.Headline;
        }
        return (summary, report);
    }

    private static string Lower(string s) => s.Length > 1 && !char.IsUpper(s[1]) ? char.ToLower(s[0]) + s[1..] : s;

    internal static SessionSummary BuildSummary(AnalysisContext ctx)
    {
        var fs = ctx.FrameStats;
        var sum = new SessionSummary
        {
            SampleCount = ctx.Samples.Count,
            DurationSec = ctx.DurationSec,
            FrameCount = fs?.Count ?? 0,
            AvgFps = fs?.AvgFps,
            P1LowFps = fs?.P1LowFps,
            P01LowFps = fs?.P01LowFps,
            AvgFrameTimeMs = fs?.AvgMs,
            MaxFrameTimeMs = fs?.MaxMs,
            StutterCount = ctx.Spikes.Count,
            FreezeCount = ctx.Freezes.Count,
            AvgCpu = ctx.Avg(s => s.CpuLoad),
            MaxCpu = ctx.Max(s => s.CpuLoad),
            AvgMaxCore = ctx.Avg(s => s.CpuMaxCoreLoad),
            AvgCpuClock = ctx.Avg(s => s.CpuClockMhz),
            AvgCpuTemp = ctx.Avg(s => s.CpuTempC),
            MaxCpuTemp = ctx.Max(s => s.CpuTempC),
            AvgGpu = ctx.Avg(s => s.GpuLoad),
            MaxGpu = ctx.Max(s => s.GpuLoad),
            AvgGpuClock = ctx.Avg(s => s.GpuCoreClockMhz),
            AvgGpuTemp = ctx.Avg(s => s.GpuTempC),
            MaxGpuTemp = ctx.Max(s => s.GpuTempC),
            AvgRamUsedMb = ctx.Avg(s => s.RamUsedMb),
            MaxRamUsedMb = ctx.Max(s => s.RamUsedMb),
            RamTotalMb = ctx.Samples.LastOrDefault(s => s.RamTotalMb.HasValue)?.RamTotalMb,
            AvgVramMb = ctx.Avg(s => s.VramUsedMb),
            MaxVramMb = ctx.Max(s => s.VramUsedMb),
            VramTotalMb = ctx.Samples.LastOrDefault(s => s.VramTotalMb.HasValue)?.VramTotalMb,
        };

        var windows = FrameStatistics.WindowedFps(ctx.Frames.SelectMany(f => f.FrameTimesMs));
        if (windows.Count > 0)
        {
            sum.MinFps = windows.Min();
            sum.MaxFps = windows.Max();
        }

        var withCores = ctx.Samples.Where(s => s.CoreLoads is { Length: > 0 }).ToList();
        if (withCores.Count > 0)
        {
            int n = withCores.Max(s => s.CoreLoads!.Length);
            var avg = new double[n];
            for (int c = 0; c < n; c++)
                avg[c] = withCores.Where(s => s.CoreLoads!.Length > c).Select(s => s.CoreLoads![c]).DefaultIfEmpty(0).Average();
            sum.PerCoreAvg = avg;
        }
        return sum;
    }

    /// <summary>
    /// 0–100. Based on 1% low / average ratio (frame pacing), stutter rate and freezes.
    /// Documented in docs/ARCHITECTURE.md.
    /// </summary>
    internal static double? SmoothnessScore(AnalysisContext ctx)
    {
        if (ctx.FrameStats is not { } fs || fs.P1LowFps is not { } p1) return null;
        double ratio = Math.Clamp(p1 / fs.AvgFps, 0, 1);
        double minutes = Math.Max(ctx.DurationSec / 60, 0.5);
        double score = ratio * 115 - (ctx.Spikes.Count / minutes) * 2 - ctx.Freezes.Count * 4;
        return Math.Round(Math.Clamp(score, 0, 100));
    }

    internal static double? OverallScore(AnalysisContext ctx, SessionSummary s, List<Finding> findings)
    {
        if (s.AvgFps is not { } fps) return null;
        double target = ctx.RefreshHz is { } hz ? Math.Max(ctx.T.TargetFps, Math.Min(hz, 144)) : ctx.T.TargetFps;
        double fpsScore = Math.Clamp(fps / target * 100, 0, 100);
        double smooth = s.SmoothnessScore ?? fpsScore;
        double penalty = findings.Sum(f => f.Severity switch { Severity.Critical => 15, Severity.Major => 7, Severity.Minor => 2, _ => 0 });
        return Math.Round(Math.Clamp(fpsScore * 0.5 + smooth * 0.5 - penalty, 0, 100));
    }

    private static IEnumerable<string> DataNotes(AnalysisContext ctx)
    {
        if (!ctx.HasFps)
            yield return "FPS и время кадра не записывались: источник кадров (PresentMon) не подключён или не имел доступа. Выводы о плавности недоступны, диагноз основан на загрузке и сенсорах.";
        if (!ctx.HasGpuLoad)
            yield return "Загрузка видеокарты недоступна — определить узкое место (CPU или GPU) невозможно.";
        if (!ctx.HasCoreLoads)
            yield return "Загрузка по ядрам недоступна — перегрузку отдельного потока проверить нельзя.";
        if (!ctx.HasCpuTemp)
            yield return "Температура процессора недоступна (нужны права администратора и драйвер доступа к сенсорам) — перегрев CPU не проверялся.";
        if (!ctx.HasGpuTemp)
            yield return "Температура видеокарты недоступна — перегрев GPU не проверялся.";
        if (!ctx.HasVram)
            yield return "Использование видеопамяти недоступно — нехватку VRAM проверить нельзя.";
    }
}
