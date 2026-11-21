using System.Windows;
using PcAnalyzer.Core.Abstractions;
using PcAnalyzer.Core.Analysis;
using PcAnalyzer.Core.Models;
using PcAnalyzer.Core.Services;
using Forms = System.Windows.Forms;

namespace PcAnalyzer.App.Infrastructure;

/// <summary>Tray icon, its menu, and balloon notifications (throttled).</summary>
public sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly ISettingsService _settings;
    private readonly SessionRecorder _recorder;
    private readonly Forms.ToolStripMenuItem _recordItem;
    private DateTime _lastBalloon = DateTime.MinValue;
    private Action? _balloonClick;

    public event EventHandler? OpenRequested;
    public event EventHandler<long>? OpenSessionRequested;
    public event EventHandler? ExitRequested;

    public TrayService(ISettingsService settings, SessionRecorder recorder)
    {
        _settings = settings;
        _recorder = recorder;
        _icon = new Forms.NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(AppPaths.ExePath) ?? System.Drawing.SystemIcons.Application,
            Text = "PC Performance Analyzer",
            Visible = true,
        };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Открыть", null, (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty));
        _recordItem = new Forms.ToolStripMenuItem("Остановить запись", null, async (_, _) => await _recorder.StopAsync()) { Visible = false };
        menu.Items.Add(_recordItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
        _icon.BalloonTipClicked += (_, _) => _balloonClick?.Invoke();

        recorder.StateChanged += (_, _) => Ui(() =>
        {
            _recordItem.Visible = recorder.IsRecording;
            _icon.Text = recorder.Active is { } s ? Trim($"Запись: {s.GameName}") : "PC Performance Analyzer";
        });
        recorder.SessionStarted += (_, s) => Notify("Запись начата", $"{s.GameName}: мониторинг производительности включён.", null, force: true);
        recorder.SessionCompleted += (_, s) => Notify("Отчёт готов",
            $"{s.GameName}: {s.Summary?.Diagnosis ?? "анализ завершён"}", () => OpenSessionRequested?.Invoke(this, s.Id), force: true);
        recorder.LiveEventRaised += (_, e) =>
        {
            if (e.Severity >= Severity.Major) Notify(e.Kind, e.Message, () => OpenRequested?.Invoke(this, EventArgs.Empty));
        };
    }

    private static string Trim(string s) => s.Length > 63 ? s[..63] : s;

    private static void Ui(Action a) => Application.Current?.Dispatcher.BeginInvoke(a);

    private void Notify(string title, string text, Action? onClick, bool force = false)
    {
        if (!_settings.Current.Notifications) return;
        Ui(() =>
        {
            // Never spam during a game: at most one problem notification per 2 minutes.
            if (!force && DateTime.UtcNow - _lastBalloon < TimeSpan.FromMinutes(2)) return;
            _lastBalloon = DateTime.UtcNow;
            _balloonClick = onClick;
            _icon.ShowBalloonTip(5000, title, text, Forms.ToolTipIcon.None);
        });
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
