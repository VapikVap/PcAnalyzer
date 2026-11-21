using System.Globalization;
using Microsoft.Extensions.Logging;
using PcAnalyzer.Core.Models;
using PcAnalyzer.Windows.Native;
using PcAnalyzer.Windows.Pdh;

namespace PcAnalyzer.Windows.Sensors;

/// <summary>
/// Vendor-independent metrics from Windows performance counters: CPU per core, effective CPU clock,
/// GPU engines (incl. per-process), VRAM, memory, paging, disks and network. No driver required.
/// </summary>
public sealed class PdhSystemSource : IDisposable
{
    private readonly ILogger _log;
    private PdhQuery? _q;
    private readonly double _ramTotalMb;

    public bool CpuAvailable { get; private set; }
    public bool GpuAvailable { get; private set; }
    public string Status { get; private set; } = "Не инициализировано";

    /// <summary>Per-process GPU utilization (max engine, %) from the last collection.</summary>
    public IReadOnlyDictionary<int, double> GpuByPid { get; private set; } = new Dictionary<int, double>();
    public IReadOnlyDictionary<int, double> VramByPid { get; private set; } = new Dictionary<int, double>();
    public string? PrimaryLuid { get; private set; }

    public PdhSystemSource(ILogger log)
    {
        _log = log;
        _ramTotalMb = NativeMethods.TotalPhysicalMemory() / 1048576.0;
        Open();
    }

