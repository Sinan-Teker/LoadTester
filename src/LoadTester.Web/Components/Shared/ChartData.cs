using LoadTester.Core.Metrics;

namespace LoadTester.Web.Components.Shared;

/// <summary>A line on a <see cref="LineChart"/>; null values leave a gap.</summary>
public sealed record ChartSeries(string Name, string Color, IReadOnlyList<double?> Values);

/// <summary>
/// Series colors, checked with the dataviz palette validator against the white card surface:
/// categorical slots 1-2 for distinct measures, a one-hue ordinal ramp for the ordered percentiles.
/// </summary>
public static class ChartColors
{
    public const string Series1 = "#2a78d6";
    public const string Series2 = "#eb6834";
    public const string P50 = "#6da7ec";
    public const string P95 = "#2a78d6";
    public const string P99 = "#104281";
}

public static class ChartData
{
    /// <summary>Keeps charts light enough to re-render every second.</summary>
    public const int MaxPoints = 180;

    /// <summary>
    /// Averages consecutive samples into at most <see cref="MaxPoints"/> buckets, each plotted at its last time.
    /// Buckets grow from the start, so appending samples only changes the last one.
    /// </summary>
    public static (double[] X, double?[][] Values) FromTimeline(IReadOnlyList<TimePoint> points, IReadOnlyList<Func<TimePoint, double?>> selectors)
    {
        var size = Math.Max(1, (int)Math.Ceiling(points.Count / (double)MaxPoints));
        var count = (points.Count + size - 1) / size;
        var x = new double[count];
        var values = selectors.Select(_ => new double?[count]).ToArray();

        for (var bucket = 0; bucket < count; bucket++)
        {
            var from = bucket * size;
            var to = Math.Min(points.Count, from + size);
            x[bucket] = points[to - 1].Seconds;

            for (var s = 0; s < selectors.Count; s++)
            {
                double sum = 0;
                var n = 0;
                for (var i = from; i < to; i++)
                {
                    if (selectors[s](points[i]) is { } value)
                    {
                        sum += value;
                        n++;
                    }
                }

                values[s][bucket] = n > 0 ? sum / n : null;
            }
        }

        return (x, values);
    }
}
