using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcAnalyzer.App.Infrastructure;
using PcAnalyzer.Core.Abstractions;
using PcAnalyzer.Core.Analysis;
using PcAnalyzer.Core.Models;
using PcAnalyzer.Core.Services;

namespace PcAnalyzer.App.ViewModels;

public sealed partial class SessionViewModel : PageViewModel
{
    private readonly SessionRecorder _recorder;
    private readonly IGameDetector _detector;
    private readonly ITelemetryService _telemetry;
    private readonly ISettingsService _settings;
    private readonly DispatcherTimer _timer;

    public LiveMonitor Live { get; }
    public ObservableCollection<RunningApp> RunningApps { get; } = new();
    public ObservableCollection<SourceRow> Sources { get; } = new();
    public ObservableCollection<EventRow> Events { get; } = new();
    public IReadOnlyList<string> LabelPresets { get; } = new[] { "", "До изменения настроек", "После изменения настроек" };

    [ObservableProperty] private bool _isRecording;
    [ObservableProperty] private string _elapsed = "00:00:00";
    [ObservableProperty] private string _gameName = "";
    [ObservableProperty] private string? _label;
    [ObservableProperty] private string _manualName = "";
    [ObservableProperty] private RunningApp? _selectedApp;
    [ObservableProperty] private string _detectedText = "";
    [ObservableProperty] private string _modeText = "";
    [ObservableProperty] private bool _canControl;
    [ObservableProperty] private bool _isElevated;
    [ObservableProperty] private bool _busy;

    public SessionViewModel(LiveMonitor live, SessionRecorder recorder, IGameDetector detector, ITelemetryService telemetry, ISettingsService settings)
    {
        Live = live;
        _recorder = recorder;
        _detector = detector;
        _telemetry = telemetry;
        _settings = settings;
        IsElevated = telemetry.IsElevated;

        recorder.StateChanged += (_, _) => Ui(SyncState);
        recorder.LiveEventRaised += (_, e) => Ui(() => Events.Insert(0, new EventRow(e)));
        recorder.SessionStarted += (_, _) => Ui(() => Events.Clear());
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        settings.Changed += (_, _) => Ui(SyncState);
        SyncState();
    }

    private static void Ui(Action a) => Application.Current?.Dispatcher.BeginInvoke(a);

    public override void OnNavigatedTo()
    {
        SyncState();
        RefreshApps();
    }

    private void SyncState()
    {
        var s = _recorder.Active;
        IsRecording = s != null;
        if (s != null)
        {
            GameName = s.GameName;
            Label = s.Label;
        }
        var mode = _settings.Current.DetectionMode;
        CanControl = mode != DetectionMode.Auto;
        ModeText = mode switch
        {
            DetectionMode.Auto => "Автоматический режим: запись начинается и заканчивается сама при запуске и закрытии игры. Для ручного управления выберите гибридный режим в настройках.",
            DetectionMode.Manual => "Ручной режим: игры определяются, но запись начинается только по кнопке.",
            _ => "Гибридный режим: игры записываются автоматически, запись также можно начать, остановить и переименовать вручную.",
        };
        Refresh();
    }

    private void Refresh()
    {
        if (_recorder.Active is { } s) Elapsed = s.Duration.ToString(@"hh\:mm\:ss");
        var g = _detector.Current;
        DetectedText = g == null ? "Игра не обнаружена" : $"{g.Name} ({g.ExeName}) — {g.Reason}";

        var src = _telemetry.Sources;
        for (int i = 0; i < src.Count; i++)
        {
            if (i >= Sources.Count) Sources.Add(new SourceRow());
            Sources[i].Name = src[i].Name;
            Sources[i].Available = src[i].Available;
            Sources[i].Message = src[i].Message;
        }
    }

    [RelayCommand]
    private void RefreshApps()
    {
        var selected = SelectedApp?.Pid;
        RunningApps.Clear();
        foreach (var a in _detector.GetRunningApps()) RunningApps.Add(a);
        SelectedApp = RunningApps.FirstOrDefault(a => a.Pid == selected)
                      ?? RunningApps.FirstOrDefault(a => a.Pid == _detector.Current?.Pid);
    }

    partial void OnSelectedAppChanged(RunningApp? value)
    {
        if (value != null) ManualName = value.Name;
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        if (Busy) return;
        Busy = true;
        try
        {
            await _recorder.StartManualAsync(ManualName, SelectedApp);
            ShowStatus(SelectedApp == null
                ? "Запись начата без привязки к процессу: FPS и показатели игры недоступны, записываются только общие метрики."
                : "Запись начата");
        }
        catch (Exception ex) { ShowStatus("Не удалось начать запись: " + ex.Message); }
        finally { Busy = false; }
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        if (Busy) return;
        Busy = true;
        try
        {
            var id = _recorder.Active?.Id;
            ShowStatus("Сохранение и анализ…");
            await _recorder.StopAsync();
            if (id is { } sid) Navigator?.OpenAnalysis(sid);
        }
        catch (Exception ex) { ShowStatus("Ошибка при остановке: " + ex.Message); }
        finally { Busy = false; }
    }

    [RelayCommand]
    private async Task SaveNameAsync()
    {
        await _recorder.RenameAsync(GameName, Label);
        ShowStatus("Название сессии сохранено");
    }

    [RelayCommand]
    private void RestartElevated()
    {
        if (_recorder.IsRecording)
        {
            ShowStatus("Сначала остановите запись — перезапуск прервёт сессию.");
            return;
        }
        if (SystemIntegration.RestartElevated()) Application.Current.Shutdown();
        else ShowStatus("Перезапуск с правами администратора отменён.");
    }
}

public sealed partial class SourceRow : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _available;
    [ObservableProperty] private string _message = "";
}

public sealed class EventRow
{
    public EventRow(LiveEvent e)
    {
        Time = AnalysisContext.Clock(e.OffsetSec);
        Kind = e.Kind;
        Message = e.Message;
        Severity = e.Severity;
    }
    public string Time { get; }
    public string Kind { get; }
    public string Message { get; }
    public Severity Severity { get; }
}
