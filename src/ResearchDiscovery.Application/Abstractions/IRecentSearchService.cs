namespace ResearchDiscovery.Application.Abstractions;

/// <summary>One entry in the caller's recent-search history (list view).</summary>
public sealed record RecentSearchSummary(
    long SearchEventId,
    DateTimeOffset CreatedUtc,
    string? QueryText,
    string Interpretation,
    int ResultCount,
    int TotalCandidates);

/// <summary>
/// A replayed search: the same plan and the same result ordering the ranker
/// produced originally, rebuilt from the logged event — NO re-ranking, no LLM.
/// Papers are re-hydrated with the caller's CURRENT bookmark/analysis state,
/// so a replayed card correctly reflects analyses run since the search.
/// Shaped to match the live /api/search response so the client reuses it verbatim.
/// </summary>
public sealed record RecentSearchReplay(
    long SearchEventId,
    SearchPlan Plan,
    IReadOnlyList<SearchHit> Hits,
    int TotalCandidates);

/// <summary>
/// Read-only history of a user's own executed searches, replayable to the
/// exact result set without touching the ranker. Every method is scoped to the
/// owning user — a caller can never read another account's searches.
/// </summary>
public interface IRecentSearchService
{
    Task<IReadOnlyList<RecentSearchSummary>> ListAsync(long userId, int limit, CancellationToken ct);

    /// <returns>The replay, or null when the event does not exist or is not owned by the caller.</returns>
    Task<RecentSearchReplay?> ReplayAsync(long userId, long searchEventId, CancellationToken ct);
}
