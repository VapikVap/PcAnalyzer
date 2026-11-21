using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PcAnalyzer.Core.Models;

namespace PcAnalyzer.Windows.Processes;

/// <summary>Per-process CPU/RAM: the target (game) every tick, top consumers every few seconds.</summary>
public sealed class ProcessMonitor : IDisposable
{
    private readonly ILogger _log;
    private readonly int _cpuCount = Environment.ProcessorCount;
    private readonly Dictionary<int, TimeSpan> _lastCpu = new();
    private DateTime _lastScan = DateTime.MinValue;

    private Process? _target;
    private TimeSpan _targetLastCpu;
    private DateTime _targetLastTime;

    public ProcessMonitor(ILogger log) => _log = log;

    public void SetTarget(int? pid)
    {
        _target?.Dispose();
        _target = null;
        if (pid is not { } p) return;
        try
        {
            _target = Process.GetProcessById(p);
            _targetLastCpu = _target.TotalProcessorTime;
            _targetLastTime = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            // Protected (anti-cheat) or already exited — game-process metrics stay unavailable.
            _log.LogInformation("Target process {Pid} not accessible: {Msg}", p, ex.Message);
            _target = null;
        }
    }

    public void FillTarget(TelemetrySample s)
    {
        if (_target == null) return;
        try
        {
            _target.Refresh();
            if (_target.HasExited) { _target.Dispose(); _target = null; return; }
            var now = DateTime.UtcNow;
            var cpu = _target.TotalProcessorTime;
            double elapsed = (now - _targetLastTime).TotalMilliseconds;
            if (elapsed > 0)
                s.GameCpuPercent = Math.Round(Math.Clamp((cpu - _targetLastCpu).TotalMilliseconds / (elapsed * _cpuCount) * 100, 0, 100), 1);
            _targetLastCpu = cpu;
            _targetLastTime = now;
            s.GameRamMb = Math.Round(_target.WorkingSet64 / 1048576.0);
        }
        catch (Exception)
        {
            // access denied mid-session: keep null values
        }
    }

    /// <summary>Returns the top CPU and RAM consumers since the previous scan.</summary>
    public List<ProcessUsage>? ScanTop(int count = 6)
    {
        var now = DateTime.UtcNow;
        double elapsed = (now - _lastScan).TotalMilliseconds;
        var current = new Dictionary<int, TimeSpan>();
        var list = new List<ProcessUsage>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (p.Id == 0) continue;
                try
                {
                    var cpu = p.TotalProcessorTime;
                    current[p.Id] = cpu;
                    double pct = 0;
                    if (_lastCpu.TryGetValue(p.Id, out var prev) && elapsed > 0)
                        pct = (cpu - prev).TotalMilliseconds / (elapsed * _cpuCount) * 100;
                    list.Add(new ProcessUsage { Name = p.ProcessName, Pid = p.Id, CpuPercent = Math.Round(Math.Clamp(pct, 0, 100), 1), RamMb = Math.Round(p.WorkingSet64 / 1048576.0) });
                }
                catch
                {
                    // no access to system/protected processes
                }
            }
        }
        bool first = _lastScan == DateTime.MinValue;
        _lastCpu.Clear();
        foreach (var kv in current) _lastCpu[kv.Key] = kv.Value;
        _lastScan = now;
        if (first) return null;

        var top = list.OrderByDescending(x => x.CpuPercent).Take(count)
            .Concat(list.OrderByDescending(x => x.RamMb).Take(3))
            .DistinctBy(x => x.Pid)
            .ToList();
        return top;
    }

    public void Dispose() => _target?.Dispose();
}
