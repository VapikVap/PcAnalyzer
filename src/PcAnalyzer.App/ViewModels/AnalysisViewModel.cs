using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcAnalyzer.App.Controls;
using PcAnalyzer.App.Infrastructure;
using PcAnalyzer.Core.Abstractions;
using PcAnalyzer.Core.Analysis;
using PcAnalyzer.Core.Models;
using PcAnalyzer.Core.Services;

namespace PcAnalyzer.App.ViewModels;

public sealed partial class AnalysisViewModel : PageViewModel
{
    private readonly ISessionStore _store;
    private readonly SessionRecorder _recorder;
    private readonly ExportService _export;
    private bool _loading;

    public long? RequestedSessionId { get; set; }

    public ObservableCollection<SessionPick> Sessions { get; } = new();
    public ObservableCollection<FindingVm> Findings { get; } = new();
    public ObservableCollection<string> Recommendations { get; } = new();
    public ObservableCollection<EventVm> Events { get; } = new();
    public ObservableCollection<string> DataNotes { get; } = new();

    [ObservableProperty] private SessionPick? _selected;
    [ObservableProperty] private GameSession? _session;
    [ObservableProperty] private SessionSummary? _summary;
    [ObservableProperty] private SessionReport? _report;
    [ObservableProperty] private bool _hasSession;
    [ObservableProperty] private bool _hasRawData;
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private string _headline = "";
    [ObservableProperty] private Brush? _headlineBrush;

    public ChartSeries FpsSeries { get; } = new("FPS", Color.FromRgb(91, 155, 255));
    public ChartSeries FrameSeries { get; } = new("Макс. время кадра", Color.FromRgb(255, 138, 71));
    public ChartSeries GpuSeries { get; } = new("GPU", Color.FromRgb(61, 116, 255));
    public ChartSeries CpuSeries { get; } = new("CPU", Color.FromRgb(43, 208, 143));
    public ChartSeries CoreSeries { get; } = new("Макс. ядро", Color.FromRgb(255, 181, 71));
    public ChartSeries CpuTempSeries { get; } = new("CPU °C", Color.FromRgb(255, 90, 95));
    public ChartSeries GpuTempSeries { get; } = new("GPU °C", Color.FromRgb(61, 116, 255));
    public ChartSeries[] FpsChart => new[] { FpsSeries };
    public ChartSeries[] FrameChart => new[] { FrameSeries };
    public ChartSeries[] LoadChart => new[] { GpuSeries, CpuSeries, CoreSeries };
    public ChartSeries[] TempChart => new[] { GpuTempSeries, CpuTempSeries };

    private readonly WindowsTweaks _tweaks;

    public AnalysisViewModel(ISessionStore store, SessionRecorder recorder, ExportService export, WindowsTweaks tweaks)
    {
        _store = store;
        _recorder = recorder;
        _export = export;
        _tweaks = tweaks;
        recorder.SessionCompleted += (_, s) => System.Windows.Application.Current.Dispatcher.BeginInvoke(() => RequestedSessionId ??= s.Id);
    }

    public override async void OnNavigatedTo()
    {
        try { await LoadListAsync(); }
        catch (Exception ex) { ShowStatus("Ошибка загрузки: " + ex.Message); }
    }

    private async Task LoadListAsync()
    {
        _loading = true;
        var list = await _store.ListSessionsAsync(limit: 100);
        Sessions.Clear();
        foreach (var s in list.Where(s => s.Status != SessionStatus.Recording)) Sessions.Add(new SessionPick(s));
        _loading = false;
        var id = RequestedSessionId ?? Selected?.Id ?? Sessions.FirstOrDefault()?.Id;
        RequestedSessionId = null;
        var pick = Sessions.FirstOrDefault(p => p.Id == id) ?? Sessions.FirstOrDefault();
        if (pick != null && pick.Id == Selected?.Id) await LoadAsync(pick.Id);
        else Selected = pick;
        if (pick == null) HasSession = false;
    }

    partial void OnSelectedChanged(SessionPick? value)
    {
        if (!_loading && value != null) _ = LoadAsync(value.Id);
    }

