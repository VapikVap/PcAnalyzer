using System.Diagnostics;

namespace PcAnalyzer.App.Infrastructure;

/// <summary>
/// Closes background programs named in a report. Windows components, drivers' helpers and security software
/// are never touched. Programs with a window get a normal close request first (so they can save), the rest are
/// terminated after a short wait.
/// </summary>
public static class ProcessCloser
{
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "Memory Compression", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso",
        "svchost", "dwm", "explorer", "fontdrvhost", "audiodg", "sihost", "ctfmon", "conhost", "spoolsv", "taskhostw", "dllhost",
        "RuntimeBroker", "SearchHost", "SearchIndexer", "StartMenuExperienceHost", "ShellExperienceHost", "TextInputHost",
        "SecurityHealthService", "SecurityHealthSystray", "MsMpEng", "NisSrv", "MpDefenderCoreService", "smartscreen", "WmiPrvSE",
        "WUDFHost", "dasHost", "LockApp", "ApplicationFrameHost", "SystemSettings", "MoUsoCoreWorker", "TiWorker", "TrustedInstaller",
        "nvcontainer", "NVDisplay.Container", "nvsphelper64", "atiesrxx", "atieclxx", "amdfendrsr", "RadeonSoftware", "igfxEM", "IntelCpHDCPSvc",
        "PcAnalyzer", "PresentMon", "PresentMon-2.6.0-x64",
    };

    public static bool IsProtected(string name) => Protected.Contains(name) || name.StartsWith("PresentMon", StringComparison.OrdinalIgnoreCase);

    /// <summary>Names (without .exe) from the list that are running now in this user session and may be closed.</summary>
    public static List<string> RunningClosable(IEnumerable<string> names)
    {
        int session = Process.GetCurrentProcess().SessionId;
        return names.Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(n => !IsProtected(n))
            .Where(n => Process.GetProcessesByName(n).Any(p => SafeSession(p) == session))
            .ToList();
    }

    /// <summary>User programs using the most memory right now (all instances of a program summed), heaviest first.</summary>
    public static List<(string Name, double Mb)> TopMemory(int count, double minMb = 300)
    {
        int session = Process.GetCurrentProcess().SessionId;
        var sums = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.SessionId != session || IsProtected(p.ProcessName)) continue;
                    sums[p.ProcessName] = sums.GetValueOrDefault(p.ProcessName) + p.WorkingSet64 / 1048576.0;
                }
                catch { /* exited or access denied */ }
            }
        }
        return sums.Where(kv => kv.Value >= minMb).OrderByDescending(kv => kv.Value).Take(count)
            .Select(kv => (kv.Key, kv.Value)).ToList();
    }

    public sealed record Result(List<string> Closed, List<string> Failed);

    public static async Task<Result> CloseAsync(IEnumerable<string> names)
    {
        int session = Process.GetCurrentProcess().SessionId;
        var closed = new List<string>();
        var failed = new List<string>();
        foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase).Where(n => !IsProtected(n)))
        {
            var procs = Process.GetProcessesByName(name).Where(p => SafeSession(p) == session).ToList();
            if (procs.Count == 0) continue;
            foreach (var p in procs)
                try { if (p.MainWindowHandle != IntPtr.Zero) p.CloseMainWindow(); } catch { /* already gone */ }

            // Give windowed programs a moment to exit on their own.
            for (int i = 0; i < 16 && procs.Any(Alive); i++) await Task.Delay(250);

            bool ok = true;
            foreach (var p in procs.Where(Alive))
            {
                try { p.Kill(); p.WaitForExit(2000); }
                catch { ok = false; } // access denied (elevated program) or protected process
            }
            (ok && !procs.Any(Alive) ? closed : failed).Add(name);
            foreach (var p in procs) p.Dispose();
        }
        return new Result(closed, failed);
    }

    private static bool Alive(Process p)
    {
        try { return !p.HasExited; } catch { return false; }
    }

    private static int SafeSession(Process p)
    {
        try { return p.SessionId; } catch { return -1; }
    }
}
