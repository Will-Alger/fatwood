namespace ResearchDiscovery.Application.Abstractions;

/// <summary>
/// Client for the arXiv OAI-PMH bulk-metadata endpoint. This is the sanctioned
/// path for harvesting the full corpus; the Atom query API (<see cref="IArxivClient"/>)
/// stays reserved for small windowed queries. Implementations own pacing and
/// 503 retry; callers just loop on resumption tokens.
/// </summary>
public interface IArxivOaiClient
{
    /// <summary>Fetches the first ListRecords page for one OAI set from a UTC date (raw XML).</summary>
    Task<string> ListRecordsAsync(string set, DateTimeOffset fromUtc, CancellationToken ct);

    /// <summary>Fetches the next page for a resumption token (raw XML). Tokens are self-contained: no set or date is re-sent.</summary>
    Task<string> ResumeAsync(string resumptionToken, CancellationToken ct);
}