    private async Task LoadAsync(long id)
    {
        var s = await _store.GetSessionAsync(id);
        if (s == null) { HasSession = false; return; }
        Session = s;
        Summary = s.Summary;
        Report = s.Report;
        HasSession = true;
        Subtitle = $"{s.StartUtc.ToLocalTime():dd.MM.yyyy HH:mm} · {Fmt.Duration(s.Duration)}" +
                   (s.Label != null ? " · " + s.Label : "") +
                   (s.Status == SessionStatus.Interrupted ? " · запись прервана" : "") +
                   (s.Origin == SessionOrigin.AutoDetected ? " · авто" : " · вручную");
        Headline = s.Report?.Headline ?? "Отчёт ещё не сформирован";
        HeadlineBrush = (Brush)new Converters.SeverityToBrush().Convert(s.Summary?.WorstSeverity ?? Severity.None, typeof(Brush), null!, null!);

        Findings.Clear();
        foreach (var f in s.Report?.Findings ?? new()) Findings.Add(new FindingVm(f, BuildActions(f)));
        Recommendations.Clear();
        foreach (var r in s.Report?.Recommendations ?? new()) Recommendations.Add(r);
        Events.Clear();
        foreach (var e in s.Report?.Events ?? new()) Events.Add(new EventVm(e));
        DataNotes.Clear();
        foreach (var n in s.Report?.DataNotes ?? new()) DataNotes.Add(n);

        var samples = await _store.LoadTelemetryAsync(id);
        HasRawData = samples.Count > 0;
        FpsSeries.SetAll(samples.Select(x => x.Fps));
        FrameSeries.SetAll(samples.Select(x => x.FrameTimeMaxMs));
        GpuSeries.SetAll(samples.Select(x => x.GpuLoad));
        CpuSeries.SetAll(samples.Select(x => x.CpuLoad));
        CoreSeries.SetAll(samples.Select(x => x.CpuMaxCoreLoad));
        CpuTempSeries.SetAll(samples.Select(x => x.CpuTempC));
        GpuTempSeries.SetAll(samples.Select(x => x.GpuTempC));
        foreach (var c in new[] { FpsSeries, FrameSeries, GpuSeries, CpuSeries, CoreSeries, CpuTempSeries, GpuTempSeries }) c.Capacity = Math.Max(2, samples.Count);
    }

