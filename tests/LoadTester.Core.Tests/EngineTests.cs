using System.Collections.Concurrent;
using System.Diagnostics;
using LoadTester.Core.Engine;
using LoadTester.Core.Metrics;
using LoadTester.Core.Scenarios;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace LoadTester.Core.Tests;

/// <summary>Runs the engine against a real Kestrel server on a random local port.</summary>
public sealed class EngineTests : IAsyncLifetime
{
    private readonly ConcurrentQueue<(string Method, string? Buster, string CacheControl, string Pragma, string Page)> _cacheProbe = new();
    private WebApplication _target = null!;
    private string _baseUrl = "";

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _target = builder.Build();

        _target.MapPost("/login", (HttpContext context) =>
        {
            context.Response.Cookies.Append("sid", "s-1");
            return Results.Json(new { data = new { token = "t-123" } });
        });
        _target.MapGet("/me", (HttpContext context) =>
            context.Request.Headers.Authorization == "Bearer t-123" && context.Request.Cookies["sid"] == "s-1"
                ? Results.Text("ok")
                : Results.StatusCode(401));
        _target.MapGet("/fresh", (HttpContext context) =>
        {
            var seen = context.Request.Cookies.ContainsKey("seen");
            context.Response.Cookies.Append("seen", "1");
            return Results.Text(seen ? "stale" : "fresh");
        });
        _target.MapGet("/echo", (HttpContext context) => Results.Text(context.Request.Headers["X-Value"].ToString()));
        _target.MapGet("/status/{code:int}", (int code) => Results.StatusCode(code));
        _target.MapGet("/slow", async () =>
        {
            await Task.Delay(2000);
            return Results.Text("late");
        });
        _target.MapMethods("/cache", ["GET", "POST"], (HttpContext context) =>
        {
            var request = context.Request;
            _cacheProbe.Enqueue((request.Method, request.Query["_cb"].FirstOrDefault(), request.Headers.CacheControl.ToString(),
                request.Headers.Pragma.ToString(), request.Query["page"].ToString()));
            return Results.Text("ok");
        });

