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
public sealed record PaperListQuery(
    IReadOnlyList<string> CategoryCodes,
    int Page,
    int PageSize,
    PaperSortOrder Sort,
    bool AnalyzedOnly = false,
    bool BookmarkedOnly = false,
    long? UserId = null);

/// <summary>Read-only browse queries. Serves exclusively from the database; never touches arXiv.</summary>
public interface IPaperQueryService
{
    Task<PagedResult<PaperDto>> GetPapersAsync(PaperListQuery query, CancellationToken ct);

    /// <summary>DTOs for a specific id set (search results); keyed by paper id.
    /// Bookmark/analysis state is the given user's; null = anonymous.</summary>
    Task<IReadOnlyDictionary<long, PaperDto>> GetPapersByIdsAsync(
        IReadOnlyCollection<long> paperIds, long? userId, CancellationToken ct);

    Task<IReadOnlyList<CategoryDto>> GetCategoriesAsync(CancellationToken ct);
}
