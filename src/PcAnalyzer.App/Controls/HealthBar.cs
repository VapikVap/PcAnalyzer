using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PcAnalyzer.App.Controls;

/// <summary>Vertical gradient bar (blue → orange) with a ruler, used for the system state score.</summary>
public sealed class HealthBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double?), typeof(HealthBar), new PropertyMetadata(null, (d, _) => ((HealthBar)d).Animate()));

    private static readonly DependencyProperty AnimatedProperty = DependencyProperty.Register(
        "Animated", typeof(double), typeof(HealthBar), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double? Value { get => (double?)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    private void Animate()
    {
        BeginAnimation(AnimatedProperty, new DoubleAnimation(Value ?? 0, TimeSpan.FromMilliseconds(700)) { EasingFunction = new CubicEase() });
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (h < 40) return;
        const double barW = 30;
        var track = new Rect(0, 0, barW, h);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(22, 255, 255, 255)), null, track, barW / 2, barW / 2);

        if (Value.HasValue)
        {
            double frac = Math.Clamp((double)GetValue(AnimatedProperty) / 100, 0, 1);
            double fillH = Math.Max(barW, h * frac);
            var gradient = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0), EndPoint = new Point(0, 1), MappingMode = BrushMappingMode.RelativeToBoundingBox,
                GradientStops =
                {
                    new GradientStop(Color.FromRgb(40, 110, 255), 0),
                    new GradientStop(Color.FromRgb(110, 90, 220), 0.45),
                    new GradientStop(Color.FromRgb(230, 100, 70), 0.85),
                    new GradientStop(Color.FromRgb(255, 90, 20), 1),
                }
            };
            dc.DrawRoundedRectangle(gradient, null, new Rect(0, h - fillH, barW, fillH), barW / 2, barW / 2);
        }

        // Ruler
        double x = barW + 14;
        int marks = 40;
        for (int i = 0; i <= marks; i++)
        {
            double y = 6 + (h - 12) * i / marks;
            bool major = i % 8 == 0;
            double t = i / (double)marks;
            var c = Color.FromArgb(major ? (byte)200 : (byte)120,
                (byte)(60 + 190 * t), (byte)(200 - 80 * t), (byte)(140 - 110 * t));
            var pen = new Pen(new SolidColorBrush(c), 1);
            pen.Freeze();
            dc.DrawLine(pen, new Point(x, y), new Point(x + (major ? 18 : 9), y));
        }
    }
}
