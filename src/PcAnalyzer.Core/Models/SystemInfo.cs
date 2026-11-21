namespace PcAnalyzer.Core.Models;

public sealed class SystemInfo
{
    public CpuInfo Cpu { get; set; } = new();
    public List<GpuInfo> Gpus { get; set; } = new();
    public MemoryInfo Memory { get; set; } = new();
    public string? Motherboard { get; set; }
    public string? BiosVersion { get; set; }
    public List<DiskInfo> Disks { get; set; } = new();
    public OsInfo Os { get; set; } = new();
    public List<DisplayInfo> Displays { get; set; } = new();
    public string? PowerPlanName { get; set; }
    public Guid? PowerPlanGuid { get; set; }
    public DateTime CollectedUtc { get; set; }

    public GpuInfo? PrimaryGpu => Gpus.FirstOrDefault(g => g.IsDiscrete) ?? Gpus.FirstOrDefault();
    public DisplayInfo? PrimaryDisplay => Displays.FirstOrDefault(d => d.IsPrimary) ?? Displays.FirstOrDefault();
}

public sealed class CpuInfo
{
    public string Name { get; set; } = "Неизвестно";
    public string? Manufacturer { get; set; }
    public int Cores { get; set; }
    public int Threads { get; set; }
    public int? BaseClockMhz { get; set; }
    public string? Socket { get; set; }
}

public sealed class GpuInfo
{
    public string Name { get; set; } = "Неизвестно";
    public string Vendor { get; set; } = "Unknown";
    public string? DriverVersion { get; set; }
    public DateTime? DriverDate { get; set; }
    public double? VramMb { get; set; }
    public bool IsDiscrete { get; set; }
}

public sealed class MemoryInfo
{
    public double TotalMb { get; set; }
    public List<MemoryModule> Modules { get; set; } = new();
}

public sealed class MemoryModule
{
    public string? Manufacturer { get; set; }
    public string? PartNumber { get; set; }
    public double CapacityMb { get; set; }
    public int? SpeedMhz { get; set; }
    public string? Slot { get; set; }
}

public sealed class DiskInfo
{
    public string Model { get; set; } = "";
    public double SizeGb { get; set; }
    public string MediaType { get; set; } = "Неизвестно";
    public string? BusType { get; set; }
}

public sealed class OsInfo
{
    public string Caption { get; set; } = "";
    public string Version { get; set; } = "";
    public string? Build { get; set; }
    public string? Architecture { get; set; }
}

public sealed class DisplayInfo
{
    public string Name { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public int RefreshHz { get; set; }
    public bool IsPrimary { get; set; }
}
