using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcAnalyzer.App.Infrastructure;
using PcAnalyzer.Core.Abstractions;
using PcAnalyzer.Core.Services;

namespace PcAnalyzer.App.ViewModels;

public enum Page { Dashboard, Session, Analysis, History, MyPc, Optimize, Settings }

public interface INavigator
{
    void Navigate(Page page);
    void OpenAnalysis(long sessionId);
}

public sealed partial class MainViewModel : ObservableObject, INavigator
{
    private readonly SessionRecorder _recorder;
    private readonly DispatcherTimer _clock;

    public DashboardViewModel Dashboard { get; }
    public SessionViewModel Session { get; }
    public AnalysisViewModel Analysis { get; }
    public HistoryViewModel History { get; }
    public MyPcViewModel MyPc { get; }
    public OptimizeViewModel Optimize { get; }
    public SettingsViewModel Settings { get; }
    public ITelemetryService Telemetry { get; }

    [ObservableProperty] private Page _currentPage = Page.Dashboard;
    [ObservableProperty] private object? _currentView;
    [ObservableProperty] private bool _isRecording;
    [ObservableProperty] private string _recordingText = "";

    // ---- Updates (GitHub Releases) ----
    private readonly UpdateService _updates;
    private readonly DispatcherTimer _updateTimer;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasUpdate))] private UpdateInfo? _availableUpdate;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(InstallUpdateCommand))] private bool _isUpdating;
    public bool HasUpdate => AvailableUpdate != null;
    [ObservableProperty] private double _updateProgress;
    [ObservableProperty] private string _updateStatus = "";
    public string CurrentVersionText => "Версия " + UpdateService.CurrentVersion;
    public string VersionShort => "v" + UpdateService.CurrentVersion;
    /// <summary>Result of a manual check shown next to the version ("актуальная версия" / "нет связи").</summary>
    [ObservableProperty] private string _versionHint = "";
    [ObservableProperty] private bool _isChecking;

    public MainViewModel(DashboardViewModel dashboard, SessionViewModel session, AnalysisViewModel analysis, HistoryViewModel history,
        MyPcViewModel myPc, OptimizeViewModel optimize, SettingsViewModel settings, SessionRecorder recorder, ITelemetryService telemetry, UpdateService updates)
    {
        Dashboard = dashboard; Session = session; Analysis = analysis; History = history; MyPc = myPc; Optimize = optimize; Settings = settings;
        Telemetry = telemetry;
        _recorder = recorder;
        _updates = updates;
        // First check shortly after start (do not slow down startup), then every 6 hours while running.
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _updateTimer.Tick += async (_, _) =>
        {
            _updateTimer.Interval = TimeSpan.FromHours(6);
            await CheckForUpdatesAsync(manual: false);
        };
        _updateTimer.Start();
        foreach (var vm in new INavigationAware[] { dashboard, session, analysis, history, myPc, optimize, settings }) vm.Navigator = this;

        CurrentView = Dashboard;
        recorder.StateChanged += (_, _) => Application.Current.Dispatcher.BeginInvoke(UpdateRecording);
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => UpdateRecording();
        _clock.Start();
    }

    private void UpdateRecording()
    {
        var s = _recorder.Active;
        IsRecording = s != null;
        RecordingText = s == null ? "" : $"{s.GameName} · {s.Duration:hh\\:mm\\:ss}";
    }

    partial void OnCurrentPageChanged(Page value)
    {
        CurrentView = value switch
        {
            Page.Session => Session,
            Page.Analysis => Analysis,
            Page.History => History,
            Page.MyPc => MyPc,
            Page.Optimize => Optimize,
            Page.Settings => Settings,
            _ => Dashboard,
        };
        if (CurrentView is INavigationAware n) n.OnNavigatedTo();
    }

    [RelayCommand]
    private void Go(string page) => Navigate(Enum.Parse<Page>(page));

    public void Navigate(Page page)
    {
        if (CurrentPage == page && CurrentView is INavigationAware n) n.OnNavigatedTo();
        CurrentPage = page;
    }

    public void OpenAnalysis(long sessionId)
    {
        Analysis.RequestedSessionId = sessionId;
        Navigate(Page.Analysis);
    }

    [RelayCommand]
    private Task CheckUpdatesNow() => CheckForUpdatesAsync(manual: true);

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (IsUpdating || IsChecking) return;
        IsChecking = true;
        if (manual) VersionHint = "проверка…";
        try
        {
            var (found, reachable) = await _updates.CheckWithStatusAsync();
            if (found != null) { AvailableUpdate = found; UpdateStatus = $"Доступна версия {found.Version}"; VersionHint = ""; }
            else if (manual) VersionHint = reachable ? "актуальная версия" : "нет связи с GitHub";
        }
        catch (Exception) { if (manual) VersionHint = "нет связи с GitHub"; /* never disturb the app */ }
        finally { IsChecking = false; }
    }

    private bool CanInstallUpdate() => !IsUpdating;

    [RelayCommand(CanExecute = nameof(CanInstallUpdate))]
    private async Task InstallUpdate()
    {
        if (AvailableUpdate is not { } update || IsUpdating) return;
        if (_recorder.IsRecording &&
            MessageBox.Show("Сейчас идёт запись сессии. Она будет сохранена, а приложение перезапустится после обновления. Продолжить?",
                "Обновление", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        IsUpdating = true;
        try
        {
            UpdateStatus = "Скачивание…";
            var progress = new Progress<double>(p => { UpdateProgress = p; UpdateStatus = $"Скачивание… {p:P0}"; });
            var installer = await _updates.DownloadAsync(update, progress);
            UpdateStatus = "Установка…";
            try { _updates.StartInstaller(installer); }
            catch (System.ComponentModel.Win32Exception)
            {
                // UAC declined.
                UpdateStatus = $"Доступна версия {update.Version}";
                IsUpdating = false;
                return;
            }
            // The installer waits for this process to exit; close normally so an active session is saved.
            if (Application.Current.Windows.OfType<MainWindow>().FirstOrDefault() is { } w) { w.AllowClose = true; w.Close(); }
            else Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            IsUpdating = false;
            UpdateStatus = $"Доступна версия {update.Version}";
            MessageBox.Show("Не удалось скачать обновление:\n" + ex.Message + "\n\nМожно скачать установщик вручную: " + update.PageUrl,
                "Обновление", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private void OpenReleasePage() => SystemIntegration.Open(AvailableUpdate?.PageUrl ?? UpdateService.ReleasesPage);
}

public interface INavigationAware
{
    INavigator? Navigator { get; set; }
    void OnNavigatedTo();
}

/// <summary>Base for page view-models.</summary>
public abstract partial class PageViewModel : ObservableObject, INavigationAware
{
    public INavigator? Navigator { get; set; }
    public virtual void OnNavigatedTo() { }

    [RelayCommand]
    protected void GoTo(string page) => Navigator?.Navigate(Enum.Parse<Page>(page));

    [ObservableProperty] private string? _statusMessage;

    protected void ShowStatus(string msg)
    {
        StatusMessage = msg;
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        t.Tick += (_, _) => { if (StatusMessage == msg) StatusMessage = null; t.Stop(); };
        t.Start();
    }
}
