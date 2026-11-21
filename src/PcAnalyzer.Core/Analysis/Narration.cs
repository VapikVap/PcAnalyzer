using System.Text;
using PcAnalyzer.Core.Models;
using static PcAnalyzer.Core.Analysis.AnalysisContext;

namespace PcAnalyzer.Core.Analysis;

/// <summary>Aggregated, already-verified facts handed to a narrator. No raw samples.</summary>
public sealed record NarrationInput(
    string GameName,
    SessionSummary Summary,
    IReadOnlyList<Finding> Findings,
    double GpuBoundShare,
    double CpuBoundShare,
    double CappedShare,
    FpsCapInfo? Cap,
    IReadOnlyList<string> DataNotes)
{
    /// <summary>Share of time both CPU and GPU were near their limit.</summary>
    public double BothShare { get; init; }
}

/// <summary>
/// Turns verified findings into plain-language text. The default implementation is local and
/// template-based; an external LLM narrator can implement the same interface later, but it must
/// only rephrase the provided facts.
/// </summary>
public interface IDiagnosisNarrator
{
    string Name { get; }
    string Compose(NarrationInput input);
}

public sealed class LocalNarrator : IDiagnosisNarrator
{
    public string Name => "Локальный анализ (без интернета)";

    public string Compose(NarrationInput x)
    {
        var s = x.Summary;
        var sb = new StringBuilder();

        // 1. What limited performance
        if (x.Cap != null && x.CappedShare >= 0.5)
        {
            sb.Append($"Большую часть игры FPS держался около {F0(x.Cap.Fps)} — это похоже на ").Append(x.Cap.MatchesRefreshRate ? "V-Sync или лимит под частоту монитора" : "ограничитель кадров")
              .Append(". Ни процессор, ни видеокарта не работали на пределе, поэтому производительность ограничена настройкой, а не железом. ");
        }
        else if (x.GpuBoundShare >= 0.5)
        {
            sb.Append($"В течение {P(x.GpuBoundShare)} игры видеокарта работала почти на максимальной загрузке (в среднем {F0(s.AvgGpu)}%). ")
              .Append("Это означает, что в данном сценарии производительность преимущественно ограничивалась видеокартой. ");
            if (x.CpuBoundShare < 0.15) sb.Append("Процессор не был основным ограничением. ");
        }
        else if (x.CpuBoundShare >= 0.3)
        {
            sb.Append($"Около {P(x.CpuBoundShare)} времени видеокарта была загружена не полностью (в среднем {F0(s.AvgGpu)}%), ")
              .Append($"а процессор работал на пределе (самое загруженное ядро в среднем {F0(s.AvgMaxCore)}%). Видеокарта ждала процессор — основное ограничение было на его стороне. ");
        }
        else if (x.BothShare + x.CpuBoundShare >= 0.4)
        {
            sb.Append($"Процессор и видеокарта большую часть игры работали близко к пределу одновременно: CPU в среднем {F0(s.AvgCpu)}% ")
              .Append($"(самое загруженное ядро {F0(s.AvgMaxCore)}%), GPU — {F0(s.AvgGpu)}%. Явного единственного узкого места нет, но у процессора почти не остаётся запаса. ");
        }
        else if (s.AvgGpu.HasValue)
        {
            sb.Append($"Явного единственного ограничителя не обнаружено: видеокарта в среднем была загружена на {F0(s.AvgGpu)}%, процессор — на {F0(s.AvgCpu)}% ")
              .Append($"(самое загруженное ядро {F0(s.AvgMaxCore)}%). ");
        }
        else
        {
            sb.Append("Недостаточно данных, чтобы точно определить, что ограничивало производительность: загрузка видеокарты не записывалась. ");
        }

        // 2. FPS and smoothness
        if (s.AvgFps.HasValue)
        {
            sb.Append($"Средний FPS — {F0(s.AvgFps)}");
            if (s.P1LowFps.HasValue) sb.Append($", 1% low — {F0(s.P1LowFps)}");
            sb.Append(". ");
            if (s.FreezeCount > 0) sb.Append($"Зафиксировано {s.FreezeCount} фриз(ов) дольше порога. ");
            else if (s.StutterCount == 0) sb.Append("Скачков времени кадра не обнаружено — игра шла ровно. ");
        }
        else
        {
            sb.Append("FPS и время кадра не записывались (источник кадров не подключён), поэтому о плавности судить нельзя. ");
        }

        // 3. Temperatures
        bool thermal = x.Findings.Any(f => f.Code is "cpu_thermal" or "gpu_thermal");
        if (!thermal && (s.MaxCpuTemp.HasValue || s.MaxGpuTemp.HasValue))
        {
            var parts = new List<string>();
            if (s.MaxCpuTemp.HasValue) parts.Add($"CPU до {F0(s.MaxCpuTemp)} °C");
            if (s.MaxGpuTemp.HasValue) parts.Add($"GPU до {F0(s.MaxGpuTemp)} °C");
            sb.Append($"Перегрева не обнаружено ({string.Join(", ", parts)}). ");
        }

        // 4. Problems
        var problems = x.Findings.Where(f => f.Severity >= Severity.Minor).ToList();
        if (problems.Count == 0)
            sb.Append("Существенных проблем по записанным данным не найдено.");
        else
            sb.Append($"Найдено проблем: {problems.Count}. Главная — «{problems[0].Title}» (уверенность: {SeverityText.Name(problems[0].Confidence)}).");

        return sb.ToString().Trim();
    }
}
