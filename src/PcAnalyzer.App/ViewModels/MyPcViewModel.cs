using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcAnalyzer.Core.Abstractions;
using PcAnalyzer.Core.Models;

namespace PcAnalyzer.App.ViewModels;

public sealed partial class MyPcViewModel : PageViewModel
{
    private readonly ISystemInfoProvider _system;
    private readonly ITelemetryService _telemetry;

    public ObservableCollection<InfoSection> Sections { get; } = new();
    public ObservableCollection<SourceRow> Sources { get; } = new();
    [ObservableProperty] private bool _loading;

    public MyPcViewModel(ISystemInfoProvider system, ITelemetryService telemetry)
    {
        _system = system;
        _telemetry = telemetry;
    }

    public override void OnNavigatedTo()
    {
        if (Sections.Count == 0) _ = LoadAsync(false);
        RefreshSources();
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync(true);

    private void RefreshSources()
    {
        Sources.Clear();
        foreach (var s in _telemetry.Sources) Sources.Add(new SourceRow { Name = s.Name, Available = s.Available, Message = s.Message });
    }

    private async Task LoadAsync(bool refresh)
    {
        Loading = true;
        try
        {
            var i = await _system.GetAsync(refresh);
            Sections.Clear();
            var g = (Geometry)Application.Current.Resources["IconCpu"];
            Sections.Add(new InfoSection("Процессор", g, new()
            {
                new("Модель", i.Cpu.Name),
                new("Ядра / потоки", $"{i.Cpu.Cores} / {i.Cpu.Threads}"),
                new("Частота (WMI)", i.Cpu.BaseClockMhz is { } b ? $"{b} МГц" : "—"),
                new("Сокет", i.Cpu.Socket is { } so && so != i.Cpu.Name ? so : "—"),
            }));
            foreach (var gpu in i.Gpus)
                Sections.Add(new InfoSection(gpu.IsDiscrete ? "Видеокарта" : "Встроенная графика", (Geometry)Application.Current.Resources["IconGpu"], new()
                {
                    new("Модель", gpu.Name),
                    new("Видеопамять", gpu.VramMb is { } v ? $"{v / 1024:0.#} ГБ" : "—"),
                    new("Драйвер", gpu.DriverVersion ?? "—"),
                    new("Дата драйвера", gpu.DriverDate?.ToString("dd.MM.yyyy") ?? "—"),
                }));
            var ram = new InfoSection("Оперативная память", (Geometry)Application.Current.Resources["IconBolt"], new() { new("Всего", $"{i.Memory.TotalMb / 1024:0.#} ГБ") });
            foreach (var m in i.Memory.Modules)
                ram.Rows.Add(new(m.Slot ?? "Модуль", $"{m.CapacityMb / 1024:0} ГБ · {(m.SpeedMhz is { } sp ? sp + " МГц" : "—")} · {m.Manufacturer} {m.PartNumber}".Trim()));
            Sections.Add(ram);
            Sections.Add(new InfoSection("Материнская плата", (Geometry)Application.Current.Resources["IconCpu"], new()
            {
                new("Модель", i.Motherboard ?? "—"),
                new("BIOS", i.BiosVersion ?? "—"),
            }));
            var disks = new InfoSection("Накопители", (Geometry)Application.Current.Resources["IconDatabase"], new());
            foreach (var d in i.Disks) disks.Rows.Add(new(d.Model, $"{d.SizeGb:0} ГБ · {d.MediaType}{(d.BusType != null ? " · " + d.BusType : "")}"));
            Sections.Add(disks);
            var displays = new InfoSection("Мониторы", (Geometry)Application.Current.Resources["IconPc"], new());
            foreach (var d in i.Displays) displays.Rows.Add(new(d.Name + (d.IsPrimary ? " (основной)" : ""), $"{d.Width}×{d.Height} · {d.RefreshHz} Гц"));
            Sections.Add(displays);
            Sections.Add(new InfoSection("Windows", (Geometry)Application.Current.Resources["IconSettings"], new()
            {
                new("Система", i.Os.Caption),
                new("Версия", $"{i.Os.Version} (сборка {i.Os.Build})"),
                new("Архитектура", i.Os.Architecture ?? "—"),
                new("План электропитания", i.PowerPlanName ?? "—"),
            }));
        }
        catch (Exception ex) { ShowStatus("Не удалось получить сведения о системе: " + ex.Message); }
        finally { Loading = false; }
        RefreshSources();
    }
}

public sealed class InfoSection(string title, Geometry icon, List<InfoRow> rows)
{
    public string Title { get; } = title;
    public Geometry Icon { get; } = icon;
    public List<InfoRow> Rows { get; } = rows;
}

public sealed record InfoRow(string Key, string Value);
