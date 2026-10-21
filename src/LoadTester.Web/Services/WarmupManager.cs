using System.Collections.Immutable;
using LoadTester.Core.Warmup;
using LoadTester.Web.Data;

namespace LoadTester.Web.Services;

/// <summary>Runs sitemap warmups in the background, one at a time, and keeps the latest one for the warmup page.</summary>
public sealed class WarmupManager(ILogger<WarmupManager> logger) : IHostedService
{
    private readonly object _gate = new();

    /// <summary>The running warmup, or the last one since the application started. Warmups are not saved.</summary>
    public WarmupRun? Current { get; private set; }

    /// <summary>Raised when a warmup starts, reports progress, is asked to stop and finishes.</summary>
    public event Action? Changed;

    /// <param name="repeat">Repeat round after round until stopped, instead of requesting every page once.</param>
    /// <returns>Why the warmup could not start; empty when it started.</returns>
    public IReadOnlyList<string> Start(WarmupDefinition definition, bool repeat)
    {
        var errors = WarmupValidator.Validate(definition);
        if (errors.Count > 0)
            return errors;

        lock (_gate)
        {
            if (Current is { IsFinished: false })
                return ["Bir ısınma zaten çalışıyor."];

            var run = new WarmupRun(definition, repeat, DateTime.UtcNow, NotifyChanged);
            Current = run;
            run.Completion = Task.Run(() => ExecuteAsync(run));
        }

        NotifyChanged();
        return [];
    }

    private async Task ExecuteAsync(WarmupRun run)
    {
        var warmup = new SitemapWarmup(run.Definition);
        try
        {
            if (run.Repeat)
            {
                // Stopped between rounds, the last finished round stays on the page.
                var stopped = await warmup.RepeatUntilCancelledAsync(run.Publish, run.AddRound, run.StopToken);
                run.Finish(RunStatus.Stopped, stopped ?? run.Report, null);
                return;
            }

            var report = await warmup.RunAsync(run.Publish, run.StopToken);
            if (run.StopRequested)
                run.Finish(RunStatus.Stopped, report, null);
            else if (report.TotalUrls == 0)
                run.Finish(RunStatus.Failed, report, SitemapWarmup.NoPagesError);
            else
                run.Finish(RunStatus.Completed, report, null);
        }
        catch (SitemapException ex)
        {
            run.Finish(RunStatus.Failed, null, ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Sitemap warmup of {SitemapUrl} failed", run.Definition.SitemapUrl);
            run.Finish(RunStatus.Failed, null, ex.Message);
        }
    }

    private void NotifyChanged()
    {
        foreach (var handler in Changed?.GetInvocationList().Cast<Action>() ?? [])
        {
            try
            {
                handler();
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "A warmup subscriber failed");
            }
        }
    }

    Task IHostedService.StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>On shutdown, stops a running warmup so no requests are left in flight.</summary>
    async Task IHostedService.StopAsync(CancellationToken cancellationToken)
    {
        if (Current is not { IsFinished: false } run)
            return;

        await run.StopAsync();
        try
        {
            await run.Completion.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }
}

/// <summary>A warmup started from the page: its settings and latest progress. Changes are reported by the manager.</summary>
public sealed class WarmupRun
{
    public const int MaxListedRounds = 20;

    private readonly CancellationTokenSource _stop = new();
    private readonly Action _changed;

    internal WarmupRun(WarmupDefinition definition, bool repeat, DateTime startedAt, Action changed)
    {
        Definition = definition;
        Repeat = repeat;
        StartedAt = startedAt;
        _changed = changed;
    }

    public WarmupDefinition Definition { get; }

    /// <summary>Repeats round after round until stopped.</summary>
    public bool Repeat { get; }

    public DateTime StartedAt { get; }
    public RunStatus Status { get; private set; } = RunStatus.Running;

    /// <summary>
    /// Latest progress of the running round, or the last finished round until the next one reports.
    /// Null until the first report and after a sitemap that could not be read.
    /// </summary>
    public WarmupReport? Report { get; private set; }

    /// <summary>Finished rounds of a repeating warmup, newest first; the last <see cref="MaxListedRounds"/>.</summary>
    public ImmutableList<WarmupRound> Rounds { get; private set; } = [];

    /// <summary>Why the warmup failed; null otherwise.</summary>
    public string? Error { get; private set; }

    public bool StopRequested => _stop.IsCancellationRequested;
    public bool IsFinished => Status != RunStatus.Running;

    internal CancellationToken StopToken => _stop.Token;
    internal Task Completion { get; set; } = Task.CompletedTask;

    public async Task StopAsync()
    {
        if (_stop.IsCancellationRequested)
            return;

        await _stop.CancelAsync();
        _changed();
    }

    internal void Publish(WarmupReport report)
    {
        Report = report;
        _changed();
    }

    internal void AddRound(WarmupRound round)
    {
        var rounds = Rounds.Insert(0, round);
        Rounds = rounds.Count > MaxListedRounds ? rounds.RemoveAt(MaxListedRounds) : rounds;

        // A round whose sitemap could not be read has no report; the previous round's stays.
        if (round.Report is not null)
            Report = round.Report;

        _changed();
    }

    internal void Finish(RunStatus status, WarmupReport? report, string? error)
    {
        Report = report;
        Error = error;
        Status = status;
        _changed();
    }
}
