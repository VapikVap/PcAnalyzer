using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using PcAnalyzer.Core.Models;

namespace PcAnalyzer.App.Infrastructure;

public enum TweakState { Ok, Recommended, Info }

/// <summary>One Windows setting that matters for games: its current state and what the app can do about it.</summary>
public sealed class TweakCheck
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public TweakState State { get; init; }
    public string Status { get; init; } = "";
    /// <summary>Button text when the app can change the setting itself.</summary>
    public string? FixLabel { get; init; }
    /// <summary>Button text when the app can only open the right Windows page.</summary>
    public string? OpenLabel { get; init; }
    public string? OpenTarget { get; init; }
    /// <summary>The app changed this setting earlier and remembers the original value.</summary>
    public bool CanUndo { get; init; }
}

/// <summary>
/// Reads and changes a small set of safe, per-user (or power-plan) Windows settings. Every change the app makes
/// is recorded with the original value in tweaks-backup.json so it can be reverted from the UI.
/// Nothing here needs administrator rights.
/// </summary>
public sealed class WindowsTweaks
{
    public const string PowerPlan = "power_plan";
    public const string GameMode = "game_mode";
    public const string BackgroundRecording = "background_recording";
    public const string GpuScheduling = "gpu_scheduling";
    public const string PageFile = "page_file";
    public const string DiskSpace = "disk_space";
    public const string GpuDriver = "gpu_driver";
    public const string Startup = "startup";

    private static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    private static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    private static readonly Guid PowerSaver = new("a1841308-3541-4fab-bc81-f71556f20b4a");
    private static readonly Guid Ultimate = new("e9a42b02-d5df-448d-aa00-03f14749eb61");

    private const string GameBarKey = @"Software\Microsoft\GameBar";
    private const string GameDvrKey = @"Software\Microsoft\Windows\CurrentVersion\GameDVR";

    private readonly ILogger _log;
    private readonly string _backupPath = Path.Combine(AppPaths.DataDir, "tweaks-backup.json");
    private Dictionary<string, string?> _backup;

    public WindowsTweaks(ILogger<WindowsTweaks> log)
    {
        _log = log;
        _backup = LoadBackup();
    }

    public bool IsPowerSaverActive => ActiveScheme() == PowerSaver;

    public IReadOnlyList<TweakCheck> Evaluate(SystemInfo? info)
    {
        var list = new List<TweakCheck>();
        void Add(Func<TweakCheck?> check, string id)
        {
            try { if (check() is { } c) list.Add(c); }
            catch (Exception ex) { _log.LogWarning(ex, "Tweak check {Id} failed", id); }
        }
        Add(CheckPowerPlan, PowerPlan);
        Add(CheckGameMode, GameMode);
        Add(CheckBackgroundRecording, BackgroundRecording);
        Add(CheckDiskSpace, DiskSpace);
        Add(CheckPageFile, PageFile);
        Add(() => CheckGpuDriver(info), GpuDriver);
        Add(CheckStartup, Startup);
        Add(CheckGpuScheduling, GpuScheduling);
        return list.OrderBy(c => c.State switch { TweakState.Recommended => 0, TweakState.Ok => 1, _ => 2 }).ToList();
    }

    // ---------------- Checks ----------------

    private TweakCheck CheckPowerPlan()
    {
        var active = ActiveScheme();
        string name = active == PowerSaver ? "Экономия энергии" : active == Balanced ? "Сбалансированный"
            : active == HighPerformance ? "Высокая производительность" : active == Ultimate ? "Максимальная производительность" : "Пользовательский";
        bool saver = active == PowerSaver;
        return new TweakCheck
        {
            Id = PowerPlan,
            Title = "План электропитания",
            Description = "«Экономия энергии» ограничивает частоту процессора и заметно снижает FPS. Для игр подходит «Сбалансированный» или «Высокая производительность».",
            State = saver ? TweakState.Recommended : TweakState.Ok,
            Status = "Сейчас: " + name,
            FixLabel = saver ? "Включить «Сбалансированный»" : null,
            CanUndo = _backup.ContainsKey(PowerPlan),
        };
    }

    private TweakCheck CheckGameMode()
    {
        // Missing value = enabled (Windows default since 1703).
        bool on = ReadDword(Registry.CurrentUser, GameBarKey, "AutoGameModeEnabled") != 0;
        return new TweakCheck
        {
            Id = GameMode,
            Title = "Игровой режим Windows",
            Description = "Во время игры Windows откладывает обновления и фоновые задачи и отдаёт игре приоритет.",
            State = on ? TweakState.Ok : TweakState.Recommended,
            Status = on ? "Включён" : "Выключен",
            FixLabel = on ? null : "Включить",
            CanUndo = _backup.ContainsKey(GameMode),
        };
    }

