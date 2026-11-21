namespace PcAnalyzer.App.ViewModels;

/// <summary>What a dashboard gauge card shows. Value null = metric unavailable.</summary>
public sealed record GaugeData(double? Value = null, double Maximum = 100, double WarnFrom = double.NaN, string Unit = "", string Caption = "", string Details = "")
{
    public string ValueText => Value.HasValue ? Value.Value.ToString("0") : "—";
}
