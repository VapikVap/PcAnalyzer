using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcAnalyzer.App.Controls;
using PcAnalyzer.Core.Abstractions;
using PcAnalyzer.Core.Analysis;
using PcAnalyzer.Core.Models;
using PcAnalyzer.Core.Services;

namespace PcAnalyzer.App.ViewModels;

public sealed partial class HistoryViewModel : PageViewModel
{
    public const string AllGames = "Все игры";
    private readonly ISessionStore _store;
    private bool _suppress;

    public ObservableCollection<string> Games { get; } = new();
    public ObservableCollection<SessionRowVm> Rows { get; } = new();
    public ObservableCollection<CompareRowVm> Comparison { get; } = new();
    public ObservableCollection<string> CompareWarnings { get; } = new();

    [ObservableProperty] private string _selectedGame = AllGames;
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private bool _hasComparison;
    [ObservableProperty] private string _compareVerdict = "";
    [ObservableProperty] private string _compareTitle = "";
    [ObservableProperty] private bool _hasTrend;
    [ObservableProperty] private bool _isEmpty;

    public ChartSeries TrendAvg { get; } = new("Средний FPS", Color.FromRgb(91, 155, 255));
    public ChartSeries TrendLow { get; } = new("1% Low", Color.FromRgb(255, 181, 71));
    public ChartSeries[] TrendChart => new[] { TrendAvg, TrendLow };

    public HistoryViewModel(ISessionStore store, SessionRecorder recorder)
    {
        _store = store;
        recorder.SessionCompleted += (_, _) => Application.Current.Dispatcher.BeginInvoke(() => _ = LoadAsync());
    }

    public override void OnNavigatedTo() => _ = LoadAsync();

    partial void OnSelectedGameChanged(string value)
    {
        if (!_suppress) _ = LoadRowsAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var games = await _store.ListGamesAsync();
            _suppress = true;
            var keep = SelectedGame;
            Games.Clear();
            Games.Add(AllGames);
            foreach (var g in games) Games.Add(g);
            SelectedGame = Games.Contains(keep) ? keep : AllGames;
            _suppress = false;
            await LoadRowsAsync();
        }
        catch (Exception ex) { ShowStatus("Ошибка загрузки истории: " + ex.Message); }
    }

    private async Task LoadRowsAsync()
    {
        var list = await _store.ListSessionsAsync(SelectedGame == AllGames ? null : SelectedGame);
        foreach (var r in Rows) r.PropertyChanged -= OnRowChanged;
        Rows.Clear();
        foreach (var s in list)
        {
            var row = new SessionRowVm(s);
            row.PropertyChanged += OnRowChanged;
            Rows.Add(row);
        }
        IsEmpty = Rows.Count == 0;
        SelectedCount = 0;
        HasComparison = false;

        // FPS trend for a single game (chronological)
        var trend = list.Where(s => s.Summary?.AvgFps != null).OrderBy(s => s.StartUtc).ToList();
        HasTrend = SelectedGame != AllGames && trend.Count >= 2;
        TrendAvg.SetAll(trend.Select(s => s.Summary!.AvgFps));
        TrendLow.SetAll(trend.Select(s => s.Summary!.P1LowFps));
        TrendAvg.Capacity = TrendLow.Capacity = Math.Max(2, trend.Count);
    }

    private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SessionRowVm.IsSelected)) return;
        SelectedCount = Rows.Count(r => r.IsSelected);
        if (SelectedCount != 2) HasComparison = false;
    }

    [RelayCommand]
    private void Open(SessionRowVm row) => Navigator?.OpenAnalysis(row.Session.Id);

    [RelayCommand]
    private async Task DeleteAsync(SessionRowVm row)
    {
        var res = MessageBox.Show($"Удалить сессию «{row.Session.GameName}» от {row.Date}?\nДанные будут удалены без возможности восстановления.",
            "Удаление сессии", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (res != MessageBoxResult.Yes) return;
        await _store.DeleteSessionAsync(row.Session.Id);
        await LoadAsync();
        ShowStatus("Сессия удалена");
    }

    [RelayCommand]
    private void Compare()
    {
        var sel = Rows.Where(r => r.IsSelected).Select(r => r.Session).OrderBy(s => s.StartUtc).ToList();
        if (sel.Count != 2) { ShowStatus("Выберите ровно две сессии для сравнения."); return; }
        var (before, after) = (sel[0], sel[1]);
        var result = SessionComparer.Compare(before, after);
        CompareTitle = $"«{before.Label ?? "До"}» {before.StartUtc.ToLocalTime():dd.MM HH:mm}  →  «{after.Label ?? "После"}» {after.StartUtc.ToLocalTime():dd.MM HH:mm}";
        CompareVerdict = result.Verdict;
        Comparison.Clear();
        foreach (var m in result.Metrics.Where(m => m.Before.HasValue || m.After.HasValue)) Comparison.Add(new CompareRowVm(m));
        CompareWarnings.Clear();
        foreach (var w in result.Warnings) CompareWarnings.Add(w);
        HasComparison = true;
    }
}

public sealed partial class SessionRowVm : ObservableObject
{
    public SessionRowVm(GameSession s)
    {
        Session = s;
        var m = s.Summary;
        Date = s.StartUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
        Duration = Fmt.Duration(s.Duration);
        Fps = m?.AvgFps is { } f ? $"{f:0}" : "—";
        Low = m?.P1LowFps is { } l ? $"{l:0}" : "—";
        Score = m?.OverallScore is { } o ? $"{o:0}" : "—";
        Diagnosis = s.Status == SessionStatus.Recording ? "Идёт запись…" : m?.Diagnosis ?? "Нет анализа";
        Severity = m?.WorstSeverity ?? Severity.None;
        Issues = m == null ? "" : m.IssueCount == 0 ? "Без проблем" : $"Проблем: {m.IssueCount}";
        Tags = string.Join(" · ", new[] { s.Label, s.Status == SessionStatus.Interrupted ? "прервана" : null }.Where(x => x != null));
    }

    public GameSession Session { get; }
    public string Date { get; }
    public string Duration { get; }
    public string Fps { get; }
    public string Low { get; }
    public string Score { get; }
    public string Diagnosis { get; }
    public string Issues { get; }
    public string Tags { get; }
    public Severity Severity { get; }
    [ObservableProperty] private bool _isSelected;
}

public sealed class CompareRowVm(MetricDelta m)
{
    public string Name { get; } = m.Name;
    public string Before { get; } = Format(m.Before, m.Unit);
    public string After { get; } = Format(m.After, m.Unit);
    public string Delta { get; } = m.DeltaPercent is { } p ? $"{(p >= 0 ? "+" : "")}{p:0.#}%" : "—";
    public DeltaVerdict Verdict { get; } = m.Verdict;
    public string VerdictText { get; } = m.Verdict switch
    {
        DeltaVerdict.Better => "лучше", DeltaVerdict.Worse => "хуже", DeltaVerdict.Same => "без изменений", _ => "нет данных"
    };

    private static string Format(double? v, string unit) => v.HasValue ? $"{v:0.#} {unit}".Trim() : "—";
}
