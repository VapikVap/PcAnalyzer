using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PcAnalyzer.App.Infrastructure;
using PcAnalyzer.App.ViewModels;
using PcAnalyzer.Core.Abstractions;
using PcAnalyzer.Core.Analysis;
using PcAnalyzer.Core.Services;
using PcAnalyzer.Data;
using PcAnalyzer.Windows;
using PcAnalyzer.Windows.Frames;
using PcAnalyzer.Windows.Games;
using PcAnalyzer.Windows.Hardware;

namespace PcAnalyzer.App;

public partial class App : Application
{
    private const string MutexName = "Local\\PcPerformanceAnalyzer.SingleInstance";
    private const string ActivateEventName = "Local\\PcPerformanceAnalyzer.Activate";
    private Mutex? _mutex;
    private EventWaitHandle? _activateEvent;
    private RegisteredWaitHandle? _activateWait;
    private ServiceProvider? _services;
    private ILogger? _log;
    private MainWindow? _window;
    private TrayService? _tray;
    private Timer? _retentionTimer;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Single instance (an elevated restart waits for the previous instance to exit).
        _mutex = new Mutex(false, MutexName);
        bool acquired;
        try { acquired = _mutex.WaitOne(e.Args.Contains("--after-elevation") ? TimeSpan.FromSeconds(15) : TimeSpan.Zero); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired)
        {
            // Launched again (desktop shortcut while the app sits in the tray): ask the running copy to show its window.
            if (!SignalRunningInstance())
                MessageBox.Show("PC Performance Analyzer уже запущен (значок в области уведомлений).", "PC Performance Analyzer", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);

        try
        {
            _services = ConfigureServices();
            _log = _services.GetRequiredService<ILogger<App>>();
            HookGlobalErrors();
            _log.LogInformation("Starting {Version}, elevated: {Elevated}", typeof(App).Assembly.GetName().Version,
                _services.GetRequiredService<ITelemetryService>().IsElevated);

            var telemetry = _services.GetRequiredService<ITelemetryService>();
            telemetry.Start();
            var recorder = _services.GetRequiredService<SessionRecorder>();
            await recorder.InitializeAsync();

            _window = _services.GetRequiredService<MainWindow>();
            _tray = _services.GetRequiredService<TrayService>();
            _tray.OpenRequested += (_, _) => _window.ShowAndActivate();
            _tray.OpenSessionRequested += (_, id) => { _window.ShowAndActivate(); _services.GetRequiredService<MainViewModel>().OpenAnalysis(id); };
            _tray.ExitRequested += (_, _) => { _window.AllowClose = true; _window.Close(); };

            var settings = _services.GetRequiredService<ISettingsService>();
            bool minimized = e.Args.Contains("--minimized") || settings.Current.StartMinimized;
            // Developer aids: --page=History opens a page, --height=N sets the window height (screenshots/testing).
            if (e.Args.FirstOrDefault(a => a.StartsWith("--page=")) is { } pageArg && Enum.TryParse<Page>(pageArg[7..], out var page))
                _services.GetRequiredService<MainViewModel>().Navigate(page);
            if (e.Args.FirstOrDefault(a => a.StartsWith("--height=")) is { } hArg && double.TryParse(hArg[9..], out var height))
            {
                _window.WindowStartupLocation = WindowStartupLocation.Manual;
                _window.Top = 0;
                _window.Left = 0;
                _window.Height = height;
            }
            if (!minimized) _window.Show();
            ListenForActivation();

            // Retention: shortly after start, then every 6 hours.
            var store = _services.GetRequiredService<ISessionStore>();
            _retentionTimer = new Timer(async _ =>
            {
                try { await store.RunRetentionAsync(settings.Current); }
                catch (Exception ex) { _log.LogError(ex, "Retention failed"); }
            }, null, TimeSpan.FromMinutes(1), TimeSpan.FromHours(6));
        }
        catch (Exception ex)
        {
            _log?.LogCritical(ex, "Startup failed");
            MessageBox.Show("Не удалось запустить приложение:\n" + ex.Message, "PC Performance Analyzer", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>Returns false when there is no running copy to signal (or it runs elevated and the event cannot be opened).</summary>
    private static bool SignalRunningInstance()
    {
        try
        {
            using var ev = EventWaitHandle.OpenExisting(ActivateEventName);
            return ev.Set();
        }
        catch { return false; }
    }

    private void ListenForActivation()
    {
        if (_activateEvent == null) return;
        _activateWait = ThreadPool.RegisterWaitForSingleObject(_activateEvent, (_, _) =>
            Dispatcher.BeginInvoke(() => _window?.ShowAndActivate()), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    private static ServiceProvider ConfigureServices()
    {
        var sc = new ServiceCollection();
        sc.AddLogging(b => b.ClearProviders().AddProvider(new FileLoggerProvider(AppPaths.LogsDir)).SetMinimumLevel(LogLevel.Information));

        sc.AddSingleton<ISettingsService>(sp => new JsonSettingsService(AppPaths.Settings, sp.GetRequiredService<ILogger<JsonSettingsService>>()));
        sc.AddSingleton<ISessionStore>(sp => new SqliteSessionStore(AppPaths.Database, sp.GetRequiredService<ILogger<SqliteSessionStore>>()));
        sc.AddSingleton<ISystemInfoProvider, SystemInfoProvider>();
        sc.AddSingleton<IFrameTimeProvider>(sp =>
        {
            // Capture the settings service, not the provider: Detach() may run during container disposal.
            var settings = sp.GetRequiredService<ISettingsService>();
            return new PresentMonProvider(() => settings.Current, sp.GetRequiredService<ILogger<PresentMonProvider>>());
        });
        sc.AddSingleton<TelemetryService>();
        sc.AddSingleton<ITelemetryService>(sp => sp.GetRequiredService<TelemetryService>());
        sc.AddSingleton<GameDetectionService>();
        sc.AddSingleton<IGameDetector>(sp => sp.GetRequiredService<GameDetectionService>());
        sc.AddSingleton<IDiagnosisNarrator, LocalNarrator>();
        sc.AddSingleton(sp => new PerformanceAnalyzer(sp.GetRequiredService<IDiagnosisNarrator>(), null, sp.GetRequiredService<ILogger<PerformanceAnalyzer>>()));
        sc.AddSingleton<SessionRecorder>();
        sc.AddSingleton<ExportService>();
        sc.AddSingleton<TrayService>();
        sc.AddSingleton<UpdateService>();

        sc.AddSingleton<LiveMonitor>();
        sc.AddSingleton<DashboardViewModel>();
        sc.AddSingleton<SessionViewModel>();
        sc.AddSingleton<AnalysisViewModel>();
        sc.AddSingleton<HistoryViewModel>();
        sc.AddSingleton<MyPcViewModel>();
        sc.AddSingleton<SettingsViewModel>();
        sc.AddSingleton<MainViewModel>();
        sc.AddSingleton<MainWindow>();
        return sc.BuildServiceProvider();
    }

    private void HookGlobalErrors()
    {
        // Windows logoff/shutdown: OnExit still runs and closes the session; log it so it is traceable.
        SessionEnding += (_, e) => _log?.LogWarning("Windows session ending ({Reason})", e.ReasonSessionEnding);
        DispatcherUnhandledException += (_, e) =>
        {
            _log?.LogError(e.Exception, "Unhandled UI exception");
            e.Handled = true; // keep the app alive; the error is in the log
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => _log?.LogCritical(e.ExceptionObject as Exception, "Unhandled exception");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            _log?.LogError(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _retentionTimer?.Dispose();
            _tray?.Dispose();
            if (_services != null)
            {
                _log?.LogInformation("Shutting down (exit code {Code})", e.ApplicationExitCode);
                // Normally the window has already stopped the session; this is the safety net
                // (e.g. Windows logoff). Then stop sensors and dispose the container asynchronously.
                var services = _services;
                var finished = Task.Run(async () =>
                {
                    await services.GetRequiredService<SessionRecorder>().DisposeAsync();
                    services.GetRequiredService<GameDetectionService>().Dispose();
                    await services.GetRequiredService<ITelemetryService>().DisposeAsync();
                }).Wait(TimeSpan.FromSeconds(25));
                _log?.LogInformation(finished ? "Exited" : "Exited after shutdown timeout");
                Task.Run(async () => await services.DisposeAsync()).Wait(TimeSpan.FromSeconds(5));
            }
        }
        catch (Exception ex) { _log?.LogError(ex, "Shutdown error"); }
        finally
        {
            _activateWait?.Unregister(null);
            _activateEvent?.Dispose();
            try { _mutex?.ReleaseMutex(); } catch { /* not owned */ }
            _mutex?.Dispose();
        }
        base.OnExit(e);
    }
}
