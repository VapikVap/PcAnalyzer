using System.Globalization;
using System.Net;
using System.Text;
using PcAnalyzer.Core.Analysis;
using PcAnalyzer.Core.Models;

namespace PcAnalyzer.Core.Export;

/// <summary>
/// Self-contained HTML report (no external resources) with inline SVG charts.
/// It can be sent to another person or printed to PDF from any browser.
/// </summary>
public static class HtmlReportExporter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Render(GameSession s, IReadOnlyList<TelemetrySample> samples)
    {
        var r = s.Report;
        var m = s.Summary ?? new SessionSummary();
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"ru\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append("<title>").Append(E(s.GameName)).Append(" — отчёт о производительности</title><style>").Append(Css).Append("</style></head><body><main>");

        sb.Append("<header><div class=\"brand\">PC Performance Analyzer</div><h1>").Append(E(s.GameName)).Append("</h1><p class=\"muted\">")
          .Append(s.StartUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm", Inv)).Append(" · ").Append(Dur(s.Duration))
          .Append(s.Label != null ? " · " + E(s.Label) : "").Append(s.Status == SessionStatus.Interrupted ? " · запись прервана" : "")
          .Append("</p></header>");

        // KPIs
        sb.Append("<section class=\"kpis\">");
        Kpi(sb, "Средний FPS", N(m.AvgFps));
        Kpi(sb, "1% Low", N(m.P1LowFps));
        Kpi(sb, "0.1% Low", N(m.P01LowFps));
        Kpi(sb, "Макс. FPS", N(m.MaxFps));
        Kpi(sb, "Плавность", m.SmoothnessScore.HasValue ? N(m.SmoothnessScore) + "/100" : "—");
        Kpi(sb, "Общая оценка", m.OverallScore.HasValue ? N(m.OverallScore) + "/100" : "—");
        sb.Append("</section>");

        if (r != null)
        {
            sb.Append("<section class=\"card hero\"><h2>").Append(E(r.Headline)).Append("</h2><p>").Append(E(r.Narrative)).Append("</p>");
            if (r.PrimaryLimiter != null) sb.Append("<p class=\"muted\">Основной ограничитель: ").Append(E(r.PrimaryLimiter)).Append("</p>");
            sb.Append("</section>");

            if (r.Recommendations.Count > 0)
            {
                sb.Append("<section class=\"card\"><h2>Что попробовать</h2><ol>");
                foreach (var rec in r.Recommendations) sb.Append("<li>").Append(E(rec)).Append("</li>");
                sb.Append("</ol></section>");
            }

            sb.Append("<section class=\"card\"><h2>Найденные проблемы</h2>");
            if (r.Findings.Count == 0) sb.Append("<p>🟢 Проблем не обнаружено.</p>");
            foreach (var f in r.Findings)
            {
                sb.Append("<div class=\"finding sev").Append((int)f.Severity).Append("\"><h3>").Append(SeverityText.Icon(f.Severity)).Append(' ').Append(E(f.Title))
                  .Append("</h3><p class=\"muted\">").Append(SeverityText.Name(f.Severity)).Append(" · уверенность: ").Append(SeverityText.Name(f.Confidence)).Append("</p><p>")
                  .Append(E(f.Description)).Append("</p>");
                if (f.When.Count > 0) sb.Append("<p><b>Когда:</b> ").Append(E(string.Join(", ", f.When))).Append("</p>");
                if (f.Evidence.Count > 0)
                {
                    sb.Append("<p><b>Доказательства:</b></p><ul>");
                    foreach (var e in f.Evidence) sb.Append("<li>").Append(E(e)).Append("</li>");
                    sb.Append("</ul>");
                }
                if (!string.IsNullOrEmpty(f.Impact)) sb.Append("<p><b>Влияние:</b> ").Append(E(f.Impact)).Append("</p>");
                if (f.Recommendations.Count > 0)
                {
                    sb.Append("<p><b>Что сделать:</b></p><ul>");
                    foreach (var e in f.Recommendations) sb.Append("<li>").Append(E(e)).Append("</li>");
                    sb.Append("</ul>");
                }
                sb.Append("</div>");
            }
            sb.Append("</section>");
        }

        // Charts
        sb.Append("<section class=\"card\"><h2>Графики</h2>");
        var t0 = samples.Count > 0 ? samples[0].TimestampUtc : s.StartUtc;
        Chart(sb, "FPS", samples, t0, ("FPS", x => x.Fps, "#5b8cff"));
        Chart(sb, "Время кадра (макс. за интервал), мс", samples, t0, ("мс", x => x.FrameTimeMaxMs, "#ff8a4c"));
        Chart(sb, "Загрузка, %", samples, t0, ("GPU", x => x.GpuLoad, "#5b8cff"), ("CPU", x => x.CpuLoad, "#33d1a0"), ("Макс. ядро", x => x.CpuMaxCoreLoad, "#ffc24c"));
        Chart(sb, "Температуры, °C", samples, t0, ("GPU", x => x.GpuTempC, "#5b8cff"), ("CPU", x => x.CpuTempC, "#ff6b6b"));
        Chart(sb, "Память, %", samples, t0, ("VRAM", x => x.VramPercent, "#a78bfa"), ("RAM", x => x.RamPercent, "#33d1a0"));
        sb.Append("</section>");

        if (r?.Events.Count > 0)
        {
            sb.Append("<section class=\"card\"><h2>События просадки производительности</h2><table><tr><th>Время</th><th>Событие</th><th>FPS</th><th>Что совпало</th></tr>");
            foreach (var e in r.Events)
                sb.Append("<tr><td>").Append(AnalysisContext.Clock(e.OffsetSec)).Append("</td><td>").Append(E(e.Kind)).Append("</td><td>")
                  .Append(e.FpsDuring.HasValue ? $"{N(e.FpsBefore)} → {N(e.FpsDuring)}" : e.WorstFrameMs.HasValue ? $"{N(e.WorstFrameMs)} мс" : "—")
                  .Append("</td><td>").Append(E(string.Join("; ", e.LikelyCauses))).Append("</td></tr>");
            sb.Append("</table></section>");
        }

        // Stats
        sb.Append("<section class=\"card\"><h2>Показатели</h2><table>");
        Row(sb, "Загрузка CPU (сред./макс.)", $"{N(m.AvgCpu)} / {N(m.MaxCpu)} %");
        Row(sb, "Самое загруженное ядро (сред.)", $"{N(m.AvgMaxCore)} %");
        Row(sb, "Частота CPU (сред.)", $"{N(m.AvgCpuClock)} МГц");
        Row(sb, "Температура CPU (сред./макс.)", $"{N(m.AvgCpuTemp)} / {N(m.MaxCpuTemp)} °C");
        Row(sb, "Загрузка GPU (сред./макс.)", $"{N(m.AvgGpu)} / {N(m.MaxGpu)} %");
        Row(sb, "Частота GPU (сред.)", $"{N(m.AvgGpuClock)} МГц");
        Row(sb, "Температура GPU (сред./макс.)", $"{N(m.AvgGpuTemp)} / {N(m.MaxGpuTemp)} °C");
        Row(sb, "RAM (сред./пик/всего)", $"{N(m.AvgRamUsedMb)} / {N(m.MaxRamUsedMb)} / {N(m.RamTotalMb)} МБ");
        Row(sb, "VRAM (сред./пик/всего)", $"{N(m.AvgVramMb)} / {N(m.MaxVramMb)} / {N(m.VramTotalMb)} МБ");
        Row(sb, "Время кадра (сред./макс.)", $"{N(m.AvgFrameTimeMs, "0.0")} / {N(m.MaxFrameTimeMs)} мс");
        Row(sb, "Скачки времени кадра / фризы", $"{m.StutterCount} / {m.FreezeCount}");
        Row(sb, "Кадров записано", m.FrameCount.ToString(Inv));
        if (m.PerCoreAvg != null) Row(sb, "Средняя загрузка по ядрам", string.Join(" · ", m.PerCoreAvg.Select((v, i) => $"#{i}: {v:0}%")));
        sb.Append("</table></section>");

        if (s.Hardware is { } hw)
        {
            sb.Append("<section class=\"card\"><h2>Конфигурация</h2><table>");
            Row(sb, "Процессор", hw.Cpu.Name);
            foreach (var g in hw.Gpus) Row(sb, "Видеокарта", $"{g.Name} (драйвер {g.DriverVersion ?? "—"})");
            Row(sb, "Оперативная память", $"{hw.Memory.TotalMb / 1024:0.#} ГБ");
            Row(sb, "Windows", $"{hw.Os.Caption} {hw.Os.Version}");
            if (hw.PrimaryDisplay is { } d) Row(sb, "Монитор", $"{d.Width}×{d.Height} @ {d.RefreshHz} Гц");
            if (hw.PowerPlanName != null) Row(sb, "План электропитания", hw.PowerPlanName);
            sb.Append("</table></section>");
        }

        if (r?.DataNotes.Count > 0)
        {
            sb.Append("<section class=\"card\"><h2>Ограничения данных</h2><ul>");
            foreach (var n in r.DataNotes) sb.Append("<li>").Append(E(n)).Append("</li>");
            sb.Append("</ul></section>");
        }

        sb.Append("<footer class=\"muted\">Сформировано: ").Append(E(r?.GeneratedBy ?? "")).Append(" · ")
          .Append(DateTime.Now.ToString("dd.MM.yyyy HH:mm", Inv)).Append(". Все выводы основаны только на записанных данных.</footer>");
        sb.Append("</main></body></html>");
        return sb.ToString();
    }

    private static void Chart(StringBuilder sb, string title, IReadOnlyList<TelemetrySample> samples, DateTime t0, params (string Name, Func<TelemetrySample, double?> Get, string Color)[] series)
    {
        if (!series.Any(x => samples.Any(s => x.Get(s).HasValue))) return;
        const int W = 900, H = 180, PadL = 40, PadB = 20;
        double tMax = Math.Max(1, samples.Count > 0 ? (samples[^1].TimestampUtc - t0).TotalSeconds : 1);
        double vMax = series.SelectMany(x => samples.Select(x.Get)).Where(v => v.HasValue).Select(v => v!.Value).DefaultIfEmpty(1).Max();
        vMax = NiceMax(vMax);

        sb.Append("<h3>").Append(E(title)).Append("</h3><div class=\"legend\">");
        foreach (var x in series) sb.Append("<span><i style=\"background:").Append(x.Color).Append("\"></i>").Append(E(x.Name)).Append("</span>");
        sb.Append("</div><svg viewBox=\"0 0 ").Append(W).Append(' ').Append(H).Append("\" class=\"chart\">");
        for (int g = 0; g <= 4; g++)
        {
            double y = (H - PadB) - (H - PadB - 10) * g / 4.0;
            sb.Append($"<line x1=\"{PadL}\" x2=\"{W}\" y1=\"{y.ToString("0.#", Inv)}\" y2=\"{y.ToString("0.#", Inv)}\" class=\"grid\"/>");
            sb.Append($"<text x=\"{PadL - 6}\" y=\"{(y + 4).ToString("0.#", Inv)}\" text-anchor=\"end\">{(vMax * g / 4).ToString("0", Inv)}</text>");
        }
        sb.Append($"<text x=\"{PadL}\" y=\"{H - 4}\">00:00</text><text x=\"{W}\" y=\"{H - 4}\" text-anchor=\"end\">{AnalysisContext.Clock(tMax)}</text>");

        foreach (var x in series)
        {
            var pts = new StringBuilder();
            bool pen = false;
            foreach (var s in samples)
            {
                if (x.Get(s) is not { } v) { pen = false; continue; }
                double px = PadL + (W - PadL) * (s.TimestampUtc - t0).TotalSeconds / tMax;
                double py = (H - PadB) - (H - PadB - 10) * Math.Min(v, vMax) / vMax;
                pts.Append(pen ? " L" : " M").Append(px.ToString("0.#", Inv)).Append(',').Append(py.ToString("0.#", Inv));
                pen = true;
            }
            sb.Append("<path d=\"").Append(pts).Append("\" fill=\"none\" stroke=\"").Append(x.Color).Append("\" stroke-width=\"1.5\"/>");
        }
        sb.Append("</svg>");
    }

    private static double NiceMax(double v)
    {
        if (v <= 0) return 1;
        if (v <= 100 && v > 50) return 100;
        double mag = Math.Pow(10, Math.Floor(Math.Log10(v)));
        double n = v / mag;
        double nice = n <= 1 ? 1 : n <= 2 ? 2 : n <= 5 ? 5 : 10;
        return nice * mag;
    }

    private static void Kpi(StringBuilder sb, string name, string value) =>
        sb.Append("<div class=\"kpi\"><div class=\"v\">").Append(E(value)).Append("</div><div class=\"muted\">").Append(E(name)).Append("</div></div>");

    private static void Row(StringBuilder sb, string k, string v) =>
        sb.Append("<tr><td class=\"muted\">").Append(E(k)).Append("</td><td>").Append(E(v)).Append("</td></tr>");

    private static string N(double? v, string fmt = "0") => v.HasValue ? v.Value.ToString(fmt, Inv) : "—";
    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
    private static string Dur(TimeSpan d) => d.TotalHours >= 1 ? $"{(int)d.TotalHours} ч {d.Minutes} мин" : $"{d.Minutes} мин {d.Seconds} с";

    private const string Css = """
        :root{color-scheme:dark}*{box-sizing:border-box}body{margin:0;background:#0b0c10;color:#e8eaf0;font:15px/1.5 "Segoe UI",system-ui,sans-serif}
        main{max-width:980px;margin:0 auto;padding:32px 16px}h1{font-size:34px;margin:4px 0}h2{font-size:20px;margin:0 0 12px}h3{font-size:16px;margin:16px 0 6px}
        .brand{color:#7b8cff;font-weight:700;letter-spacing:.08em;text-transform:uppercase;font-size:12px}.muted{color:#8b90a0}
        .kpis{display:grid;grid-template-columns:repeat(auto-fit,minmax(130px,1fr));gap:12px;margin:24px 0}
        .kpi,.card{background:#14161c;border:1px solid #22252e;border-radius:16px;padding:16px}.kpi .v{font-size:28px;font-weight:600}
        .card{margin:16px 0;padding:20px}.hero{background:linear-gradient(180deg,#151a2e,#14161c)}
        .finding{border-left:3px solid #33d1a0;padding:4px 0 4px 14px;margin:14px 0}.sev1{border-color:#ffc24c}.sev2{border-color:#ff8a4c}.sev3{border-color:#ff5c5c}
        table{width:100%;border-collapse:collapse}td,th{text-align:left;padding:6px 8px;border-bottom:1px solid #22252e;vertical-align:top}
        .chart{width:100%;height:auto;background:#101217;border-radius:10px}.chart text{fill:#8b90a0;font-size:11px}.grid{stroke:#22252e}
        .legend{display:flex;gap:14px;font-size:13px;color:#8b90a0;margin-bottom:6px}.legend i{display:inline-block;width:10px;height:10px;border-radius:3px;margin-right:6px}
        footer{margin-top:24px;font-size:13px}@media print{body{background:#fff;color:#000}.kpi,.card{background:#fff;border-color:#ccc}}
        """;
}
