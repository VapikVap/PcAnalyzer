using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace PcAnalyzer.App.Infrastructure;

public sealed record UpdateInfo(Version Version, string Tag, string DownloadUrl, long Size, string? Sha256, string PageUrl);

/// <summary>
/// Checks the latest GitHub release of the public repository and installs it: downloads the
/// installer asset (PcAnalyzer-Setup-*.exe), verifies size/SHA-256 and runs it with /UPDATE.
/// The installer waits for this process to exit, installs silently and starts the new version.
/// </summary>
public sealed class UpdateService
{
    public const string Repository = "VapikVap/PcAnalyzer";
    public static string ReleasesPage => $"https://github.com/{Repository}/releases";

    // Must stay above Http: static initializers run in declaration order and CreateClient() reads it.
    public static Version CurrentVersion { get; } = Normalize(
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0));

    private static readonly HttpClient Http = CreateClient();
    private readonly ILogger _log;

    public UpdateService(ILogger<UpdateService> log) => _log = log;

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PcAnalyzer-Updater", CurrentVersion.ToString()));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }

    /// <summary>Returns the newer release, or null when up to date / offline / no installer attached.</summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await Http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", ct);
            if (!resp.IsSuccessStatusCode)
            {
                // 404 = no releases yet; 403 = rate limit. Neither is worth bothering the user about.
                _log.LogInformation("Update check: HTTP {Status}", (int)resp.StatusCode);
                return null;
            }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            if (root.TryGetProperty("draft", out var d) && d.GetBoolean()) return null;
            if (root.TryGetProperty("prerelease", out var p) && p.GetBoolean()) return null;

            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!TryParseTag(tag, out var version) || version <= CurrentVersion)
            {
                _log.LogInformation("Update check: latest {Tag}, current {Current} — up to date", tag, CurrentVersion);
                return null;
            }

            foreach (var a in root.GetProperty("assets").EnumerateArray())
            {
                var name = a.GetProperty("name").GetString() ?? "";
                if (!name.StartsWith("PcAnalyzer-Setup", StringComparison.OrdinalIgnoreCase) ||
                    !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;

                string? sha = null;
                if (a.TryGetProperty("digest", out var dg) && dg.GetString() is { } digest &&
                    digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    sha = digest[7..];

                var info = new UpdateInfo(version, tag, a.GetProperty("browser_download_url").GetString()!,
                    a.GetProperty("size").GetInt64(), sha, root.GetProperty("html_url").GetString() ?? ReleasesPage);
                _log.LogInformation("Update available: {Tag} (current {Current})", tag, CurrentVersion);
                return info;
            }
            _log.LogWarning("Release {Tag} has no installer asset", tag);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            _log.LogInformation("Update check failed: {Message}", ex.Message);
            return null;
        }
    }

    /// <summary>Downloads and verifies the installer. Returns its local path.</summary>
    public async Task<string> DownloadAsync(UpdateInfo update, IProgress<double>? progress, CancellationToken ct = default)
    {
        var dir = Path.Combine(Path.GetTempPath(), "PcAnalyzer-Update");
        Directory.CreateDirectory(dir);
        foreach (var old in Directory.EnumerateFiles(dir, "*.exe"))
            try { File.Delete(old); } catch { /* still running from a previous attempt */ }

        var path = Path.Combine(dir, $"PcAnalyzer-Setup-{update.Version}.exe");
        var tmp = path + ".part";
        using (var resp = await Http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? update.Size;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(tmp);
            var buf = new byte[81920];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress?.Report((double)done / total);
            }
        }

        var len = new FileInfo(tmp).Length;
        if (update.Size > 0 && len != update.Size)
            throw new InvalidDataException($"Файл скачан не полностью ({len} из {update.Size} байт)");
        if (update.Sha256 != null)
        {
            await using var fs = File.OpenRead(tmp);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
            if (!hash.Equals(update.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Контрольная сумма установщика не совпадает");
        }
        File.Move(tmp, path, overwrite: true);
        _log.LogInformation("Update {Tag} downloaded to {Path}", update.Tag, path);
        return path;
    }

    /// <summary>Starts the installer in update mode (it waits for this app to exit). Throws if the user declined UAC.</summary>
    public void StartInstaller(string installerPath)
    {
        Process.Start(new ProcessStartInfo(installerPath)
        {
            UseShellExecute = true,
            Arguments = "/UPDATE /SILENT /SUPPRESSMSGBOXES /NORESTART",
        });
        _log.LogInformation("Installer started, exiting for update");
    }

    public static bool TryParseTag(string tag, out Version version)
    {
        var s = tag.Trim().TrimStart('v', 'V');
        var dash = s.IndexOfAny(new[] { '-', '+' });
        if (dash >= 0) s = s[..dash];
        if (Version.TryParse(s, out var v)) { version = Normalize(v); return true; }
        version = new Version(0, 0, 0);
        return false;
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));
}