    [RelayCommand]
    private async Task ExportHtmlAsync()
    {
        if (Session == null) return;
        try
        {
            var path = await _export.ExportHtmlAsync(Session);
            SystemIntegration.RevealInExplorer(path);
            ShowStatus("Отчёт сохранён: " + path);
        }
        catch (Exception ex) { ShowStatus("Не удалось экспортировать: " + ex.Message); }
    }

    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        if (Session == null) return;
        try
        {
            var path = await _export.ExportCsvAsync(Session);
            SystemIntegration.RevealInExplorer(path);
            ShowStatus("CSV сохранён: " + path);
        }
        catch (Exception ex) { ShowStatus("Не удалось экспортировать: " + ex.Message); }
    }

    [RelayCommand]
    private async Task ReanalyzeAsync()
    {
        if (Session == null) return;
        if (!HasRawData) { ShowStatus("Сырые данные этой сессии удалены по сроку хранения — повторный анализ невозможен."); return; }
        await _recorder.ReanalyzeAsync(Session.Id);
        await LoadAsync(Session.Id);
        ShowStatus("Анализ выполнен заново с текущими порогами");
    }

    // ---------------- "Сделать" buttons for findings ----------------

    private List<FindingAction> BuildActions(Finding f)
    {
        var list = new List<FindingAction>();
        FindingAction Open(string label, string target) => new(label, false, () => { Try(() => WindowsTweaks.Open(target)); return Task.CompletedTask; });
        FindingAction GoOptimize() => new("Проверить настройки Windows", false, () => { Navigator?.Navigate(Page.Optimize); return Task.CompletedTask; });
        void AddPowerPlanFix()
        {
            if (!SafeIsPowerSaver()) return;
            list.Add(new FindingAction("Включить «Сбалансированный» план", true, () =>
            {
                if (Try(() => _tweaks.Apply(WindowsTweaks.PowerPlan)))
                    ShowStatus("План электропитания переключён на «Сбалансированный». Вернуть можно на странице «Оптимизация».");
                return Task.CompletedTask;
            }));
        }

        switch (f.Code)
        {
            case "background":
                var running = ProcessCloser.RunningClosable(BackgroundNames(f));
                if (running.Count > 0)
                    list.Add(new FindingAction("Закрыть: " + string.Join(", ", running), true, () => CloseProcessesAsync(running),
                        "Программы получат обычный запрос на закрытие; не ответившие за 4 с будут завершены"));
                list.Add(Open("Автозагрузка", "ms-settings:startupapps"));
                break;
            case "power_saver_plan":
                AddPowerPlanFix();
                list.Add(Open("Параметры питания", "ms-settings:powersleep"));
                break;
            case "cpu_clock_low":
            case "cpu_clock_unstable":
                AddPowerPlanFix();
                list.Add(Open("Параметры питания", "ms-settings:powersleep"));
                break;
            case "ram":
                list.Add(new FindingAction("Освободить память", true, FreeMemoryAsync,
                    "Показать программы, которые сейчас занимают больше всего памяти, и закрыть выбранные"));
                list.Add(Open("Автозагрузка", "ms-settings:startupapps"));
                list.Add(GoOptimize());
                break;
            case "disk":
                list.Add(Open("Освободить место", "ms-settings:storagesense"));
                list.Add(GoOptimize());
                break;
            case "vram":
                list.Add(Open("Параметры графики Windows", "ms-settings:display-advancedgraphics"));
                break;
            case "stutter":
            case "degradation":
            case "gpu_drop_cpu_peak":
            case "cpu_saturated":
                list.Add(GoOptimize());
                break;
        }
        return list;
    }

    /// <summary>Process names from the finding; older reports only have them in the evidence lines ("name: в среднем …").</summary>
    private static IEnumerable<string> BackgroundNames(Finding f) =>
        f.Subjects.Count > 0 ? f.Subjects
            : f.Evidence.Select(e => e.Split(':')[0].Trim()).Where(n => n.Length > 0 && !n.Contains(' '));

    private async Task FreeMemoryAsync()
    {
        var top = await Task.Run(() => ProcessCloser.TopMemory(5));
        if (top.Count == 0) { ShowStatus("Сейчас нет программ, занимающих много памяти (больше 300 МБ)."); return; }
        var lines = string.Join("\n", top.Select(t => $"•  {t.Name} — {t.Mb / 1024:0.0} ГБ"));
        var answer = System.Windows.MessageBox.Show(
            $"Больше всего памяти сейчас занимают:\n\n{lines}\n\nЗакрыть их? Несохранённые данные в них могут быть потеряны.\n" +
            "Если какая-то из них нужна — нажмите «Нет» и закройте лишние вручную.",
            "Освободить память", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
        if (answer != System.Windows.MessageBoxResult.Yes) return;
        await CloseConfirmedAsync(top.Select(t => t.Name).ToList());
    }

    private async Task CloseProcessesAsync(List<string> names)
    {
        var answer = System.Windows.MessageBox.Show(
            $"Закрыть программы: {string.Join(", ", names)}?\n\nНесохранённые данные в них могут быть потеряны.",
            "Закрыть фоновые программы", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
        if (answer != System.Windows.MessageBoxResult.Yes) return;
        await CloseConfirmedAsync(names);
    }

    private async Task CloseConfirmedAsync(List<string> names)
    {
        var r = await ProcessCloser.CloseAsync(names);
        ShowStatus((r.Closed.Count > 0 ? "Закрыто: " + string.Join(", ", r.Closed) + ". " : "") +
                   (r.Failed.Count > 0 ? "Не удалось закрыть: " + string.Join(", ", r.Failed) + " (возможно, нужны права администратора)." : ""));
        if (Session != null) await LoadAsync(Session.Id); // refresh buttons
    }

    private bool SafeIsPowerSaver()
    {
        try { return _tweaks.IsPowerSaverActive; } catch { return false; }
    }

    private bool Try(Action a)
    {
        try { a(); return true; }
        catch (Exception ex) { ShowStatus("Не получилось: " + ex.Message); return false; }
    }
}

public sealed class SessionPick(GameSession s)
{
    public long Id { get; } = s.Id;
    public string Text { get; } = $"{s.GameName} — {s.StartUtc.ToLocalTime():dd.MM.yyyy HH:mm}" + (s.Label != null ? $" ({s.Label})" : "");
    public override string ToString() => Text;
}

public sealed class FindingVm(Finding f, IReadOnlyList<FindingAction> actions)
{
    public Finding F { get; } = f;
    /// <summary>Buttons that carry out the recommendations right from the report.</summary>
    public IReadOnlyList<FindingAction> Actions { get; } = actions;
    public bool HasActions => Actions.Count > 0;
    public string Title => F.Title;
    public string Description => F.Description;
    public Severity Severity => F.Severity;
    public string SeverityText => Core.Analysis.SeverityText.Name(F.Severity);
    public string ConfidenceText => "Уверенность: " + Core.Analysis.SeverityText.Name(F.Confidence);
    public string When => F.When.Count > 0 ? string.Join(", ", F.When) : "";
    public bool HasWhen => F.When.Count > 0;
    public IReadOnlyList<string> Evidence => F.Evidence;
    public string Impact => F.Impact;
    public IReadOnlyList<string> Recommendations => F.Recommendations;
}

public sealed class FindingAction(string label, bool primary, Func<Task> run, string? tooltip = null)
{
    public string Label { get; } = label;
    public bool IsPrimary { get; } = primary;
    public string? ToolTip { get; } = tooltip;
    public IAsyncRelayCommand Command { get; } = new AsyncRelayCommand(run);
}

public sealed class EventVm(PerformanceEvent e)
{
    public string Time { get; } = AnalysisContext.Clock(e.OffsetSec);
    public string Kind { get; } = e.Kind;
    public string Fps { get; } = e.FpsDuring.HasValue ? $"{e.FpsBefore:0} → {e.FpsDuring:0}" : e.WorstFrameMs.HasValue ? $"{e.WorstFrameMs:0} мс" : "—";
    public string Causes { get; } = string.Join("\n", e.LikelyCauses);
}

public static class Fmt
{
    public static string Duration(TimeSpan d) => d.TotalHours >= 1 ? $"{(int)d.TotalHours} ч {d.Minutes} мин" : d.TotalMinutes >= 1 ? $"{d.Minutes} мин {d.Seconds} с" : $"{d.Seconds} с";
}
