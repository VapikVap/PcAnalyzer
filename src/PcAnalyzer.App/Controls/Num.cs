using System.Globalization;
using System.Windows.Data;

namespace PcAnalyzer.App.Controls;

public static class Num
{
    /// <summary>Alternation index → 1-based number.</summary>
    public static readonly IValueConverter PlusOne = new PlusOneConverter();

    private sealed class PlusOneConverter : IValueConverter
    {
        public object Convert(object value, Type t, object p, CultureInfo c) => value is int i ? (i + 1).ToString() : "";
        public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
    }
}
