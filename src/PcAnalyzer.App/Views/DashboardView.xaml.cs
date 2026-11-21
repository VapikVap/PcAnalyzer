using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;

namespace PcAnalyzer.App.Views;

public partial class DashboardView : UserControl
{
    public static readonly IValueConverter LoadToHeight = new LoadHeightConverter();

    public DashboardView() => InitializeComponent();

    private sealed class LoadHeightConverter : IValueConverter
    {
        public object Convert(object value, Type t, object p, CultureInfo c) => value is double d ? Math.Max(2, 34 * Math.Clamp(d, 0, 100) / 100) : 2.0;
        public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
    }
}
