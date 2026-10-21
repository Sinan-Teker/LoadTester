using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using LoadTester.Core.Metrics;
using LoadTester.Core.Scenarios;

namespace LoadTester.Core.Engine;

/// <summary>
/// Runs a scenario: starts and stops virtual users according to the load profile and aggregates the results.
/// </summary>
public sealed class LoadTestEngine
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(1);

    private readonly ScenarioDefinition _definition;
    private readonly Func<HttpMessageHandler>? _handlerFactory;

    /// <param name="handlerFactory">
    /// Optional handler for each virtual user (used by tests). By default every user gets its own
    /// <see cref="SocketsHttpHandler"/>, so connections and cookies are per user like in a browser.
    /// </param>
    public LoadTestEngine(ScenarioDefinition definition, Func<HttpMessageHandler>? handlerFactory = null)
    {
        _definition = definition;
        _handlerFactory = handlerFactory;
    }

    /// <summary>
    /// Runs until the load profile ends or <paramref name="cancellationToken"/> is cancelled.
    /// <paramref name="onProgress"/> receives the cumulative report and the newest sample about once a second.
    /// </summary>
    /// <exception cref="ScenarioValidationException">The scenario is not runnable.</exception>
    public async Task<LoadTestResult> RunAsync(Action<RunReport, TimePoint>? onProgress = null, CancellationToken cancellationToken = default)
    {
        var scenario = CompiledScenario.Compile(_definition);
        var metrics = new MetricsCollector(scenario.Steps.Select(s => (s.Name, s.Method.Method, s.UrlText)));
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var finish = new CancellationTokenSource();
        var run = new RunContext(scenario, metrics, abort, finish, _handlerFactory);
        var timeline = new List<TimePoint>();
        var lastSample = TimeSpan.Zero;

        var load = Task.Run(() => scenario.Load.Mode == LoadMode.ArrivalRate
            ? RunArrivalRateAsync(run)
            : RunVirtualUsersAsync(run));

        using (var timer = new PeriodicTimer(SampleInterval))
        {
            while (await Task.WhenAny(timer.WaitForNextTickAsync().AsTask(), load).ConfigureAwait(false) != load)
                Sample(final: false);
        }

        await load.ConfigureAwait(false);
        var report = Sample(final: true);

        return new LoadTestResult
        {
            Report = report,
            Timeline = timeline,
            Cancelled = cancellationToken.IsCancellationRequested,
        };

        RunReport Sample(bool final)
        {
            var now = run.Elapsed;
            var interval = metrics.TakeInterval();
            var current = metrics.BuildReport(now.TotalSeconds, run.ActiveVirtualUsers, run.PeakVirtualUsers);

            // A sliver of an interval at the very end would show up as a misleading spike, so it only counts in the totals.
            var seconds = (now - lastSample).TotalSeconds;
            if (final && seconds < 0.5)
                return current;

            lastSample = now;
            var point = new TimePoint
            {
                Seconds = Math.Round(now.TotalSeconds, 2),
                VirtualUsers = run.ActiveVirtualUsers,
                RequestsPerSecond = Math.Round(interval.Requests / seconds, 2),
                FailuresPerSecond = Math.Round(interval.Failures / seconds, 2),
                P50Ms = Round(interval.P50Ms),
                P95Ms = Round(interval.P95Ms),
                P99Ms = Round(interval.P99Ms),
            };
            timeline.Add(point);

            try
            {
                onProgress?.Invoke(current, point);
            }
            catch
            {
                // An observer failing must not take the run down.
            }

            return current;
        }
    }

    /// <summary>Closed model: keeps the number of looping virtual users at the profile's target.</summary>
    private static async Task RunVirtualUsersAsync(RunContext run)
    {
        var profile = run.Scenario.Load;
        var running = new Stack<CancellationTokenSource>(); // stop signals; the most recently started user is on top
        var nextId = 0;

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        try
        {
            do
            {
                var elapsed = run.Elapsed.TotalSeconds;
                if (elapsed >= profile.TotalSeconds)
                    break;

                var target = profile.VirtualUsersAt(elapsed);
                while (running.Count < target)
                {
                    var stop = new CancellationTokenSource();
                    running.Push(stop);
                    run.UserStarted();
                    _ = RunVirtualUserAsync(run, ++nextId, stop.Token);
                }

                while (running.Count > target)
                    running.Pop().Cancel();
            }
            while (await timer.WaitForNextTickAsync(run.Abort.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (run.Abort.IsCancellationRequested)
        {
        }

        while (running.Count > 0)
            running.Pop().Cancel();

        await run.DrainAsync().ConfigureAwait(false);
    }

    private static async Task RunVirtualUserAsync(RunContext run, int id, CancellationToken stop)
    {
        try
        {
            await Task.Yield();
            using var user = run.CreateUser(id);
            while (!stop.IsCancellationRequested && !run.Abort.IsCancellationRequested)
            {
                await RunIterationAsync(run, user, stop, pauseAfterLastStep: true).ConfigureAwait(false);

                // Keeps a user whose iterations fail synchronously from monopolizing a thread-pool thread.
                await Task.Yield();
            }
        }
        catch (Exception ex)
        {
            run.Metrics.RecordEngineError(ex);
        }
        finally
        {
            run.UserFinished();
        }
    }

    /// <summary>Open model: starts iterations at the profile's rate, reusing idle virtual users.</summary>
    private static async Task RunArrivalRateAsync(RunContext run)
    {
        var profile = run.Scenario.Load;
        var idle = new ConcurrentBag<VirtualUser>();
        var created = new List<VirtualUser>();
        long started = 0;

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(5));
        try
        {
            do
            {
                var elapsed = run.Elapsed.TotalSeconds;
                if (elapsed >= profile.TotalSeconds)
                    break;

                for (var due = (long)profile.ArrivalsUntil(elapsed); started < due; started++)
                {
                    if (run.ActiveVirtualUsers >= profile.MaxVirtualUsers)
                    {
                        run.Metrics.RecordDroppedIteration();
                        continue;
                    }

                    if (!idle.TryTake(out var user))
                    {
                        user = run.CreateUser(created.Count + 1);
                        created.Add(user);
                    }

                    run.UserStarted();
                    _ = RunPooledIterationAsync(run, user, idle);
                }
            }
            while (await timer.WaitForNextTickAsync(run.Abort.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (run.Abort.IsCancellationRequested)
        {
        }

        await run.Finish.CancelAsync().ConfigureAwait(false);
        await run.DrainAsync().ConfigureAwait(false);
        foreach (var user in created)
            user.Dispose();
    }

    private static async Task RunPooledIterationAsync(RunContext run, VirtualUser user, ConcurrentBag<VirtualUser> idle)
    {
        try
        {
            await Task.Yield();
            await RunIterationAsync(run, user, run.Finish.Token, pauseAfterLastStep: false).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            run.Metrics.RecordEngineError(ex);
        }
        finally
        {
            idle.Add(user);
            run.UserFinished();
        }
    }

    /// <param name="stop">Soft stop: the iteration ends at the next step boundary.</param>
    private static async Task RunIterationAsync(RunContext run, VirtualUser user, CancellationToken stop, bool pauseAfterLastStep)
    {
        var scenario = run.Scenario;
        var steps = scenario.Steps;
        user.BeginIteration(scenario);

        for (var i = 0; i < steps.Length; i++)
        {
            if (i > 0 && stop.IsCancellationRequested)
                return;

            await ExecuteStepAsync(run, user, steps[i]).ConfigureAwait(false);
            if (run.Abort.IsCancellationRequested)
                return;

            if (pauseAfterLastStep || i < steps.Length - 1)
            {
                var pause = scenario.NextThinkTime();
                if (pause > TimeSpan.Zero)
                    await Task.Delay(pause, stop).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }

        run.Metrics.RecordIteration();
    }

    private static async Task ExecuteStepAsync(RunContext run, VirtualUser user, CompiledStep step)
    {
        HttpRequestMessage request;
        try
        {
            request = step.CreateRequest(user, run.Scenario);
        }
        catch (Exception ex)
        {
            run.Metrics.RecordRequest(step.Index, null, 0, Shorten("İstek oluşturulamadı: " + ex.Message));
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(run.Abort.Token);
        timeout.CancelAfter(run.Scenario.Timeout);
        var started = Stopwatch.GetTimestamp();
        var status = 0;
        string error;
        try
        {
            using var response = await user.Client.SendAsync(request, timeout.Token).ConfigureAwait(false);
            status = (int)response.StatusCode;

            string? body = null;
            if (step.NeedsBody)
                body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            else
                await response.Content.CopyToAsync(Stream.Null, timeout.Token).ConfigureAwait(false);

            var elapsed = Stopwatch.GetElapsedTime(started);
            run.Metrics.RecordRequest(step.Index, ToMicroseconds(elapsed), status, step.Evaluate(response, body, user));
            return;
        }
        catch (OperationCanceledException) when (run.Abort.IsCancellationRequested)
        {
            return; // stopped by the user or after the grace period: not a result
        }
        catch (OperationCanceledException)
        {
            error = $"Zaman aşımı ({run.Scenario.Settings.TimeoutSeconds} sn)";
        }
        catch (HttpRequestException ex)
        {
            error = Describe(ex);
        }
        catch (Exception ex)
        {
            error = Shorten(ex.Message);
        }
        finally
        {
            request.Dispose();
        }

        run.Metrics.RecordRequest(step.Index, ToMicroseconds(Stopwatch.GetElapsedTime(started)), status, error);

        // After a failed QUIC connection .NET refuses HTTP/3 to that server for minutes, failing the user's
        // following requests without sending them. New connections make the next request a real attempt.
        if (run.Scenario.Settings.HttpVersion == HttpProtocolVersion.Http3)
            user.RenewClient();
    }

    internal static string Describe(HttpRequestException exception)
    {
        var message = exception.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError => "Ad çözümlenemedi: " + exception.Message,
            HttpRequestError.ConnectionError => "Bağlantı kurulamadı: " + exception.Message,
            // The innermost error names the cause, e.g. no common application protocol when HTTP/2 is refused.
            HttpRequestError.SecureConnectionError => "TLS/SSL hatası: " + exception.GetBaseException().Message,
            HttpRequestError.ResponseEnded => "Sunucu bağlantıyı kapattı",
            HttpRequestError.InvalidResponse => "Geçersiz yanıt: " + exception.Message,
            HttpRequestError.VersionNegotiationError => "HTTP sürümü kullanılamadı: " + exception.Message,
            HttpRequestError.HttpProtocolError => "Protokol hatası: " + exception.Message,
            _ => exception.Message,
        };
        return Shorten(message);
    }

    internal static string Shorten(string message) => message.Length <= 200 ? message : message[..200] + "…";

    private static long ToMicroseconds(TimeSpan elapsed) => elapsed.Ticks / TimeSpan.TicksPerMicrosecond;

    private static double? Round(double? value) => value is null ? null : Math.Round(value.Value, 2);

    private sealed class RunContext(
        CompiledScenario scenario,
        MetricsCollector metrics,
        CancellationTokenSource abort,
        CancellationTokenSource finish,
        Func<HttpMessageHandler>? handlerFactory)
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private int _active;
        private int _peak;

        public CompiledScenario Scenario { get; } = scenario;
        public MetricsCollector Metrics { get; } = metrics;

        /// <summary>Hard stop: also cancels requests in flight. Linked to the caller's token.</summary>
        public CancellationTokenSource Abort { get; } = abort;

        /// <summary>Soft stop for arrival-rate iterations at the end of the profile.</summary>
        public CancellationTokenSource Finish { get; } = finish;

        public TimeSpan Elapsed => _clock.Elapsed;
        public int ActiveVirtualUsers => Volatile.Read(ref _active);
        public int PeakVirtualUsers => Volatile.Read(ref _peak);

        public void UserStarted()
        {
            var active = Interlocked.Increment(ref _active);
            int peak;
            while (active > (peak = Volatile.Read(ref _peak)) && Interlocked.CompareExchange(ref _peak, active, peak) != peak)
            {
            }
        }

        public void UserFinished() => Interlocked.Decrement(ref _active);

        public VirtualUser CreateUser(int id)
        {
            if (handlerFactory is not null)
                return new VirtualUser(id, () => new HttpMessageInvoker(handlerFactory(), disposeHandler: true), cookies: null);

            var cookies = Scenario.Settings.UseCookies ? new CookieContainer() : null;
            return new VirtualUser(id, () => new HttpMessageInvoker(CreateHandler(cookies), disposeHandler: true), cookies);
        }

        private SocketsHttpHandler CreateHandler(CookieContainer? cookies)
        {
            var settings = Scenario.Settings;
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = settings.FollowRedirects,
                AutomaticDecompression = DecompressionMethods.All,
                ConnectTimeout = Scenario.Timeout,
                ConnectCallback = Metrics.Traffic.ConnectAsync,
                UseCookies = cookies is not null,
            };
            if (cookies is not null)
                handler.CookieContainer = cookies;
            if (settings.IgnoreCertificateErrors)
                handler.SslOptions.RemoteCertificateValidationCallback = static (_, _, _, _) => true;

            return handler;
        }

        /// <summary>Waits for running users to finish their current step; cancels what is left after a grace period.</summary>
        public async Task DrainAsync()
        {
            var deadline = Elapsed + TimeSpan.FromSeconds(Math.Min(30, Scenario.Settings.TimeoutSeconds));
            while (ActiveVirtualUsers > 0)
            {
                if (!Abort.IsCancellationRequested && Elapsed >= deadline)
                    await Abort.CancelAsync().ConfigureAwait(false);

                await Task.Delay(20).ConfigureAwait(false);
            }
        }
    }
}
