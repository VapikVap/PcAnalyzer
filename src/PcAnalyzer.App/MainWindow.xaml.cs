using System.ComponentModel;
using System.Windows;
using PcAnalyzer.App.ViewModels;
using Microsoft.Extensions.Logging;
using PcAnalyzer.Core.Abstractions;
using PcAnalyzer.Core.Services;

namespace PcAnalyzer.App;

public partial class MainWindow : Window
{
    private readonly ISettingsService _settings;

    /// <summary>Set by the app when the user chose "Exit" (tray) so closing really exits.</summary>
    public bool AllowClose { get; set; }

    private readonly SessionRecorder _recorder;
    private readonly ILogger _log;

    public MainWindow(MainViewModel vm, ISettingsService settings, SessionRecorder recorder, ILogger<MainWindow> log)
    {
        InitializeComponent();
        DataContext = vm;
        _settings = settings;
        _recorder = recorder;
        _log = log;
        StateChanged += OnStateChangedInternal;
    }

    private void OnStateChangedInternal(object? sender, EventArgs e)
    {
        // Compensate for the invisible resize border when maximized with a custom chrome.
        Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        if (WindowState == WindowState.Minimized && _settings.Current.MinimizeToTray) Hide();
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMaximize(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private bool _savingSession;

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose && _settings.Current.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        // Save and analyse an active session before exiting, without freezing the UI on shutdown.
        if (_recorder.IsRecording && !_savingSession)
        {
            e.Cancel = true;
            _savingSession = true;
            Title = "PC Performance Analyzer — сохранение сессии…";
            IsEnabled = false;
            try { await _recorder.StopAsync(); }
            catch (Exception ex) { _log.LogError(ex, "Failed to stop session on close"); }
            AllowClose = true;
            Close();
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        Application.Current.Shutdown();
    }

    public void ShowAndActivate()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }
}
