namespace ResearchDiscovery.Application.Abstractions;

/// <summary>Toggles the bookmark state of a paper.</summary>
public interface IBookmarkService
{
    /// <summary>
    /// Sets the bookmark state. Returns false when no paper with that arXiv id
    /// exists; idempotent otherwise (bookmarking twice is a no-op).
    /// </summary>
    Task<bool> SetAsync(string arxivId, bool bookmarked, CancellationToken ct);
}
