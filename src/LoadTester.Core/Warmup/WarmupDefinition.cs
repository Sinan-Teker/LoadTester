using LoadTester.Core.Scenarios;

namespace LoadTester.Core.Warmup;

/// <summary>
/// A cache warmup: every page listed in a sitemap is requested once before a load test,
/// so the test does not measure empty caches.
/// </summary>
public sealed class WarmupDefinition
{
    /// <summary>A sitemap, a sitemap index or a text sitemap. A bare site address reads /sitemap.xml.</summary>
    public string SitemapUrl { get; set; } = "";

    /// <summary>Sent with the sitemap requests and with every warmup request.</summary>
    public List<NameValue> Headers { get; set; } = [];

    /// <summary>Requests in flight at the same time.</summary>
    public int Concurrency { get; set; } = 4;

    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Pause after each request before the same worker sends the next one.</summary>
    public int DelayMs { get; set; }

    /// <summary>Applies to the warmup requests; sitemap requests always follow redirects.</summary>
    public bool FollowRedirects { get; set; } = true;

    public bool IgnoreCertificateErrors { get; set; }
}
