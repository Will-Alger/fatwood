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

        if (query.CategoryCodes.Count > 0)
        {
            var codes = query.CategoryCodes;
            papers = papers.Where(p => p.PaperCategories.Any(pc => codes.Contains(pc.Category.Code)));
        }

        if (query.AnalyzedOnly)
        {
            papers = papers.Where(p => p.AnalysisResult != null);
        }

        var totalItems = await papers.CountAsync(ct);

        papers = query.Sort switch
        {
            PaperSortOrder.PublishedAsc =>
                papers.OrderBy(p => p.PublishedUtc).ThenBy(p => p.Id),
            // Unanalyzed papers (null score) sort last so a mixed listing still
            // leads with scored candidates.
            PaperSortOrder.ScoreDesc =>
                papers.OrderByDescending(p => p.AnalysisResult != null)
                    .ThenByDescending(p => p.AnalysisResult!.CompositeScore)
                    .ThenByDescending(p => p.PublishedUtc)
                    .ThenByDescending(p => p.Id),
            _ =>
                papers.OrderByDescending(p => p.PublishedUtc).ThenByDescending(p => p.Id),
        };

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
                Analysis = p.AnalysisResult == null ? null : new
                {
                    p.AnalysisResult.CompositeScore,
                    p.AnalysisResult.Model,
                    p.AnalysisResult.SchemaVersion,
                    p.AnalysisResult.CreatedUtc,
                    p.AnalysisResult.ResultJson,
                },
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
                r.Doi,
                r.Analysis == null ? null : new PaperAnalysisDto(
                    r.Analysis.CompositeScore,
                    r.Analysis.Model,
                    r.Analysis.SchemaVersion,
                    r.Analysis.CreatedUtc,
                    ParseDetails(r.Analysis.ResultJson))))
            .ToList();

        var totalPages = totalItems == 0 ? 0 : (int)Math.Ceiling(totalItems / (double)query.PageSize);
        return new PagedResult<PaperDto>(items, query.Page, query.PageSize, totalItems, totalPages);
    }

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
