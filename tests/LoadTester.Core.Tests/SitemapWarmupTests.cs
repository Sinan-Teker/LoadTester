using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using LoadTester.Core.Scenarios;
using LoadTester.Core.Warmup;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace LoadTester.Core.Tests;

public class SitemapParserTests
{
    private static (SitemapKind Kind, List<string> Locations) Parse(string text, int maxLocations = 1000) =>
        SitemapParser.Parse(Encoding.UTF8.GetBytes(text), maxLocations);

    internal static byte[] Gzip(byte[] content)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest))
            gzip.Write(content);
        return output.ToArray();
    }

    [Fact]
    public void UrlSetListsThePageLocations()
    {
        var (kind, locations) = Parse("""
            <?xml version="1.0" encoding="UTF-8"?>
            <?xml-stylesheet type="text/xsl" href="/sitemap.xsl"?>
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9" xmlns:image="http://www.google.com/schemas/sitemap-image/1.1">
              <url>
                <loc>
                  https://example.test/a?x=1&amp;y=2
                </loc>
                <lastmod>2026-10-01</lastmod>
                <image:image><image:loc>https://example.test/a.jpg</image:loc></image:image>
              </url>
              <url><loc><![CDATA[https://example.test/b]]></loc></url>
            </urlset>
            """);

        Assert.Equal(SitemapKind.Pages, kind);
        Assert.Equal(new[] { "https://example.test/a?x=1&y=2", "https://example.test/b" }, locations);
    }

    [Fact]
    public void SitemapIndexListsTheSitemapLocations()
    {
        var (kind, locations) = Parse("""
            <sitemapindex xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <sitemap><loc>https://example.test/pages.xml</loc><lastmod>2026-10-01</lastmod></sitemap>
              <sitemap><loc>https://example.test/posts.xml.gz</loc></sitemap>
            </sitemapindex>
            """);

        Assert.Equal(SitemapKind.Index, kind);
        Assert.Equal(new[] { "https://example.test/pages.xml", "https://example.test/posts.xml.gz" }, locations);
    }

    [Fact]
    public void GzippedSitemapsAreDecompressed()
    {
        var xml = Encoding.UTF8.GetBytes("<urlset><url><loc>https://example.test/a</loc></url></urlset>");

        var (_, locations) = SitemapParser.Parse(Gzip(xml), 1000);

        Assert.Equal(new[] { "https://example.test/a" }, locations);
    }

    [Fact]
    public void TextSitemapsListOneAddressPerLine()
    {
        var (kind, locations) = Parse("﻿https://example.test/a\r\n\r\n  https://example.test/b  \n");

        Assert.Equal(SitemapKind.Pages, kind);
        Assert.Equal(new[] { "https://example.test/a", "https://example.test/b" }, locations);
    }

    [Fact]
    public void LocationsAreCapped()
    {
        var xml = "<urlset>" + string.Concat(Enumerable.Range(0, 10).Select(i => $"<url><loc>https://example.test/{i}</loc></url>")) + "</urlset>";

        Assert.Equal(3, Parse(xml, maxLocations: 3).Locations.Count);
    }

    [Theory]
    [InlineData("<html><body>Bulunamadı</body></html>", "Sitemap değil: kök öğe <html>")]
    [InlineData("<urlset><url><loc>https://example.test/a</loc></url>", "Geçersiz XML")]
    public void OtherContentIsRejected(string content, string message)
    {
        var exception = Assert.Throws<FormatException>(() => Parse(content));

        Assert.StartsWith(message, exception.Message);
    }

    [Fact]
    public void DocumentTypeDefinitionsAreNotProcessed()
    {
        // Entities declared in a DTD would allow expansion attacks; the DTD is ignored, so using one fails.
        var exception = Assert.Throws<FormatException>(() => Parse("""
            <!DOCTYPE urlset [<!ENTITY a "https://example.test/a">]>
            <urlset><url><loc>&a;</loc></url></urlset>
            """));

        Assert.StartsWith("Geçersiz XML", exception.Message);
    }
}

public class WarmupValidatorTests
{
    [Fact]
    public void DefaultsAreValid()
    {
        Assert.Empty(WarmupValidator.Validate(new WarmupDefinition { SitemapUrl = "https://example.test" }));
    }

