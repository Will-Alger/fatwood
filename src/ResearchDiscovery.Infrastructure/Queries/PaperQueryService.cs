using System.Text.Json;
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
        var userId = query.UserId;

        if (query.CategoryCodes.Count > 0)
        {
            var codes = query.CategoryCodes;
            papers = papers.Where(p => p.PaperCategories.Any(pc => codes.Contains(pc.Category.Code)));
        }

        // Analyses and bookmarks are per-user; anonymous callers using these
        // filters simply match nothing.
        if (query.AnalyzedOnly)
        {
            papers = papers.Where(p => p.AnalysisResults.Any(a => a.UserId == userId));
        }

        if (query.BookmarkedOnly)
        {
            papers = papers.Where(p => p.Bookmarks.Any(b => b.UserId == userId));
        }

        var totalItems = await papers.CountAsync(ct);

        papers = query.Sort switch
        {
            PaperSortOrder.PublishedAsc =>
                papers.OrderBy(p => p.PublishedUtc).ThenBy(p => p.Id),
            // Unanalyzed papers (null score) sort last so a mixed listing still
            // leads with scored candidates.
            PaperSortOrder.ScoreDesc =>
                papers.OrderByDescending(p => p.AnalysisResults.Any(a => a.UserId == userId))
                    .ThenByDescending(p => p.AnalysisResults
                        .Where(a => a.UserId == userId)
                        .Select(a => a.CompositeScore)
                        .FirstOrDefault())
                    .ThenByDescending(p => p.PublishedUtc)
                    .ThenByDescending(p => p.Id),
            _ =>
                papers.OrderByDescending(p => p.PublishedUtc).ThenByDescending(p => p.Id),
        };

        var rows = await ProjectRows(
                papers.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize), userId)
            .ToListAsync(ct);

        var items = rows.Select(r => ToDto(r)).ToList();

        var totalPages = totalItems == 0 ? 0 : (int)Math.Ceiling(totalItems / (double)query.PageSize);
        return new PagedResult<PaperDto>(items, query.Page, query.PageSize, totalItems, totalPages);
    }

    public async Task<IReadOnlyDictionary<long, PaperDto>> GetPapersByIdsAsync(
        IReadOnlyCollection<long> paperIds, long? userId, CancellationToken ct)
    {
        var ids = paperIds.ToList();
        var rows = await ProjectRows(
                db.Papers.AsNoTracking().Where(p => ids.Contains(p.Id)), userId)
            .ToListAsync(ct);

        return rows.ToDictionary(r => r.Id, r => ToDto(r));
    }

    private static IQueryable<PaperRow> ProjectRows(
        IQueryable<Domain.Entities.Paper> papers, long? userId) =>
        papers.Select(p => new PaperRow(
            p.Id,
            p.ArxivId,
            p.Title,
            p.Abstract,
            p.Authors,
            p.PrimaryCategory.Code,
            p.PaperCategories.Select(pc => pc.Category.Code).ToList(),
            p.PublishedUtc,
            p.UpdatedUtc,
            p.AbsUrl,
            p.PdfUrl,
            p.Doi,
            p.CodeUrl,
            userId != null && p.Bookmarks.Any(b => b.UserId == userId),
            p.AnalysisResults
                .Where(a => a.UserId == userId)
                .Select(a => new AnalysisRow(
                    a.CompositeScore,
                    a.Model,
                    a.SchemaVersion,
                    a.CreatedUtc,
                    a.ResultJson))
                .FirstOrDefault()));

    private static PaperDto ToDto(PaperRow r) =>
        new(
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
            r.Doi,
            r.Analysis == null ? null : new PaperAnalysisDto(
                r.Analysis.CompositeScore,
                r.Analysis.Model,
                r.Analysis.SchemaVersion,
                r.Analysis.CreatedUtc,
                ParseDetails(r.Analysis.ResultJson)),
            r.CodeUrl,
            r.IsBookmarked);

    private sealed record PaperRow(
        long Id,
        string ArxivId,
        string Title,
        string Abstract,
        string Authors,
        string PrimaryCategory,
        List<string> Categories,
        DateTimeOffset PublishedUtc,
        DateTimeOffset UpdatedUtc,
        string AbsUrl,
        string PdfUrl,
        string? Doi,
        string? CodeUrl,
        bool IsBookmarked,
        AnalysisRow? Analysis);

    private sealed record AnalysisRow(
        decimal? CompositeScore,
        string Model,
        int SchemaVersion,
        DateTimeOffset CreatedUtc,
        string ResultJson);

    /// <summary>
    /// The stored JSON is model-produced and schema-validated at write time,
    /// but guard parsing anyway so one corrupt row can't break a whole page.
    /// </summary>
    private static JsonElement ParseDetails(string resultJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(resultJson);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            using var empty = JsonDocument.Parse("{}");
            return empty.RootElement.Clone();
        }
    }

    public async Task<IReadOnlyList<CategoryDto>> GetCategoriesAsync(CancellationToken ct) =>
        await db.Categories
            .AsNoTracking()
            .OrderBy(c => c.Code)
            .Select(c => new CategoryDto(c.Code, c.Name, c.PaperCategories.Count))
            .ToListAsync(ct);
}
