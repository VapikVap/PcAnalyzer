using PcAnalyzer.Core.Models;
using static PcAnalyzer.Core.Analysis.AnalysisContext;

namespace PcAnalyzer.Core.Analysis.Rules;

public sealed class VramRule : IAnalysisRule
{
    public IEnumerable<Finding> Evaluate(AnalysisContext ctx)
    {
        if (!ctx.HasVram) yield break;
        bool Full(int i) => ctx.Samples[i].VramPercent >= ctx.T.VramFullPercent;
        double share = ctx.ShareIdx(Full, i => ctx.Samples[i].VramPercent.HasValue);
        if (share < 0.05) yield break;

        var full = ctx.Where(Full).ToList();
        int spikesFull = full.Count(i => ctx.SpikeAt[i]);
        int notFullCount = ctx.Samples.Count - full.Count;
        int spikesNotFull = ctx.Where(i => !Full(i) && ctx.SpikeAt[i]).Count();
        double rateFull = full.Count == 0 ? 0 : spikesFull / (double)full.Count;
        double rateNot = notFullCount == 0 ? 0 : spikesNotFull / (double)notFullCount;
        bool confirmed = ctx.FrameStats != null && spikesFull >= 3 && rateFull > Math.Max(2 * rateNot, 0.05);

        double? used = ctx.Max(s => s.VramUsedMb);
        double? total = ctx.Samples.LastOrDefault(s => s.VramTotalMb.HasValue)?.VramTotalMb;

        var f = new Finding
        {
            Code = "vram",
            Severity = confirmed ? Severity.Major : Severity.Minor,
            Confidence = confirmed ? Confidence.High : ctx.FrameStats != null ? Confidence.Medium : Confidence.Low,
            Title = confirmed ? "Нехватка видеопамяти (VRAM)" : "Видеопамять почти полностью заполнена",
            Description = confirmed
                ? $"Видеопамять была заполнена на {F0(ctx.T.VramFullPercent)}%+ в течение {P(share)} времени, и именно в эти моменты чаще возникали скачки времени кадра. " +
                  "Когда VRAM не хватает, видеокарта подгружает данные через системную память — это вызывает фризы."
                : $"Видеопамять была заполнена на {F0(ctx.T.VramFullPercent)}%+ в течение {P(share)} времени. " +
                  (ctx.FrameStats != null ? "Явной связи с фризами не обнаружено, но запаса памяти нет." : "Без данных о кадрах проверить влияние на плавность нельзя."),
            When = ctx.Ranges(Full),
            Share = share,
            Impact = confirmed ? "Подёргивания и фризы, особенно при поворотах камеры и смене локаций." : "Риск фризов при более высоких настройках текстур.",
        };
        f.Evidence.Add($"Пиковое использование VRAM: {F0(used)} из {F0(total)} МБ");
        if (ctx.FrameStats != null)
            f.Evidence.Add($"Спайки времени кадра при заполненной VRAM: {spikesFull} ({F1(rateFull * 100)}% замеров) против {F1(rateNot * 100)}% в остальное время");
        if (ctx.Avg(s => s.GameVramMb) is { } g) f.Evidence.Add($"Игра в среднем занимала {F0(g)} МБ видеопамяти");
        f.Recommendations.Add("Снизить качество текстур на одну ступень — это главный потребитель видеопамяти.");
        f.Recommendations.Add("Отключить HD-пакеты текстур, трассировку лучей и генерацию кадров, если они включены — они заметно увеличивают расход VRAM.");
        f.Recommendations.Add("Закрыть браузер и другие программы с аппаратным ускорением во время игры.");
        yield return f;
    }
}

