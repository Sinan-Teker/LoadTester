using LoadTester.Core.Metrics;

namespace LoadTester.Core.Warmup;

public enum WarmupPhase
{
    /// <summary>Downloading the sitemap and the sitemaps it lists.</summary>
    ReadingSitemaps,

    /// <summary>Requesting the pages found.</summary>
    Warming,

    Finished,
}

/// <summary>Progress of a warmup round as of <see cref="ElapsedSeconds"/>.</summary>
public sealed class WarmupReport
{
    /// <summary>1 for a single warmup; counts up when the warmup repeats until it is stopped.</summary>
    public int Round { get; init; }

    public WarmupPhase Phase { get; init; }

    /// <summary>Since the round started.</summary>
    public double ElapsedSeconds { get; init; }

    /// <summary>Sitemap files downloaded, index files and unreadable ones included.</summary>
    public int SitemapsRead { get; init; }

    /// <summary>Distinct pages to request; grows while the sitemaps are read.</summary>
    public int TotalUrls { get; init; }

    public int Completed { get; init; }
    public int Failed { get; init; }

    /// <summary>Completed requests per second since the first page request.</summary>
    public double RequestsPerSecond { get; init; }

    /// <summary>Over completed requests, failed ones included; null until the first one.</summary>
    public LatencyStats? Latency { get; init; }

    public List<StatusCount> StatusCodes { get; init; } = [];

    /// <summary>The first failed pages in the order they finished; <see cref="Failed"/> has the total.</summary>
    public List<WarmupUrlResult> Failures { get; init; } = [];

    /// <summary>The slowest successful pages, slowest first.</summary>
    public List<WarmupUrlResult> Slowest { get; init; } = [];

    /// <summary>Sitemaps that could not be read and entries that were skipped.</summary>
    public List<string> Warnings { get; init; } = [];

    public double FailureRate => Completed > 0 ? (double)Failed / Completed : 0;
}

/// <summary>A finished round of a warmup that repeats until it is stopped.</summary>
/// <param name="Report">The round's final report; null when the sitemap could not be read.</param>
/// <param name="Error">Why the round failed: the sitemap could not be read or listed no pages. Null otherwise.</param>
public sealed record WarmupRound(int Number, DateTime StartedAt, WarmupReport? Report, string? Error);

/// <param name="Url">The address as requested (percent-encoded).</param>
/// <param name="StatusCode">0 when no response was received.</param>
/// <param name="Error">Failure reason; null when the response status was 2xx or 3xx.</param>
public sealed record WarmupUrlResult(string Url, int StatusCode, double DurationMs, string? Error);
