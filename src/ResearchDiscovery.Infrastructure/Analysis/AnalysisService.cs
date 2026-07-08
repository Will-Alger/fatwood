using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ResearchDiscovery.Application.Abstractions;
using ResearchDiscovery.Application.Options;
using ResearchDiscovery.Domain.Entities;
using ResearchDiscovery.Infrastructure.Persistence;

namespace ResearchDiscovery.Infrastructure.Analysis;

/// <summary>
/// Orchestrates one bounded analysis run: selects the newest not-yet-analyzed
/// papers in a single category, analyzes them one at a time, and persists
/// each result immediately so a cancelled run keeps its progress. Re-running
/// is idempotent — papers with a current-schema analysis are never re-sent
/// to the model, so no tokens are spent twice.
/// </summary>
public class AnalysisService(
    AppDbContext db,
    IPaperAnalyzer analyzer,
    ILogger<AnalysisService> logger) : IAnalysisService
{
    public async Task<AnalysisSummary> AnalyzeAsync(AnalysisRequest request, CancellationToken ct)
    {
        var categoryExists = await db.Categories
            .AnyAsync(c => c.Code == request.CategoryCode, ct);
        if (!categoryExists)
        {
            throw new UnknownCategoryException(request.CategoryCode);
        }

        var candidates = db.Papers
            .Include(p => p.PrimaryCategory)
            .Include(p => p.PaperCategories).ThenInclude(pc => pc.Category)
            .Include(p => p.AnalysisResult)
            .Where(p => p.PaperCategories.Any(pc => pc.Category.Code == request.CategoryCode))
            .Where(p => p.AnalysisResult == null
                || p.AnalysisResult.SchemaVersion < AnalysisOptions.CurrentSchemaVersion);

        if (request.Since is { } since)
        {
            candidates = candidates.Where(p => p.PublishedUtc >= since);
        }

        var papers = await candidates
            .OrderByDescending(p => p.PublishedUtc)
            .ThenByDescending(p => p.Id)
            .Take(request.MaxPapers)
            .ToListAsync(ct);

        logger.LogInformation(
            "Analysis run for {Category}: {Count} paper(s) selected (max {Max}, since {Since})",
            request.CategoryCode, papers.Count, request.MaxPapers, request.Since);

        var analyzed = 0;
        var declined = 0;
        var failed = 0;

        foreach (var paper in papers)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var analysis = await analyzer.AnalyzeAsync(paper, ct);
                if (analysis is null)
                {
                    declined++;
                    continue;
                }

                if (paper.AnalysisResult is { } stale)
                {
                    stale.SchemaVersion = analysis.SchemaVersion;
                    stale.Model = analysis.Model;
                    stale.ResultJson = analysis.ResultJson;
                    stale.CompositeScore = analysis.CompositeScore;
                    stale.CreatedUtc = DateTimeOffset.UtcNow;
                }
                else
                {
                    db.AnalysisResults.Add(new AnalysisResult
                    {
                        PaperId = paper.Id,
                        SchemaVersion = analysis.SchemaVersion,
                        Model = analysis.Model,
                        ResultJson = analysis.ResultJson,
                        CompositeScore = analysis.CompositeScore,
                        CreatedUtc = DateTimeOffset.UtcNow,
                    });
                }

                // Persist per paper: analysis runs take minutes and each
                // result costs real tokens — never lose completed work.
                await db.SaveChangesAsync(ct);
                analyzed++;

                logger.LogInformation(
                    "Analyzed {ArxivId} ({Done}/{Total}): score {Score} via {Model}",
                    paper.ArxivId, analyzed, papers.Count, analysis.CompositeScore, analysis.Model);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (DbUpdateException ex)
            {
                // Unique PaperId index tripped: a concurrent run analyzed this
                // paper first. Drop our copy and move on.
                db.ChangeTracker.Clear();
                failed++;
                logger.LogWarning(ex,
                    "Skipping {ArxivId}: analysis row already written by a concurrent run",
                    paper.ArxivId);
            }
            catch (Exception ex)
            {
                failed++;
                logger.LogError(ex, "Analysis of {ArxivId} failed", paper.ArxivId);
            }
        }

        return new AnalysisSummary(request.CategoryCode, papers.Count, analyzed, declined, failed);
    }
}
