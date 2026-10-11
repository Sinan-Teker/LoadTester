using LoadTester.Core.Metrics;
using LoadTester.Core.Scenarios;
using LoadTester.Core.Serialization;
using LoadTester.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace LoadTester.Web.Services;

public sealed record RunSummary(
    int Id,
    int ScenarioId,
    string ScenarioName,
    RunStatus Status,
    DateTime StartedAt,
    double DurationSeconds,
    long TotalRequests,
    long FailedRequests,
    double RequestsPerSecond,
    double? P95Ms,
    int PeakVirtualUsers);

public sealed class RunDetails
{
    public required TestRunEntity Run { get; init; }
    public required ScenarioDefinition Definition { get; init; }
    public RunReport? Report { get; init; }
    public IReadOnlyList<TimePoint> Timeline { get; init; } = [];
}

public sealed class RunStore(IDbContextFactory<AppDbContext> dbFactory)
{
    public async Task<int> CreateAsync(int scenarioId, string scenarioName, ScenarioDefinition definition, DateTime startedAt)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var run = new TestRunEntity
        {
            ScenarioId = scenarioId,
            ScenarioName = scenarioName,
            DefinitionJson = LoadTesterJson.Serialize(definition),
            Status = RunStatus.Running,
            StartedAt = startedAt,
        };
        db.Runs.Add(run);
        await db.SaveChangesAsync();
        return run.Id;
    }

    public async Task CompleteAsync(int runId, RunStatus status, LoadTestResult? result, string? error)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var run = await db.Runs.FirstOrDefaultAsync(r => r.Id == runId);
        if (run is null)
            return;

        run.Status = status;
        run.EndedAt = DateTime.UtcNow;
        run.Error = error;
        if (result is null)
        {
            run.DurationSeconds = (run.EndedAt.Value - run.StartedAt).TotalSeconds;
        }
        else
        {
            var report = result.Report;
            run.DurationSeconds = report.ElapsedSeconds;
            run.TotalRequests = report.Requests;
            run.FailedRequests = report.Failures;
            run.RequestsPerSecond = report.RequestsPerSecond;
            run.P95Ms = report.Latency?.P95Ms;
            run.PeakVirtualUsers = report.PeakVirtualUsers;
            run.ReportJson = LoadTesterJson.Serialize(report);
            run.TimelineJson = LoadTesterJson.Serialize(result.Timeline);
        }

        await db.SaveChangesAsync();
    }

    public async Task<List<RunSummary>> ListAsync(int take = 200)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Runs.AsNoTracking()
            .OrderByDescending(r => r.Id)
            .Take(take)
            .Select(r => new RunSummary(
                r.Id, r.ScenarioId, r.ScenarioName, r.Status, r.StartedAt, r.DurationSeconds,
                r.TotalRequests, r.FailedRequests, r.RequestsPerSecond, r.P95Ms, r.PeakVirtualUsers))
            .ToListAsync();
    }

    public async Task<RunDetails?> GetAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var run = await db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id);
        if (run is null)
            return null;

        return new RunDetails
        {
            Run = run,
            Definition = LoadTesterJson.Deserialize<ScenarioDefinition>(run.DefinitionJson),
            Report = run.ReportJson is null ? null : LoadTesterJson.Deserialize<RunReport>(run.ReportJson),
            Timeline = run.TimelineJson is null ? [] : LoadTesterJson.Deserialize<List<TimePoint>>(run.TimelineJson),
        };
    }

    /// <summary>Running tests cannot be deleted.</summary>
    public async Task<bool> DeleteAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Runs.Where(r => r.Id == id && r.Status != RunStatus.Running).ExecuteDeleteAsync() > 0;
    }

    /// <summary>Runs still marked as running at startup were cut off by a previous shutdown.</summary>
    public async Task MarkInterruptedAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Runs
            .Where(r => r.Status == RunStatus.Running)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, RunStatus.Interrupted));
    }
}