    [Fact]
    public void SitemapAddressIsRequired()
    {
        Assert.Contains("Sitemap adresi boş olamaz.", WarmupValidator.Validate(new WarmupDefinition { SitemapUrl = " " }));
    }

    [Fact]
    public void InvalidSettingsAreReported()
    {
        var errors = WarmupValidator.Validate(new WarmupDefinition
        {
            SitemapUrl = "ftp://example.test/sitemap.xml",
            Headers = [new NameValue("X Warmup", "1")],
            Concurrency = 0,
            TimeoutSeconds = 0,
            DelayMs = -1,
        });

        Assert.Equal(5, errors.Count);
        Assert.Contains(errors, e => e.StartsWith("Sitemap adresi, http://"));
        Assert.Contains(errors, e => e.Contains("\"X Warmup\""));
    }
}

/// <summary>Warms up a real Kestrel server on a random local port.</summary>
public sealed class SitemapWarmupTests : IAsyncLifetime
{
    private readonly ConcurrentQueue<(string Path, string Warmup, string UserAgent, string AcceptEncoding)> _requests = new();
    private WebApplication _target = null!;
    private string _baseUrl = "";

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _target = builder.Build();

        _target.Use(async (context, next) =>
        {
            var request = context.Request;
            _requests.Enqueue((request.Path + request.QueryString, request.Headers["X-Warmup"].ToString(),
                request.Headers.UserAgent.ToString(), request.Headers.AcceptEncoding.ToString()));
            await next(context);
        });

        _target.MapGet("/sitemap.xml", () => Xml(Index("/pages.xml", "/posts.xml.gz")));
        _target.MapGet("/pages.xml", () => Xml(UrlSet("/page/1", "/page/2", "/page/3")));
        _target.MapGet("/posts.xml.gz", () =>
            Results.Bytes(SitemapParserTests.Gzip(Encoding.UTF8.GetBytes(UrlSet("/page/3", "/post/1"))), "application/x-gzip"));
        _target.MapGet("/moved.xml", () => Results.Redirect("/mixed.xml", permanent: true));
        _target.MapGet("/mixed.xml", () => Xml(UrlSet("/status/404", "/page/1", "/status/500", "/redirect")));
        _target.MapGet("/partly-broken.xml", () => Xml(Index("/missing.xml", "/pages.xml")));
        _target.MapGet("/invalid.xml", () => Xml("<urlset><url><loc>mailto:info@example.test</loc></url><url><loc></loc></url></urlset>"));
        _target.MapGet("/slow.xml", () => Xml(UrlSet([.. Enumerable.Range(1, 10).Select(i => $"/slow?n={i}")])));
        _target.MapGet("/page/{id}", () => Results.Text("page"));
        _target.MapGet("/post/{id}", () => Results.Text("post"));
        _target.MapGet("/status/{code:int}", (int code) => Results.StatusCode(code));
        _target.MapGet("/redirect", () => Results.Redirect("/page/9"));
        _target.MapGet("/slow", async (HttpContext context) =>
        {
            await Task.Delay(2000, context.RequestAborted);
            return Results.Text("late");
        });

