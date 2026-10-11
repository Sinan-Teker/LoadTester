using System.Net.Quic;
using System.Text.RegularExpressions;
using LoadTester.Core.Extraction;
using LoadTester.Core.Templates;

namespace LoadTester.Core.Scenarios;

/// <summary>Checks a scenario before it is saved or run. Messages are user-facing (Turkish).</summary>
public static class ScenarioValidator
{
    public const int MaxTarget = 50_000;
    public const int MaxDurationSeconds = 24 * 60 * 60;
    public const int MaxTimeoutSeconds = 600;

    public static List<string> Validate(ScenarioDefinition scenario)
    {
        var errors = new List<string>();

        var defined = new HashSet<string>(StringComparer.Ordinal);
        foreach (var variable in scenario.Variables)
        {
            if (Template.IsValidVariableName(variable.Name.Trim()))
                defined.Add(variable.Name.Trim());
            else
                errors.Add($"Geçersiz değişken adı: \"{variable.Name}\" (harf, rakam ve _ kullanılabilir; rakamla başlayamaz).");
        }

        foreach (var extractor in scenario.Steps.SelectMany(s => s.Extractors))
        {
            if (Template.IsValidVariableName(extractor.Variable.Trim()))
                defined.Add(extractor.Variable.Trim());
        }

        ValidateLoad(scenario.Load, errors);
        ValidateSettings(scenario.Settings, errors);

        if (!string.IsNullOrWhiteSpace(scenario.BaseUrl)
            && CheckTemplate(scenario.BaseUrl, "Temel URL", defined, errors) is { IsConstant: true }
            && !IsHttpUrl(scenario.BaseUrl.Trim()))
        {
            errors.Add("Temel URL, http:// veya https:// ile başlayan geçerli bir adres olmalı.");
        }

        if (scenario.Settings.HttpVersion == HttpProtocolVersion.Http3 && IsPlainHttp(scenario.BaseUrl))
            errors.Add("Temel URL: HTTP/3 yalnızca https:// adreslerle kullanılabilir.");

        foreach (var variable in scenario.Variables)
            CheckTemplate(variable.Value, $"\"{variable.Name.Trim()}\" değişkeni", defined, errors);

        ValidateHeaders(scenario.Headers, "Ortak başlıklar", defined, errors);

        if (scenario.Steps.Count == 0)
            errors.Add("En az bir adım ekleyin.");

        for (var i = 0; i < scenario.Steps.Count; i++)
            ValidateStep(scenario, scenario.Steps[i], StepLabel(i, scenario.Steps[i]), defined, errors);

        return errors;
    }

    public static string StepLabel(int index, StepDefinition step) =>
        string.IsNullOrWhiteSpace(step.Name) ? $"Adım {index + 1}" : $"Adım {index + 1} ({step.Name.Trim()})";

    private static void ValidateLoad(LoadProfile load, List<string> errors)
    {
        if (load.Stages.Count == 0)
        {
            errors.Add("En az bir yük aşaması ekleyin.");
            return;
        }

        if (load.Stages.Any(s => s.DurationSeconds < 0))
            errors.Add("Aşama süreleri negatif olamaz.");
        if (load.StartTarget < 0 || load.Stages.Any(s => s.Target < 0))
            errors.Add("Hedef değerler negatif olamaz.");

        if (load.TotalSeconds <= 0)
            errors.Add("Toplam test süresi 0'dan büyük olmalı.");
        else if (load.TotalSeconds > MaxDurationSeconds)
            errors.Add("Toplam test süresi en fazla 24 saat olabilir.");

        if (load.PeakTarget <= 0)
            errors.Add("Yük profilinde en az bir hedef 0'dan büyük olmalı.");
        else if (load.PeakTarget > MaxTarget)
            errors.Add($"Hedef değer en fazla {MaxTarget:N0} olabilir.");

        if (load.Mode == LoadMode.ArrivalRate && load.MaxVirtualUsers is < 1 or > MaxTarget)
            errors.Add($"Eş zamanlı kullanıcı sınırı 1 ile {MaxTarget:N0} arasında olmalı.");
    }

    private static void ValidateSettings(RequestSettings settings, List<string> errors)
    {
        if (settings.TimeoutSeconds is < 1 or > MaxTimeoutSeconds)
            errors.Add($"Zaman aşımı 1 ile {MaxTimeoutSeconds} saniye arasında olmalı.");

        if (settings.ThinkTimeMinMs < 0 || settings.ThinkTimeMaxMs < 0)
            errors.Add("Bekleme süreleri negatif olamaz.");
        else if (settings.ThinkTimeMinMs > settings.ThinkTimeMaxMs)
            errors.Add("Beklemenin en az değeri, en çok değerinden büyük olamaz.");

        if (settings.CacheBusting && !IsQueryName(settings.CacheBustingParameter.Trim()))
            errors.Add("Önbellek atlama parametresinin adı boş olamaz; harf, rakam, _, - ve . kullanılabilir.");

        if (settings.HttpVersion == HttpProtocolVersion.Http3 && !IsQuicSupported())
            errors.Add("Bu bilgisayarda HTTP/3 kullanılamıyor: işletim sistemi QUIC desteklemiyor (Windows 11, Windows Server 2022 ya da libmsquic kurulu Linux gerekir).");
    }

