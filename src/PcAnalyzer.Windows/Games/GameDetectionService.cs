using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PcAnalyzer.Core.Abstractions;
using PcAnalyzer.Windows.Native;

namespace PcAnalyzer.Windows.Games;

/// <summary>
/// Detects running games from several independent signals, so browsers and regular apps don't create
/// false sessions:
///  • user list (always a game) / user exclusions (never);
///  • known game executables;
///  • executable located in a launcher library (Steam, Epic, GOG, Xbox…);
///  • foreground window covering the whole monitor + sustained 3D load from that process.
/// A candidate must be stable for several checks before <see cref="GameStarted"/> fires.
/// </summary>
public sealed class GameDetectionService : IGameDetector, IDisposable
{
    private const int StableChecks = 3;
    private readonly TelemetryService _telemetry;
    private readonly ISettingsService _settings;
    private readonly ILogger _log;
    private readonly Timer _timer;
    private readonly string _ownProcess = Process.GetCurrentProcess().ProcessName;

    private int _candidatePid;
    private int _candidateHits;
    private Process? _currentProcess;

    public event EventHandler<DetectedGame>? GameStarted;
    public event EventHandler<DetectedGame>? GameExited;
    public DetectedGame? Current { get; private set; }
    public bool Enabled { get; set; } = true;

    public GameDetectionService(TelemetryService telemetry, ISettingsService settings, ILogger<GameDetectionService> log)
    {
        _telemetry = telemetry;
        _settings = settings;
        _log = log;
        _timer = new Timer(_ => Check(), null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
    }

    private int _busy;
    private void Check()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            if (Current != null) { CheckExit(); return; }
            if (!Enabled) return;
            var game = FindCandidate();
            if (game == null) { _candidatePid = 0; _candidateHits = 0; return; }
            if (game.Pid != _candidatePid) { _candidatePid = game.Pid; _candidateHits = 1; return; }
            if (++_candidateHits < StableChecks) return;

            try { _currentProcess = Process.GetProcessById(game.Pid); } catch { return; }
            Current = game;
            _log.LogInformation("Game detected: {Name} ({Exe}, pid {Pid}) — {Reason}", game.Name, game.ExeName, game.Pid, game.Reason);
            GameStarted?.Invoke(this, game);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Game detection check failed");
        }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    private void CheckExit()
    {
        bool exited;
        try { exited = _currentProcess == null || _currentProcess.HasExited; }
        catch { exited = true; }
        if (!exited) return;
        var g = Current!;
        Current = null;
        _currentProcess?.Dispose();
        _currentProcess = null;
        _candidatePid = 0;
        _candidateHits = 0;
        _log.LogInformation("Game exited: {Name}", g.Name);
        GameExited?.Invoke(this, g);
    }

    private DetectedGame? FindCandidate()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero || hwnd == NativeMethods.GetShellWindow() || hwnd == NativeMethods.GetDesktopWindow()) return null;
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint upid);
        int pid = (int)upid;
        if (pid <= 4) return null;

        using var p = TryGet(pid);
        if (p == null) return null;
        string name = p.ProcessName;
        if (name.Equals(_ownProcess, StringComparison.OrdinalIgnoreCase)) return null;
        string exe = name + ".exe";
        var s = _settings.Current;
        if (s.ExcludedApps.Any(x => Same(x, exe))) return null;

        string? path = TryPath(p);
        string display = DisplayName(p, path, name);

        if (s.UserGames.Any(x => Same(x, exe)))
            return new DetectedGame(pid, display, exe, path, "Есть в вашем списке игр");
        if (GameCatalog.NonGames.Contains(name)) return null;
        if (GameCatalog.KnownGames.TryGetValue(name, out var known))
            return new DetectedGame(pid, known, exe, path, "Известная игра");

        double gpu = _telemetry.GpuByPid.TryGetValue(pid, out var g) ? g : 0;
        bool fullscreen = IsFullscreen(hwnd);
        string lower = path?.ToLowerInvariant() ?? "";
        bool inLibrary = GameCatalog.LibraryMarkers.Any(m => lower.Contains(m)) && !lower.Contains(@"\windows\");

        if (inLibrary && gpu >= 10)
            return new DetectedGame(pid, display, exe, path, "Запущена из библиотеки игрового лаунчера");
        if (fullscreen && gpu >= 25)
            return new DetectedGame(pid, display, exe, path, $"Полноэкранное приложение с 3D-нагрузкой ({gpu:0}% GPU)");
        return null;
    }

    private static bool Same(string configured, string exe) =>
        string.Equals(configured.Trim(), exe, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(configured.Trim() + ".exe", exe, StringComparison.OrdinalIgnoreCase);

    private static bool IsFullscreen(IntPtr hwnd)
    {
        if (!NativeMethods.GetWindowRect(hwnd, out var r)) return false;
        var mon = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var mi = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(mon, ref mi)) return false;
        var m = mi.rcMonitor;
        return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
    }

    private static Process? TryGet(int pid)
    {
        try { return Process.GetProcessById(pid); } catch { return null; }
    }

    private static string? TryPath(Process p)
    {
        try { return p.MainModule?.FileName; } catch { return null; }
    }

    private static string DisplayName(Process p, string? path, string fallback)
    {
        if (GameCatalog.KnownGames.TryGetValue(fallback, out var known)) return known;
        try
        {
            if (path != null)
            {
                var vi = FileVersionInfo.GetVersionInfo(path);
                var n = !string.IsNullOrWhiteSpace(vi.ProductName) ? vi.ProductName : vi.FileDescription;
                if (!string.IsNullOrWhiteSpace(n) && n.Length < 60 && !IsExeLike(n, fallback)) return n.Trim();
            }
        }
        catch { /* ignore */ }
        try { if (!string.IsNullOrWhiteSpace(p.MainWindowTitle) && !IsExeLike(p.MainWindowTitle, fallback)) return p.MainWindowTitle; } catch { }
        return Prettify(fallback);
    }

    private static bool IsExeLike(string text, string processName) =>
        text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || text.Equals(processName, StringComparison.OrdinalIgnoreCase);

    /// <summary>"ONCE_HUMAN" → "Once Human", "my-game" → "My Game".</summary>
    internal static string Prettify(string processName)
    {
        var words = processName.Replace('_', ' ').Replace('-', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !w.Equals("win64", StringComparison.OrdinalIgnoreCase) && !w.Equals("shipping", StringComparison.OrdinalIgnoreCase))
            .Select(w => w.Length > 1 && (w.All(char.IsUpper) || w.All(char.IsLower)) ? char.ToUpper(w[0]) + w[1..].ToLowerInvariant() : w);
        var result = string.Join(' ', words);
        return result.Length > 0 ? result : processName;
    }

    public IReadOnlyList<RunningApp> GetRunningApps()
    {
        var list = new List<RunningApp>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.MainWindowHandle == IntPtr.Zero || string.IsNullOrWhiteSpace(p.MainWindowTitle)) continue;
                    if (p.ProcessName.Equals(_ownProcess, StringComparison.OrdinalIgnoreCase)) continue;
                    var path = TryPath(p);
                    list.Add(new RunningApp(p.Id, DisplayName(p, path, p.ProcessName), p.ProcessName + ".exe", path, p.MainWindowTitle));
                }
                catch { /* process exited or no access */ }
            }
        }
        return list.OrderBy(a => GameCatalog.NonGames.Contains(Path.GetFileNameWithoutExtension(a.ExeName))).ThenBy(a => a.Name).ToList();
    }

    public void Dispose()
    {
        _timer.Dispose();
        _currentProcess?.Dispose();
    }
}
