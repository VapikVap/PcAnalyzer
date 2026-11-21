using System.Text.Json;
using Microsoft.Extensions.Logging;
using PcAnalyzer.Core.Abstractions;
using PcAnalyzer.Core.Models;
using PcAnalyzer.Core.Serialization;

namespace PcAnalyzer.App.Infrastructure;

public sealed class JsonSettingsService : ISettingsService
{
    private readonly string _path;
    private readonly ILogger _log;

    public AppSettings Current { get; private set; }
    public event EventHandler? Changed;

    public JsonSettingsService(string path, ILogger<JsonSettingsService> log)
    {
        _path = path;
        _log = log;
        Current = Load();
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), Json.Pretty) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Settings file is corrupted, using defaults");
            try { File.Copy(_path, _path + ".bad", true); } catch { /* ignore */ }
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Json.Pretty));
            File.Move(tmp, _path, true);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to save settings");
            throw;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
