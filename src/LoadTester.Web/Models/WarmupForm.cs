using LoadTester.Core.Warmup;

namespace LoadTester.Web.Models;

/// <summary>Editable copy of a warmup. Headers are edited as text, one per line.</summary>
public sealed class WarmupForm
{
    public string SitemapUrl { get; set; } = "";
    public string HeadersText { get; set; } = "";
    public int Concurrency { get; set; }
    public int TimeoutSeconds { get; set; }
    public int DelayMs { get; set; }
    public bool FollowRedirects { get; set; }
    public bool IgnoreCertificateErrors { get; set; }

    public static WarmupForm From(WarmupDefinition definition) => new()
    {
        SitemapUrl = definition.SitemapUrl,
        HeadersText = NameValueText.Format(definition.Headers, ": "),
        Concurrency = definition.Concurrency,
        TimeoutSeconds = definition.TimeoutSeconds,
        DelayMs = definition.DelayMs,
        FollowRedirects = definition.FollowRedirects,
        IgnoreCertificateErrors = definition.IgnoreCertificateErrors,
    };

    /// <summary>Builds the definition; header lines that cannot be parsed are reported in <paramref name="errors"/>.</summary>
    public WarmupDefinition ToDefinition(List<string> errors) => new()
    {
        SitemapUrl = SitemapUrl.Trim(),
        Headers = NameValueText.Parse(HeadersText, ':', "Başlıklar", "Ad: değer", errors),
        Concurrency = Concurrency,
        TimeoutSeconds = TimeoutSeconds,
        DelayMs = DelayMs,
        FollowRedirects = FollowRedirects,
        IgnoreCertificateErrors = IgnoreCertificateErrors,
    };
}
