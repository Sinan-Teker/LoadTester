using LoadTester.Core.Scenarios;

namespace LoadTester.Web.Models;

/// <summary>Editable copy of a scenario. Header and variable lists are edited as text, one entry per line.</summary>
public sealed class ScenarioForm
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? BaseUrl { get; set; }
    public LoadProfile Load { get; set; } = new();
    public RequestSettings Settings { get; set; } = new();
    public string VariablesText { get; set; } = "";
    public string HeadersText { get; set; } = "";
    public List<StepForm> Steps { get; set; } = [];

    public static ScenarioForm From(string name, string? description, ScenarioDefinition definition) => new()
    {
        Name = name,
        Description = description,
        BaseUrl = definition.BaseUrl,
        Load = definition.Load,
        Settings = definition.Settings,
        VariablesText = NameValueText.Format(definition.Variables, "="),
        HeadersText = NameValueText.Format(definition.Headers, ": "),
        Steps = definition.Steps.Select(StepForm.From).ToList(),
    };

    /// <summary>Builds the definition; lines that cannot be parsed are reported in <paramref name="errors"/>.</summary>
    public ScenarioDefinition ToDefinition(List<string> errors) => new()
    {
        BaseUrl = string.IsNullOrWhiteSpace(BaseUrl) ? null : BaseUrl.Trim(),
        Load = Load,
        Settings = Settings,
        Variables = NameValueText.Parse(VariablesText, '=', "Değişkenler", "ad=değer", errors),
        Headers = NameValueText.Parse(HeadersText, ':', "Ortak başlıklar", "Ad: değer", errors),
        Steps = Steps.Select((step, index) => step.ToDefinition(index, errors)).ToList(),
    };
}

public sealed class StepForm
{
    public string Name { get; set; } = "";
    public string Method { get; set; } = "GET";
    public string Url { get; set; } = "";
    public string HeadersText { get; set; } = "";
    public string? Body { get; set; }
    public string? ExpectedStatus { get; set; } = "200-399";
    public string? ExpectedBodyText { get; set; }
    public List<Extractor> Extractors { get; set; } = [];

    public static StepForm From(StepDefinition step) => new()
    {
        Name = step.Name,
        Method = step.Method,
        Url = step.Url,
        HeadersText = NameValueText.Format(step.Headers, ": "),
        Body = step.Body,
        ExpectedStatus = step.ExpectedStatus,
        ExpectedBodyText = step.ExpectedBodyText,
        Extractors = step.Extractors.Select(e => new Extractor { Variable = e.Variable, Source = e.Source, Expression = e.Expression }).ToList(),
    };

    public StepDefinition ToDefinition(int index, List<string> errors) => new()
    {
        Name = Name.Trim(),
        Method = Method,
        Url = Url.Trim(),
        Headers = NameValueText.Parse(HeadersText, ':', $"Adım {index + 1} başlıkları", "Ad: değer", errors),
        Body = string.IsNullOrWhiteSpace(Body) ? null : Body,
        ExpectedStatus = ExpectedStatus?.Trim() ?? "",
        ExpectedBodyText = string.IsNullOrEmpty(ExpectedBodyText) ? null : ExpectedBodyText,
        Extractors = Extractors
            .Select(e => new Extractor { Variable = e.Variable.Trim(), Source = e.Source, Expression = e.Expression.Trim() })
            .ToList(),
    };
}

public static class NameValueText
{
    public static string Format(IEnumerable<NameValue> items, string separator) =>
        string.Join("\n", items.Select(item => item.Name + separator + item.Value));

    /// <summary>Parses one "name{separator}value" pair per line; blank lines are skipped.</summary>
    public static List<NameValue> Parse(string? text, char separator, string label, string example, List<string> errors)
    {
        var items = new List<NameValue>();
        if (string.IsNullOrWhiteSpace(text))
            return items;

        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0)
                continue;

            var index = line.IndexOf(separator);
            if (index <= 0)
            {
                errors.Add($"{label}, satır {i + 1}: \"{line}\" anlaşılamadı (beklenen biçim: {example}).");
                continue;
            }

            items.Add(new NameValue(line[..index].Trim(), line[(index + 1)..].Trim()));
        }

        return items;
    }
}
