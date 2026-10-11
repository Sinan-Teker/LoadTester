using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LoadTester.Core.Extraction;
using LoadTester.Core.Scenarios;
using LoadTester.Core.Templates;

namespace LoadTester.Core.Engine;

/// <summary>A validated scenario with templates, matchers and regexes prepared once for the whole run.</summary>
internal sealed class CompiledScenario
{
    private CompiledScenario(ScenarioDefinition definition)
    {
        Load = definition.Load;
        Settings = definition.Settings;
        Timeout = TimeSpan.FromSeconds(definition.Settings.TimeoutSeconds);
        BaseUrl = Template.Parse(definition.BaseUrl?.Trim());
        Variables = definition.Variables.Select(v => (v.Name.Trim(), Template.Parse(v.Value))).ToArray();
        Steps = definition.Steps.Select((step, index) => new CompiledStep(index, step, definition.Headers, definition.Settings)).ToArray();
    }

    public LoadProfile Load { get; }
    public RequestSettings Settings { get; }
    public TimeSpan Timeout { get; }
    public Template BaseUrl { get; }
    public (string Name, Template Value)[] Variables { get; }
    public CompiledStep[] Steps { get; }

    /// <exception cref="ScenarioValidationException">The scenario is not runnable.</exception>
    public static CompiledScenario Compile(ScenarioDefinition definition)
    {
        var errors = ScenarioValidator.Validate(definition);
        if (errors.Count > 0)
            throw new ScenarioValidationException(errors);

        return new CompiledScenario(definition);
    }

    public TimeSpan NextThinkTime()
    {
        int min = Settings.ThinkTimeMinMs, max = Settings.ThinkTimeMaxMs;
        if (max <= 0)
            return TimeSpan.Zero;

        return TimeSpan.FromMilliseconds(min >= max ? max : Random.Shared.Next(min, max + 1));
    }

    /// <summary>
    /// Absolute http(s) URLs are used as they are; anything else is appended to the base URL.
    /// <paramref name="extraQuery"/> ("name=value") is added to the query string, e.g. for cache busting.
    /// </summary>
    public Uri ResolveUri(string url, ITemplateContext context, string? extraQuery = null)
    {
        var target = url.Trim();
        if (!IsAbsoluteHttp(target))
        {
            var baseUrl = BaseUrl.Render(context).Trim().TrimEnd('/');
            target = target.Length == 0 ? baseUrl
                : target[0] == '?' ? baseUrl + target
                : baseUrl + "/" + target.TrimStart('/');
        }

        if (extraQuery is not null)
            target = AppendQuery(target, extraQuery);

        return TryCreateHttpUri(target, out var uri)
            ? uri
            : throw new InvalidOperationException($"Geçersiz URL: {target}");
    }

    private static bool IsAbsoluteHttp(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>Adds "name=value" to the query string; a fragment stays at the end.</summary>
    private static string AppendQuery(string url, string pair)
    {
        var hash = url.IndexOf('#');
        var main = hash < 0 ? url : url[..hash];
        var fragment = hash < 0 ? "" : url[hash..];
        var separator = !main.Contains('?') ? "?" : main.EndsWith('?') || main.EndsWith('&') ? "" : "&";
        return main + separator + pair + fragment;
    }

    private static bool TryCreateHttpUri(string text, out Uri uri) =>
        Uri.TryCreate(text, UriKind.Absolute, out uri!) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}

internal sealed class CompiledStep
{
    private const string DefaultUserAgent = "LoadTester/1.0";

    private static readonly HashSet<string> ContentHeaderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Type", "Content-Encoding", "Content-Language", "Content-Location", "Content-MD5",
        "Content-Range", "Content-Disposition", "Expires", "Last-Modified", "Allow",
    };

    private static readonly (string Name, string Value)[] NoCacheHeaders = [("Cache-Control", "no-cache"), ("Pragma", "no-cache")];

    private readonly (string Name, Template Value, bool IsContentHeader)[] _headers;
    private readonly Template? _body;
    private readonly string? _defaultContentType;
    private readonly bool _hasUserAgent;
    private readonly string? _cacheBustingParameter;
    private readonly (string Name, string Value)[] _cacheHeaders = [];
    private readonly Version? _version;

