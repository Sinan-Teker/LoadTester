using LoadTester.Core.Metrics;

namespace LoadTester.Core.Tests;

public class LatencyHistogramTests
{
    [Fact]
    public void Percentiles_AreWithinOnePercent()
    {
        var histogram = new LatencyHistogram();
        var samples = Enumerable.Range(1, 100_000).Select(i => (long)i * 10).ToArray(); // 10 µs .. 1 s
        foreach (var sample in samples.OrderBy(_ => Random.Shared.Next()))
            histogram.Record(sample);

        foreach (var percentile in new[] { 50.0, 90, 95, 99 })
        {
            var exact = samples[(int)Math.Ceiling(percentile / 100 * samples.Length) - 1] / 1000.0;
            Assert.InRange(histogram.PercentileMs(percentile), exact * 0.99, exact * 1.01);
        }
    }

    [Fact]
    public void Stats_KeepExactMinMaxAndMean()
    {
        var histogram = new LatencyHistogram();
        histogram.Record(1_000);
        histogram.Record(3_000);
        histogram.Record(8_000);

        var stats = histogram.ToStats()!;

        Assert.Equal(1, stats.MinMs);
        Assert.Equal(8, stats.MaxMs);
        Assert.Equal(4, stats.MeanMs, precision: 6);
        Assert.InRange(stats.P99Ms, 7.92, 8);
    }

    [Fact]
    public void Add_MergesHistograms()
    {
        var a = new LatencyHistogram();
        var b = new LatencyHistogram();
        a.Record(2_000);
        b.Record(4_000);

        a.Add(b);

        Assert.Equal(2, a.Count);
        Assert.Equal(4, a.ToStats()!.MaxMs);
        Assert.Null(new LatencyHistogram().ToStats());
    }
}
