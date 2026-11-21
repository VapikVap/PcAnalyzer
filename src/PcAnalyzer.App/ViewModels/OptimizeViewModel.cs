using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcAnalyzer.App.Infrastructure;
using PcAnalyzer.Core.Abstractions;

namespace PcAnalyzer.App.ViewModels;

/// <summary>"Оптимизация": Windows settings that matter for games, with one-click fixes and undo.</summary>
public sealed partial class OptimizeViewModel : PageViewModel
{
    private readonly WindowsTweaks _tweaks;
    private readonly ISystemInfoProvider _system;
    private readonly ILogger _log;

    public ObservableCollection<TweakVm> Checks { get; } = new();
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _hasFixable;
    [ObservableProperty] private bool _loading;

    public OptimizeViewModel(WindowsTweaks tweaks, ISystemInfoProvider system, ILogger<OptimizeViewModel> log)
    {
        _tweaks = tweaks;
        _system = system;
        _log = log;
    }

    public override void OnNavigatedTo() => _ = RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        Loading = true;
        try
        {
            var info = await _system.GetAsync(false);
            var checks = await Task.Run(() => _tweaks.Evaluate(info));
            Checks.Clear();
            foreach (var c in checks) Checks.Add(new TweakVm(c, this));
            int rec = checks.Count(c => c.State == TweakState.Recommended);
            Summary = rec == 0 ? "Всё в порядке: настройки Windows не мешают играм." : $"Можно улучшить: {rec} из {checks.Count(c => c.State != TweakState.Info)}";
            HasFixable = checks.Any(c => c.State == TweakState.Recommended && c.FixLabel != null);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Optimize refresh failed");
            ShowStatus("Не удалось проверить настройки: " + ex.Message);
        }
        finally { Loading = false; }
    }

    [RelayCommand]
    private async Task FixAllAsync()
    {
        var done = new List<string>();
        foreach (var c in Checks.Where(c => c.Check.State == TweakState.Recommended && c.Check.FixLabel != null).ToList())
            if (Run(() => _tweaks.Apply(c.Check.Id))) done.Add(c.Check.Title);
        await RefreshAsync();
        if (done.Count > 0) ShowStatus("Исправлено: " + string.Join(", ", done));
    }

    internal async void Fix(TweakVm t)
    {
        if (Run(() => _tweaks.Apply(t.Check.Id))) ShowStatus($"Готово: {t.Check.Title.ToLowerInvariant()}. Вернуть прежнее значение можно кнопкой «Вернуть».");
        await RefreshAsync();
    }

    internal async void Undo(TweakVm t)
    {
        if (Run(() => _tweaks.Undo(t.Check.Id))) ShowStatus($"Возвращено прежнее значение: {t.Check.Title.ToLowerInvariant()}");
        await RefreshAsync();
    }

    internal void Open(TweakVm t)
    {
        if (t.Check.OpenTarget is { } target) Run(() => WindowsTweaks.Open(target));
    }

    private bool Run(Action a)
    {
        try { a(); return true; }
        catch (Exception ex)
        {
            _log.LogError(ex, "Tweak action failed");
            ShowStatus("Не получилось: " + ex.Message);
            return false;
        }
    }
}

public sealed partial class TweakVm(TweakCheck check, OptimizeViewModel owner)
{
    public TweakCheck Check { get; } = check;
    public string Title => Check.Title;
    public string Description => Check.Description;
    public string Status => Check.Status;
    public TweakState State => Check.State;
    public bool IsOk => Check.State == TweakState.Ok;
    public bool IsRecommended => Check.State == TweakState.Recommended;
    public string StateText => Check.State switch { TweakState.Ok => "В порядке", TweakState.Recommended => "Рекомендуется", _ => "Справка" };
    public string? FixLabel => Check.FixLabel;
    public bool HasFix => Check.FixLabel != null;
    public string? OpenLabel => Check.OpenLabel;
    public bool HasOpen => Check.OpenLabel != null && Check.OpenTarget != null;
    public bool CanUndo => Check.CanUndo;

    [RelayCommand] private void Fix() => owner.Fix(this);
    [RelayCommand] private void Undo() => owner.Undo(this);
    [RelayCommand] private void Open() => owner.Open(this);
}
