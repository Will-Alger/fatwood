namespace ResearchDiscovery.Application.Abstractions;

/// <summary>One page of an arXiv query for a single category and date range.</summary>
public sealed record ArxivQuery(
    string CategoryCode,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    int Start,
    int MaxResults);

public sealed record ArxivEntry(
    string ArxivId,
    int Version,
    string Title,
    string Abstract,
    IReadOnlyList<string> Authors,
    string PrimaryCategory,
    IReadOnlyList<string> Categories,
    DateTimeOffset Published,
    DateTimeOffset Updated,
    string AbsUrl,
    string PdfUrl,
    string? Doi,
    string? CodeUrl = null);

public sealed record ArxivPage(int TotalResults, IReadOnlyList<ArxivEntry> Entries);

/// <summary>
/// Client for the arXiv Atom query API. Implementations are responsible for
/// rate limiting and retry; callers just page through results.
/// </summary>
public interface IArxivClient
{
    Task<ArxivPage> QueryAsync(ArxivQuery query, CancellationToken ct);
}
