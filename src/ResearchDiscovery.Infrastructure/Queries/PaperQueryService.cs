using Microsoft.EntityFrameworkCore;
using ResearchDiscovery.Application.Abstractions;
using ResearchDiscovery.Application.Dtos;
using ResearchDiscovery.Infrastructure.Persistence;

namespace ResearchDiscovery.Infrastructure.Queries;

/// <summary>
/// Read-only browse queries, served entirely from the database. This class
/// (and the whole browse path) has no reference to the arXiv client:
/// browsing never calls arXiv and never triggers ingestion.
/// </summary>
public class PaperQueryService(AppDbContext db) : IPaperQueryService
{
    public async Task<PagedResult<PaperDto>> GetPapersAsync(PaperListQuery query, CancellationToken ct)
    {
        IQueryable<Domain.Entities.Paper> papers = db.Papers.AsNoTracking();

        if (query.CategoryCodes.Count > 0)
        {
            var codes = query.CategoryCodes;
            papers = papers.Where(p => p.PaperCategories.Any(pc => codes.Contains(pc.Category.Code)));
        }

        var totalItems = await papers.CountAsync(ct);

        papers = query.Sort == PaperSortOrder.PublishedAsc
            ? papers.OrderBy(p => p.PublishedUtc).ThenBy(p => p.Id)
            : papers.OrderByDescending(p => p.PublishedUtc).ThenByDescending(p => p.Id);

        var rows = await papers
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(p => new
            {
                p.ArxivId,
                p.Title,
                p.Abstract,
                p.Authors,
                PrimaryCategory = p.PrimaryCategory.Code,
                Categories = p.PaperCategories.Select(pc => pc.Category.Code).ToList(),
                p.PublishedUtc,
                p.UpdatedUtc,
                p.AbsUrl,
                p.PdfUrl,
                p.Doi,
            })
            .ToListAsync(ct);

        var items = rows
            .Select(r => new PaperDto(
                r.ArxivId,
                r.Title,
                r.Abstract,
                r.Authors.Split("; ", StringSplitOptions.RemoveEmptyEntries),
                r.PrimaryCategory,
                r.Categories,
                r.PublishedUtc,
                r.UpdatedUtc,
                r.AbsUrl,
                r.PdfUrl,
                r.Doi))
            .ToList();

        var totalPages = totalItems == 0 ? 0 : (int)Math.Ceiling(totalItems / (double)query.PageSize);
        return new PagedResult<PaperDto>(items, query.Page, query.PageSize, totalItems, totalPages);
    }

    public async Task<IReadOnlyList<CategoryDto>> GetCategoriesAsync(CancellationToken ct) =>
        await db.Categories
            .AsNoTracking()
            .OrderBy(c => c.Code)
            .Select(c => new CategoryDto(c.Code, c.Name, c.PaperCategories.Count))
            .ToListAsync(ct);
}
