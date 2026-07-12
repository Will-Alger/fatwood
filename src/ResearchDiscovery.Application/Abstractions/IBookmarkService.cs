namespace ResearchDiscovery.Application.Abstractions;

/// <summary>Toggles a user's bookmark state on a paper.</summary>
public interface IBookmarkService
{
    /// <summary>
    /// Sets the bookmark state for the given user. Returns false when no paper
    /// with that arXiv id exists; idempotent otherwise (bookmarking twice is a
    /// no-op).
    /// </summary>
    Task<bool> SetAsync(long userId, string arxivId, bool bookmarked, CancellationToken ct);
}
