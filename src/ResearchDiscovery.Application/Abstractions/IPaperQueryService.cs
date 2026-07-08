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
    bool AnalyzedOnly = false);

/// <summary>Read-only browse queries. Serves exclusively from the database; never touches arXiv.</summary>
public interface IPaperQueryService
{
    Task<PagedResult<PaperDto>> GetPapersAsync(PaperListQuery query, CancellationToken ct);

    Task<IReadOnlyList<CategoryDto>> GetCategoriesAsync(CancellationToken ct);
}