    private void Open()
    {
        try
        {
            _q?.Dispose();
            _q = new PdhQuery();
            var missing = new List<string>();
            void Add(string key, string path) { if (!_q.TryAdd(key, path)) missing.Add(path); }

            Add("cpu_cores", @"\Processor Information(*)\% Processor Time");
            Add("cpu_total", @"\Processor Information(_Total)\% Processor Time");
            Add("cpu_perf", @"\Processor Information(_Total)\% Processor Performance");
            Add("cpu_freq", @"\Processor Information(_Total)\Processor Frequency");
            Add("mem_avail", @"\Memory\Available MBytes");
            Add("mem_commit", @"\Memory\% Committed Bytes In Use");
            Add("mem_pagein", @"\Memory\Pages Input/sec");
            Add("pagefile", @"\Paging File(_Total)\% Usage");
            Add("disk_idle", @"\PhysicalDisk(*)\% Idle Time");
            Add("disk_read", @"\PhysicalDisk(_Total)\Disk Read Bytes/sec");
            Add("disk_write", @"\PhysicalDisk(_Total)\Disk Write Bytes/sec");
            Add("disk_queue", @"\PhysicalDisk(_Total)\Avg. Disk Queue Length");
            Add("net_rx", @"\Network Interface(*)\Bytes Received/sec");
            Add("net_tx", @"\Network Interface(*)\Bytes Sent/sec");
            Add("gpu_engine", @"\GPU Engine(*)\Utilization Percentage");
            Add("gpu_adapter_mem", @"\GPU Adapter Memory(*)\Dedicated Usage");
            Add("gpu_proc_mem", @"\GPU Process Memory(*)\Dedicated Usage");

            CpuAvailable = _q.Has("cpu_total");
            GpuAvailable = _q.Has("gpu_engine");
            _q.Collect(); // prime rate counters
            Status = missing.Count == 0 ? "Работает" : $"Недоступно счётчиков: {missing.Count}";
            if (missing.Count > 0) _log.LogWarning("PDH counters unavailable: {Paths}", string.Join("; ", missing));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "PDH init failed");
            Status = "Ошибка: " + ex.Message;
            CpuAvailable = GpuAvailable = false;
        }
    }

    public void Reopen() => Open();

    /// <summary>Collects and writes all PDH-based metrics into <paramref name="s"/>.</summary>
    public bool Fill(TelemetrySample s, int? targetPid)
    {
        if (_q == null || !_q.Collect()) return false;

        // ---- CPU ----
        s.CpuLoad = Clamp(_q.Get("cpu_total"));
        var cores = _q.GetArray("cpu_cores");
        if (cores != null)
        {
            var perCore = cores
                .Where(c => !c.Instance.Contains("_Total", StringComparison.OrdinalIgnoreCase))
                .Select(c => (key: CoreKey(c.Instance), v: Math.Clamp(c.Value, 0, 100)))
                .OrderBy(c => c.key)
                .Select(c => Math.Round(c.v, 1))
                .ToArray();
            if (perCore.Length > 0)
            {
                s.CoreLoads = perCore;
                s.CpuMaxCoreLoad = perCore.Max();
            }
        }
        var perf = _q.Get("cpu_perf");
        var freq = _q.Get("cpu_freq");
        s.CpuPerfPercent = perf;
        if (perf is > 0 && freq is > 0) s.CpuClockMhz = Math.Round(freq.Value * perf.Value / 100);

        // ---- Memory ----
        if (_q.Get("mem_avail") is { } avail && _ramTotalMb > 0)
        {
            s.RamTotalMb = Math.Round(_ramTotalMb);
            s.RamUsedMb = Math.Round(_ramTotalMb - avail);
        }
        s.CommitPercent = _q.Get("mem_commit");
        s.PageInputsPerSec = _q.Get("mem_pagein");
        s.PagefilePercent = _q.Get("pagefile");

        // ---- Disk ----
        var idle = _q.GetArray("disk_idle");
        if (idle != null)
        {
            var disks = idle.Where(d => d.Instance != "_Total").ToList();
            if (disks.Count > 0) s.DiskActivePercent = Math.Round(Math.Clamp(100 - disks.Min(d => d.Value), 0, 100), 1);
        }
        s.DiskReadBps = _q.Get("disk_read");
        s.DiskWriteBps = _q.Get("disk_write");
        s.DiskQueue = _q.Get("disk_queue");

        // ---- Network ----
        s.NetRxBps = SumOrNull(_q.GetArray("net_rx"));
        s.NetTxBps = SumOrNull(_q.GetArray("net_tx"));

        // ---- GPU (WDDM engines) ----
        FillGpu(s, targetPid);
        return true;
    }

    private void FillGpu(TelemetrySample s, int? targetPid)
    {
        var mem = _q!.GetArray("gpu_adapter_mem");
        if (mem is { Count: > 0 })
        {
            // Primary adapter = the one using the most dedicated memory (the discrete GPU on hybrid systems).
            var top = mem.OrderByDescending(m => m.Value).First();
            PrimaryLuid = LuidOf(top.Instance);
            s.VramUsedMb = Math.Round(top.Value / 1048576.0);
        }

        var engines = _q.GetArray("gpu_engine");
        if (engines == null || PrimaryLuid == null) return;

        var perEngine = new Dictionary<string, (string type, double sum)>();
        var perPid = new Dictionary<int, Dictionary<string, double>>();
        foreach (var (inst, value) in engines)
        {
            if (LuidOf(inst) != PrimaryLuid) continue;
            var pid = PidOf(inst);
            var eng = Between(inst, "_eng_", "_engtype_");
            var type = After(inst, "_engtype_");
            if (eng == null || type == null) continue;
            perEngine.TryGetValue(eng, out var cur);
            perEngine[eng] = (type, cur.sum + value);
            if (pid is { } p)
            {
                if (!perPid.TryGetValue(p, out var d)) perPid[p] = d = new();
                d.TryGetValue(eng, out var pv);
                d[eng] = pv + value;
            }
        }
        if (perEngine.Count == 0) return;

        double ByType(string t) => perEngine.Values.Where(e => e.type.Equals(t, StringComparison.OrdinalIgnoreCase)).Select(e => e.sum).DefaultIfEmpty(0).Max();
        s.Gpu3DLoad = Math.Round(Math.Min(100, ByType("3D")), 1);
        s.GpuCopyLoad = Math.Round(Math.Min(100, ByType("Copy")), 1);
        s.GpuComputeLoad = Math.Round(Math.Min(100, Math.Max(ByType("Compute_0"), ByType("Compute_1"))), 1);
        s.GpuVideoLoad = Math.Round(Math.Min(100, Math.Max(ByType("VideoDecode"), ByType("VideoEncode"))), 1);
        s.GpuLoad = Math.Round(Math.Min(100, perEngine.Values.Max(e => e.sum)), 1);

        GpuByPid = perPid.ToDictionary(kv => kv.Key, kv => Math.Min(100, kv.Value.Values.Max()));
        if (targetPid is { } tp && GpuByPid.TryGetValue(tp, out var g)) s.GameGpuPercent = Math.Round(g, 1);

        var procMem = _q.GetArray("gpu_proc_mem");
        if (procMem != null)
        {
            var vram = new Dictionary<int, double>();
            foreach (var (inst, value) in procMem)
                if (PidOf(inst) is { } p && LuidOf(inst) == PrimaryLuid)
                    vram[p] = vram.GetValueOrDefault(p) + value / 1048576.0;
            VramByPid = vram;
            if (targetPid is { } tp2 && vram.TryGetValue(tp2, out var v)) s.GameVramMb = Math.Round(v);
        }
    }

    // "pid_1234_luid_0x00000000_0x0000D1B5_phys_0_eng_0_engtype_3D"
    private static int? PidOf(string inst) =>
        inst.StartsWith("pid_", StringComparison.Ordinal) && Between(inst, "pid_", "_luid") is { } p && int.TryParse(p, out var v) ? v : null;

    private static string? LuidOf(string inst)
    {
        int i = inst.IndexOf("luid_", StringComparison.Ordinal);
        if (i < 0) return null;
        int j = inst.IndexOf("_phys", i, StringComparison.Ordinal);
        return j < 0 ? inst[i..] : inst[i..j];
    }

    private static string? Between(string s, string a, string b)
    {
        int i = s.IndexOf(a, StringComparison.Ordinal);
        if (i < 0) return null;
        i += a.Length;
        int j = s.IndexOf(b, i, StringComparison.Ordinal);
        return j < 0 ? null : s[i..j];
    }

    private static string? After(string s, string a)
    {
        int i = s.IndexOf(a, StringComparison.Ordinal);
        return i < 0 ? null : s[(i + a.Length)..];
    }

    // "0,3" → group 0, core 3 (sortable)
    private static long CoreKey(string inst)
    {
        var parts = inst.Split(',');
        if (parts.Length == 2 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var g) &&
            int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var c))
            return (long)g * 1000 + c;
        return long.MaxValue;
    }

    private static double? Clamp(double? v) => v.HasValue ? Math.Round(Math.Clamp(v.Value, 0, 100), 1) : null;
    private static double? SumOrNull(List<(string Instance, double Value)>? a) => a is { Count: > 0 } ? a.Sum(x => x.Value) : null;

    public void Dispose() => _q?.Dispose();
}
