using LoadTester.Core.Scenarios;

namespace LoadTester.Core.Warmup;

/// <summary>Checks a warmup before it starts. Messages are user-facing (Turkish).</summary>
public static class WarmupValidator
{
    public const int MaxConcurrency = 200;
    public const int MaxDelayMs = 60_000;

    public static List<string> Validate(WarmupDefinition warmup)
    {
        var errors = new List<string>();

        var url = warmup.SitemapUrl.Trim();
        if (url.Length == 0)
            errors.Add("Sitemap adresi boş olamaz.");
        else if (!ScenarioValidator.IsHttpUrl(url))
            errors.Add("Sitemap adresi, http:// veya https:// ile başlayan geçerli bir adres olmalı.");

        foreach (var header in warmup.Headers)
        {
            if (!ScenarioValidator.IsToken(header.Name.Trim()))
                errors.Add($"Başlıklar: geçersiz başlık adı \"{header.Name}\".");
        }

        if (warmup.Concurrency is < 1 or > MaxConcurrency)
            errors.Add($"Eş zamanlı istek sayısı 1 ile {MaxConcurrency} arasında olmalı.");

        if (warmup.TimeoutSeconds is < 1 or > ScenarioValidator.MaxTimeoutSeconds)
            errors.Add($"Zaman aşımı 1 ile {ScenarioValidator.MaxTimeoutSeconds} saniye arasında olmalı.");

        if (warmup.DelayMs is < 0 or > MaxDelayMs)
            errors.Add($"İstekten sonra bekleme 0 ile {MaxDelayMs:N0} ms arasında olmalı.");

        return errors;
    }
}