    private TweakCheck CheckBackgroundRecording()
    {
        // "Record what happened": Xbox Game Bar keeps encoding the last minutes of gameplay all the time.
        bool on = ReadDword(Registry.CurrentUser, GameDvrKey, "HistoricalCaptureEnabled") == 1;
        return new TweakCheck
        {
            Id = BackgroundRecording,
            Title = "Фоновая запись игр (Xbox Game Bar)",
            Description = "Функция «Записать то, что произошло» постоянно кодирует видео игры в фоне и отнимает несколько процентов производительности видеокарты.",
            State = on ? TweakState.Recommended : TweakState.Ok,
            Status = on ? "Включена" : "Выключена",
            FixLabel = on ? "Выключить" : null,
            CanUndo = _backup.ContainsKey(BackgroundRecording),
        };
    }

    private TweakCheck CheckDiskSpace()
    {
        var low = DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady && d.TotalSize > 0)
            .Where(d => (double)d.AvailableFreeSpace / d.TotalSize < 0.10)
            .Select(d => $"{d.Name.TrimEnd('\\')} — свободно {d.AvailableFreeSpace / 1073741824.0:0.#} из {d.TotalSize / 1073741824.0:0} ГБ")
            .ToList();
        return new TweakCheck
        {
            Id = DiskSpace,
            Title = "Свободное место на дисках",
            Description = "Когда на диске меньше 10% свободного места, SSD работает медленнее, а игры дольше подгружают данные.",
            State = low.Count > 0 ? TweakState.Recommended : TweakState.Ok,
            Status = low.Count > 0 ? string.Join("; ", low) : "Места достаточно на всех дисках",
            OpenLabel = low.Count > 0 ? "Освободить место" : null,
            OpenTarget = "ms-settings:storagesense",
        };
    }

    private TweakCheck CheckPageFile()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management");
        var files = key?.GetValue("PagingFiles") as string[];
        bool disabled = files == null || files.All(string.IsNullOrWhiteSpace);
        return new TweakCheck
        {
            Id = PageFile,
            Title = "Файл подкачки",
            Description = "Без файла подкачки игры могут вылетать при нехватке памяти, даже если её формально хватает.",
            State = disabled ? TweakState.Recommended : TweakState.Ok,
            Status = disabled ? "Отключён" : "Включён",
            OpenLabel = disabled ? "Открыть настройки" : null,
            OpenTarget = "SystemPropertiesPerformance.exe",
        };
    }

    private static TweakCheck? CheckGpuDriver(SystemInfo? info)
    {
        var gpu = info?.PrimaryGpu;
        if (gpu?.DriverDate is not { } date) return null;
        var age = DateTime.Now - date;
        bool old = age.TotalDays > 365;
        string? url = gpu.Vendor.ToUpperInvariant() switch
        {
            var v when v.Contains("NVIDIA") => "https://www.nvidia.com/Download/index.aspx",
            var v when v.Contains("AMD") || v.Contains("ATI") => "https://www.amd.com/en/support/download/drivers.html",
            var v when v.Contains("INTEL") => "https://www.intel.com/content/www/us/en/download-center/home.html",
            _ => null,
        };
        return new TweakCheck
        {
            Id = GpuDriver,
            Title = "Драйвер видеокарты",
            Description = "Новые драйверы часто исправляют фризы и повышают FPS в свежих играх.",
            State = old ? TweakState.Recommended : TweakState.Ok,
            Status = $"{gpu.Name}: драйвер от {date:dd.MM.yyyy}" + (old ? $" (старше {(int)(age.TotalDays / 30)} мес.)" : ""),
            OpenLabel = old && url != null ? "Скачать драйвер" : null,
            OpenTarget = url,
        };
    }

    private static TweakCheck CheckStartup()
    {
        int count = CountStartup(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run")
                  + CountStartup(Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Run", @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run")
                  + CountStartup(Registry.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32");
        bool many = count >= 8;
        return new TweakCheck
        {
            Id = Startup,
            Title = "Автозагрузка",
            Description = "Программы из автозагрузки работают в фоне всё время, в том числе во время игры.",
            State = many ? TweakState.Recommended : TweakState.Ok,
            Status = $"Программ в автозагрузке: {count}",
            OpenLabel = "Открыть автозагрузку",
            OpenTarget = "ms-settings:startupapps",
        };
    }

    private static TweakCheck CheckGpuScheduling()
    {
        int mode = ReadDword(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", -1);
        string status = mode switch { 2 => "Включено", 1 => "Выключено", _ => "По умолчанию" };
        return new TweakCheck
        {
            Id = GpuScheduling,
            Title = "Аппаратное планирование GPU",
            Description = "На современных видеокартах (NVIDIA 10xx+, AMD RX 5000+) может немного снизить задержку; нужно для генерации кадров DLSS 3. Меняется в параметрах графики, требуется перезагрузка.",
            State = TweakState.Info,
            Status = status,
            OpenLabel = "Параметры графики",
            OpenTarget = "ms-settings:display-advancedgraphics",
        };
    }

    // ---------------- Actions ----------------

    public void Apply(string id)
    {
        switch (id)
        {
            case PowerPlan:
                Remember(PowerPlan, ActiveScheme()?.ToString());
                SetScheme(Balanced);
                break;
            case GameMode:
                Remember(GameMode, ReadRaw(Registry.CurrentUser, GameBarKey, "AutoGameModeEnabled"));
                WriteDword(Registry.CurrentUser, GameBarKey, "AutoGameModeEnabled", 1);
                break;
            case BackgroundRecording:
                Remember(BackgroundRecording, ReadRaw(Registry.CurrentUser, GameDvrKey, "HistoricalCaptureEnabled"));
                WriteDword(Registry.CurrentUser, GameDvrKey, "HistoricalCaptureEnabled", 0);
                break;
            default:
                throw new InvalidOperationException("Это изменение нельзя выполнить автоматически: " + id);
        }
        _log.LogInformation("Tweak applied: {Id}", id);
    }

    public void Undo(string id)
    {
        if (!_backup.TryGetValue(id, out var original)) return;
        switch (id)
        {
            case PowerPlan:
                if (Guid.TryParse(original, out var g)) SetScheme(g);
                break;
            case GameMode:
                RestoreDword(Registry.CurrentUser, GameBarKey, "AutoGameModeEnabled", original);
                break;
            case BackgroundRecording:
                RestoreDword(Registry.CurrentUser, GameDvrKey, "HistoricalCaptureEnabled", original);
                break;
        }
        _backup.Remove(id);
        SaveBackup();
        _log.LogInformation("Tweak reverted: {Id}", id);
    }

    public static void Open(string target)
    {
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
    }

    // ---------------- Helpers ----------------

    private void Remember(string id, string? original)
    {
        // Keep the very first original value if the tweak is applied several times.
        if (_backup.ContainsKey(id)) return;
        _backup[id] = original;
        SaveBackup();
    }

    private Dictionary<string, string?> LoadBackup()
    {
        try
        {
            if (File.Exists(_backupPath))
                return JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllText(_backupPath)) ?? new();
        }
        catch (Exception ex) { _log.LogWarning(ex, "Tweak backup unreadable"); }
        return new();
    }

    private void SaveBackup()
    {
        Directory.CreateDirectory(AppPaths.DataDir);
        File.WriteAllText(_backupPath, JsonSerializer.Serialize(_backup));
    }

    private static int ReadDword(RegistryKey root, string path, string name, int fallback = -1)
    {
        using var k = root.OpenSubKey(path);
        return k?.GetValue(name) is int v ? v : fallback;
    }

    /// <summary>Original value as text; null = the value did not exist.</summary>
    private static string? ReadRaw(RegistryKey root, string path, string name)
    {
        using var k = root.OpenSubKey(path);
        return k?.GetValue(name) is int v ? v.ToString() : null;
    }

    private static void WriteDword(RegistryKey root, string path, string name, int value)
    {
        using var k = root.CreateSubKey(path, writable: true);
        k.SetValue(name, value, RegistryValueKind.DWord);
    }

    private static void RestoreDword(RegistryKey root, string path, string name, string? original)
    {
        using var k = root.CreateSubKey(path, writable: true);
        if (original != null && int.TryParse(original, out var v)) k.SetValue(name, v, RegistryValueKind.DWord);
        else k.DeleteValue(name, throwOnMissingValue: false);
    }

    private static int CountStartup(RegistryKey root, string runPath, string approvedPath)
    {
        using var run = root.OpenSubKey(runPath);
        if (run == null) return 0;
        using var approved = root.OpenSubKey(approvedPath);
        int n = 0;
        foreach (var name in run.GetValueNames())
        {
            if (string.IsNullOrEmpty(name)) continue;
            // StartupApproved: first byte 2/6 = enabled, 3/7 = disabled in Task Manager.
            if (approved?.GetValue(name) is byte[] { Length: > 0 } b && (b[0] & 1) == 1) continue;
            n++;
        }
        return n;
    }

    private static Guid? ActiveScheme()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out var ptr) != 0) return null;
        try { return Marshal.PtrToStructure<Guid>(ptr); }
        finally { LocalFree(ptr); }
    }

    private static void SetScheme(Guid scheme)
    {
        uint rc = PowerSetActiveScheme(IntPtr.Zero, ref scheme);
        if (rc != 0) throw new InvalidOperationException($"Не удалось переключить план электропитания (код {rc}).");
    }

    [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);
    [DllImport("powrprof.dll")] private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr hMem);
}