    public CompiledStep(int index, StepDefinition step, IEnumerable<NameValue> commonHeaders, RequestSettings settings)
    {
        Index = index;
        Name = step.Name.Trim();
        Method = new HttpMethod(step.Method.Trim().ToUpperInvariant());
        UrlText = step.Url.Trim();
        Url = Template.Parse(UrlText);

        // Step headers override common headers with the same name; Content-Length is always computed.
        var stepHeaderNames = step.Headers.Select(h => h.Name.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _headers = commonHeaders
            .Where(h => !stepHeaderNames.Contains(h.Name.Trim()))
            .Concat(step.Headers)
            .Select(h => (Name: h.Name.Trim(), h.Value))
            .Where(h => !h.Name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            .Select(h => (h.Name, Template.Parse(h.Value), ContentHeaderNames.Contains(h.Name)))
            .ToArray();
        _hasUserAgent = _headers.Any(h => h.Name.Equals("User-Agent", StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(step.Body))
        {
            _body = Template.Parse(step.Body);
            if (!_headers.Any(h => h.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)))
            {
                var trimmed = step.Body.AsSpan().TrimStart();
                _defaultContentType = trimmed.Length > 0 && trimmed[0] is '{' or '['
                    ? "application/json"
                    : "text/plain; charset=utf-8";
            }
        }

        Status = StatusMatcher.TryParse(step.ExpectedStatus, out var matcher) ? matcher : StatusMatcher.Any;
        ExpectedText = string.IsNullOrEmpty(step.ExpectedBodyText) ? null : step.ExpectedBodyText;
        Extractors = step.Extractors.Select(e => new CompiledExtractor(e)).ToArray();
        NeedsBody = ExpectedText is not null || Extractors.Any(e => e.Source != ExtractorSource.Header);

        // Caches only answer GET and HEAD, so other methods are sent unchanged. Headers set by the user win.
        if (settings.CacheBusting && (Method == HttpMethod.Get || Method == HttpMethod.Head))
        {
            _cacheBustingParameter = settings.CacheBustingParameter.Trim();
            _cacheHeaders = NoCacheHeaders
                .Where(h => !_headers.Any(x => x.Name.Equals(h.Name, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
        }

        _version = settings.HttpVersion switch
        {
            HttpProtocolVersion.Http2 => HttpVersion.Version20,
            HttpProtocolVersion.Http3 => HttpVersion.Version30,
            _ => null,
        };
    }

    public int Index { get; }
    public string Name { get; }
    public HttpMethod Method { get; }
    public string UrlText { get; }
    public Template Url { get; }
    public StatusMatcher Status { get; }
    public string? ExpectedText { get; }
    public CompiledExtractor[] Extractors { get; }

    /// <summary>True when assertions or extractors need the response body as text.</summary>
    public bool NeedsBody { get; }

    public HttpRequestMessage CreateRequest(VirtualUser user, CompiledScenario scenario)
    {
        var cacheBuster = _cacheBustingParameter is null ? null : _cacheBustingParameter + "=" + Guid.NewGuid().ToString("N");
        var request = new HttpRequestMessage(Method, scenario.ResolveUri(Url.Render(user), user, cacheBuster));
        if (_version is not null)
        {
            // Exact, so a run never reports HTTP/1.1 results as the chosen version.
            request.Version = _version;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        }

        if (_body is not null)
        {
            request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(_body.Render(user)));
            if (_defaultContentType is not null)
                request.Content.Headers.TryAddWithoutValidation("Content-Type", _defaultContentType);
        }

        foreach (var (name, value, isContentHeader) in _headers)
        {
            if (isContentHeader)
                request.Content?.Headers.TryAddWithoutValidation(name, value.Render(user));
            else
                request.Headers.TryAddWithoutValidation(name, value.Render(user));
        }

        if (!_hasUserAgent)
            request.Headers.TryAddWithoutValidation("User-Agent", DefaultUserAgent);

        foreach (var (name, value) in _cacheHeaders)
            request.Headers.TryAddWithoutValidation(name, value);

        return request;
    }

    /// <summary>Checks the response against the step's expectations and runs the extractors.</summary>
    /// <returns>The failure reason, or null when the step succeeded.</returns>
    public string? Evaluate(HttpResponseMessage response, string? body, VirtualUser user)
    {
        var status = (int)response.StatusCode;
        if (!Status.Matches(status))
            return $"HTTP {status}";

        if (ExpectedText is not null && (body is null || !body.Contains(ExpectedText, StringComparison.Ordinal)))
            return "Yanıtta beklenen metin yok";

        if (Extractors.Length == 0)
            return null;

        JsonDocument? json = null;
        try
        {
            foreach (var extractor in Extractors)
            {
                if (!extractor.TryExtract(response, body, ref json, out var value))
                    return $"Değişken çıkarılamadı: {extractor.Variable}";

                user.SetVariable(extractor.Variable, value);
            }

            return null;
        }
        finally
        {
            json?.Dispose();
        }
    }
}

internal sealed class CompiledExtractor
{
    private readonly Regex? _regex;
    private readonly JsonPath? _path;

    public CompiledExtractor(Extractor extractor)
    {
        Variable = extractor.Variable.Trim();
        Source = extractor.Source;
        Expression = extractor.Expression.Trim();

        if (Source == ExtractorSource.Regex)
            _regex = new Regex(extractor.Expression, RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        else if (Source == ExtractorSource.JsonPath && JsonPath.TryParse(Expression, out var path))
            _path = path;
    }

    public string Variable { get; }
    public ExtractorSource Source { get; }
    public string Expression { get; }

    /// <param name="json">Parsed body shared by the step's extractors; created on first use.</param>
    public bool TryExtract(HttpResponseMessage response, string? body, ref JsonDocument? json, out string value)
    {
        value = "";
        switch (Source)
        {
            case ExtractorSource.Header:
                if (!response.Headers.TryGetValues(Expression, out var values)
                    && !response.Content.Headers.TryGetValues(Expression, out values))
                {
                    return false;
                }

                value = values.FirstOrDefault() ?? "";
                return true;

            case ExtractorSource.Regex:
                if (body is null)
                    return false;
                try
                {
                    var match = _regex!.Match(body);
                    if (!match.Success)
                        return false;

                    value = match.Groups.Count > 1 ? match.Groups[1].Value : match.Value;
                    return true;
                }
                catch (RegexMatchTimeoutException)
                {
                    return false;
                }

            default:
                if (body is null || _path is null)
                    return false;
                if (json is null)
                {
                    try
                    {
                        json = JsonDocument.Parse(body);
                    }
                    catch (JsonException)
                    {
                        return false;
                    }
                }

                return _path.TryEvaluate(json.RootElement, out value);
        }
    }
}