    private static void ValidateStep(ScenarioDefinition scenario, StepDefinition step, string label, HashSet<string> defined, List<string> errors)
    {
        if (!IsToken(step.Method.Trim()))
            errors.Add($"{label}: geçersiz HTTP metodu \"{step.Method}\".");

        var url = step.Url.Trim();
        var hasBaseUrl = !string.IsNullOrWhiteSpace(scenario.BaseUrl);
        if (url.Length == 0)
        {
            if (!hasBaseUrl)
                errors.Add($"{label}: URL boş olamaz.");
        }
        else if (CheckTemplate(url, $"{label} URL", defined, errors) is { IsConstant: true } && !IsHttpUrl(url))
        {
            if (url.Contains("://", StringComparison.Ordinal))
                errors.Add($"{label}: yalnızca http ve https adresleri desteklenir.");
            else if (!hasBaseUrl)
                errors.Add($"{label}: göreli URL için senaryoya temel URL girin ya da tam adres yazın.");
        }

        if (scenario.Settings.HttpVersion == HttpProtocolVersion.Http3 && IsPlainHttp(url))
            errors.Add($"{label}: HTTP/3 yalnızca https:// adreslerle kullanılabilir.");

        ValidateHeaders(step.Headers, $"{label} başlıkları", defined, errors);

        if (!string.IsNullOrEmpty(step.Body))
            CheckTemplate(step.Body, $"{label} gövdesi", defined, errors);

        if (!StatusMatcher.TryParse(step.ExpectedStatus, out _))
            errors.Add($"{label}: beklenen durum kodu anlaşılamadı (ör. 200 · 200-299 · 2xx · 200,302).");

        foreach (var extractor in step.Extractors)
        {
            var name = extractor.Variable.Trim();
            if (!Template.IsValidVariableName(name))
            {
                errors.Add($"{label}: çıkarılacak değişkenin adı geçersiz (\"{extractor.Variable}\").");
                continue;
            }

            var expression = extractor.Expression.Trim();
            if (expression.Length == 0)
            {
                errors.Add($"{label}: \"{name}\" için ifade boş olamaz.");
                continue;
            }

            switch (extractor.Source)
            {
                case ExtractorSource.JsonPath when !JsonPath.TryParse(expression, out _):
                    errors.Add($"{label}: \"{name}\" için JSON yolu geçersiz (ör. $.data.token, items[0].id).");
                    break;
                case ExtractorSource.Regex when !IsValidRegex(extractor.Expression):
                    errors.Add($"{label}: \"{name}\" için düzenli ifade geçersiz.");
                    break;
                case ExtractorSource.Header when !IsToken(expression):
                    errors.Add($"{label}: \"{name}\" için başlık adı geçersiz.");
                    break;
            }
        }
    }

    private static void ValidateHeaders(List<NameValue> headers, string label, HashSet<string> defined, List<string> errors)
    {
        foreach (var header in headers)
        {
            var name = header.Name.Trim();
            if (!IsToken(name))
                errors.Add($"{label}: geçersiz başlık adı \"{header.Name}\".");
            CheckTemplate(header.Value, $"{label} ({name})", defined, errors);
        }
    }

    private static Template? CheckTemplate(string? text, string label, HashSet<string> defined, List<string> errors)
    {
        if (!Template.TryParse(text, out var template, out var error))
        {
            errors.Add($"{label}: {error}");
            return null;
        }

        foreach (var name in template.VariableNames)
        {
            if (!defined.Contains(name))
                errors.Add($"{label}: tanımlanmamış değişken " + "{{" + name + "}}.");
        }

        return template;
    }

    private static bool IsHttpUrl(string text) =>
        Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>Unencrypted address, placeholders or not. HTTP/3 always runs over TLS.</summary>
    private static bool IsPlainHttp(string? url) =>
        url is not null && url.TrimStart().StartsWith("http://", StringComparison.OrdinalIgnoreCase);

    /// <summary>HTTP/3 needs QUIC from the operating system (msquic).</summary>
    private static bool IsQuicSupported() =>
        (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) && QuicConnection.IsSupported;

    /// <summary>RFC 9110 token: visible ASCII without separators.</summary>
    private static bool IsToken(string text) =>
        text.Length > 0 && text.All(c => c is > ' ' and < '\u007f' && !"()<>@,;:\\\"/[]?={}".Contains(c));

    /// <summary>Query parameter names that never need escaping.</summary>
    private static bool IsQueryName(string text) =>
        text.Length > 0 && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');

    private static bool IsValidRegex(string pattern)
    {
        try
        {
            _ = new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

public sealed class ScenarioValidationException(IReadOnlyList<string> errors)
    : Exception(string.Join(Environment.NewLine, errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
