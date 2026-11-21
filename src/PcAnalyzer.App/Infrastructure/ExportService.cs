using System.Text;
using PcAnalyzer.Core.Abstractions;
using PcAnalyzer.Core.Export;
using PcAnalyzer.Core.Models;

namespace PcAnalyzer.App.Infrastructure;

public sealed class ExportService(ISessionStore store, ISettingsService settings)
{
    private string Folder()
    {
        var dir = string.IsNullOrWhiteSpace(settings.Current.ExportFolder) ? AppPaths.DefaultExportDir : settings.Current.ExportFolder!;
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string SafeName(GameSession s)
    {
        var name = new string(s.GameName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
        return $"{name} {s.StartUtc.ToLocalTime():yyyy-MM-dd HH-mm}";
    }

    /// <summary>Self-contained HTML report (can be shared or printed to PDF).</summary>
    public async Task<string> ExportHtmlAsync(GameSession s)
    {
        var samples = await store.LoadTelemetryAsync(s.Id);
        var path = Path.Combine(Folder(), SafeName(s) + ".html");
        await File.WriteAllTextAsync(path, HtmlReportExporter.Render(s, samples), new UTF8Encoding(false));
        return path;
    }

    /// <summary>Telemetry CSV (+ frame times CSV when available). Returns the telemetry file path.</summary>
    public async Task<string> ExportCsvAsync(GameSession s)
    {
        var samples = await store.LoadTelemetryAsync(s.Id);
        var frames = await store.LoadFramesAsync(s.Id);
        var basePath = Path.Combine(Folder(), SafeName(s));
        var path = basePath + " telemetry.csv";
        await Task.Run(() =>
        {
            CsvExporter.WriteTelemetry(path, s, samples);
            if (frames.Count > 0) CsvExporter.WriteFrames(basePath + " frametimes.csv", s, frames);
        });
        return path;
    }
}
