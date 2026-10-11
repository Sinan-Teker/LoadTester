using LoadTester.Core.Engine;
using LoadTester.Core.Scenarios;

namespace LoadTester.Core.Tests;

public class CacheBustingTests
{
    private static ScenarioDefinition Scenario(bool cacheBusting, params StepDefinition[] steps) => new()
    {
        BaseUrl = "http://example.test",
        Load = new LoadProfile { Stages = [new LoadStage(1, 1)] },
        Settings = new RequestSettings { CacheBusting = cacheBusting },
        Steps = [.. steps],
    };

    private static HttpRequestMessage CreateRequest(ScenarioDefinition definition, int step = 0)
    {
        var scenario = CompiledScenario.Compile(definition);
        using var user = new VirtualUser(1, new HttpMessageInvoker(new SocketsHttpHandler()), cookies: null);
        user.BeginIteration(scenario);
        return scenario.Steps[step].CreateRequest(user, scenario);
    }

    private static string? QueryValue(Uri uri, string name) =>
        uri.Query.TrimStart('?').Split('&')
            .Select(pair => pair.Split('=', 2))
            .FirstOrDefault(parts => parts[0] == name) is { Length: 2 } match ? match[1] : null;

    [Fact]
    public void GetRequestsGetAUniqueParameterAndNoCacheHeaders()
    {
        var definition = Scenario(true, new StepDefinition { Url = "/products" });

        var first = CreateRequest(definition);
        var second = CreateRequest(definition);

        Assert.Equal("/products", first.RequestUri!.AbsolutePath);
        Assert.Matches("^[0-9a-f]{32}$", QueryValue(first.RequestUri, "_cb"));
        Assert.NotEqual(QueryValue(first.RequestUri, "_cb"), QueryValue(second.RequestUri!, "_cb"));
        Assert.Equal("no-cache", first.Headers.GetValues("Cache-Control").Single());
        Assert.Equal("no-cache", first.Headers.GetValues("Pragma").Single());
    }

    [Fact]
    public void ExistingQueryAndFragmentArePreserved()
    {
        var request = CreateRequest(Scenario(true, new StepDefinition { Url = "https://cdn.example.test/a?x=1&y=2#top" }));

        Assert.StartsWith("?x=1&y=2&_cb=", request.RequestUri!.Query);
        Assert.Equal("#top", request.RequestUri.Fragment);
    }

    [Fact]
    public void CustomParameterNameIsUsed()
    {
        var definition = Scenario(true, new StepDefinition { Method = "HEAD", Url = "/health" });
        definition.Settings.CacheBustingParameter = " v ";

        var request = CreateRequest(definition);

        Assert.NotNull(QueryValue(request.RequestUri!, "v"));
        Assert.Null(QueryValue(request.RequestUri!, "_cb"));
    }

    [Fact]
    public void OtherMethodsAreLeftUnchanged()
    {
        var request = CreateRequest(Scenario(true, new StepDefinition { Method = "POST", Url = "/orders", Body = "{}" }));

        Assert.Equal("", request.RequestUri!.Query);
        Assert.False(request.Headers.Contains("Cache-Control"));
        Assert.False(request.Headers.Contains("Pragma"));
    }

    [Fact]
    public void HeadersSetByTheUserWin()
    {
        var definition = Scenario(true, new StepDefinition { Url = "/products", Headers = [new NameValue("Cache-Control", "max-age=0")] });
        definition.Headers = [new NameValue("pragma", "x")];

        var request = CreateRequest(definition);

        Assert.Equal("max-age=0", request.Headers.GetValues("Cache-Control").Single());
        Assert.Equal("x", request.Headers.GetValues("Pragma").Single());
        Assert.NotNull(QueryValue(request.RequestUri!, "_cb"));
    }

    [Fact]
    public void NothingChangesWhenDisabled()
    {
        var request = CreateRequest(Scenario(false, new StepDefinition { Url = "/products?page=2" }));

        Assert.False(new RequestSettings().CacheBusting);
        Assert.Equal("?page=2", request.RequestUri!.Query);
        Assert.False(request.Headers.Contains("Cache-Control"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a b")]
    [InlineData("x&y")]
    public void InvalidParameterNamesAreRejected(string name)
    {
        var definition = Scenario(true, new StepDefinition { Url = "/products" });
        definition.Settings.CacheBustingParameter = name;

        Assert.Contains(ScenarioValidator.Validate(definition), e => e.Contains("Önbellek atlama"));
    }
}
