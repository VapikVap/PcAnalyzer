using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using PcAnalyzer.Core.Analysis;
using PcAnalyzer.Core.Models;

namespace PcAnalyzer.App.Converters;

public sealed class BoolToVisibility : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        bool b = value switch { bool x => x, null => false, int i => i > 0, string s => !string.IsNullOrEmpty(s), _ => true };
        return b ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class InverseBool : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is bool b ? !b : true;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => value is bool b ? !b : false;
}

public sealed class SeverityToBrush : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value switch
    {
        Severity.Critical => Application.Current.Resources["BadBrush"],
        Severity.Major => Application.Current.Resources["MajorBrush"],
        Severity.Minor => Application.Current.Resources["WarnBrush"],
        _ => Application.Current.Resources["GoodBrush"],
    };
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class VerdictToBrush : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value switch
    {
        DeltaVerdict.Better => Application.Current.Resources["GoodBrush"],
        DeltaVerdict.Worse => Application.Current.Resources["BadBrush"],
        _ => Application.Current.Resources["MutedBrush"],
    };
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Compares the bound enum value with the parameter (for RadioButtons).</summary>
public sealed class EnumEquals : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value?.ToString() == p?.ToString();
    public object ConvertBack(object value, Type t, object p, CultureInfo c) =>
        value is true && p is string s ? Enum.Parse(t, s) : Binding.DoNothing;
}

public sealed class SourceToBrush : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value is true ? Application.Current.Resources["GoodBrush"] : (Brush)Application.Current.Resources["MutedBrush"];
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}
