using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using PcAnalyzer.App.Controls;
using PcAnalyzer.Core.Abstractions;
using PcAnalyzer.Core.Models;

namespace PcAnalyzer.App.ViewModels;

/// <summary>
/// UI-side mirror of the telemetry stream: latest values + rolling chart history.
/// All updates are marshalled to the UI thread; values stay null when a metric is unavailable.
/// </summary>
public sealed partial class LiveMonitor : ObservableObject
{
    private readonly ITelemetryService _telemetry;
    private readonly ISettingsService _settings;

    [ObservableProperty] private TelemetrySample? _sample;
    [ObservableProperty] private double? _cpuClockGhz;
    [ObservableProperty] private double? _ramUsedGb;
    [ObservableProperty] private double? _ramTotalGb;
    [ObservableProperty] private double? _vramUsedGb;
    [ObservableProperty] private double? _vramTotalGb;
    [ObservableProperty] private double? _diskReadMb;
    [ObservableProperty] private double? _diskWriteMb;
    [ObservableProperty] private bool _hasFps;
    [ObservableProperty] private string _frameSourceStatus = "";
    [ObservableProperty] private double? _healthScore;
    [ObservableProperty] private string _healthLabel = "—";
    [ObservableProperty] private Brush _healthBrush = Brushes.Gray;
    [ObservableProperty] private string _healthDetails = "";
    [ObservableProperty] private string _cpuName = "Процессор";
    [ObservableProperty] private string _gpuName = "Видеокарта";
    [ObservableProperty] private string _ramName = "Оперативная память";

    public ChartSeries CpuLoad { get; } = new("CPU", Color.FromRgb(43, 208, 143));
    public ChartSeries CpuMaxCore { get; } = new("Макс. ядро", Color.FromRgb(255, 181, 71));
    public ChartSeries GpuLoad { get; } = new("GPU", Color.FromRgb(61, 116, 255));
    public ChartSeries Fps { get; } = new("FPS", Color.FromRgb(91, 155, 255));
    public ChartSeries FrameTimeMax { get; } = new("Макс. время кадра", Color.FromRgb(255, 138, 71));
    public ChartSeries FrameTimeAvg { get; } = new("Время кадра", Color.FromRgb(91, 155, 255));
    public ChartSeries CpuTemp { get; } = new("CPU °C", Color.FromRgb(255, 90, 95));
    public ChartSeries GpuTemp { get; } = new("GPU °C", Color.FromRgb(61, 116, 255));
    public ChartSeries RamPct { get; } = new("RAM %", Color.FromRgb(43, 208, 143));
    public ChartSeries VramPct { get; } = new("VRAM %", Color.FromRgb(167, 139, 250));

    public ChartSeries[] LoadChart => new[] { GpuLoad, CpuLoad, CpuMaxCore };
    public ChartSeries[] FpsChart => new[] { Fps };
    public ChartSeries[] FrameChart => new[] { FrameTimeMax, FrameTimeAvg };
    public ChartSeries[] TempChart => new[] { GpuTemp, CpuTemp };
    public ChartSeries[] MemoryChart => new[] { VramPct, RamPct };

    public ObservableCollection<CoreLoadItem> Cores { get; } = new();

    public LiveMonitor(ITelemetryService telemetry, ISettingsService settings, ISystemInfoProvider system)
    {
        _telemetry = telemetry;
        _settings = settings;
        telemetry.Tick += (_, e) => Application.Current?.Dispatcher.BeginInvoke(() => Update(e.Sample));
        settings.Changed += (_, _) => Application.Current?.Dispatcher.BeginInvoke(ApplyCapacity);
        ApplyCapacity();
        _ = LoadNamesAsync(system);
    }

    private async Task LoadNamesAsync(ISystemInfoProvider system)
    {
        try
        {
            var info = await system.GetAsync();
            CpuName = ShortCpuName(info.Cpu.Name);
            GpuName = info.PrimaryGpu?.Name ?? "Видеокарта не определена";
            var speed = info.Memory.Modules.Select(m => m.SpeedMhz).FirstOrDefault(s => s > 0);
            RamName = $"{info.Memory.TotalMb / 1024:0} ГБ" + (speed.HasValue ? $" · {speed} МГц" : "");
        }
        catch { /* names are cosmetic */ }
    }

    [ObservableProperty] private bool _smooth = true;