public sealed class RamRule : IAnalysisRule
{
    public IEnumerable<Finding> Evaluate(AnalysisContext ctx)
    {
        if (!ctx.Samples.Any(s => s.RamPercent.HasValue)) yield break;
        bool Full(int i) => ctx.Samples[i].RamPercent >= ctx.T.RamFullPercent || ctx.Samples[i].CommitPercent >= 90;
        double share = ctx.ShareIdx(Full, i => ctx.Samples[i].RamPercent.HasValue);
        if (share < 0.05) yield break;

        bool Paging(int i) => ctx.Samples[i].PageInputsPerSec >= 100;
        int spikesWithPaging = ctx.Where(i => ctx.SpikeAt[i] && Paging(i)).Count();
        int pagingCount = ctx.Where(i => Full(i) && Paging(i)).Count();
        bool confirmed = spikesWithPaging >= 3 || (ctx.FrameStats == null && pagingCount >= 10);

        double? total = ctx.Samples.LastOrDefault(s => s.RamTotalMb.HasValue)?.RamTotalMb;
        var f = new Finding
        {
            Code = "ram",
            Severity = confirmed ? Severity.Major : Severity.Minor,
            Confidence = confirmed && ctx.FrameStats != null ? Confidence.High : Confidence.Medium,
            Title = confirmed ? "Нехватка оперативной памяти — активное использование файла подкачки" : "Оперативная память почти заполнена",
            Description = $"Оперативная память была заполнена на {F0(ctx.T.RamFullPercent)}%+ в течение {P(share)} времени (пик {F0(ctx.Max(s => s.RamPercent))}%). " +
                          (confirmed ? "Система активно читала данные из файла подкачки, и это совпадало со скачками времени кадра." : "Активного обращения к файлу подкачки во время фризов не обнаружено."),
            When = ctx.Ranges(Full),
            Share = share,
            Impact = confirmed ? "Фризы и долгие подгрузки при смене локаций." : "Запаса памяти почти нет — при открытых фоновых программах возможны фризы.",
        };
        f.Evidence.Add($"Пиковое использование RAM: {F0(ctx.Max(s => s.RamUsedMb))} из {F0(total)} МБ");
        if (ctx.Max(s => s.PageInputsPerSec) is { } pi) f.Evidence.Add($"Чтение страниц из файла подкачки: до {F0(pi)} стр/с");
        if (ctx.Avg(s => s.GameRamMb) is { } g) f.Evidence.Add($"Игра в среднем занимала {F0(g)} МБ RAM");
        f.Recommendations.Add("Закрыть браузер, лаунчеры и другие программы, занимающие много памяти, перед игрой.");
        f.Recommendations.Add("Убедиться, что файл подкачки включён и находится на SSD.");
        if (total is { } t && t <= 17000)
            f.Recommendations.Add($"При регулярной нехватке — расширить RAM (сейчас {F0(t / 1024)} ГБ; для современных игр рекомендуется 32 ГБ).");
        yield return f;
    }
}

public sealed class DiskRule : IAnalysisRule
{
    public IEnumerable<Finding> Evaluate(AnalysisContext ctx)
    {
        if (!ctx.Samples.Any(s => s.DiskActivePercent.HasValue)) yield break;
        bool Busy(int i) => ctx.Samples[i].DiskActivePercent >= ctx.T.DiskBusyPercent;
        double share = ctx.ShareIdx(Busy, i => ctx.Samples[i].DiskActivePercent.HasValue);
        int spikeSamples = ctx.Where(i => ctx.SpikeAt[i]).Count();
        int spikesBusy = ctx.Where(i => ctx.SpikeAt[i] && Busy(i)).Count();
        bool correlated = spikesBusy >= 3 && spikeSamples > 0 && spikesBusy >= spikeSamples * 0.3;
        if (!correlated && share < 0.2) yield break;

        var busy = ctx.Where(Busy).ToList();
        var f = new Finding
        {
            Code = "disk",
            Severity = correlated ? (spikesBusy >= 10 ? Severity.Major : Severity.Minor) : Severity.Minor,
            Confidence = correlated ? Confidence.Medium : Confidence.Low,
            Title = correlated ? "Фризы совпадают с пиковой нагрузкой на накопитель" : "Накопитель долго работал на пределе",
            Description = correlated
                ? $"{spikesBusy} из {spikeSamples} моментов со скачками времени кадра пришлись на время, когда накопитель был занят на {F0(ctx.T.DiskBusyPercent)}%+. Игра, вероятно, ждала подгрузки данных."
                : $"Накопитель был занят на {F0(ctx.T.DiskBusyPercent)}%+ в течение {P(share)} времени. Прямой связи с фризами не обнаружено.",
            When = ctx.Ranges(Busy),
            Share = share,
            Impact = correlated ? "Подтормаживания при подгрузке локаций и текстур." : "Возможны долгие загрузки.",
        };
        f.Evidence.Add($"Чтение в эти моменты: {F1(ctx.Avg(s => s.DiskReadBps, busy) / 1048576)} МБ/с, запись: {F1(ctx.Avg(s => s.DiskWriteBps, busy) / 1048576)} МБ/с");
        if (ctx.Avg(s => s.DiskQueue, busy) is { } q) f.Evidence.Add($"Средняя очередь диска: {F1(q)}");
        if (ctx.Hardware?.Disks.Any(d => d.MediaType.Contains("HDD")) == true)
            f.Recommendations.Add("В системе есть HDD — если игра установлена на нём, перенос на SSD заметно уменьшит фризы подгрузки.");
        f.Recommendations.Add("Проверить, не идёт ли во время игры фоновая загрузка/обновление (лаунчеры, Windows Update, облачная синхронизация, антивирусная проверка).");
        f.Recommendations.Add("Оставить на системном и игровом диске не менее 10–15% свободного места.");
        yield return f;
    }
}

