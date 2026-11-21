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

    public AnalysisViewModel(ISessionStore store, SessionRecorder recorder, ExportService export)
    {
        _store = store;
        _recorder = recorder;
        _export = export;
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
        foreach (var f in s.Report?.Findings ?? new()) Findings.Add(new FindingVm(f));
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
}

public sealed class SessionPick(GameSession s)
{
    public long Id { get; } = s.Id;
    public string Text { get; } = $"{s.GameName} — {s.StartUtc.ToLocalTime():dd.MM.yyyy HH:mm}" + (s.Label != null ? $" ({s.Label})" : "");
    public override string ToString() => Text;
}

public sealed class FindingVm(Finding f)
{
    public Finding F { get; } = f;
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
