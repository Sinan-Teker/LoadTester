using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using LoadTester.Core.Engine;
using LoadTester.Core.Metrics;

namespace LoadTester.Core.Warmup;

/// <summary>
/// Reads a sitemap and requests every page in it once, reading each response to the end,
/// so server, application and CDN caches are filled before a load test.
/// </summary>
public sealed class SitemapWarmup(WarmupDefinition definition)
{
    /// <summary>User-facing (Turkish) reason for a warmup whose sitemap had no usable address.</summary>
    public const string NoPagesError = "Sitemap'te ısıtılacak adres bulunamadı.";

    /// <summary>What the load test sends, so the warmup fills the same compressed variant in caches.</summary>
    private const string DefaultAcceptEncoding = "gzip, deflate, br";

    private const int MaxSitemapDownloads = 4;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>Pause between rounds, so a small sitemap does not turn into a tight loop.</summary>
    internal TimeSpan RoundPause { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Pause after a failed round; the site may be unavailable for a while, e.g. under the load test.</summary>
    internal TimeSpan RetryPause { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Requests every page once; runs until all were requested or <paramref name="cancellationToken"/> is cancelled.
    /// <paramref name="onProgress"/> receives a report about twice a second.
    /// </summary>
    /// <exception cref="ArgumentException">The definition is not valid (see <see cref="WarmupValidator"/>).</exception>
    /// <exception cref="SitemapException">The sitemap could not be read.</exception>
    public Task<WarmupReport> RunAsync(Action<WarmupReport>? onProgress = null, CancellationToken cancellationToken = default)
    {
        Validate();
        return RunRoundAsync(1, onProgress, cancellationToken);
    }

    /// <summary>
    /// Warms up round after round until <paramref name="cancellationToken"/> is cancelled. Every round reads the sitemap
    /// again, so pages added in the meantime are warmed too. A failed round is reported and does not end the warmup.
    /// </summary>
    /// <param name="onProgress">Receives a report of the running round about twice a second.</param>
    /// <param name="onRound">Receives every finished round, failed ones included.</param>
    /// <returns>The report of the round that was stopped; null when the warmup was stopped between rounds.</returns>
    /// <exception cref="ArgumentException">The definition is not valid (see <see cref="WarmupValidator"/>).</exception>
    public async Task<WarmupReport?> RepeatUntilCancelledAsync(
        Action<WarmupReport>? onProgress = null, Action<WarmupRound>? onRound = null, CancellationToken cancellationToken = default)
    {
        Validate();
        for (var number = 1; ; number++)
        {
            var startedAt = DateTime.UtcNow;
            WarmupReport? report = null;
            string? error;
            try
            {
                report = await RunRoundAsync(number, onProgress, cancellationToken).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                    return report;

                error = report.TotalUrls == 0 ? NoPagesError : null;
            }
            catch (SitemapException ex)
            {
                error = ex.Message;
            }

            try
            {
                onRound?.Invoke(new WarmupRound(number, startedAt, report, error));
            }
            catch
            {
                // An observer failing must not take the warmup down.
            }

            await Task.Delay(error is null ? RoundPause : RetryPause, cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (cancellationToken.IsCancellationRequested)
                return null;
        }
    }

    private void Validate()
    {
        var errors = WarmupValidator.Validate(definition);
        if (errors.Count > 0)
            throw new ArgumentException(string.Join(Environment.NewLine, errors), nameof(definition));
    }

    private async Task<WarmupReport> RunRoundAsync(int round, Action<WarmupReport>? onProgress, CancellationToken cancellationToken)
    {
        var progress = new WarmupProgress(round);
        var work = Task.Run(() => RunCoreAsync(progress, cancellationToken));

        using (var timer = new PeriodicTimer(ProgressInterval))
        {
            while (await Task.WhenAny(timer.WaitForNextTickAsync().AsTask(), work).ConfigureAwait(false) != work)
            {
                try
                {
                    onProgress?.Invoke(progress.Snapshot(finished: false));
                }
                catch
                {
                    // An observer failing must not take the warmup down.
                }
            }
        }

        await work.ConfigureAwait(false);
        return progress.Snapshot(finished: true);
    }

    private async Task RunCoreAsync(WarmupProgress progress, CancellationToken cancellationToken)
    {
        var headers = definition.Headers.Select(h => (Name: h.Name.Trim(), h.Value)).ToList();
        if (!headers.Any(h => h.Name.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)))
            headers.Add(("User-Agent", CompiledStep.DefaultUserAgent));

        // Sitemaps are decompressed by the handler, which asks for the encodings it can decode.
        var sitemapHeaders = headers.Where(h => !h.Name.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (!headers.Any(h => h.Name.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase)))
            headers.Add(("Accept-Encoding", DefaultAcceptEncoding));

        SitemapContents sitemap;
        using (var sitemapClient = CreateClient(followRedirects: true, DecompressionMethods.All))
        {
            var reader = new SitemapReader(sitemapClient, sitemapHeaders, definition.TimeoutSeconds, Math.Min(definition.Concurrency, MaxSitemapDownloads));
            try
            {
                sitemap = await reader.ReadAsync(SitemapReader.ResolveRoot(definition.SitemapUrl), progress.SitemapRead, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }

        progress.StartWarming(sitemap);

        // Pages are not decompressed: the body is only drained, and the Accept-Encoding header is sent as given.
        using var client = CreateClient(definition.FollowRedirects, DecompressionMethods.None);
        var pageHeaders = headers.ToArray();
        var delay = TimeSpan.FromMilliseconds(definition.DelayMs);
        var options = new ParallelOptions { MaxDegreeOfParallelism = definition.Concurrency, CancellationToken = cancellationToken };
        try
        {
            await Parallel.ForEachAsync(sitemap.Urls, options, async (url, token) =>
            {
                if (await WarmAsync(client, url, pageHeaders, token).ConfigureAwait(false) is { } result)
                    progress.Record(result);

                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, token).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    /// <returns>The outcome, or null when the warmup was stopped while the request was in flight.</returns>
    private async Task<WarmupUrlResult?> WarmAsync(HttpMessageInvoker client, string url, (string Name, string Value)[] headers, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(definition.TimeoutSeconds));
        var started = Stopwatch.GetTimestamp();
        var status = 0;
        string? error;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            foreach (var (name, value) in headers)
                request.Headers.TryAddWithoutValidation(name, value);

            using var response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
            status = (int)response.StatusCode;
            await response.Content.CopyToAsync(Stream.Null, timeout.Token).ConfigureAwait(false);
            error = status is >= 200 and < 400 ? null : $"HTTP {status}";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            error = $"Zaman aşımı ({definition.TimeoutSeconds} sn)";
        }
        catch (HttpRequestException ex)
        {
            error = LoadTestEngine.Describe(ex);
        }
        catch (Exception ex)
        {
            error = LoadTestEngine.Shorten(ex.Message);
        }

        return new WarmupUrlResult(url, status, Stopwatch.GetElapsedTime(started).TotalMilliseconds, error);
    }

    private HttpMessageInvoker CreateClient(bool followRedirects, DecompressionMethods decompression)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = followRedirects,
            AutomaticDecompression = decompression,
            ConnectTimeout = TimeSpan.FromSeconds(definition.TimeoutSeconds),
            UseCookies = false, // every request is a first visit; a Cookie header given by the user is sent as it is
        };
        if (definition.IgnoreCertificateErrors)
            handler.SslOptions.RemoteCertificateValidationCallback = static (_, _, _, _) => true;

        return new HttpMessageInvoker(handler, disposeHandler: true);
    }
}

/// <summary>Thread-safe state of a running warmup round; <see cref="Snapshot"/> copies it for observers.</summary>
internal sealed class WarmupProgress(int round)
{
    public const int MaxListedFailures = 500;
    public const int SlowestCount = 20;

    private readonly object _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly LatencyHistogram _latency = new();
    private readonly Dictionary<int, long> _statusCodes = [];
    private readonly List<WarmupUrlResult> _failures = [];
    private readonly PriorityQueue<WarmupUrlResult, double> _slowest = new(); // the fastest of them on top
    private WarmupPhase _phase = WarmupPhase.ReadingSitemaps;
    private TimeSpan _warmingStarted;
    private int _sitemaps;
    private int _urls;
    private int _completed;
    private int _failed;
    private string[] _warnings = [];

    public void SitemapRead(int sitemaps, int urls)
    {
        lock (_gate)
        {
            _sitemaps = sitemaps;
            _urls = urls;
        }
    }

    public void StartWarming(SitemapContents sitemap)
    {
        lock (_gate)
        {
            _phase = WarmupPhase.Warming;
            _warmingStarted = _clock.Elapsed;
            _sitemaps = sitemap.SitemapsRead;
            _urls = sitemap.Urls.Count;
            _warnings = [.. sitemap.Warnings];
        }
    }

    public void Record(WarmupUrlResult result)
    {
        lock (_gate)
        {
            _completed++;
            _latency.Record((long)(result.DurationMs * 1000));
            if (result.StatusCode > 0)
                CollectionsMarshal.GetValueRefOrAddDefault(_statusCodes, result.StatusCode, out _)++;

            if (result.Error is not null)
            {
                _failed++;
                if (_failures.Count < MaxListedFailures)
                    _failures.Add(result);
            }
            else if (_slowest.Count < SlowestCount)
            {
                _slowest.Enqueue(result, result.DurationMs);
            }
            else
            {
                _slowest.EnqueueDequeue(result, result.DurationMs);
            }
        }
    }

    public WarmupReport Snapshot(bool finished)
    {
        lock (_gate)
        {
            var elapsed = _clock.Elapsed;
            var warmingSeconds = (elapsed - _warmingStarted).TotalSeconds;
            return new WarmupReport
            {
                Round = round,
                Phase = finished ? WarmupPhase.Finished : _phase,
                ElapsedSeconds = elapsed.TotalSeconds,
                SitemapsRead = _sitemaps,
                TotalUrls = _urls,
                Completed = _completed,
                Failed = _failed,
                RequestsPerSecond = _phase == WarmupPhase.Warming && warmingSeconds > 0 ? _completed / warmingSeconds : 0,
                Latency = _latency.ToStats(),
                StatusCodes = _statusCodes.OrderBy(p => p.Key).Select(p => new StatusCount(p.Key, p.Value)).ToList(),
                Failures = [.. _failures],
                Slowest = _slowest.UnorderedItems.Select(i => i.Element).OrderByDescending(r => r.DurationMs).ToList(),
                Warnings = [.. _warnings],
            };
        }
    }
}
