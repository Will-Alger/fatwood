using ResearchDiscovery.Application.Dtos;

namespace ResearchDiscovery.Application.Abstractions;

public enum PaperSortOrder
{
    PublishedDesc = 0,
    PublishedAsc = 1,

    /// <summary>Composite analysis score, best first; unanalyzed papers sort last.</summary>
    ScoreDesc = 2,
}

/// <param name="UserId">Whose bookmarks/analyses to surface; null (anonymous)
/// sees corpus data only.</param>
/// <param name="WindowDays">Restrict to papers published in the last N days;
/// null = the whole corpus.</param>
public sealed record PaperListQuery(
    IReadOnlyList<string> CategoryCodes,
    int Page,
    int PageSize,
    PaperSortOrder Sort,
    bool AnalyzedOnly = false,
    bool BookmarkedOnly = false,
    long? UserId = null,
    int? WindowDays = null);

/// <summary>Read-only browse queries. Serves exclusively from the database; never touches arXiv.</summary>
public interface IPaperQueryService
{
    Task<PagedResult<PaperDto>> GetPapersAsync(PaperListQuery query, CancellationToken ct);

    /// <summary>DTOs for a specific id set (search results); keyed by paper id.
    /// Bookmark/analysis state is the given user's; null = anonymous.</summary>
    Task<IReadOnlyDictionary<long, PaperDto>> GetPapersByIdsAsync(
        IReadOnlyCollection<long> paperIds, long? userId, CancellationToken ct);

    /// <summary>DTOs keyed by arXiv id, with the caller's current bookmark/
    /// analysis state — used to fold freshly-completed analyses into a live
    /// result list without re-running the search.</summary>
    Task<IReadOnlyDictionary<string, PaperDto>> GetPapersByArxivIdsAsync(
        IReadOnlyCollection<string> arxivIds, long? userId, CancellationToken ct);

    Task<IReadOnlyList<CategoryDto>> GetCategoriesAsync(CancellationToken ct);
}
