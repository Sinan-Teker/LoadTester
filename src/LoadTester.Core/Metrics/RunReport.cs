using System.Text.Json.Serialization;

namespace LoadTester.Core.Metrics;

public sealed record LatencyStats(double MinMs, double MeanMs, double P50Ms, double P90Ms, double P95Ms, double P99Ms, double MaxMs);

/// <summary>Cumulative results of a run as of <see cref="ElapsedSeconds"/>.</summary>
public sealed class RunReport
{
    public double ElapsedSeconds { get; init; }
    public int ActiveVirtualUsers { get; init; }
    public int PeakVirtualUsers { get; init; }
    public long Requests { get; init; }
    public long Failures { get; init; }
    public long Iterations { get; init; }

    /// <summary>Arrival-rate mode: iterations not started because the concurrency cap was reached.</summary>
    public long DroppedIterations { get; init; }

    /// <summary>Bytes on the wire, including headers and TLS overhead.</summary>
    public long BytesReceived { get; init; }
    public long BytesSent { get; init; }

    /// <summary>Over all steps; null until the first response.</summary>
    public LatencyStats? Latency { get; init; }

    public List<StepReport> Steps { get; init; } = [];
    public List<ErrorReport> Errors { get; init; } = [];

    [JsonIgnore]
    public double RequestsPerSecond => ElapsedSeconds > 0 ? Requests / ElapsedSeconds : 0;

    [JsonIgnore]
    public double FailureRate => Requests > 0 ? (double)Failures / Requests : 0;
}

public sealed class StepReport
{
    public int Index { get; init; }
    public string Name { get; init; } = "";
    public string Method { get; init; } = "";
    public string Url { get; init; } = "";
    public long Requests { get; init; }
    public long Failures { get; init; }
    public LatencyStats? Latency { get; init; }
    public List<StatusCount> StatusCodes { get; init; } = [];

    [JsonIgnore]
    public double FailureRate => Requests > 0 ? (double)Failures / Requests : 0;
}

public sealed record StatusCount(int Code, long Count);

/// <summary>A distinct failure reason and how often it happened. <see cref="StepIndex"/> is -1 for engine errors.</summary>
public sealed record ErrorReport(int StepIndex, string Message, long Count);

/// <summary>One sample of the time series; the engine takes one per second.</summary>
public sealed class TimePoint
{
    /// <summary>Seconds since the start of the run, at the end of the sampled interval.</summary>
    public double Seconds { get; init; }
    public int VirtualUsers { get; init; }
    public double RequestsPerSecond { get; init; }
    public double FailuresPerSecond { get; init; }

    /// <summary>Latency percentiles of the interval; null when no request finished in it.</summary>
    public double? P50Ms { get; init; }
    public double? P95Ms { get; init; }
    public double? P99Ms { get; init; }
}

public sealed class LoadTestResult
{
    public required RunReport Report { get; init; }
    public required IReadOnlyList<TimePoint> Timeline { get; init; }

    /// <summary>True when the run was stopped through the cancellation token before its planned end.</summary>
    public bool Cancelled { get; init; }
}
