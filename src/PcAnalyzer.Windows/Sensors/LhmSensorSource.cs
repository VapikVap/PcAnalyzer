using LibreHardwareMonitor.Hardware;
using Microsoft.Extensions.Logging;
using PcAnalyzer.Core.Models;

namespace PcAnalyzer.Windows.Sensors;

/// <summary>
/// Hardware sensors via LibreHardwareMonitorLib (MPL-2.0): temperatures, clocks, power and fans.
/// GPU sensors use vendor APIs (NVAPI / ADL / Intel) and work without admin rights.
/// CPU temperature/power need administrator rights and the kernel access driver LHM supports;
/// when that is not available the values stay null and the UI says so.
/// </summary>
public sealed class LhmSensorSource : IDisposable
{
    private const int MaxFailures = 5;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    private readonly ILogger _log;
    private Computer? _pc;
    private int _failures;
    private DateTime _retryAfter = DateTime.MinValue;

    public string Status { get; private set; } = "Не инициализировано";
    public bool HasCpuTemp { get; private set; }
    public bool HasGpuSensors { get; private set; }
    public string? GpuName { get; private set; }

    public LhmSensorSource(ILogger log)
    {
        _log = log;
        Open();
    }

    private void Open()
    {
        try
        {
            Close();
            _pc = new Computer { IsCpuEnabled = true, IsGpuEnabled = true };
            _pc.Open();
            _failures = 0;
            Status = "Работает";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "LibreHardwareMonitor init failed");
            Status = "Ошибка инициализации: " + ex.Message;
            _pc = null;
            _retryAfter = DateTime.UtcNow + RetryDelay;
        }
    }

    private void Close()
    {
        try { _pc?.Close(); } catch (Exception ex) { _log.LogDebug(ex, "LHM close"); }
        _pc = null;
    }

    public void Fill(TelemetrySample s)
    {
        if (_pc == null)
        {
            if (DateTime.UtcNow >= _retryAfter) Open(); // automatic recovery
            if (_pc == null) return;
        }
        try
        {
            IHardware? gpu = null;
            foreach (var hw in _pc.Hardware)
            {
                hw.Update();
                foreach (var sub in hw.SubHardware) sub.Update();
                if (hw.HardwareType == HardwareType.Cpu) FillCpu(hw, s);
                else if (hw.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd)
                    gpu ??= hw;
            }
            gpu ??= _pc.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.GpuIntel);
            if (gpu != null) FillGpu(gpu, s);
            HasGpuSensors = gpu != null && s.GpuTempC.HasValue;
            GpuName = gpu?.Name;
            _failures = 0;
        }
        catch (Exception ex)
        {
            _failures++;
            _log.LogWarning(ex, "LHM update failed ({N})", _failures);
            if (_failures >= MaxFailures)
            {
                Status = "Сбой сенсоров, переподключение…";
                Close();
                _retryAfter = DateTime.UtcNow + RetryDelay;
            }
        }
    }

    private void FillCpu(IHardware hw, TelemetrySample s)
    {
        var sensors = hw.Sensors;
        float? Temp(params string[] names)
        {
            foreach (var n in names)
            {
                var v = sensors.FirstOrDefault(x => x.SensorType == SensorType.Temperature && x.Name.Equals(n, StringComparison.OrdinalIgnoreCase))?.Value;
                if (v is > 0 and < 150) return v;
            }
            return null;
        }
        var t = Temp("CPU Package", "Core (Tctl/Tdie)", "Core (Tctl)", "Core Max", "Core Average", "CPU Cores");
        if (t == null)
        {
            var any = sensors.Where(x => x.SensorType == SensorType.Temperature && x.Value is > 0 and < 150).Select(x => x.Value!.Value).ToList();
            if (any.Count > 0) t = any.Max();
        }
        if (t.HasValue) s.CpuTempC = Math.Round(t.Value, 1);
        HasCpuTemp = t.HasValue;

        var p = sensors.FirstOrDefault(x => x.SensorType == SensorType.Power && x.Name.Contains("Package", StringComparison.OrdinalIgnoreCase))?.Value;
        if (p is > 0) s.CpuPowerW = Math.Round(p.Value, 1);
    }

    private static void FillGpu(IHardware hw, TelemetrySample s)
    {
        float? Get(SensorType type, params string[] names)
        {
            foreach (var n in names)
            {
                var v = hw.Sensors.FirstOrDefault(x => x.SensorType == type && x.Name.Equals(n, StringComparison.OrdinalIgnoreCase))?.Value;
                if (v.HasValue && float.IsFinite(v.Value)) return v;
            }
            return null;
        }

        if (Get(SensorType.Temperature, "GPU Core") is { } tc and > 0) s.GpuTempC = Math.Round(tc, 1);
        if (Get(SensorType.Temperature, "GPU Hot Spot") is { } hs and > 0) s.GpuHotSpotC = Math.Round(hs, 1);
        if (Get(SensorType.Clock, "GPU Core") is { } cc and > 0) s.GpuCoreClockMhz = Math.Round(cc);
        if (Get(SensorType.Clock, "GPU Memory") is { } mc and > 0) s.GpuMemClockMhz = Math.Round(mc);
        var power = Get(SensorType.Power, "GPU Package", "GPU Power", "GPU Core", "GPU Board Power");
        if (power is > 0) s.GpuPowerW = Math.Round(power.Value, 1);
        var fan = hw.Sensors.Where(x => x.SensorType == SensorType.Fan && x.Value.HasValue).Select(x => x.Value!.Value).ToList();
        if (fan.Count > 0) s.GpuFanRpm = Math.Round(fan.Max());
        if (Get(SensorType.Load, "GPU Core") is { } load) s.GpuLoad = Math.Round(Math.Clamp(load, 0, 100), 1);
        if (Get(SensorType.SmallData, "GPU Memory Total") is { } tot and > 0) s.VramTotalMb = Math.Round(tot);
        if (s.VramUsedMb == null && Get(SensorType.SmallData, "GPU Memory Used") is { } used and > 0) s.VramUsedMb = Math.Round(used);
    }

    public void Dispose() => Close();
}
