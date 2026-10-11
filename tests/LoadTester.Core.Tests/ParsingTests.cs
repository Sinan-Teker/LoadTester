using System.Text.Json;
using LoadTester.Core.Extraction;
using LoadTester.Core.Scenarios;
using LoadTester.Core.Templates;

namespace LoadTester.Core.Tests;

public class TemplateTests
{
    private sealed class Context(Dictionary<string, string> variables) : ITemplateContext
    {
        public int VirtualUserId => 7;
        public long Iteration => 3;
        public bool TryGetVariable(string name, out string value) => variables.TryGetValue(name, out value!);
    }

    private static readonly Context Sample = new(new() { ["token"] = "abc" });

    [Fact]
    public void RendersVariablesAndBuiltIns()
    {
        var template = Template.Parse("Bearer {{token}} vu={{$vu}} it={{ $iteration }}");

        Assert.False(template.IsConstant);
        Assert.Equal("Bearer abc vu=7 it=3", template.Render(Sample));
        Assert.Equal(new[] { "token" }, template.VariableNames);
    }

    [Fact]
    public void RandomIntStaysInRange()
    {
        var template = Template.Parse("{{$randomInt(5, 9)}}");

        for (var i = 0; i < 200; i++)
            Assert.InRange(int.Parse(template.Render(Sample)), 5, 9);
    }

    [Fact]
    public void GuidAndTimestampAreGenerated()
    {
        Assert.True(Guid.TryParse(Template.Parse("{{$guid}}").Render(Sample), out _));
        Assert.True(long.Parse(Template.Parse("{{$timestamp}}").Render(Sample)) > 1_600_000_000_000);
    }

    [Fact]
    public void TextWithoutPlaceholdersIsConstant()
    {
        var template = Template.Parse("{\"a\": {\"b\": 1}}");

        Assert.True(template.IsConstant);
        Assert.Equal("{\"a\": {\"b\": 1}}", template.Render(Sample));
    }

    [Theory]
    [InlineData("{{token")]
    [InlineData("{{ }}")]
    [InlineData("{{1abc}}")]
    [InlineData("{{$nope}}")]
    [InlineData("{{$guid(1)}}")]
    [InlineData("{{$randomInt(9,5)}}")]
    public void RejectsInvalidPlaceholders(string text)
    {
        Assert.False(Template.TryParse(text, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }
}

public class StatusMatcherTests
{
    [Theory]
    [InlineData("200", 200, true)]
    [InlineData("200", 201, false)]
    [InlineData("200-299", 204, true)]
    [InlineData("2xx, 404", 404, true)]
    [InlineData("2XX", 302, false)]
    [InlineData("", 500, true)]
    public void Matches(string expression, int status, bool expected)
    {
        Assert.True(StatusMatcher.TryParse(expression, out var matcher));
        Assert.Equal(expected, matcher.Matches(status));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("299-200")]
    [InlineData("99")]
    [InlineData("6xx")]
    public void RejectsInvalidExpressions(string expression)
    {
        Assert.False(StatusMatcher.TryParse(expression, out _));
    }
}

public class JsonPathTests
{
    private const string Json = """
        { "data": { "token": "t-1", "count": 3, "ok": true, "none": null },
          "items": [ { "id": 10 }, { "id": 20, "display name": "x" } ] }
        """;

    [Theory]
    [InlineData("$.data.token", "t-1")]
    [InlineData("data.token", "t-1")]
    [InlineData("$.data.count", "3")]
    [InlineData("$.data.ok", "true")]
    [InlineData("$.items[1].id", "20")]
    [InlineData("items[1]['display name']", "x")]
    public void SelectsValues(string expression, string expected)
    {
        using var document = JsonDocument.Parse(Json);

        Assert.True(JsonPath.TryParse(expression, out var path));
        Assert.True(path.TryEvaluate(document.RootElement, out var value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("$.data.missing")]
    [InlineData("$.data.none")]
    [InlineData("$.items[5].id")]
    [InlineData("$.data[0]")]
    public void MissingValuesFail(string expression)
    {
        using var document = JsonDocument.Parse(Json);

        Assert.True(JsonPath.TryParse(expression, out var path));
        Assert.False(path.TryEvaluate(document.RootElement, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("$..token")]
    [InlineData("$.items[x]")]
    [InlineData("$.items[0")]
    public void RejectsInvalidPaths(string expression)
    {
        Assert.False(JsonPath.TryParse(expression, out _));
    }
}

public class ScenarioValidatorTests
{
    private static ScenarioDefinition Valid() => new()
    {
        BaseUrl = "https://example.com",
        Load = new LoadProfile { Stages = [new LoadStage(10, 5)] },
        Variables = [new NameValue("user", "u{{$vu}}")],
        Steps =
        [
            new StepDefinition
            {
                Method = "POST",
                Url = "/login",
                Body = "{\"user\": \"{{user}}\"}",
                Extractors = [new Extractor { Variable = "token", Expression = "$.token" }],
            },
            new StepDefinition { Url = "/me", Headers = [new NameValue("Authorization", "Bearer {{token}}")] },
        ],
    };

    [Fact]
    public void AcceptsValidScenario()
    {
        Assert.Empty(ScenarioValidator.Validate(Valid()));
    }

    [Fact]
    public void ReportsUndefinedVariables()
    {
        var scenario = Valid();
        scenario.Steps[1].Headers[0].Value = "Bearer {{tokn}}";

        var error = Assert.Single(ScenarioValidator.Validate(scenario));
        Assert.Contains("{{tokn}}", error);
    }

    [Fact]
    public void RelativeUrlNeedsBaseUrl()
    {
        var scenario = Valid();
        scenario.BaseUrl = null;

        Assert.Equal(2, ScenarioValidator.Validate(scenario).Count);
    }

    [Fact]
    public void ReportsBrokenLoadProfileAndSteps()
    {
        var scenario = Valid();
        scenario.Load.Stages = [new LoadStage(0, 0)];
        scenario.Steps[0].ExpectedStatus = "abc";
        scenario.Steps[0].Extractors[0].Expression = "$..";
        scenario.Settings.ThinkTimeMinMs = 500;

        Assert.Equal(5, ScenarioValidator.Validate(scenario).Count);
    }
}
