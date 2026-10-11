using System.Net;
using System.Net.Quic;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using LoadTester.Core.Engine;
using LoadTester.Core.Metrics;
using LoadTester.Core.Scenarios;
using LoadTester.Core.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;

namespace LoadTester.Core.Tests;

public class HttpVersionTests
{
    private static ScenarioDefinition Scenario(HttpProtocolVersion? version, string baseUrl = "https://example.test", string url = "/products") => new()
    {
        BaseUrl = baseUrl,
        Load = new LoadProfile { Stages = [new LoadStage(1, 1)] },
        Settings = new RequestSettings { HttpVersion = version },
        Steps = [new StepDefinition { Url = url }],
    };

    private static HttpRequestMessage CreateRequest(ScenarioDefinition definition)
    {
        var scenario = CompiledScenario.Compile(definition);
        using var user = new VirtualUser(1, () => new HttpMessageInvoker(new SocketsHttpHandler()), cookies: null);
        user.BeginIteration(scenario);
        return scenario.Steps[0].CreateRequest(user, scenario);
    }

    [Fact]
    public void WithoutAChoiceRequestsKeepTheDotNetDefault()
    {
        var request = CreateRequest(Scenario(null));

        Assert.Null(new RequestSettings().HttpVersion);
        Assert.Equal(HttpVersion.Version11, request.Version);
        Assert.Equal(HttpVersionPolicy.RequestVersionOrLower, request.VersionPolicy);
    }

    [Fact]
    public void ChosenVersionIsRequiredExactly()
    {
        var request = CreateRequest(Scenario(HttpProtocolVersion.Http2));

        Assert.Equal(HttpVersion.Version20, request.Version);
        Assert.Equal(HttpVersionPolicy.RequestVersionExact, request.VersionPolicy);
    }

    [Fact]
    public void Http3NeedsHttps()
    {
        var plainBaseUrl = ScenarioValidator.Validate(Scenario(HttpProtocolVersion.Http3, baseUrl: "http://example.test"));
        var plainStepUrl = ScenarioValidator.Validate(Scenario(HttpProtocolVersion.Http3, url: "HTTP://other.test/products"));
        var https = ScenarioValidator.Validate(Scenario(HttpProtocolVersion.Http3));

        Assert.Contains(plainBaseUrl, e => e.StartsWith("Temel URL: HTTP/3"));
        Assert.Contains(plainStepUrl, e => e.StartsWith("Adım 1: HTTP/3"));
        Assert.DoesNotContain(https, e => e.Contains("https://"));
    }

    [Fact]
    public void Http2AcceptsPlainHttp()
    {
        Assert.Empty(ScenarioValidator.Validate(Scenario(HttpProtocolVersion.Http2, baseUrl: "http://example.test")));
    }

    [Fact]
    public void StoredScenariosWithoutTheSettingUseTheDefault()
    {
        var stored = LoadTesterJson.Deserialize<ScenarioDefinition>("""{"settings":{"timeoutSeconds":30,"cacheBusting":false}}""");
        var json = LoadTesterJson.Serialize(Scenario(HttpProtocolVersion.Http2));

        Assert.Null(stored.Settings.HttpVersion);
        Assert.Contains("\"httpVersion\":\"Http2\"", json);
        Assert.Equal(HttpProtocolVersion.Http2, LoadTesterJson.Deserialize<ScenarioDefinition>(json).Settings.HttpVersion);
    }
}

/// <summary>Runs the engine against Kestrel endpoints that speak different HTTP versions.</summary>
public sealed class HttpVersionEngineTests : IAsyncLifetime
{
    private WebApplication _target = null!;
    private ListenOptions _http1 = null!, _h2c = null!, _tls = null!, _tlsHttp1 = null!;

    private string Http1Url => $"http://127.0.0.1:{_http1.IPEndPoint!.Port}";
    private string H2cUrl => $"http://127.0.0.1:{_h2c.IPEndPoint!.Port}";
    private string TlsUrl => $"https://127.0.0.1:{_tls.IPEndPoint!.Port}";
    private string TlsHttp1Url => $"https://127.0.0.1:{_tlsHttp1.IPEndPoint!.Port}";

