using System.Runtime.InteropServices;
using static PcAnalyzer.Windows.Native.NativeMethods;

namespace PcAnalyzer.Windows.Pdh;

/// <summary>
/// Thin wrapper around a PDH query. Counter paths are added in English (locale independent,
/// important for non-English Windows). Wildcard counters are read as arrays.
/// </summary>
public sealed class PdhQuery : IDisposable
{
    private IntPtr _query;
    private readonly Dictionary<string, IntPtr> _counters = new();
    private IntPtr _buffer = IntPtr.Zero;
    private uint _bufferSize;

    public PdhQuery()
    {
        uint r = PdhOpenQuery(null, IntPtr.Zero, out _query);
        if (r != 0) throw new InvalidOperationException($"PdhOpenQuery failed: 0x{r:X8}");
    }

    public IReadOnlyCollection<string> Keys => _counters.Keys;

    public bool TryAdd(string key, string englishPath)
    {
        uint r = PdhAddEnglishCounter(_query, englishPath, IntPtr.Zero, out var counter);
        if (r != 0) return false;
        _counters[key] = counter;
        return true;
    }

    public bool Has(string key) => _counters.ContainsKey(key);

    public bool Collect() => PdhCollectQueryData(_query) == 0;

    public double? Get(string key)
    {
        if (!_counters.TryGetValue(key, out var c)) return null;
        uint r = PdhGetFormattedCounterValue(c, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, out _, out var v);
        if (r != 0 || (v.CStatus != PDH_CSTATUS_VALID_DATA && v.CStatus != PDH_CSTATUS_NEW_DATA)) return null;
        return double.IsFinite(v.doubleValue) ? v.doubleValue : null;
    }

    /// <summary>Reads a wildcard counter. Returns null when unavailable (e.g. first collection).</summary>
    public List<(string Instance, double Value)>? GetArray(string key)
    {
        if (!_counters.TryGetValue(key, out var c)) return null;
        uint size = 0;
        uint r = PdhGetFormattedCounterArray(c, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out uint count, IntPtr.Zero);
        if (r != PDH_MORE_DATA) return r == 0 ? new() : null;
        if (size > _bufferSize)
        {
            if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
            _bufferSize = Math.Max(size, _bufferSize * 2);
            _buffer = Marshal.AllocHGlobal((int)_bufferSize);
        }
        size = _bufferSize;
        r = PdhGetFormattedCounterArray(c, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out count, _buffer);
        if (r != 0) return null;

        // PDH_FMT_COUNTERVALUE_ITEM_W = { LPWSTR szName; PDH_FMT_COUNTERVALUE FmtValue; }
        // The inner struct contains a double, so it is 8-aligned: item = 24 bytes on x86 and x64.
        const int itemSize = 24, statusOffset = 8, valueOffset = 16;
        var list = new List<(string, double)>((int)count);
        for (int i = 0; i < count; i++)
        {
            IntPtr item = _buffer + i * itemSize;
            string name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) ?? "";
            uint status = (uint)Marshal.ReadInt32(item + statusOffset);
            double value = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item + valueOffset));
            if ((status == PDH_CSTATUS_VALID_DATA || status == PDH_CSTATUS_NEW_DATA) && double.IsFinite(value))
                list.Add((name, value));
        }
        return list;
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero) { PdhCloseQuery(_query); _query = IntPtr.Zero; }
        if (_buffer != IntPtr.Zero) { Marshal.FreeHGlobal(_buffer); _buffer = IntPtr.Zero; }
    }
}
