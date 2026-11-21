using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using PcAnalyzer.Core.Abstractions;
using PcAnalyzer.Core.Models;
using PcAnalyzer.Windows.Native;

namespace PcAnalyzer.Windows.Hardware;

/// <summary>Hardware/OS inventory via WMI, registry and user32. Cached; the power plan is re-read every call.</summary>
public sealed class SystemInfoProvider : ISystemInfoProvider
{
    private readonly ILogger _log;
    private SystemInfo? _cache;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public SystemInfoProvider(ILogger<SystemInfoProvider> log) => _log = log;

    public async Task<SystemInfo> GetAsync(bool refresh = false)
    {
        await _lock.WaitAsync();
        try
        {
            if (_cache == null || refresh) _cache = await Task.Run(Collect);
            ReadPowerPlan(_cache);
            _cache.Displays = Displays();
            return _cache;
        }
        finally { _lock.Release(); }
    }

    private SystemInfo Collect()
    {
        var info = new SystemInfo { CollectedUtc = DateTime.UtcNow };
        Try("CPU", () =>
        {
            foreach (var o in Query("SELECT Name, Manufacturer, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, SocketDesignation FROM Win32_Processor"))
            {
                info.Cpu = new CpuInfo
                {
                    Name = Str(o, "Name")?.Trim() ?? "Неизвестно",
                    Manufacturer = Str(o, "Manufacturer"),
                    Cores = Int(o, "NumberOfCores") ?? 0,
                    Threads = Int(o, "NumberOfLogicalProcessors") ?? Environment.ProcessorCount,
                    BaseClockMhz = Int(o, "MaxClockSpeed"),
                    Socket = Str(o, "SocketDesignation"),
                };
                break;
            }
        });
        Try("GPU", () => info.Gpus = Gpus());
        Try("RAM", () =>
        {
            info.Memory.TotalMb = Math.Round(NativeMethods.TotalPhysicalMemory() / 1048576.0);
            foreach (var o in Query("SELECT Manufacturer, PartNumber, Capacity, Speed, ConfiguredClockSpeed, DeviceLocator FROM Win32_PhysicalMemory"))
            {
                info.Memory.Modules.Add(new MemoryModule
                {
                    Manufacturer = Str(o, "Manufacturer")?.Trim(),
                    PartNumber = Str(o, "PartNumber")?.Trim(),
                    CapacityMb = (Long(o, "Capacity") ?? 0) / 1048576.0,
                    SpeedMhz = Int(o, "ConfiguredClockSpeed") is > 0 and var c ? c : Int(o, "Speed"),
                    Slot = Str(o, "DeviceLocator"),
                });
            }
        });
        Try("Board", () =>
        {
            foreach (var o in Query("SELECT Manufacturer, Product FROM Win32_BaseBoard"))
            { info.Motherboard = $"{Str(o, "Manufacturer")} {Str(o, "Product")}".Trim(); break; }
            foreach (var o in Query("SELECT SMBIOSBIOSVersion FROM Win32_BIOS"))
            { info.BiosVersion = Str(o, "SMBIOSBIOSVersion"); break; }
        });
        Try("Disks", () => info.Disks = Disks());
        Try("OS", () =>
        {
            foreach (var o in Query("SELECT Caption, Version, BuildNumber, OSArchitecture FROM Win32_OperatingSystem"))
            {
                var display = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion", null) as string;
                var ubr = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "UBR", null);
                info.Os = new OsInfo
                {
                    Caption = Str(o, "Caption")?.Replace("Microsoft ", "").Replace("Майкрософт ", "") ?? "Windows",
                    Version = display ?? Str(o, "Version") ?? "",
                    Build = Str(o, "BuildNumber") + (ubr != null ? "." + ubr : ""),
                    Architecture = Str(o, "OSArchitecture"),
                };
                break;
            }
        });
        return info;
    }

    private List<GpuInfo> Gpus()
    {
        var registryVram = RegistryVram();
        var list = new List<GpuInfo>();
        foreach (var o in Query("SELECT Name, AdapterCompatibility, DriverVersion, DriverDate, AdapterRAM FROM Win32_VideoController"))
        {
            var name = Str(o, "Name") ?? "Неизвестно";
            if (name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) || name.Contains("Remote", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Virtual", StringComparison.OrdinalIgnoreCase) || name.Contains("Parsec", StringComparison.OrdinalIgnoreCase)) continue;
            double? vram = registryVram.TryGetValue(name, out var rv) ? rv : (Long(o, "AdapterRAM") is > 0 and var ar ? ar / 1048576.0 : null);
            string vendor = name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ? "NVIDIA"
                : name.Contains("AMD", StringComparison.OrdinalIgnoreCase) || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ? "AMD"
                : name.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? "Intel" : Str(o, "AdapterCompatibility") ?? "Unknown";
            bool discrete = vendor == "NVIDIA" || name.Contains("Arc", StringComparison.OrdinalIgnoreCase) ||
                            (vendor == "AMD" && (vram ?? 0) >= 2048 && !name.Equals("AMD Radeon(TM) Graphics", StringComparison.OrdinalIgnoreCase));
            DateTime? date = null;
            if (Str(o, "DriverDate") is { Length: >= 8 } dd && DateTime.TryParseExact(dd[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) date = d;
            list.Add(new GpuInfo { Name = name, Vendor = vendor, DriverVersion = FriendlyDriver(vendor, Str(o, "DriverVersion")), DriverDate = date, VramMb = vram.HasValue ? Math.Round(vram.Value) : null, IsDiscrete = discrete });
        }
        return list.OrderByDescending(g => g.IsDiscrete).ToList();
    }

    /// <summary>NVIDIA driver "32.0.15.6094" → "560.94".</summary>
    private static string? FriendlyDriver(string vendor, string? v)
    {
        if (v == null || vendor != "NVIDIA") return v;
        var digits = v.Replace(".", "");
        if (digits.Length < 5) return v;
        var last5 = digits[^5..];
        return $"{last5[..3]}.{last5[3..]} ({v})";
    }

    private Dictionary<string, double> RegistryVram()
    {
        var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls == null) return map;
            foreach (var sub in cls.GetSubKeyNames().Where(n => n.All(char.IsDigit)))
            {
                using var k = cls.OpenSubKey(sub);
                if (k?.GetValue("DriverDesc") is not string desc) continue;
                var v = k.GetValue("HardwareInformation.qwMemorySize");
                double? bytes = v switch { long l => l, byte[] b when b.Length >= 8 => BitConverter.ToInt64(b, 0), byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b, 0), int i => (uint)i, _ => null };
                if (bytes is > 0) map[desc] = bytes.Value / 1048576.0;
            }
        }
        catch (Exception ex) { _log.LogDebug(ex, "Registry VRAM read failed"); }
        return map;
    }

    private List<DiskInfo> Disks()
    {
        var list = new List<DiskInfo>();
        try
        {
            using var s = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT FriendlyName, MediaType, BusType, Size FROM MSFT_PhysicalDisk");
            foreach (ManagementObject o in s.Get())
            {
                int media = Convert.ToInt32(o["MediaType"] ?? 0);
                int bus = Convert.ToInt32(o["BusType"] ?? 0);
                list.Add(new DiskInfo
                {
                    Model = Str(o, "FriendlyName") ?? "Диск",
                    SizeGb = Math.Round(Convert.ToDouble(o["Size"] ?? 0) / 1e9),
                    MediaType = media switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => bus == 17 ? "SSD" : "Неизвестно" },
                    BusType = bus switch { 17 => "NVMe", 11 => "SATA", 7 => "USB", 10 => "SAS", 8 => "RAID", _ => null },
                });
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "MSFT_PhysicalDisk unavailable, falling back to Win32_DiskDrive");
            foreach (var o in Query("SELECT Model, Size, InterfaceType FROM Win32_DiskDrive"))
                list.Add(new DiskInfo { Model = Str(o, "Model") ?? "Диск", SizeGb = Math.Round((Long(o, "Size") ?? 0) / 1e9), BusType = Str(o, "InterfaceType") });
        }
        return list;
    }

    private static List<DisplayInfo> Displays()
    {
        var list = new List<DisplayInfo>();
        var dd = new NativeMethods.DISPLAY_DEVICE { cb = Marshal.SizeOf<NativeMethods.DISPLAY_DEVICE>() };
        for (uint i = 0; NativeMethods.EnumDisplayDevices(null, i, ref dd, 0); i++)
        {
            if ((dd.StateFlags & NativeMethods.DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0)
            {
                var dm = new NativeMethods.DEVMODE { dmSize = (short)Marshal.SizeOf<NativeMethods.DEVMODE>() };
                if (NativeMethods.EnumDisplaySettings(dd.DeviceName, NativeMethods.ENUM_CURRENT_SETTINGS, ref dm))
                {
                    var mon = new NativeMethods.DISPLAY_DEVICE { cb = Marshal.SizeOf<NativeMethods.DISPLAY_DEVICE>() };
                    string name = NativeMethods.EnumDisplayDevices(dd.DeviceName, 0, ref mon, 0) ? mon.DeviceString : dd.DeviceString;
                    list.Add(new DisplayInfo
                    {
                        Name = name,
                        Width = dm.dmPelsWidth,
                        Height = dm.dmPelsHeight,
                        RefreshHz = dm.dmDisplayFrequency,
                        IsPrimary = (dd.StateFlags & NativeMethods.DISPLAY_DEVICE_PRIMARY_DEVICE) != 0,
                    });
                }
            }
            dd = new NativeMethods.DISPLAY_DEVICE { cb = Marshal.SizeOf<NativeMethods.DISPLAY_DEVICE>() };
        }
        return list;
    }

    private void ReadPowerPlan(SystemInfo info)
    {
        try
        {
            if (NativeMethods.PowerGetActiveScheme(IntPtr.Zero, out var ptr) != 0) return;
            var guid = Marshal.PtrToStructure<Guid>(ptr);
            NativeMethods.LocalFree(ptr);
            info.PowerPlanGuid = guid;
            uint size = 0;
            NativeMethods.PowerReadFriendlyName(IntPtr.Zero, ref guid, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size);
            if (size == 0) return;
            var buf = Marshal.AllocHGlobal((int)size);
            try
            {
                if (NativeMethods.PowerReadFriendlyName(IntPtr.Zero, ref guid, IntPtr.Zero, IntPtr.Zero, buf, ref size) == 0)
                    info.PowerPlanName = Marshal.PtrToStringUni(buf);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch (Exception ex) { _log.LogDebug(ex, "Power plan read failed"); }
    }

    private void Try(string what, Action a)
    {
        try { a(); } catch (Exception ex) { _log.LogWarning(ex, "System info: {What} failed", what); }
    }

    private static IEnumerable<ManagementBaseObject> Query(string wql)
    {
        using var s = new ManagementObjectSearcher(wql);
        foreach (var o in s.Get()) yield return o;
    }

    private static string? Str(ManagementBaseObject o, string p) => o[p]?.ToString();
    private static int? Int(ManagementBaseObject o, string p) => o[p] is { } v ? Convert.ToInt32(v, CultureInfo.InvariantCulture) : null;
    private static long? Long(ManagementBaseObject o, string p) => o[p] is { } v ? Convert.ToInt64(v, CultureInfo.InvariantCulture) : null;
}