        await _target.StartAsync();
        _baseUrl = _target.Urls.First();
    }

    public async Task DisposeAsync() => await _target.DisposeAsync();

    private static WarmupDefinition Warmup(string sitemapUrl, params NameValue[] headers) => new()
    {
        SitemapUrl = sitemapUrl,
        Headers = [.. headers],
    };

    private static IResult Xml(string content) => Results.Text(content, "application/xml");

    private string UrlSet(params string[] paths) =>
        "<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">"
        + string.Concat(paths.Select(p => $"<url><loc>{_baseUrl}{p}</loc></url>"))
        + "</urlset>";

    private string Index(params string[] paths) =>
        "<sitemapindex xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">"
        + string.Concat(paths.Select(p => $"<sitemap><loc>{_baseUrl}{p}</loc></sitemap>"))
        + "</sitemapindex>";

    [Fact]
    public async Task SiteAddress_WarmsEveryPageOfTheSitemapIndexOnceWithTheHeaders()
    {
        var report = await new SitemapWarmup(Warmup(_baseUrl, new NameValue("X-Warmup", "1"))).RunAsync();
        var pages = _requests.Where(r => r.Path.StartsWith("/page/") || r.Path.StartsWith("/post/")).ToList();

        Assert.Equal(WarmupPhase.Finished, report.Phase);
        Assert.Equal(1, report.Round);
        Assert.Equal(3, report.SitemapsRead);
        Assert.Equal(4, report.TotalUrls); // /page/3 is listed in both sitemaps
        Assert.Equal(4, report.Completed);
        Assert.Equal(0, report.Failed);
        Assert.Empty(report.Warnings);
        Assert.Equal(new[] { "/page/1", "/page/2", "/page/3", "/post/1" }, pages.Select(r => r.Path).Order());
        Assert.Equal(7, _requests.Count);
        Assert.All(_requests, r =>
        {
            Assert.Equal("1", r.Warmup);
            Assert.Equal("LoadTester/1.0", r.UserAgent);
        });
        Assert.All(pages, r => Assert.Equal("gzip, deflate, br", r.AcceptEncoding));
        Assert.Equal(200, Assert.Single(report.StatusCodes).Code);
        Assert.Equal(4, report.Slowest.Count);
        Assert.NotNull(report.Latency);
        Assert.True(report.RequestsPerSecond > 0);
    }

    [Fact]
    public async Task HeadersGivenByTheUserWin()
    {
        var report = await new SitemapWarmup(Warmup($"{_baseUrl}/pages.xml",
            new NameValue("User-Agent", "Mobile"), new NameValue("Accept-Encoding", "gzip"))).RunAsync();

        Assert.Equal(3, report.Completed);
        Assert.All(_requests, r => Assert.Equal("Mobile", r.UserAgent));
        Assert.All(_requests.Where(r => r.Path.StartsWith("/page/")), r => Assert.Equal("gzip", r.AcceptEncoding));
    }

    [Fact]
    public async Task FailuresAreListed_AndOnlyTheSitemapIsReadThroughRedirectsWhenTheyAreOff()
    {
        var definition = Warmup($"{_baseUrl}/moved.xml");
        definition.FollowRedirects = false;

        var report = await new SitemapWarmup(definition).RunAsync();

        Assert.Equal(4, report.Completed);
        Assert.Equal(2, report.Failed);
        Assert.Equal(new[] { "HTTP 404", "HTTP 500" }, report.Failures.Select(f => f.Error).Order());
        Assert.Equal(new[] { 200, 302, 404, 500 }, report.StatusCodes.Select(s => s.Code));
        Assert.Equal(new[] { $"{_baseUrl}/page/1", $"{_baseUrl}/redirect" }, report.Slowest.Select(s => s.Url).Order());
        Assert.DoesNotContain(_requests, r => r.Path == "/page/9");
    }

    [Fact]
    public async Task UnreadableSitemap_Throws()
    {
        var exception = await Assert.ThrowsAsync<SitemapException>(() => new SitemapWarmup(Warmup($"{_baseUrl}/missing.xml")).RunAsync());

        Assert.Equal($"{_baseUrl}/missing.xml okunamadı: HTTP 404", exception.Message);
    }

    [Fact]
    public async Task UnreadableChildSitemaps_AreWarnings()
    {
        var report = await new SitemapWarmup(Warmup($"{_baseUrl}/partly-broken.xml")).RunAsync();

        Assert.Equal(3, report.SitemapsRead);
        Assert.Equal(3, report.Completed);
        Assert.Equal($"{_baseUrl}/missing.xml okunamadı: HTTP 404", Assert.Single(report.Warnings));
    }

    [Fact]
    public async Task InvalidAddresses_AreSkipped()
    {
        var report = await new SitemapWarmup(Warmup($"{_baseUrl}/invalid.xml")).RunAsync();

        Assert.Equal(0, report.TotalUrls);
        Assert.Equal(0, report.Completed);
        Assert.StartsWith("2 geçersiz adres atlandı", Assert.Single(report.Warnings));
    }

    [Fact]
    public async Task SlowPages_TimeOut()
    {
        var definition = Warmup($"{_baseUrl}/slow.xml");
        definition.TimeoutSeconds = 1;
        definition.Concurrency = 10;

        var report = await new SitemapWarmup(definition).RunAsync();

        Assert.Equal(10, report.Failed);
        Assert.All(report.Failures, f => Assert.Equal("Zaman aşımı (1 sn)", f.Error));
    }

    [Fact]
    public async Task RepeatUntilCancelled_ReadsTheSitemapAgainEveryRound()
    {
        var warmup = new SitemapWarmup(Warmup($"{_baseUrl}/pages.xml")) { RoundPause = TimeSpan.FromMilliseconds(50) };
        using var cancellation = new CancellationTokenSource();
        var rounds = new List<WarmupRound>();

        var stopped = await warmup.RepeatUntilCancelledAsync(onRound: round =>
        {
            rounds.Add(round);
            if (round.Number == 3)
                cancellation.Cancel();
        }, cancellationToken: cancellation.Token);

        Assert.Null(stopped); // stopped in the pause after the third round
        Assert.Equal(new[] { 1, 2, 3 }, rounds.Select(r => r.Number));
        Assert.All(rounds, r =>
        {
            Assert.Null(r.Error);
            Assert.Equal(r.Number, r.Report!.Round);
            Assert.Equal(3, r.Report.Completed);
        });
        Assert.Equal(3, _requests.Count(r => r.Path == "/pages.xml"));
        Assert.Equal(3, _requests.Count(r => r.Path == "/page/1"));
    }

    [Fact]
    public async Task RepeatUntilCancelled_KeepsGoingAfterRoundsWhoseSitemapCannotBeRead()
    {
        var warmup = new SitemapWarmup(Warmup($"{_baseUrl}/missing.xml")) { RetryPause = TimeSpan.FromMilliseconds(50) };
        using var cancellation = new CancellationTokenSource();
        var rounds = new List<WarmupRound>();

        await warmup.RepeatUntilCancelledAsync(onRound: round =>
        {
            rounds.Add(round);
            if (round.Number == 2)
                cancellation.Cancel();
        }, cancellationToken: cancellation.Token);

        Assert.Equal(2, rounds.Count);
        Assert.All(rounds, r =>
        {
            Assert.Null(r.Report);
            Assert.Equal($"{_baseUrl}/missing.xml okunamadı: HTTP 404", r.Error);
        });
    }

    [Fact]
    public async Task RepeatUntilCancelled_RoundsWithoutPagesFailWithTheReasons()
    {
        var warmup = new SitemapWarmup(Warmup($"{_baseUrl}/invalid.xml")) { RetryPause = TimeSpan.FromMilliseconds(50) };
        using var cancellation = new CancellationTokenSource();
        WarmupRound? first = null;

        await warmup.RepeatUntilCancelledAsync(onRound: round =>
        {
            first ??= round;
            cancellation.Cancel();
        }, cancellationToken: cancellation.Token);

        Assert.Equal(SitemapWarmup.NoPagesError, first!.Error);
        Assert.StartsWith("2 geçersiz adres atlandı", Assert.Single(first.Report!.Warnings));
    }

    [Fact]
    public async Task RepeatUntilCancelled_ReturnsTheRoundThatWasStopped()
    {
        var definition = Warmup($"{_baseUrl}/slow.xml");
        definition.Concurrency = 2;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var rounds = 0;

        var stopped = await new SitemapWarmup(definition).RepeatUntilCancelledAsync(onRound: _ => rounds++, cancellationToken: cancellation.Token);

        Assert.Equal(0, rounds);
        Assert.NotNull(stopped);
        Assert.Equal(1, stopped.Round);
        Assert.Equal(WarmupPhase.Finished, stopped.Phase);
        Assert.Equal(0, stopped.Completed);
    }

    [Fact]
    public async Task Cancellation_StopsPromptlyWithoutCountingAbortedRequests()
    {
        var definition = Warmup($"{_baseUrl}/slow.xml");
        definition.Concurrency = 2;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var progress = 0;
        var stopwatch = Stopwatch.StartNew();

        var report = await new SitemapWarmup(definition).RunAsync(_ => progress++, cancellation.Token);

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(4), $"took {stopwatch.Elapsed}");
        Assert.Equal(WarmupPhase.Finished, report.Phase);
        Assert.Equal(0, report.Completed);
        Assert.True(progress >= 1);
    }
}
