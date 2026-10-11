using System.Collections.Concurrent;
using System.Collections.Immutable;
using LoadTester.Core.Engine;
using LoadTester.Core.Metrics;
using LoadTester.Core.Scenarios;
using LoadTester.Web.Data;

namespace LoadTester.Web.Services;

public sealed record StartRunResult(int? RunId, IReadOnlyList<string> Errors)
{
    public static StartRunResult Started(int runId) => new(runId, []);
    public static StartRunResult Failed(IReadOnlyList<string> errors) => new(null, errors);
}

/// <summary>Starts load tests in the background, keeps their live state and saves the results.</summary>
public sealed class RunManager(ScenarioStore scenarios, RunStore runs, ILogger<RunManager> logger) : IHostedService
{
    private readonly ConcurrentDictionary<int, ActiveRun> _active = new();
    private readonly SemaphoreSlim _startLock = new(1, 1);

    /// <summary>Raised when a run starts or finishes.</summary>
    public event Action? RunsChanged;

    public ActiveRun? Get(int runId) => _active.GetValueOrDefault(runId);

    public ActiveRun? FindByScenario(int scenarioId) => _active.Values.FirstOrDefault(r => r.ScenarioId == scenarioId);

    public async Task<StartRunResult> StartRunAsync(int scenarioId)
    {
        ActiveRun run;
        await _startLock.WaitAsync();
        try
        {
            if (FindByScenario(scenarioId) is { } running)
                return new StartRunResult(running.RunId, ["Bu senaryo zaten çalışıyor."]);

            var scenario = await scenarios.FindAsync(scenarioId);
            if (scenario is null)
                return StartRunResult.Failed(["Senaryo bulunamadı; silinmiş olabilir."]);

            var definition = ScenarioStore.ParseDefinition(scenario.DefinitionJson);
            var errors = ScenarioValidator.Validate(definition);
            if (errors.Count > 0)
                return StartRunResult.Failed(errors);

            var startedAt = DateTime.UtcNow;
            var runId = await runs.CreateAsync(scenario.Id, scenario.Name, definition, startedAt);
            run = new ActiveRun(runId, scenario.Id, scenario.Name, definition, startedAt);
            _active[runId] = run;
            run.Completion = Task.Run(() => ExecuteAsync(run));
        }
        finally
        {
            _startLock.Release();
        }

        NotifyRunsChanged();
        return StartRunResult.Started(run.RunId);
    }

    private async Task ExecuteAsync(ActiveRun run)
    {
        RunStatus status;
        LoadTestResult? result = null;
        string? error = null;
        try
        {
            result = await new LoadTestEngine(run.Definition).RunAsync(run.Publish, run.StopToken);
            status = result.Cancelled ? RunStatus.Stopped : RunStatus.Completed;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Load test run {RunId} failed", run.RunId);
            status = RunStatus.Failed;
            error = ex.Message;
        }

        try
        {
            await runs.CompleteAsync(run.RunId, status, result, error);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not save the results of run {RunId}", run.RunId);
        }

        _active.TryRemove(run.RunId, out _);
        run.MarkFinished();
        NotifyRunsChanged();
    }

    private void NotifyRunsChanged()
    {
        foreach (var handler in RunsChanged?.GetInvocationList().Cast<Action>() ?? [])
        {
            try
            {
                handler();
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "A runs-changed subscriber failed");
            }
        }
    }

    Task IHostedService.StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>On shutdown, stops running tests so their partial results are saved.</summary>
    async Task IHostedService.StopAsync(CancellationToken cancellationToken)
    {
        var running = _active.Values.ToList();
        foreach (var run in running)
            await run.StopAsync();

        try
        {
            await Task.WhenAll(running.Select(r => r.Completion)).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }
}

/// <summary>Live state of a running test. <see cref="Changed"/> fires on the engine's thread.</summary>
public sealed class ActiveRun
{
    private readonly CancellationTokenSource _stop = new();

    internal ActiveRun(int runId, int scenarioId, string scenarioName, ScenarioDefinition definition, DateTime startedAt)
    {
        RunId = runId;
        ScenarioId = scenarioId;
        ScenarioName = scenarioName;
        Definition = definition;
        StartedAt = startedAt;
    }

    public int RunId { get; }
    public int ScenarioId { get; }
    public string ScenarioName { get; }
    public ScenarioDefinition Definition { get; }
    public DateTime StartedAt { get; }

    /// <summary>Latest cumulative report; null until the first sample.</summary>
    public RunReport? Report { get; private set; }

    public ImmutableList<TimePoint> Timeline { get; private set; } = [];
    public bool StopRequested => _stop.IsCancellationRequested;

    /// <summary>True once the results are saved and the run left the active list.</summary>
    public bool IsFinished { get; private set; }

    /// <summary>Raised after every sample, on a stop request and when the run finishes.</summary>
    public event Action? Changed;

    internal CancellationToken StopToken => _stop.Token;
    internal Task Completion { get; set; } = Task.CompletedTask;

    public async Task StopAsync()
    {
        if (_stop.IsCancellationRequested)
            return;

        await _stop.CancelAsync();
        Notify();
    }

    internal void Publish(RunReport report, TimePoint point)
    {
        Report = report;
        Timeline = Timeline.Add(point);
        Notify();
    }

    internal void MarkFinished()
    {
        IsFinished = true;
        Notify();
    }

    private void Notify()
    {
        foreach (var handler in Changed?.GetInvocationList().Cast<Action>() ?? [])
        {
            try
            {
                handler();
            }
            catch
            {
                // A page that went away must not break the run.
            }
        }
    }
}
