using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using PcAnalyzer.Core.Abstractions;
using PcAnalyzer.Core.Models;
using PcAnalyzer.Core.Services;

namespace PcAnalyzer.App.ViewModels;

public sealed partial class DashboardViewModel : PageViewModel
{
    private readonly SessionRecorder _recorder;
    private readonly IGameDetector _detector;
    private readonly ISessionStore _store;
    private readonly ISettingsService _settings;

    public LiveMonitor Live { get; }

    [ObservableProperty] private string _activityText = "";
    [ObservableProperty] private string? _lastSessionText;
    [ObservableProperty] private long? _lastSessionId;
    [ObservableProperty] private bool _cpuTempAvailable;
    [ObservableProperty] private bool _gpuTempAvailable;

    public DashboardViewModel(LiveMonitor live, SessionRecorder recorder, IGameDetector detector, ISessionStore store, ISettingsService settings)
    {
        Live = live;
        _recorder = recorder;
        _detector = detector;
        _store = store;
        _settings = settings;
        live.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(LiveMonitor.Sample) || live.Sample is not { } s) return;
            CpuTempAvailable = s.CpuTempC != null;
            GpuTempAvailable = s.GpuTempC != null;
            UpdateCards(s);
            UpdateActivity();
        };
        recorder.SessionCompleted += (_, _) => Application.Current.Dispatcher.BeginInvoke(() => _ = LoadLastAsync());
        _ = LoadLastAsync();
    }

    public double CpuTempWarn => _settings.Current.Thresholds.CpuTempWarnC;
    public double GpuTempWarn => _settings.Current.Thresholds.GpuTempWarnC;

    public override void OnNavigatedTo()
    {
        _ = LoadLastAsync();
        OnPropertyChanged(nameof(CpuTempWarn));
        OnPropertyChanged(nameof(GpuTempWarn));
    }

    // Gauge cards: temperature when the sensor exists, otherwise load — and the caption says which.
    [ObservableProperty] private GaugeData _cpu = new();
    [ObservableProperty] private GaugeData _gpu = new();
    [ObservableProperty] private GaugeData _ram = new();
    [ObservableProperty] private GaugeData _vram = new();

    private void UpdateCards(TelemetrySample s)
    {
        var t = _settings.Current.Thresholds;
        Cpu = s.CpuTempC is { } ct
            ? new GaugeData(ct, 110, t.CpuTempWarnC, "°C", "Температура", Join($"Загрузка {s.CpuLoad:0}%", s.CpuClockMhz is { } f ? $"{f / 1000:0.0} ГГц" : null))
            : new GaugeData(s.CpuLoad, 100, double.NaN, "%", "Загрузка", Join(s.CpuClockMhz is { } f2 ? $"{f2 / 1000:0.0} ГГц" : null, s.CpuMaxCoreLoad is { } mc ? $"макс. ядро {mc:0}%" : null, "t° недоступна"));
        Gpu = s.GpuTempC is { } gt
            ? new GaugeData(gt, 110, t.GpuTempWarnC, "°C", "Температура", Join(s.GpuLoad is { } gl ? $"Загрузка {gl:0}%" : null, s.GpuCoreClockMhz is { } gc ? $"{gc:0} МГц" : null, s.GpuPowerW is { } gp ? $"{gp:0} Вт" : null))
            : new GaugeData(s.GpuLoad, 100, double.NaN, "%", "Загрузка", Join("t° недоступна"));
        Ram = new GaugeData(s.RamPercent, 100, t.RamFullPercent, "%", "Использование",
            s.RamUsedMb is { } ru && s.RamTotalMb is { } rt ? $"{ru / 1024:0.0} из {rt / 1024:0.0} ГБ" + (s.PagefilePercent is { } pf ? $" · подкачка {pf:0}%" : "") : "нет данных");
        Vram = new GaugeData(s.VramPercent, 100, t.VramFullPercent, "%", "Видеопамять",
            s.VramUsedMb is { } vu ? $"{vu / 1024:0.0}" + (s.VramTotalMb is { } vt ? $" из {vt / 1024:0.0} ГБ" : " ГБ (объём неизвестен)") : "нет данных");
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void OpenLast()
    {
        if (LastSessionId is { } id) Navigator?.OpenAnalysis(id);
        else Navigator?.Navigate(Page.Analysis);
    }

    private static string Join(params string?[] parts) => string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));

    private void UpdateActivity()
    {
        if (_recorder.Active is { } s) ActivityText = $"Идёт запись: {s.GameName} · {s.Duration:hh\\:mm\\:ss}";
        else if (_detector.Current is { } g) ActivityText = $"Запущена игра: {g.Name}" + (_settings.Current.DetectionMode == DetectionMode.Manual ? " · ручной режим, запись не начата" : "");
        else ActivityText = "Мониторинг активен · игра не обнаружена";
    }

    private async Task LoadLastAsync()
    {
        try
        {
            var last = (await _store.ListSessionsAsync(limit: 1)).FirstOrDefault();
            LastSessionId = last?.Id;
            LastSessionText = last == null ? null
                : $"{last.GameName} · {last.StartUtc.ToLocalTime():dd.MM HH:mm} · {last.Summary?.Diagnosis ?? "без анализа"}";
        }
        catch { LastSessionText = null; }
    }
}
