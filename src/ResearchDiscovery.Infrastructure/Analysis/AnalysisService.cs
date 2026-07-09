using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ResearchDiscovery.Application.Abstractions;
using ResearchDiscovery.Application.Options;
using ResearchDiscovery.Domain.Entities;
using ResearchDiscovery.Infrastructure.Persistence;
using ResearchDiscovery.Infrastructure.Profile;

namespace ResearchDiscovery.Infrastructure.Analysis;

/// <summary>
/// Orchestrates bounded analysis runs — a category sweep or an explicit
/// selection (the top slice of a search). Results cache per (paper, schema
/// version, profile version): re-running is idempotent, and editing the
/// profile invalidates without deleting (rows re-analyze on demand). Each
/// result persists immediately so a cancelled run keeps its progress.
/// </summary>
public class AnalysisService(
    AppDbContext db,
    IPaperAnalyzer analyzer,
    ProfileService profileService,
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

        var profile = await profileService.GetAsync(ct);
        var profileVersion = profile?.Version ?? 0;

        var candidates = SelectStale(profileVersion)
            .Where(p => p.PaperCategories.Any(pc => pc.Category.Code == request.CategoryCode));

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
            "Analysis run for {Category}: {Count} paper(s) selected (max {Max}, since {Since}, profile v{ProfileVersion})",
            request.CategoryCode, papers.Count, request.MaxPapers, request.Since, profileVersion);

        return await RunAsync(request.CategoryCode, papers, profile, ct);
    }

    public async Task<AnalysisSummary> AnalyzeSelectionAsync(
        IReadOnlyList<string> arxivIds, CancellationToken ct)
    {
        var ids = arxivIds.Distinct(StringComparer.Ordinal).ToList();
        var profile = await profileService.GetAsync(ct);
        var profileVersion = profile?.Version ?? 0;

        var papers = await SelectStale(profileVersion)
            .Where(p => ids.Contains(p.ArxivId))
            .ToListAsync(ct);

        logger.LogInformation(
            "Selection analysis: {Stale} of {Requested} paper(s) need analysis (profile v{ProfileVersion})",
            papers.Count, ids.Count, profileVersion);

        return await RunAsync("selection", papers, profile, ct);
    }

    /// <summary>Papers whose analysis is missing or stale for the current schema + profile.</summary>
    private IQueryable<Paper> SelectStale(int profileVersion) =>
        db.Papers
            .Include(p => p.PrimaryCategory)
            .Include(p => p.PaperCategories).ThenInclude(pc => pc.Category)
            .Include(p => p.AnalysisResult)
            .Where(p => p.AnalysisResult == null
                || p.AnalysisResult.SchemaVersion < AnalysisOptions.CurrentSchemaVersion
                || p.AnalysisResult.ProfileVersion != profileVersion);

    private async Task<AnalysisSummary> RunAsync(
        string label, List<Paper> papers, UserProfile? profile, CancellationToken ct)
    {
        var profileDescription = ProfileService.Describe(profile);
        var profileVersion = profile?.Version ?? 0;

        var analyzed = 0;
        var declined = 0;
        var failed = 0;

        foreach (var paper in papers)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var analysis = await analyzer.AnalyzeAsync(
                    paper, profileDescription, profileVersion, ct);
                if (analysis is null)
                {
                    declined++;
                    continue;
                }

                if (paper.AnalysisResult is { } stale)
                {
                    stale.SchemaVersion = analysis.SchemaVersion;
                    stale.ProfileVersion = analysis.ProfileVersion;
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
                        ProfileVersion = analysis.ProfileVersion,
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

        return new AnalysisSummary(label, papers.Count, analyzed, declined, failed);
    }
}