        await _target.StartAsync();
        _baseUrl = _target.Urls.First();
    }

    public async Task DisposeAsync() => await _target.DisposeAsync();

    private ScenarioDefinition Scenario(int virtualUsers, int seconds, params StepDefinition[] steps) => new()
    {
        BaseUrl = _baseUrl,
        Load = new LoadProfile { StartTarget = virtualUsers, Stages = [new LoadStage(seconds, virtualUsers)] },
        Steps = [.. steps],
    };

    [Fact]
    public async Task VirtualUsers_RunMultiStepFlowWithExtractionAndCookies()
    {
        var scenario = Scenario(3, 2,
            new StepDefinition
            {
                Name = "Giriş",
                Method = "POST",
                Url = "/login",
                Body = "{\"user\": \"u{{$vu}}\"}",
                Extractors = [new Extractor { Variable = "token", Source = ExtractorSource.JsonPath, Expression = "$.data.token" }],
            },
            new StepDefinition
            {
                Name = "Profil",
                Url = "/me",
                Headers = [new NameValue("Authorization", "Bearer {{token}}")],
                ExpectedBodyText = "ok",
            });

        var progress = 0;
        var result = await new LoadTestEngine(scenario).RunAsync((_, _) => progress++);
        var report = result.Report;

        Assert.False(result.Cancelled);
        Assert.Empty(report.Errors);
        Assert.True(report.Requests > 10, $"only {report.Requests} requests");
        Assert.Equal(report.Steps[0].Requests, report.Steps[1].Requests, tolerance: 3);
        Assert.Equal(3, report.PeakVirtualUsers);
        Assert.True(report.Iterations > 3);
        Assert.True(report.BytesSent > 0 && report.BytesReceived > 0);
        Assert.NotNull(report.Latency);
        Assert.Contains(report.Steps[1].StatusCodes, s => s.Code == 200);
        Assert.True(progress >= 1);
        Assert.NotEmpty(result.Timeline);
    }

    [Fact]
    public async Task Cookies_PersistWithinAnIterationAndResetBetweenIterations()
    {
        var scenario = Scenario(1, 2,
            new StepDefinition { Url = "/fresh", ExpectedBodyText = "fresh" },
            new StepDefinition { Url = "/fresh", ExpectedBodyText = "stale" });

        var report = (await new LoadTestEngine(scenario).RunAsync()).Report;

        Assert.True(report.Iterations > 2);
        Assert.Empty(report.Errors);
    }

    [Fact]
    public async Task Variables_AreRenderedIntoHeaders()
    {
        var scenario = Scenario(1, 1,
            new StepDefinition { Url = "/echo", Headers = [new NameValue("X-Value", "{{name}}-{{$vu}}")], ExpectedBodyText = "abc-1" });
        scenario.Variables = [new NameValue("name", "abc")];

        var report = (await new LoadTestEngine(scenario).RunAsync()).Report;

        Assert.True(report.Requests > 0);
        Assert.Empty(report.Errors);
    }

    [Fact]
    public async Task ArrivalRate_StartsIterationsAtTheConfiguredRate()
    {
        var scenario = Scenario(0, 2, new StepDefinition { Url = "/status/204" });
        scenario.Load = new LoadProfile
        {
            Mode = LoadMode.ArrivalRate,
            StartTarget = 50,
            Stages = [new LoadStage(2, 50)],
            MaxVirtualUsers = 100,
        };

        var report = (await new LoadTestEngine(scenario).RunAsync()).Report;

        Assert.InRange(report.Iterations, 90, 101);
        Assert.Equal(report.Iterations, report.Requests);
        Assert.Equal(0, report.DroppedIterations);
    }

    [Fact]
    public async Task UnexpectedStatus_IsAFailureWithReason()
    {
        var report = (await new LoadTestEngine(Scenario(1, 1, new StepDefinition { Url = "/status/500" })).RunAsync()).Report;

        Assert.True(report.Requests > 0);
        Assert.Equal(report.Requests, report.Failures);
        var error = Assert.Single(report.Errors);
        Assert.Equal("HTTP 500", error.Message);
        Assert.Equal(0, error.StepIndex);
        Assert.Equal(500, Assert.Single(report.Steps[0].StatusCodes).Code);
    }

    [Fact]
    public async Task SlowResponses_TimeOut()
    {
        var scenario = Scenario(1, 2, new StepDefinition { Url = "/slow" });
        scenario.Settings.TimeoutSeconds = 1;

        var report = (await new LoadTestEngine(scenario).RunAsync()).Report;

        Assert.Contains(report.Errors, e => e.Message == "Zaman aşımı (1 sn)");
    }

    [Fact]
    public async Task Cancellation_StopsPromptlyWithoutCountingAbortedRequests()
    {
        var scenario = Scenario(2, 60, new StepDefinition { Url = "/slow" });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var stopwatch = Stopwatch.StartNew();

        var result = await new LoadTestEngine(scenario).RunAsync(cancellationToken: cancellation.Token);

        Assert.True(result.Cancelled);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"took {stopwatch.Elapsed}");
        Assert.Equal(0, result.Report.Requests);
    }

    [Fact]
    public async Task UnreachableHost_IsReportedAsConnectionError()
    {
        var scenario = Scenario(1, 1, new StepDefinition { Url = "http://127.0.0.1:1/" });

        var report = (await new LoadTestEngine(scenario).RunAsync()).Report;

        Assert.True(report.Failures > 0);
        Assert.StartsWith("Bağlantı kurulamadı", report.Errors[0].Message);
    }

    [Fact]
    public async Task CacheBusting_ReachesTheServerOnGetRequestsOnly()
    {
        var scenario = Scenario(2, 1,
            new StepDefinition { Url = "/cache?page=3" },
            new StepDefinition { Method = "POST", Url = "/cache", Body = "{}" });
        scenario.Settings.CacheBusting = true;

        var report = (await new LoadTestEngine(scenario).RunAsync()).Report;
        var gets = _cacheProbe.Where(r => r.Method == "GET").ToList();
        var posts = _cacheProbe.Where(r => r.Method == "POST").ToList();

        Assert.Empty(report.Errors);
        Assert.True(gets.Count > 5 && posts.Count > 5, $"{gets.Count} GET, {posts.Count} POST");
        Assert.Equal(gets.Count, gets.Select(r => r.Buster).Distinct().Count());
        Assert.All(gets, r =>
        {
            Assert.NotNull(r.Buster);
            Assert.Equal("no-cache", r.CacheControl);
            Assert.Equal("no-cache", r.Pragma);
            Assert.Equal("3", r.Page);
        });
        Assert.All(posts, r =>
        {
            Assert.Null(r.Buster);
            Assert.Equal("", r.CacheControl);
        });
    }

    [Fact]
    public async Task InvalidScenario_Throws()
    {
        await Assert.ThrowsAsync<ScenarioValidationException>(() => new LoadTestEngine(new ScenarioDefinition()).RunAsync());
    }
}
