using ResearchDiscovery.Application.Dtos;

namespace ResearchDiscovery.Application.Abstractions;

public enum PaperSortOrder
{
    PublishedDesc = 0,
    PublishedAsc = 1,

    /// <summary>Composite analysis score, best first; unanalyzed papers sort last.</summary>
    ScoreDesc = 2,
}

public sealed record PaperListQuery(
    IReadOnlyList<string> CategoryCodes,
    int Page,
    int PageSize,
    PaperSortOrder Sort,
    bool AnalyzedOnly = false,
    bool BookmarkedOnly = false);

/// <summary>Read-only browse queries. Serves exclusively from the database; never touches arXiv.</summary>
public interface IPaperQueryService
{
    Task<PagedResult<PaperDto>> GetPapersAsync(PaperListQuery query, CancellationToken ct);

    /// <summary>DTOs for a specific id set (search results); keyed by paper id.</summary>
    Task<IReadOnlyDictionary<long, PaperDto>> GetPapersByIdsAsync(
        IReadOnlyCollection<long> paperIds, CancellationToken ct);

    Task<IReadOnlyList<CategoryDto>> GetCategoriesAsync(CancellationToken ct);
}
