using System.Globalization;

namespace Minicon.SimpleAdmin.WebUI.Helpers;

/// <summary>
/// Server-side SVG geometry for the metric charts (sparklines and history charts), so the
/// status pages need no chart library. Coordinates use invariant culture ("12.5", never "12,5").
/// </summary>
internal sealed class ChartGeometry
{
    private readonly double _width;
    private readonly double _height;

    public ChartGeometry(IReadOnlyList<double> values, double width, double height, double? includeMin = null, double? includeMax = null)
    {
        Values = values;
        _width = width;
        _height = height;

        var min = values.Count > 0 ? values.Min() : 0;
        var max = values.Count > 0 ? values.Max() : 1;
        if (includeMin.HasValue) min = Math.Min(min, includeMin.Value);
        if (includeMax.HasValue) max = Math.Max(max, includeMax.Value);
        if (max - min < 1e-9) max = min + 1; // flat line → avoid division by zero
        Min = min;
        Max = max;
    }

    public IReadOnlyList<double> Values { get; }
    public double Min { get; }
    public double Max { get; }

    public double X(int index) => Values.Count <= 1 ? _width : index * _width / (Values.Count - 1);

    public double Y(double value) => _height - (value - Min) / (Max - Min) * _height;

    /// <summary>Polyline path through all values ("M x y L x y …").</summary>
    public string LinePath()
    {
        if (Values.Count == 0) return string.Empty;

        // A single point still renders as a short horizontal line
        if (Values.Count == 1) return $"M0 {F(Y(Values[0]))} L{F(_width)} {F(Y(Values[0]))}";

        var parts = new List<string>(Values.Count);
        for (var i = 0; i < Values.Count; i++)
        {
            parts.Add((i == 0 ? "M" : "L") + F(X(i)) + " " + F(Y(Values[i])));
        }
        return string.Join(' ', parts);
    }

    /// <summary>Closed area under the line, for the gradient fill.</summary>
    public string AreaPath() =>
        Values.Count == 0 ? string.Empty : $"{LinePath()} L{F(_width)} {F(_height)} L0 {F(_height)} Z";

    public static string F(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
