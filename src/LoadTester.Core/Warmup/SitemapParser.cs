using System.IO.Compression;
using System.Text;
using System.Xml;

namespace LoadTester.Core.Warmup;

internal enum SitemapKind
{
    /// <summary>A urlset or a text sitemap: addresses of pages.</summary>
    Pages,

    /// <summary>A sitemapindex: addresses of other sitemaps.</summary>
    Index,
}

/// <summary>
/// Reads sitemap files as defined on sitemaps.org: XML urlset and sitemapindex, or plain UTF-8 text with one address
/// per line. Either one may be gzipped.
/// </summary>
internal static class SitemapParser
{
    /// <summary>The protocol limits a sitemap to 50 MB uncompressed; files a little over it are still read.</summary>
    public const int MaxBytes = 64 * 1024 * 1024;

    private static readonly XmlReaderSettings XmlSettings = new()
    {
        DtdProcessing = DtdProcessing.Ignore, // no entity expansion, nothing fetched
        XmlResolver = null,
        CheckCharacters = false,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = true,
    };

    /// <summary>Returns the locations as written (trimmed, not validated), at most <paramref name="maxLocations"/>.</summary>
    /// <exception cref="FormatException">The content is not a sitemap. The message is user-facing.</exception>
    public static (SitemapKind Kind, List<string> Locations) Parse(byte[] content, int maxLocations)
    {
        try
        {
            if (content is [0x1F, 0x8B, ..])
                content = Gunzip(content);

            return IsXml(content)
                ? ReadXml(content, maxLocations)
                : (SitemapKind.Pages, ReadLines(content, maxLocations));
        }
        catch (InvalidDataException)
        {
            throw new FormatException("Gzip dosyası açılamadı");
        }
        catch (XmlException ex)
        {
            throw new FormatException("Geçersiz XML: " + ex.Message);
        }
    }

    private static byte[] Gunzip(byte[] content)
    {
        using var gzip = new GZipStream(new MemoryStream(content), CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = gzip.Read(buffer)) > 0)
        {
            if (output.Length + read > MaxBytes)
                throw new FormatException($"Açılmış dosya {MaxBytes / (1024 * 1024)} MB'tan büyük");

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    /// <summary>Text sitemaps are plain UTF-8, so anything else that starts with '&lt;' is XML.</summary>
    private static bool IsXml(ReadOnlySpan<byte> content)
    {
        if (content is [0xEF, 0xBB, 0xBF, ..])
            content = content[3..];
        else if (content is [0xFF, 0xFE, ..] or [0xFE, 0xFF, ..])
            return true; // UTF-16 byte order mark

        content = content.TrimStart(" \t\r\n"u8);
        return content.Length > 0 && content[0] == (byte)'<';
    }

    private static (SitemapKind, List<string>) ReadXml(byte[] content, int maxLocations)
    {
        using var xml = XmlReader.Create(new MemoryStream(content), XmlSettings);
        xml.MoveToContent();
        var kind = xml.LocalName switch
        {
            "urlset" => SitemapKind.Pages,
            "sitemapindex" => SitemapKind.Index,
            _ => throw new FormatException($"Sitemap değil: kök öğe <{xml.Name}>, <urlset> ya da <sitemapindex> bekleniyordu"),
        };

        // Only <loc> directly under <url> or <sitemap>; extensions such as <image:loc> sit deeper.
        var entry = kind == SitemapKind.Pages ? "url" : "sitemap";
        var locations = new List<string>();
        var inEntry = false;
        while (!xml.EOF && locations.Count < maxLocations)
        {
            if (xml.NodeType == XmlNodeType.Element)
            {
                if (xml.Depth == 1)
                {
                    inEntry = xml.LocalName == entry;
                }
                else if (inEntry && xml.Depth == 2 && xml.LocalName == "loc")
                {
                    locations.Add(xml.ReadElementContentAsString().Trim());
                    continue; // the reader is already on the next node
                }
            }

            xml.Read();
        }

        return (kind, locations);
    }

    private static List<string> ReadLines(byte[] content, int maxLocations)
    {
        using var reader = new StreamReader(new MemoryStream(content), Encoding.UTF8);
        var locations = new List<string>();
        while (locations.Count < maxLocations && reader.ReadLine() is { } line)
        {
            if (line.Trim() is { Length: > 0 } location)
                locations.Add(location);
        }

        return locations;
    }
}
