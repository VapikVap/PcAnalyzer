using System.Windows.Media;

namespace PcAnalyzer.App.Controls;

/// <summary>A bounded series of values for <see cref="LiveChart"/>. Null = gap (metric unavailable).</summary>
public sealed class ChartSeries
{
    private readonly List<double?> _values = new();

    public ChartSeries(string name, Color color, int capacity = 180)
    {
        Name = name;
        Color = color;
        Capacity = capacity;
        Brush = new SolidColorBrush(color);
        Brush.Freeze();
    }

    public string Name { get; }
    public Color Color { get; }
    public Brush Brush { get; }
    public int Capacity { get; set; }
    public IReadOnlyList<double?> Values => _values;
    public bool HasData => _values.Any(v => v.HasValue);
    public double? Last => _values.Count > 0 ? _values[^1] : null;

    public event EventHandler? Updated;

    public void Add(double? value)
    {
        _values.Add(value);
        if (_values.Count > Capacity) _values.RemoveRange(0, _values.Count - Capacity);
        Updated?.Invoke(this, EventArgs.Empty);
    }

    public void SetAll(IEnumerable<double?> values)
    {
        _values.Clear();
        _values.AddRange(values);
        Capacity = Math.Max(Capacity, _values.Count);
        Updated?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        _values.Clear();
        Updated?.Invoke(this, EventArgs.Empty);
    }
}