public sealed class BackgroundProcessRule : IAnalysisRule
{
    private static readonly HashSet<string> Ignore = new(StringComparer.OrdinalIgnoreCase) { "Idle", "_Total", "System Idle Process" };

    public IEnumerable<Finding> Evaluate(AnalysisContext ctx)
    {
        var snaps = ctx.Samples.Select((s, i) => (s, i)).Where(x => x.s.TopProcesses != null).ToList();
        if (snaps.Count < 3) yield break;
        string? gameExe = Path.GetFileNameWithoutExtension(ctx.Session.ExeName ?? "");

        var stats = new Dictionary<string, (double cpuSum, double ramMax, double cpuSpike, int spikeN)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (s, i) in snaps)
        {
            foreach (var p in s.TopProcesses!)
            {
                if (Ignore.Contains(p.Name) || (!string.IsNullOrEmpty(gameExe) && p.Name.Equals(gameExe, StringComparison.OrdinalIgnoreCase))) continue;
                stats.TryGetValue(p.Name, out var st);
                st.cpuSum += p.CpuPercent;
                st.ramMax = Math.Max(st.ramMax, p.RamMb);
                if (ctx.SpikeAt[i] || (i + 1 < ctx.SpikeAt.Length && ctx.SpikeAt[i + 1])) { st.cpuSpike += p.CpuPercent; st.spikeN++; }
                stats[p.Name] = st;
            }
        }

        var heavy = stats
            .Select(kv => (name: kv.Key, avg: kv.Value.cpuSum / snaps.Count, ram: kv.Value.ramMax, spikeAvg: kv.Value.spikeN > 0 ? kv.Value.cpuSpike / kv.Value.spikeN : 0))
            .Where(x => x.avg >= ctx.T.BackgroundCpuPercent || x.spikeAvg >= ctx.T.BackgroundCpuPercent * 2)
            .OrderByDescending(x => x.avg)
            .Take(5)
            .ToList();
        if (heavy.Count == 0) yield break;

        double sum = heavy.Sum(h => h.avg);
        var f = new Finding
        {
            Code = "background",
            Severity = sum >= 15 ? Severity.Major : Severity.Minor,
            Confidence = Confidence.Medium,
            Title = "Фоновые процессы отнимали ресурсы",
            Description = $"Во время игры сторонние программы в сумме использовали около {F0(sum)}% процессора: {string.Join(", ", heavy.Select(h => h.name))}.",
            Impact = "Меньше ресурсов для игры, возможны микрофризы в моменты активности этих программ.",
        };
        f.Subjects.AddRange(heavy.Select(h => h.name));
        foreach (var h in heavy)
            f.Evidence.Add($"{h.name}: в среднем {F1(h.avg)}% CPU" + (h.spikeAvg > 0 ? $", во время фризов {F1(h.spikeAvg)}%" : "") + $", до {F0(h.ram)} МБ RAM");
        f.Recommendations.Add($"Закрыть перед игрой: {string.Join(", ", heavy.Select(h => h.name))} (если это не нужно во время игры).");
        f.Recommendations.Add("Проверить автозагрузку (Диспетчер задач → Автозагрузка) и расписание антивирусной проверки.");
        yield return f;
    }
}
