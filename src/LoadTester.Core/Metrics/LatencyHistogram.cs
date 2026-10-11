namespace LoadTester.Core.Metrics;

/// <summary>
/// Log-bucketed latency histogram (about 0.5% relative error), so percentiles can be computed
/// without keeping every sample. Not thread-safe; callers synchronize.
/// </summary>
public sealed class LatencyHistogram
{
    private const double Growth = 1.01;
    private const int BucketCount = 2400; // 1.01^2400 µs ≈ 6.5 hours

    private static readonly double InverseLogGrowth = 1 / Math.Log(Growth);
    private static readonly double[] Midpoints = Enumerable.Range(0, BucketCount).Select(i => Math.Pow(Growth, i + 0.5)).ToArray();

    private readonly long[] _counts = new long[BucketCount];

    public long Count { get; private set; }
    public long SumMicroseconds { get; private set; }
    public long MinMicroseconds { get; private set; } = long.MaxValue;
    public long MaxMicroseconds { get; private set; }

    public void Record(long microseconds)
    {
        if (microseconds < 0)
            microseconds = 0;

        _counts[BucketOf(microseconds)]++;
        Count++;
        SumMicroseconds += microseconds;
        if (microseconds < MinMicroseconds)
            MinMicroseconds = microseconds;
        if (microseconds > MaxMicroseconds)
            MaxMicroseconds = microseconds;
    }

    public void Add(LatencyHistogram other)
    {
        if (other.Count == 0)
            return;

        for (var i = 0; i < BucketCount; i++)
            _counts[i] += other._counts[i];

        Count += other.Count;
        SumMicroseconds += other.SumMicroseconds;
        MinMicroseconds = Math.Min(MinMicroseconds, other.MinMicroseconds);
        MaxMicroseconds = Math.Max(MaxMicroseconds, other.MaxMicroseconds);
    }

    public void Clear()
    {
        Array.Clear(_counts);
        Count = 0;
        SumMicroseconds = 0;
        MinMicroseconds = long.MaxValue;
        MaxMicroseconds = 0;
    }

    /// <summary>Milliseconds below which <paramref name="percentile"/>% of the samples fall.</summary>
    public double PercentileMs(double percentile)
    {
        if (Count == 0)
            return 0;

        var rank = Math.Max(1, (long)Math.Ceiling(percentile / 100 * Count));
        long seen = 0;
        for (var i = 0; i < BucketCount; i++)
        {
            seen += _counts[i];
            if (seen >= rank)
                return Math.Clamp(Midpoints[i], MinMicroseconds, MaxMicroseconds) / 1000;
        }

        return MaxMicroseconds / 1000.0;
    }

    public LatencyStats? ToStats() => Count == 0
        ? null
        : new LatencyStats(
            MinMicroseconds / 1000.0,
            SumMicroseconds / (double)Count / 1000,
            PercentileMs(50),
            PercentileMs(90),
            PercentileMs(95),
            PercentileMs(99),
            MaxMicroseconds / 1000.0);

    private static int BucketOf(long microseconds) =>
        microseconds <= 1 ? 0 : Math.Min(BucketCount - 1, (int)(Math.Log(microseconds) * InverseLogGrowth));
}
