using System.Runtime.InteropServices;
using LoadTester.Core.Engine;

namespace LoadTester.Core.Metrics;

/// <summary>Thread-safe aggregation of request results into per-step totals and a rolling one-second interval.</summary>
internal sealed class MetricsCollector
{
    public const string OtherErrorsMessage = "Diğer hatalar";
    private const int MaxDistinctErrors = 200;

    private readonly object _gate = new();
    private readonly StepState[] _steps;
    private readonly Dictionary<(int Step, string Message), long> _errors = [];
    private readonly LatencyHistogram _interval = new();
    private long _intervalRequests;
    private long _intervalFailures;
    private long _iterations;
    private long _droppedIterations;

    public MetricsCollector(IEnumerable<(string Name, string Method, string Url)> steps)
    {
        _steps = steps.Select(s => new StepState(s.Name, s.Method, s.Url)).ToArray();
    }

    public TrafficCounter Traffic { get; } = new();

    /// <param name="microseconds">Duration, or null when the request could not even be sent.</param>
    /// <param name="statusCode">0 when no response was received.</param>
    /// <param name="error">Failure reason, or null for a successful request.</param>
    public void RecordRequest(int step, long? microseconds, int statusCode, string? error)
    {
        lock (_gate)
        {
            var state = _steps[step];
            state.Requests++;
            _intervalRequests++;

            if (microseconds is { } value)
            {
                state.Latency.Record(value);
                _interval.Record(value);
            }

            if (statusCode > 0)
                CollectionsMarshal.GetValueRefOrAddDefault(state.StatusCodes, statusCode, out _)++;

            if (error is not null)
            {
                state.Failures++;
                _intervalFailures++;
                AddError(step, error);
            }
        }
    }

    public void RecordEngineError(Exception exception)
    {
        lock (_gate)
            AddError(-1, exception.Message);
    }

    public void RecordIteration() => Interlocked.Increment(ref _iterations);

    public void RecordDroppedIteration() => Interlocked.Increment(ref _droppedIterations);

    /// <summary>Returns what finished since the previous call and starts a new interval.</summary>
    public IntervalSample TakeInterval()
    {
        lock (_gate)
        {
            var hasLatency = _interval.Count > 0;
            var sample = new IntervalSample(
                _intervalRequests,
                _intervalFailures,
                hasLatency ? _interval.PercentileMs(50) : null,
                hasLatency ? _interval.PercentileMs(95) : null,
                hasLatency ? _interval.PercentileMs(99) : null);

            _interval.Clear();
            _intervalRequests = 0;
            _intervalFailures = 0;
            return sample;
        }
    }

    public RunReport BuildReport(double elapsedSeconds, int activeVirtualUsers, int peakVirtualUsers)
    {
        lock (_gate)
        {
            var overall = new LatencyHistogram();
            var steps = new List<StepReport>(_steps.Length);
            long requests = 0, failures = 0;
            for (var i = 0; i < _steps.Length; i++)
            {
                var state = _steps[i];
                overall.Add(state.Latency);
                requests += state.Requests;
                failures += state.Failures;
                steps.Add(new StepReport
                {
                    Index = i,
                    Name = state.Name,
                    Method = state.Method,
                    Url = state.Url,
                    Requests = state.Requests,
                    Failures = state.Failures,
                    Latency = state.Latency.ToStats(),
                    StatusCodes = state.StatusCodes.OrderBy(p => p.Key).Select(p => new StatusCount(p.Key, p.Value)).ToList(),
                });
            }

            return new RunReport
            {
                ElapsedSeconds = elapsedSeconds,
                ActiveVirtualUsers = activeVirtualUsers,
                PeakVirtualUsers = peakVirtualUsers,
                Requests = requests,
                Failures = failures,
                Iterations = Interlocked.Read(ref _iterations),
                DroppedIterations = Interlocked.Read(ref _droppedIterations),
                BytesReceived = Traffic.Received,
                BytesSent = Traffic.Sent,
                Latency = overall.ToStats(),
                Steps = steps,
                Errors = _errors
                    .OrderByDescending(e => e.Value)
                    .Select(e => new ErrorReport(e.Key.Step, e.Key.Message, e.Value))
                    .ToList(),
            };
        }
    }

    private void AddError(int step, string message)
    {
        var key = (step, message);
        if (!_errors.ContainsKey(key) && _errors.Count >= MaxDistinctErrors)
            key = (step, OtherErrorsMessage);

        CollectionsMarshal.GetValueRefOrAddDefault(_errors, key, out _)++;
    }

    private sealed class StepState(string name, string method, string url)
    {
        public string Name { get; } = name;
        public string Method { get; } = method;
        public string Url { get; } = url;
        public long Requests;
        public long Failures;
        public LatencyHistogram Latency { get; } = new();
        public Dictionary<int, long> StatusCodes { get; } = [];
    }
}

internal readonly record struct IntervalSample(long Requests, long Failures, double? P50Ms, double? P95Ms, double? P99Ms);
