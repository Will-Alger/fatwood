using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ResearchDiscovery.Application.Abstractions;

namespace ResearchDiscovery.Infrastructure.Arxiv;

/// <summary>One OAI-PMH ListRecords page: live entries, the token for the next page (null on the final page), and the server's total count when advertised.</summary>
public sealed record OaiPage(
    IReadOnlyList<ArxivEntry> Entries,
    string? ResumptionToken,
    int? CompleteListSize);

/// <summary>
/// Parses arXiv OAI-PMH ListRecords responses (metadataPrefix=arXiv). Pure and
/// stateless so it can be unit tested against saved real responses. Unlike the
/// Atom format, OAI metadata carries no version number and date-only
/// timestamps; entries surface as version 1 at UTC midnight, which is safe
/// because the bulk harvest is add-only.
/// </summary>
public static partial class ArxivOaiParser
{
    private static readonly XNamespace Oai = "http://www.openarchives.org/OAI/2.0/";
    private static readonly XNamespace ArxivMeta = "http://arxiv.org/OAI/arXiv/";

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    // Same code-availability signal as the Atom path: the first repository URL
    // the authors chose to advertise in the comment or the abstract.
    [GeneratedRegex(@"https?://(?:www\.)?(?:github\.com|gitlab\.com|bitbucket\.org|huggingface\.co)/[^\s""'<>,;)\]}]+", RegexOptions.IgnoreCase)]
    private static partial Regex CodeUrlRegex();

    public static OaiPage Parse(string oaiXml)
    {
        var doc = XDocument.Parse(oaiXml);
        var root = doc.Element(Oai + "OAI-PMH")
            ?? throw new FormatException("Response is not an OAI-PMH document.");

        var error = root.Element(Oai + "error");
        if (error is not null)
        {
            var code = (string?)error.Attribute("code") ?? "unknown";

            // noRecordsMatch is not a failure — it is how OAI-PMH says "empty
            // result set" (e.g. a set with no records in the date window).
            if (code == "noRecordsMatch")
            {
                return new OaiPage([], null, null);
            }

            throw new FormatException($"OAI-PMH error {code}: {((string?)error)?.Trim()}");
        }

        var listRecords = root.Element(Oai + "ListRecords")
            ?? throw new FormatException("OAI-PMH response has no ListRecords element.");

        var entries = listRecords.Elements(Oai + "record")
            .Select(ParseRecord)
            .Where(e => e is not null)
            .Select(e => e!)
            .ToList();

        // The final page carries either no resumptionToken or an empty one
        // (the spec allows both); normalize to null so callers just loop on it.
        var tokenElement = listRecords.Element(Oai + "resumptionToken");
        var token = tokenElement?.Value.Trim();
        var completeListSize = (int?)tokenElement?.Attribute("completeListSize");

        return new OaiPage(
            entries,
            string.IsNullOrEmpty(token) ? null : token,
            completeListSize);
    }

    private static ArxivEntry? ParseRecord(XElement record)
    {
        // Withdrawn papers surface as header-only records; there is nothing to ingest.
        if ((string?)record.Element(Oai + "header")?.Attribute("status") == "deleted")
        {
            return null;
        }

        var meta = record.Element(Oai + "metadata")?.Element(ArxivMeta + "arXiv");
        var id = (string?)meta?.Element(ArxivMeta + "id");
        if (meta is null || string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var title = NormalizeWhitespace((string?)meta.Element(ArxivMeta + "title") ?? string.Empty);
        var abstractText = NormalizeWhitespace((string?)meta.Element(ArxivMeta + "abstract") ?? string.Empty);

        var authors = (meta.Element(ArxivMeta + "authors")?.Elements(ArxivMeta + "author") ?? [])
            .Select(FormatAuthor)
            .Where(n => n.Length > 0)
            .ToList();

        // <categories> is a single space-separated string; the first token is
        // the primary category (the OAI format has no separate element for it).
        var categories = ((string?)meta.Element(ArxivMeta + "categories") ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var primaryCategory = categories.FirstOrDefault() ?? "unknown";
        if (categories.Count == 0)
        {
            categories.Add(primaryCategory);
        }

        var created = ParseDate((string?)meta.Element(ArxivMeta + "created"));

        // <updated> only appears once a revision exists; a v1-only paper has
        // just <created>.
        var updated = ParseDate((string?)meta.Element(ArxivMeta + "updated")) ?? created;

        // Some records carry multiple space-separated DOIs; the first is the
        // primary publication.
        var doi = ((string?)meta.Element(ArxivMeta + "doi"))?
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        var comments = (string?)meta.Element(ArxivMeta + "comments") ?? string.Empty;
        var codeUrl = ExtractCodeUrl($"{comments} {abstractText}");

        return new ArxivEntry(
            id.Trim(),
            Version: 1,
            title,
            abstractText,
            authors,
            primaryCategory,
            categories,
            created ?? default,
            updated ?? default,
            AbsUrl: $"https://arxiv.org/abs/{id.Trim()}",
            PdfUrl: $"https://arxiv.org/pdf/{id.Trim()}",
            doi,
            codeUrl);
    }

    /// <summary>"Forenames Keyname"; collaborations and mononyms have only a keyname.</summary>
    private static string FormatAuthor(XElement author)
    {
        var keyname = NormalizeWhitespace((string?)author.Element(ArxivMeta + "keyname") ?? string.Empty);
        var forenames = NormalizeWhitespace((string?)author.Element(ArxivMeta + "forenames") ?? string.Empty);
        return forenames.Length > 0 ? $"{forenames} {keyname}".Trim() : keyname;
    }

    /// <summary>First advertised repository URL in the text, trailing punctuation trimmed.</summary>
    public static string? ExtractCodeUrl(string text)
    {
        var match = CodeUrlRegex().Match(text);
        if (!match.Success)
        {
            return null;
        }

        var url = match.Value.TrimEnd('.', ':');
        return url.Length <= 512 ? url : null;
    }

    // OAI arXiv dates are date-only ("2016-12-24") with no zone; treat as UTC midnight.
    private static DateTimeOffset? ParseDate(string? value) =>
        DateTime.TryParseExact(
            value?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date)
            ? new DateTimeOffset(date, TimeSpan.Zero)
            : null;

    private static string NormalizeWhitespace(string value) =>
        WhitespaceRegex().Replace(value, " ").Trim();
}
