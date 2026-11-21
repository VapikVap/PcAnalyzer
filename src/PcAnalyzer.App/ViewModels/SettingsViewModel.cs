using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PcAnalyzer.App.Infrastructure;
using PcAnalyzer.Core.Abstractions;
using PcAnalyzer.Core.Models;
using PcAnalyzer.Core.Services;

namespace PcAnalyzer.App.ViewModels;

public sealed partial class SettingsViewModel : PageViewModel
{
    private readonly ISettingsService _settings;
    private readonly ISessionStore _store;
    private readonly ITelemetryService _telemetry;
    private readonly SessionRecorder _recorder;

    public IReadOnlyList<IntervalOption> Intervals { get; } = new[]
    {
        new IntervalOption(500, "0,5 с — максимальная детализация"),
        new IntervalOption(1000, "1 с — рекомендуется"),
        new IntervalOption(2000, "2 с — экономно"),
        new IntervalOption(5000, "5 с — минимальная нагрузка"),
    };
    public IReadOnlyList<int> ChartWindows { get; } = new[] { 1, 3, 5, 10 };

    [ObservableProperty] private DetectionMode _detectionMode;
    [ObservableProperty] private IntervalOption? _interval;
    [ObservableProperty] private bool _powerSaving;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _startMinimized;
    [ObservableProperty] private bool _minimizeToTray;
    [ObservableProperty] private bool _closeToTray;
    [ObservableProperty] private bool _notifications;
    [ObservableProperty] private bool _usePresentMon;
    [ObservableProperty] private string? _presentMonPath;
    [ObservableProperty] private string _frameSourceStatus = "";
    [ObservableProperty] private double _cpuTempWarn;
    [ObservableProperty] private double _gpuTempWarn;
    [ObservableProperty] private double _fpsDropPercent;
    [ObservableProperty] private double _freezeMs;
    [ObservableProperty] private double _vramFull;
    [ObservableProperty] private double _ramFull;
    [ObservableProperty] private int _historyDays;
    [ObservableProperty] private int _rawDays;
    [ObservableProperty] private int _maxDbMb;
    [ObservableProperty] private int _minAutoSeconds;
    [ObservableProperty] private string _userGames = "";
    [ObservableProperty] private string _excludedApps = "";
    [ObservableProperty] private int _chartMinutes;
    [ObservableProperty] private bool _chartSmoothing;
    [ObservableProperty] private string? _exportFolder;
    [ObservableProperty] private string _dbSize = "";
    [ObservableProperty] private bool _isElevated;

    public SettingsViewModel(ISettingsService settings, ISessionStore store, ITelemetryService telemetry, SessionRecorder recorder)
    {
        _settings = settings;
        _store = store;
        _telemetry = telemetry;
        _recorder = recorder;
        IsElevated = telemetry.IsElevated;
        Load();
    }

    public string DataFolder => AppPaths.DataDir;
    public string DefaultExportFolder => AppPaths.DefaultExportDir;

    public override void OnNavigatedTo() => Load();

    private void Load()
    {
        var s = _settings.Current;
        DetectionMode = s.DetectionMode;
        Interval = Intervals.FirstOrDefault(i => i.Ms == s.SampleIntervalMs) ?? Intervals[1];
        PowerSaving = s.PowerSavingMonitoring;
        StartWithWindows = SystemIntegration.IsAutostartEnabled();
        StartMinimized = s.StartMinimized;
        MinimizeToTray = s.MinimizeToTray;
        CloseToTray = s.CloseToTray;
        Notifications = s.Notifications;
        UsePresentMon = s.FrameSource == FrameSourceKind.PresentMon;
        PresentMonPath = s.PresentMonPath;
        FrameSourceStatus = _telemetry.FrameProvider.StatusMessage;
        var t = s.Thresholds;
        CpuTempWarn = t.CpuTempWarnC; GpuTempWarn = t.GpuTempWarnC; FpsDropPercent = t.FpsDropPercent;
        FreezeMs = t.FreezeMs; VramFull = t.VramFullPercent; RamFull = t.RamFullPercent;
        HistoryDays = s.HistoryRetentionDays; RawDays = s.RawTelemetryRetentionDays; MaxDbMb = s.MaxDatabaseMb;
        MinAutoSeconds = s.MinAutoSessionSeconds;
        UserGames = string.Join(Environment.NewLine, s.UserGames);
        ExcludedApps = string.Join(Environment.NewLine, s.ExcludedApps);
        ChartMinutes = s.ChartWindowMinutes; ChartSmoothing = s.ChartSmoothing;
        ExportFolder = s.ExportFolder;
        DbSize = $"{_store.DatabaseSizeBytes / 1048576.0:0.#} МБ";
    }