    /// <summary>"Intel(R) Core(TM) i7-3770K CPU @ 3.50GHz" → "Intel Core i7-3770K".</summary>
    internal static string ShortCpuName(string name)
    {
        var n = name.Replace("(R)", "").Replace("(TM)", "").Replace("(tm)", "");
        int at = n.IndexOf(" @", StringComparison.Ordinal);
        if (at > 0) n = n[..at];
        n = System.Text.RegularExpressions.Regex.Replace(n, @"\s+(CPU|Processor)\b", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        n = System.Text.RegularExpressions.Regex.Replace(n, @"\s+\d+-Core\b.*$", "");
        return System.Text.RegularExpressions.Regex.Replace(n, @"\s{2,}", " ").Trim();
    }

    private void ApplyCapacity()
    {
        var s = _settings.Current;
        Smooth = s.ChartSmoothing;
        int interval = Math.Clamp(s.SampleIntervalMs, 250, 5000);
        int cap = Math.Max(30, s.ChartWindowMinutes * 60_000 / interval);
        foreach (var series in new[] { CpuLoad, CpuMaxCore, GpuLoad, Fps, FrameTimeMax, FrameTimeAvg, CpuTemp, GpuTemp, RamPct, VramPct })
            series.Capacity = cap;
    }

    private void Update(TelemetrySample s)
    {
        Sample = s;
        CpuClockGhz = s.CpuClockMhz / 1000.0;
        RamUsedGb = s.RamUsedMb / 1024.0;
        RamTotalGb = s.RamTotalMb / 1024.0;
        VramUsedGb = s.VramUsedMb / 1024.0;
        VramTotalGb = s.VramTotalMb / 1024.0;
        DiskReadMb = s.DiskReadBps / 1048576.0;
        DiskWriteMb = s.DiskWriteBps / 1048576.0;
        HasFps = s.Fps.HasValue;
        FrameSourceStatus = _telemetry.FrameProvider.StatusMessage;

        CpuLoad.Add(s.CpuLoad);
        CpuMaxCore.Add(s.CpuMaxCoreLoad);
        GpuLoad.Add(s.GpuLoad);
        Fps.Add(s.Fps);
        FrameTimeMax.Add(s.FrameTimeMaxMs);
        FrameTimeAvg.Add(s.FrameTimeAvgMs);
        CpuTemp.Add(s.CpuTempC);
        GpuTemp.Add(s.GpuTempC);
        RamPct.Add(s.RamPercent);
        VramPct.Add(s.VramPercent);

        if (s.CoreLoads is { } cores)
        {
            while (Cores.Count < cores.Length) Cores.Add(new CoreLoadItem { Index = Cores.Count });
            while (Cores.Count > cores.Length) Cores.RemoveAt(Cores.Count - 1);
            for (int i = 0; i < cores.Length; i++) Cores[i].Load = cores[i];
        }
        ComputeHealth(s);
    }

    /// <summary>
    /// Current system state 0–100 from available sensors only (temperatures, memory, disk).
    /// Missing sensors are listed instead of being assumed fine.
    /// </summary>
    private void ComputeHealth(TelemetrySample s)
    {
        var t = _settings.Current.Thresholds;
        double score = 100;
        var notes = new List<string>();
        var missing = new List<string>();

        void Temp(string name, double? v, double warn)
        {
            if (v is not { } x) { missing.Add($"температура {name}"); return; }
            if (x >= warn) { score -= 35; notes.Add($"{name} {x:0} °C — перегрев"); }
            else if (x >= warn - 10) { score -= 10; notes.Add($"{name} {x:0} °C — горячо"); }
        }
        Temp("CPU", s.CpuTempC, t.CpuTempWarnC);
        Temp("GPU", s.GpuTempC, t.GpuTempWarnC);
        if (s.RamPercent is { } r) { if (r >= t.RamFullPercent) { score -= 20; notes.Add($"RAM занята на {r:0}%"); } else if (r >= 80) score -= 5; }
        if (s.VramPercent is { } v && v >= t.VramFullPercent) { score -= 10; notes.Add($"VRAM занята на {v:0}%"); }
        if (s.DiskActivePercent is >= 95) { score -= 5; notes.Add($"Диск занят на {s.DiskActivePercent:0}%"); }

        HealthScore = Math.Clamp(score, 0, 100);
        (HealthLabel, HealthBrush) = HealthScore >= 85 ? ("Хорошо", (Brush)Application.Current.Resources["GoodBrush"])
            : HealthScore >= 60 ? ("Внимание", (Brush)Application.Current.Resources["WarnBrush"])
            : ("Проблема", (Brush)Application.Current.Resources["BadBrush"]);
        var text = notes.Count > 0 ? string.Join("\n", notes) : "Температуры, память и диск в норме.";
        if (missing.Count > 0) text += "\nНе учитывается (нет данных): " + string.Join(", ", missing) + ".";
        HealthDetails = text;
    }
}

public sealed partial class CoreLoadItem : ObservableObject
{
    public int Index { get; init; }
    [ObservableProperty] private double _load;
}
