using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace PcAnalyzer.App.Controls;

/// <summary>
/// Lightweight line chart drawn directly in OnRender (StreamGeometry): cheap enough to update every
/// second without affecting game performance. Gaps (null) break the line instead of drawing zeros.
/// </summary>
public sealed class LiveChart : FrameworkElement
{
    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(
        nameof(Series), typeof(IEnumerable<ChartSeries>), typeof(LiveChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnSeriesChanged));

    public static readonly DependencyProperty MaxValueProperty = DependencyProperty.Register(
        nameof(MaxValue), typeof(double), typeof(LiveChart), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(
        nameof(Unit), typeof(string), typeof(LiveChart), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SmoothProperty = DependencyProperty.Register(
        nameof(Smooth), typeof(bool), typeof(LiveChart), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(bool), typeof(LiveChart), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable<ChartSeries>? Series { get => (IEnumerable<ChartSeries>?)GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }
    /// <summary>Fixed Y maximum; NaN = auto.</summary>
    public double MaxValue { get => (double)GetValue(MaxValueProperty); set => SetValue(MaxValueProperty, value); }
    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    public bool Smooth { get => (bool)GetValue(SmoothProperty); set => SetValue(SmoothProperty, value); }
    public bool Fill { get => (bool)GetValue(FillProperty); set => SetValue(FillProperty, value); }

    private static readonly Pen GridPen;
    private static readonly Brush LabelBrush;
    private static readonly Typeface Face = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    static LiveChart()
    {
        GridPen = new Pen(new SolidColorBrush(Color.FromArgb(26, 255, 255, 255)), 1) { DashStyle = new DashStyle(new double[] { 2, 4 }, 0) };
        GridPen.Freeze();
        LabelBrush = new SolidColorBrush(Color.FromArgb(110, 255, 255, 255));
        LabelBrush.Freeze();
    }

    private static void OnSeriesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chart = (LiveChart)d;
        if (e.OldValue is IEnumerable<ChartSeries> old) foreach (var s in old) s.Updated -= chart.OnUpdated;
        if (e.NewValue is IEnumerable<ChartSeries> nw) foreach (var s in nw) s.Updated += chart.OnUpdated;
    }

    private void OnUpdated(object? sender, EventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 20 || h < 20) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        const double padL = 34, padB = 4, padT = 6;
        double plotW = w - padL, plotH = h - padB - padT;

        var series = Series?.ToList() ?? new List<ChartSeries>();
        double dpiScale = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (!series.Any(s => s.HasData))
        {
            // Honest empty state instead of a fake zero line.
            var empty = new FormattedText("Нет данных", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 13, LabelBrush, dpiScale);
            dc.DrawText(empty, new Point((w - empty.Width) / 2, (h - empty.Height) / 2));
            return;
        }
        double max = MaxValue;
        if (double.IsNaN(max))
        {
            double m = series.SelectMany(s => s.Values).Where(v => v.HasValue).Select(v => v!.Value).DefaultIfEmpty(0).Max();
            max = NiceMax(m * 1.1);
        }
        if (max <= 0) max = 1;

        for (int i = 0; i <= 4; i++)
        {
            double y = padT + plotH - plotH * i / 4.0;
            dc.DrawLine(GridPen, new Point(padL, y), new Point(w, y));
            var ft = new FormattedText((max * i / 4).ToString(max >= 20 ? "0" : "0.##", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, Face, 10.5, LabelBrush, dpiScale);
            dc.DrawText(ft, new Point(padL - 6 - ft.Width, y - ft.Height / 2));
        }

        foreach (var s in series)
        {
            var vals = s.Values;
            int cap = Math.Max(s.Capacity, 2);
            if (vals.Count < 2) continue;
            double step = plotW / (cap - 1);
            double x0 = padL + plotW - (vals.Count - 1) * step;

            var segments = new List<List<Point>>();
            List<Point>? cur = null;
            for (int i = 0; i < vals.Count; i++)
            {
                if (vals[i] is not { } v) { cur = null; continue; }
                if (cur == null) { cur = new List<Point>(); segments.Add(cur); }
                double y = padT + plotH - plotH * Math.Clamp(v / max, 0, 1);
                cur.Add(new Point(x0 + i * step, y));
            }

            var pen = new Pen(s.Brush, 1.8) { LineJoin = PenLineJoin.Round };
            pen.Freeze();
            Brush? fill = null;
            if (Fill)
            {
                var lg = new LinearGradientBrush(Color.FromArgb(70, s.Color.R, s.Color.G, s.Color.B), Color.FromArgb(0, s.Color.R, s.Color.G, s.Color.B), 90);
                lg.Freeze();
                fill = lg;
            }

            foreach (var seg in segments)
            {
                if (seg.Count < 2) { dc.DrawEllipse(s.Brush, null, seg[0], 1.5, 1.5); continue; }
                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    ctx.BeginFigure(seg[0], false, false);
                    if (Smooth && seg.Count > 2) DrawSmooth(ctx, seg);
                    else ctx.PolyLineTo(seg.Skip(1).ToList(), true, true);
                }
                geo.Freeze();
                if (fill != null)
                {
                    var area = new StreamGeometry();
                    using (var ctx = area.Open())
                    {
                        ctx.BeginFigure(new Point(seg[0].X, padT + plotH), true, true);
                        ctx.LineTo(seg[0], false, false);
                        if (Smooth && seg.Count > 2) DrawSmooth(ctx, seg);
                        else ctx.PolyLineTo(seg.Skip(1).ToList(), false, false);
                        ctx.LineTo(new Point(seg[^1].X, padT + plotH), false, false);
                    }
                    area.Freeze();
                    dc.DrawGeometry(fill, null, area);
                }
                dc.DrawGeometry(null, pen, geo);
            }
        }
    }

    /// <summary>Monotone-ish Catmull-Rom → Bézier.</summary>
    private static void DrawSmooth(StreamGeometryContext ctx, List<Point> p)
    {
        for (int i = 0; i < p.Count - 1; i++)
        {
            var p0 = i > 0 ? p[i - 1] : p[i];
            var p1 = p[i];
            var p2 = p[i + 1];
            var p3 = i + 2 < p.Count ? p[i + 2] : p2;
            const double t = 0.18;
            var c1 = new Point(p1.X + (p2.X - p0.X) * t, p1.Y + (p2.Y - p0.Y) * t);
            var c2 = new Point(p2.X - (p3.X - p1.X) * t, p2.Y - (p3.Y - p1.Y) * t);
            double lo = Math.Min(p1.Y, p2.Y), hi = Math.Max(p1.Y, p2.Y);
            c1.Y = Math.Clamp(c1.Y, lo, hi);
            c2.Y = Math.Clamp(c2.Y, lo, hi);
            ctx.BezierTo(c1, c2, p2, true, true);
        }
    }

    private static double NiceMax(double v)
    {
        if (v <= 0) return 1;
        double mag = Math.Pow(10, Math.Floor(Math.Log10(v)));
        double n = v / mag;
        double nice = n <= 1 ? 1 : n <= 2 ? 2 : n <= 2.5 ? 2.5 : n <= 5 ? 5 : 10;
        return nice * mag;
    }
}