    [RelayCommand]
    private void Save()
    {
        var s = _settings.Current;
        s.DetectionMode = DetectionMode;
        s.SampleIntervalMs = Interval?.Ms ?? 1000;
        s.PowerSavingMonitoring = PowerSaving;
        s.StartMinimized = StartMinimized;
        s.MinimizeToTray = MinimizeToTray;
        s.CloseToTray = CloseToTray;
        s.Notifications = Notifications;
        s.FrameSource = UsePresentMon ? FrameSourceKind.PresentMon : FrameSourceKind.None;
        s.PresentMonPath = string.IsNullOrWhiteSpace(PresentMonPath) ? null : PresentMonPath.Trim().Trim('"');
        var t = s.Thresholds;
        t.CpuTempWarnC = Math.Clamp(CpuTempWarn, 50, 110);
        t.GpuTempWarnC = Math.Clamp(GpuTempWarn, 50, 110);
        t.FpsDropPercent = Math.Clamp(FpsDropPercent, 5, 80);
        t.FreezeMs = Math.Clamp(FreezeMs, 50, 2000);
        t.VramFullPercent = Math.Clamp(VramFull, 50, 100);
        t.RamFullPercent = Math.Clamp(RamFull, 50, 100);
        s.HistoryRetentionDays = Math.Max(0, HistoryDays);
        s.RawTelemetryRetentionDays = Math.Max(0, RawDays);
        s.MaxDatabaseMb = Math.Max(0, MaxDbMb);
        s.MinAutoSessionSeconds = Math.Clamp(MinAutoSeconds, 0, 3600);
        s.UserGames = SplitList(UserGames);
        s.ExcludedApps = SplitList(ExcludedApps);
        s.ChartWindowMinutes = ChartMinutes;
        s.ChartSmoothing = ChartSmoothing;
        s.ExportFolder = string.IsNullOrWhiteSpace(ExportFolder) ? null : ExportFolder;

        try
        {
            SystemIntegration.SetAutostart(StartWithWindows);
            s.StartWithWindows = StartWithWindows;
            _settings.Save();
            Load();
            ShowStatus("Настройки сохранены");
        }
        catch (Exception ex)
        {
            ShowStatus("Не удалось сохранить настройки: " + ex.Message);
        }
    }

    private static List<string> SplitList(string text) =>
        text.Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    [RelayCommand]
    private void BrowsePresentMon()
    {
        var dlg = new OpenFileDialog { Title = "Выберите PresentMon (консольная версия)", Filter = "PresentMon|PresentMon*.exe|Программы|*.exe" };
        if (dlg.ShowDialog() == true) { PresentMonPath = dlg.FileName; UsePresentMon = true; }
    }

    [RelayCommand]
    private void BrowseExport()
    {
        var dlg = new OpenFolderDialog { Title = "Папка для экспорта отчётов" };
        if (dlg.ShowDialog() == true) ExportFolder = dlg.FolderName;
    }

    [RelayCommand]
    private void OpenPresentMonPage() => SystemIntegration.Open("https://github.com/GameTechDev/PresentMon/releases");

    [RelayCommand]
    private void OpenDataFolder() => SystemIntegration.Open(AppPaths.DataDir);

    [RelayCommand]
    private async Task CleanupAsync()
    {
        Save();
        var r = await _store.RunRetentionAsync(_settings.Current);
        DbSize = $"{_store.DatabaseSizeBytes / 1048576.0:0.#} МБ";
        ShowStatus($"Очистка выполнена: удалено сессий — {r.SessionsDeleted}, очищено сырых данных — {r.RawDataPurged}.");
    }

    [RelayCommand]
    private void RestartElevated()
    {
        if (_recorder.IsRecording) { ShowStatus("Сначала остановите запись — перезапуск прервёт сессию."); return; }
        if (SystemIntegration.RestartElevated()) Application.Current.Shutdown();
        else ShowStatus("Перезапуск с правами администратора отменён.");
    }
}

public sealed record IntervalOption(int Ms, string Text)
{
    public override string ToString() => Text;
}
