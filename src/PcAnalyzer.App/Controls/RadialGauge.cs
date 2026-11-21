using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PcAnalyzer.App.Controls;

/// <summary>
/// Semicircular "tick" gauge: radial dashes from left to right, filled ones glow blue.
/// The value animates smoothly. A null value (metric unavailable) draws only inactive ticks.
/// </summary>
public sealed class RadialGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double?), typeof(RadialGauge), new PropertyMetadata(null, OnValueChanged));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(RadialGauge), new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty WarnFromProperty = DependencyProperty.Register(
        nameof(WarnFrom), typeof(double), typeof(RadialGauge), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty AnimatedProperty = DependencyProperty.Register(
        "Animated", typeof(double), typeof(RadialGauge), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double? Value { get => (double?)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    /// <summary>Ticks above this value are drawn in warning colour.</summary>
    public double WarnFrom { get => (double)GetValue(WarnFromProperty); set => SetValue(WarnFromProperty, value); }

    private const int Ticks = 46;
    private const double StartDeg = 200, SweepDeg = 220; // from lower-left, over the top, to lower-right

    private static readonly Pen Inactive = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(46, 255, 255, 255)), 2.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });

    private static Pen Freeze(Pen p) { p.Freeze(); return p; }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var g = (RadialGauge)d;
        double target = g.Value ?? 0;
        var anim = new DoubleAnimation(target, TimeSpan.FromMilliseconds(600)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        g.BeginAnimation(AnimatedProperty, anim);
        g.InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 10 || h < 10) return;
        double cx = w / 2;
        double r = Math.Min(w / 2, h * 0.62) - 4;
        double cy = r + 4;
        double inner = r * 0.74;

        double frac = Value.HasValue ? Math.Clamp((double)GetValue(AnimatedProperty) / Math.Max(Maximum, 1e-6), 0, 1) : 0;
        int active = (int)Math.Round(frac * Ticks);
        int warnTick = double.IsNaN(WarnFrom) ? int.MaxValue : (int)Math.Round(Math.Clamp(WarnFrom / Maximum, 0, 1) * Ticks);

        for (int i = 0; i < Ticks; i++)
        {
            double t = i / (double)(Ticks - 1);
            double deg = StartDeg - SweepDeg * t;
            double rad = deg * Math.PI / 180;
            var p1 = new Point(cx + Math.Cos(rad) * inner, cy - Math.Sin(rad) * inner);
            var p2 = new Point(cx + Math.Cos(rad) * r, cy - Math.Sin(rad) * r);
            Pen pen;
            if (i < active)
            {
                Color c = i >= warnTick
                    ? Lerp(Color.FromRgb(255, 170, 60), Color.FromRgb(255, 80, 80), Math.Clamp((i - warnTick) / 8.0, 0, 1))
                    : Lerp(Color.FromRgb(40, 110, 255), Color.FromRgb(70, 170, 255), t);
                pen = new Pen(new SolidColorBrush(c), 2.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                pen.Freeze();
            }
            else pen = Inactive;
            dc.DrawLine(pen, p1, p2);
        }
    }

    private static Color Lerp(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
}
