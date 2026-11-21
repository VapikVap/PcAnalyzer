using System.Diagnostics;
using Microsoft.Win32;

namespace PcAnalyzer.App.Infrastructure;

/// <summary>Autostart (HKCU\...\Run), elevation restart and opening files/folders.</summary>
public static class SystemIntegration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "PcPerformanceAnalyzer";

    public static void SetAutostart(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(RunValue, $"\"{AppPaths.ExePath}\" --minimized");
        else if (key.GetValue(RunValue) != null) key.DeleteValue(RunValue);
    }

    public static bool IsAutostartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) is string;
    }

    /// <summary>Starts a new elevated instance (UAC prompt). Returns false if the user declined.</summary>
    public static bool RestartElevated()
    {
        try
        {
            Process.Start(new ProcessStartInfo(AppPaths.ExePath) { UseShellExecute = true, Verb = "runas", Arguments = "--after-elevation" });
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    public static void Open(string pathOrUrl)
    {
        Process.Start(new ProcessStartInfo(pathOrUrl) { UseShellExecute = true });
    }

    public static void RevealInExplorer(string file)
    {
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true });
    }
}
