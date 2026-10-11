namespace LoadTester.Core.Scenarios;

/// <summary>
/// Everything needed to run a load test: the steps every virtual user repeats and the load profile.
/// Persisted as JSON, so it stays a plain mutable object graph.
/// </summary>
public sealed class ScenarioDefinition
{
    /// <summary>Prepended to step URLs that are not absolute.</summary>
    public string? BaseUrl { get; set; }

    public LoadProfile Load { get; set; } = new();

    public RequestSettings Settings { get; set; } = new();

    /// <summary>Variable values, evaluated again at the start of every iteration.</summary>
    public List<NameValue> Variables { get; set; } = [];

    /// <summary>Headers sent with every step; a step header with the same name wins.</summary>
    public List<NameValue> Headers { get; set; } = [];

    public List<StepDefinition> Steps { get; set; } = [];

    public static ScenarioDefinition CreateDefault() => new()
    {
        Load = new LoadProfile { Stages = [new LoadStage(10, 10), new LoadStage(30, 10)] },
        Steps = [new StepDefinition()],
    };
}

public sealed class NameValue
{
    public NameValue()
    {
    }

    public NameValue(string name, string value)
    {
        Name = name;
        Value = value;
    }

    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
}

public enum LoadMode
{
    /// <summary>Closed model: a number of virtual users run the steps back to back.</summary>
    VirtualUsers,

    /// <summary>Open model: iterations start at a set rate per second, regardless of response times.</summary>
    ArrivalRate,
}

public sealed class LoadProfile
{
    public LoadMode Mode { get; set; } = LoadMode.VirtualUsers;

    /// <summary>Load at t = 0 (virtual users or iterations per second); the first stage ramps from here.</summary>
    public int StartTarget { get; set; }

    /// <summary>Consecutive stages; each one ramps linearly from the previous target to its own.</summary>
    public List<LoadStage> Stages { get; set; } = [];

    /// <summary>Arrival-rate mode only: cap on concurrently running iterations. Arrivals over the cap are dropped.</summary>
    public int MaxVirtualUsers { get; set; } = 1000;

    public int TotalSeconds => Stages.Sum(s => Math.Max(0, s.DurationSeconds));

    public int PeakTarget => Stages.Aggregate(StartTarget, (peak, s) => Math.Max(peak, s.Target));

    /// <summary>Target load at <paramref name="seconds"/>, linearly interpolated inside the current stage.</summary>
    public double TargetAt(double seconds)
    {
        double from = StartTarget, stageStart = 0;
        foreach (var stage in Stages)
        {
            var duration = Math.Max(0, stage.DurationSeconds);
            if (seconds < stageStart + duration)
                return from + (stage.Target - from) * (seconds - stageStart) / duration;

            stageStart += duration;
            from = stage.Target;
        }

        return from;
    }

    /// <summary>Number of virtual users that should be running at <paramref name="seconds"/>.</summary>
    public int VirtualUsersAt(double seconds) => (int)Math.Round(TargetAt(seconds), MidpointRounding.AwayFromZero);

    /// <summary>Iterations that should have started by <paramref name="seconds"/>: the integral of the rate curve.</summary>
    public double ArrivalsUntil(double seconds)
    {
        double from = StartTarget, stageStart = 0, total = 0;
        foreach (var stage in Stages)
        {
            var duration = Math.Max(0, stage.DurationSeconds);
            if (seconds < stageStart + duration)
            {
                var t = seconds - stageStart;
                return total + from * t + (stage.Target - from) * t * t / (2.0 * duration);
            }

            total += (from + stage.Target) / 2.0 * duration;
            stageStart += duration;
            from = stage.Target;
        }

        return total;
    }
}

public sealed class LoadStage
{
    public LoadStage()
    {
    }

    public LoadStage(int durationSeconds, int target)
    {
        DurationSeconds = durationSeconds;
        Target = target;
    }

    public int DurationSeconds { get; set; }
    public int Target { get; set; }
}

public sealed class RequestSettings
{
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Random pause after each step, between min and max (inclusive).</summary>
    public int ThinkTimeMinMs { get; set; }
    public int ThinkTimeMaxMs { get; set; }

    public bool FollowRedirects { get; set; } = true;

    /// <summary>Gives every virtual user a cookie jar; it is emptied at the start of each iteration.</summary>
    public bool UseCookies { get; set; } = true;

    public bool IgnoreCertificateErrors { get; set; }

    /// <summary>
    /// Protocol every request must use; a server that does not support it fails the request instead of downgrading.
    /// Null sends requests as HTTP/1.1, the .NET default.
    /// </summary>
    public HttpProtocolVersion? HttpVersion { get; set; }

    /// <summary>
    /// Adds a unique query parameter and no-cache headers to GET and HEAD requests,
    /// so CDNs and proxies cannot answer from their cache and the load reaches the origin.
    /// </summary>
    public bool CacheBusting { get; set; }

    /// <summary>Name of the query parameter added when <see cref="CacheBusting"/> is on.</summary>
    public string CacheBustingParameter { get; set; } = "_cb";
}

public enum HttpProtocolVersion
{
    /// <summary>Negotiated through TLS (ALPN) on https; on http the server must accept HTTP/2 without upgrade (h2c).</summary>
    Http2,

    /// <summary>QUIC over UDP to the same host and port; https only, and the operating system must support QUIC.</summary>
    Http3,
}

public sealed class StepDefinition
{
    public string Name { get; set; } = "";
    public string Method { get; set; } = "GET";
    public string Url { get; set; } = "";
    public List<NameValue> Headers { get; set; } = [];
    public string? Body { get; set; }

    /// <summary>Accepted status codes, e.g. "200", "200-299, 304" or "2xx". Empty accepts any status.</summary>
    public string ExpectedStatus { get; set; } = "200-399";

    /// <summary>When set, the response body must contain this text.</summary>
    public string? ExpectedBodyText { get; set; }

    public List<Extractor> Extractors { get; set; } = [];
}

public enum ExtractorSource
{
    JsonPath,
    Regex,
    Header,
}

/// <summary>Captures a value from a response into a variable that later steps use as {{name}}.</summary>
public sealed class Extractor
{
    public string Variable { get; set; } = "";
    public ExtractorSource Source { get; set; } = ExtractorSource.JsonPath;
    public string Expression { get; set; } = "";
}
