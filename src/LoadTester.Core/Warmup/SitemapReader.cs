using LoadTester.Core.Engine;

namespace LoadTester.Core.Warmup;

/// <summary>The sitemap could not be read. The message is user-facing (Turkish).</summary>
public sealed class SitemapException(string message) : Exception(message);

internal sealed class SitemapContents
{
    /// <summary>Distinct page addresses, percent-encoded, in sitemap order.</summary>
    public List<string> Urls { get; } = [];

    public List<string> Warnings { get; } = [];
    public int SitemapsRead { get; set; }
}

/// <summary>Collects the page addresses of a sitemap, reading the sitemaps an index lists level by level.</summary>
/// <param name="concurrency">Sitemaps downloaded at the same time.</param>
internal sealed class SitemapReader(HttpMessageInvoker client, (string Name, string Value)[] headers, int timeoutSeconds, int concurrency)
{
    public const int MaxUrls = 200_000;
    public const int MaxSitemaps = 1_000;

    /// <summary>Levels of sitemap files. The protocol has an index over sitemaps; some sites nest one more index.</summary>
    public const int MaxDepth = 3;

    private const int MaxListedFailures = 20;

    /// <summary>A bare site address means the conventional /sitemap.xml.</summary>
    public static Uri ResolveRoot(string address)
    {
        var uri = new Uri(address.Trim(), UriKind.Absolute);
        return uri.AbsolutePath == "/" && uri.Query.Length == 0 ? new Uri(uri, "/sitemap.xml") : uri;
    }

    /// <param name="onProgress">Receives the number of sitemaps read and of pages found after each sitemap.</param>
    /// <exception cref="SitemapException">The first sitemap could not be read.</exception>
    public async Task<SitemapContents> ReadAsync(Uri root, Action<int, int>? onProgress, CancellationToken cancellationToken)
    {
        var contents = new SitemapContents();
        var urls = new HashSet<string>(StringComparer.Ordinal);
        var sitemaps = new HashSet<string>(StringComparer.Ordinal) { root.AbsoluteUri };
        int failedSitemaps = 0, invalid = 0, tooDeep = 0;
        bool sitemapLimitReached = false, urlLimitReached = false;

        // Downloads run ahead in parallel but are processed in order, so the pages keep the sitemap order.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pending = new Queue<Task<Download>>();
        try
        {
            List<Uri> level = [root];
            for (var depth = 0; level.Count > 0 && !urlLimitReached; depth++)
            {
                List<Uri> next = [];
                var queued = 0;
                while ((queued < level.Count || pending.Count > 0) && !urlLimitReached)
                {
                    while (queued < level.Count && pending.Count < concurrency)
                        pending.Enqueue(DownloadAsync(level[queued++], stop.Token));

                    var download = await pending.Dequeue().ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    contents.SitemapsRead++;

                    if (download.Error is null)
                        Collect(download, depth == MaxDepth - 1, next);
                    else if (depth == 0)
                        throw new SitemapException($"{download.Uri} okunamadı: {download.Error}");
                    else if (++failedSitemaps <= MaxListedFailures)
                        contents.Warnings.Add($"{download.Uri} okunamadı: {download.Error}");

                    onProgress?.Invoke(contents.SitemapsRead, contents.Urls.Count);
                }

                level = next;
            }
        }
        finally
        {
            // Downloads still running after a stop, an error or the page limit. They never throw.
            await stop.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(pending).ConfigureAwait(false);
        }

        if (failedSitemaps > MaxListedFailures)
            contents.Warnings.Add($"{failedSitemaps - MaxListedFailures:N0} sitemap daha okunamadı.");
        if (tooDeep > 0)
            contents.Warnings.Add($"İç içe en çok {MaxDepth} seviye sitemap okunur; daha derindeki {tooDeep:N0} sitemap atlandı.");
        if (sitemapLimitReached)
            contents.Warnings.Add($"En çok {MaxSitemaps:N0} sitemap okunur; kalanlar atlandı.");
        if (invalid > 0)
            contents.Warnings.Add($"{invalid:N0} geçersiz adres atlandı (yalnızca http ve https adresleri kullanılır).");
        if (urlLimitReached)
            contents.Warnings.Add($"En çok {MaxUrls:N0} adres ısıtılır; kalanlar atlandı.");

        return contents;

        void Collect(Download download, bool lastLevel, List<Uri> next)
        {
            foreach (var location in download.Locations)
            {
                if (!TryResolve(download.Uri, location, out var uri))
                {
                    invalid++;
                }
                else if (download.Kind == SitemapKind.Index)
                {
                    if (lastLevel)
                        tooDeep++;
                    else if (sitemaps.Count >= MaxSitemaps)
                        sitemapLimitReached = true;
                    else if (sitemaps.Add(uri.AbsoluteUri))
                        next.Add(uri);
                }
                else
                {
                    var url = uri.GetLeftPart(UriPartial.Query); // fragments are never sent
                    if (urls.Contains(url))
                        continue;

                    if (urls.Count >= MaxUrls)
                    {
                        urlLimitReached = true;
                        return;
                    }

                    urls.Add(url);
                    contents.Urls.Add(url);
                }
            }
        }
    }

    /// <summary>The protocol asks for absolute addresses; relative ones are resolved against the sitemap anyway.</summary>
    private static bool TryResolve(Uri sitemap, string location, out Uri uri)
    {
        uri = null!;
        return location.Length > 0
            && Uri.TryCreate(sitemap, location, out uri!)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    /// <summary>Downloads and parses one sitemap. Failures are returned, not thrown.</summary>
    private async Task<Download> DownloadAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            foreach (var (name, value) in headers)
                request.Headers.TryAddWithoutValidation(name, value);

            using var response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return Failed($"HTTP {(int)response.StatusCode}");

            if (await ReadBodyAsync(response.Content, timeout.Token).ConfigureAwait(false) is not { } body)
                return Failed($"Dosya {SitemapParser.MaxBytes / (1024 * 1024)} MB'tan büyük");

            var (kind, locations) = SitemapParser.Parse(body, MaxUrls + 1);
            return new Download(uri, kind, locations, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failed("İptal edildi");
        }
        catch (OperationCanceledException)
        {
            return Failed($"Zaman aşımı ({timeoutSeconds} sn)");
        }
        catch (HttpRequestException ex)
        {
            return Failed(LoadTestEngine.Describe(ex));
        }
        catch (FormatException ex)
        {
            return Failed(ex.Message);
        }
        catch (Exception ex)
        {
            return Failed(LoadTestEngine.Shorten(ex.Message));
        }

        Download Failed(string error) => new(uri, SitemapKind.Pages, [], error);
    }

    /// <returns>The body, or null when it is larger than a sitemap may be.</returns>
    private static async Task<byte[]?> ReadBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > SitemapParser.MaxBytes)
            return null;

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var body = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (body.Length + read > SitemapParser.MaxBytes)
                return null;

            body.Write(buffer, 0, read);
        }

        return body.ToArray();
    }

    private sealed record Download(Uri Uri, SitemapKind Kind, List<string> Locations, string? Error);
}
