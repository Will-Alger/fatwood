using System.Text.RegularExpressions;
using System.Xml.Linq;
using ResearchDiscovery.Application.Abstractions;

namespace ResearchDiscovery.Infrastructure.Arxiv;

/// <summary>
/// Parses arXiv Atom query responses. Pure and stateless so it can be unit
/// tested against saved real responses.
/// </summary>
public static partial class ArxivAtomParser
{
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace ArxivNs = "http://arxiv.org/schemas/atom";
    private static readonly XNamespace OpenSearch = "http://a9.com/-/spec/opensearch/1.1/";

    [GeneratedRegex(@"^(?<id>.+?)v(?<version>\d+)$")]
    private static partial Regex VersionedIdRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    public static ArxivPage Parse(string atomXml)
    {
        var doc = XDocument.Parse(atomXml);
        var feed = doc.Element(Atom + "feed")
            ?? throw new FormatException("Response is not an Atom feed.");

        var totalResults = (int?)feed.Element(OpenSearch + "totalResults") ?? 0;

        var entries = feed.Elements(Atom + "entry")
            .Select(ParseEntry)
            .Where(e => e is not null)
            .Select(e => e!)
            .ToList();

        return new ArxivPage(totalResults, entries);
    }

    private static ArxivEntry? ParseEntry(XElement entry)
    {
        var rawId = (string?)entry.Element(Atom + "id");
        if (string.IsNullOrWhiteSpace(rawId))
        {
            return null;
        }

        // e.g. "http://arxiv.org/abs/2506.00764v1" or ".../abs/math/0309136v2"
        var absIndex = rawId.IndexOf("/abs/", StringComparison.Ordinal);
        if (absIndex < 0)
        {
            // arXiv reports query errors as a pseudo-entry whose id is not an abs URL.
            return null;
        }

        var versionedId = rawId[(absIndex + "/abs/".Length)..];
        var match = VersionedIdRegex().Match(versionedId);
        var (arxivId, version) = match.Success
            ? (match.Groups["id"].Value, int.Parse(match.Groups["version"].Value))
            : (versionedId, 1);

        var title = NormalizeWhitespace((string?)entry.Element(Atom + "title") ?? string.Empty);
        var summary = NormalizeWhitespace((string?)entry.Element(Atom + "summary") ?? string.Empty);

        var authors = entry.Elements(Atom + "author")
            .Select(a => NormalizeWhitespace((string?)a.Element(Atom + "name") ?? string.Empty))
            .Where(n => n.Length > 0)
            .ToList();

        var categories = entry.Elements(Atom + "category")
            .Select(c => (string?)c.Attribute("term"))
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var primaryCategory = (string?)entry.Element(ArxivNs + "primary_category")?.Attribute("term")
            ?? categories.FirstOrDefault()
            ?? "unknown";

        if (!categories.Contains(primaryCategory, StringComparer.Ordinal))
        {
            categories.Insert(0, primaryCategory);
        }

        var published = (DateTimeOffset?)entry.Element(Atom + "published") ?? default;
        var updated = (DateTimeOffset?)entry.Element(Atom + "updated") ?? published;

        var links = entry.Elements(Atom + "link").ToList();
        var absUrl = links
                .FirstOrDefault(l => (string?)l.Attribute("rel") == "alternate")
                ?.Attribute("href")?.Value
            ?? rawId;
        var pdfUrl = links
                .FirstOrDefault(l => (string?)l.Attribute("title") == "pdf")
                ?.Attribute("href")?.Value
            ?? absUrl.Replace("/abs/", "/pdf/", StringComparison.Ordinal);

        var doi = (string?)entry.Element(ArxivNs + "doi");

        return new ArxivEntry(
            arxivId,
            version,
            title,
            summary,
            authors,
            primaryCategory,
            categories,
            published,
            updated,
            absUrl,
            pdfUrl,
            string.IsNullOrWhiteSpace(doi) ? null : doi);
    }

    private static string NormalizeWhitespace(string value) =>
        WhitespaceRegex().Replace(value, " ").Trim();
}