    public async Task InitializeAsync()
    {
        var certificate = CreateCertificate();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrelHttpsConfiguration();
        builder.WebHost.UseQuic();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Listen(IPAddress.Loopback, 0, o => (_http1 = o).Protocols = HttpProtocols.Http1);
            kestrel.Listen(IPAddress.Loopback, 0, o => (_h2c = o).Protocols = HttpProtocols.Http2);
            kestrel.Listen(IPAddress.Loopback, FreePort(), o =>
            {
                (_tls = o).Protocols = HttpProtocols.Http1AndHttp2AndHttp3;
                o.UseHttps(certificate);
            });
            kestrel.Listen(IPAddress.Loopback, 0, o =>
            {
                (_tlsHttp1 = o).Protocols = HttpProtocols.Http1;
                o.UseHttps(certificate);
            });
        });
        _target = builder.Build();
        _target.MapGet("/protocol", (HttpContext context) => Results.Text(context.Request.Protocol));

        await _target.StartAsync();
    }

    public async Task DisposeAsync() => await _target.DisposeAsync();

    private static async Task<RunReport> RunAsync(string baseUrl, HttpProtocolVersion? version, string? expectedProtocol = null)
    {
        var scenario = new ScenarioDefinition
        {
            BaseUrl = baseUrl,
            Load = new LoadProfile { StartTarget = 1, Stages = [new LoadStage(1, 1)] },
            Settings = new RequestSettings { HttpVersion = version, IgnoreCertificateErrors = true },
            Steps = [new StepDefinition { Url = "/protocol", ExpectedBodyText = expectedProtocol }],
        };

        return (await new LoadTestEngine(scenario).RunAsync()).Report;
    }

    [Fact]
    public async Task WithoutAChoice_RequestsUseHttp11()
    {
        var report = await RunAsync(TlsUrl, null, "HTTP/1.1");

        Assert.True(report.Requests > 0);
        Assert.Empty(report.Errors);
    }

    [Fact]
    public async Task Http2_IsNegotiatedOverTls()
    {
        var report = await RunAsync(TlsUrl, HttpProtocolVersion.Http2, "HTTP/2");

        Assert.True(report.Requests > 0);
        Assert.Empty(report.Errors);
        Assert.True(report.BytesSent > 0 && report.BytesReceived > 0);
    }

    [Fact]
    public async Task Http2_OverPlainHttpUsesPriorKnowledge()
    {
        var report = await RunAsync(H2cUrl, HttpProtocolVersion.Http2, "HTTP/2");

        Assert.True(report.Requests > 0);
        Assert.Empty(report.Errors);
    }

    [QuicFact]
    public async Task Http3_RunsOverQuic()
    {
        var report = await RunAsync(TlsUrl, HttpProtocolVersion.Http3, "HTTP/3");

        Assert.True(report.Requests > 0);
        Assert.Empty(report.Errors);
    }

    [QuicFact]
    public async Task Http3_EveryFailedRequestIsARealAttempt()
    {
        // Nothing listens on UDP there. Without new connections after a failure, .NET would fail the following
        // requests without sending them ("HTTP sürümü kullanılamadı").
        var report = await RunAsync(TlsHttp1Url, HttpProtocolVersion.Http3);

        Assert.True(report.Requests > 1 && report.Failures == report.Requests, $"{report.Failures}/{report.Requests} failed");
        Assert.All(report.Errors, e => Assert.StartsWith("Bağlantı kurulamadı", e.Message));
    }

    [Fact]
    public async Task Http2_FailsInsteadOfFallingBackToHttp11()
    {
        var plain = await RunAsync(Http1Url, HttpProtocolVersion.Http2);
        var tls = await RunAsync(TlsHttp1Url, HttpProtocolVersion.Http2);

        // .NET words a server that answers in HTTP/1.1 in several ways, depending on timing.
        Assert.True(plain.Requests > 0 && plain.Failures == plain.Requests);
        Assert.All(plain.Errors, e => Assert.Contains("HTTP/2", e.Message));
        Assert.True(tls.Requests > 0 && tls.Failures == tls.Requests);
        Assert.All(tls.Errors, e =>
        {
            Assert.StartsWith("TLS/SSL hatası", e.Message);
            Assert.DoesNotContain("see inner exception", e.Message);
        });
    }

    /// <summary>Kestrel cannot choose the port itself when HTTP/3 (UDP) shares the endpoint with TCP.</summary>
    private static int FreePort()
    {
        using var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port)); // throws if UDP has it
        return port;
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());

        // Windows TLS cannot use the ephemeral key of a freshly created certificate; a PFX round trip persists it.
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null);
    }
}

/// <summary>Skipped where the operating system has no QUIC, which HTTP/3 needs.</summary>
public sealed class QuicFactAttribute : FactAttribute
{
    public QuicFactAttribute()
    {
        if (!((OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) && QuicConnection.IsSupported))
            Skip = "QUIC is not supported on this machine.";
    }
}
