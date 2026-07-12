using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ResearchDiscovery.Api.Auth;
using ResearchDiscovery.Api.Hosting;
using ResearchDiscovery.Application.Abstractions;
using ResearchDiscovery.Application.Options;
using ResearchDiscovery.Infrastructure.Persistence;

namespace ResearchDiscovery.Api.Controllers;

/// <summary>
/// Ops-only analysis triggers (Phase 2), admin-role gated. Bulk analysis
/// spends real tokens per paper; opening selection runs to regular
/// (budget-gated) users is planned with the multi-user data model.
/// </summary>
[ApiController]
[Route("api/admin/analysis")]
[Authorize(Policy = AuthPolicies.Admin)]
public class AdminAnalysisController(
    AnalysisJobQueue queue,
    AppDbContext db,
    IOptions<AnalysisOptions> options) : ControllerBase
{
    public sealed record RunRequest(string CategoryCode, int? MaxPapers, int? SinceDays);

    public sealed record CategoryCoverageView(
        string CategoryCode,
        int TotalPapers,
        int AnalyzedPapers);

    [HttpPost("run")]
    public async Task<IActionResult> TriggerRun([FromBody] RunRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.CategoryCode))
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                detail: "categoryCode is required.");
        }

        if (request.MaxPapers is < 1 or > 500)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                detail: "maxPapers must be between 1 and 500.");
        }

        // Unlike browse filters, an unknown category on an analysis trigger is
        // an operator mistake — fail loudly instead of queueing a no-op.
        var exists = await db.Categories.AnyAsync(c => c.Code == request.CategoryCode, ct);
        if (!exists)
        {
            return Problem(statusCode: StatusCodes.Status404NotFound,
                detail: $"Unknown category code '{request.CategoryCode}'.");
        }

        var job = new AnalysisRequest(
            request.CategoryCode,
            request.MaxPapers ?? options.Value.DefaultMaxPapers,
            request.SinceDays is { } d and > 0 ? DateTimeOffset.UtcNow.AddDays(-d) : null);

        if (!queue.TryEnqueue(new AnalysisJob.Category(job)
        {
            RequestedByUserId = HttpContext.GetAppUser()?.Id,
        }))
        {
            return Problem(statusCode: StatusCodes.Status429TooManyRequests,
                detail: "The analysis queue is full; try again once queued runs complete.");
        }

        return Accepted(value: new
        {
            message = $"Analysis run for {job.CategoryCode} queued ({job.MaxPapers} paper cap).",
            checkStatusAt = "/api/admin/analysis/coverage",
        });
    }

    /// <param name="SearchEventId">The logged search the selection came from,
    /// when triggered from search results — telemetry context only.</param>
    public sealed record SelectionRequest(IReadOnlyList<string> ArxivIds, long? SearchEventId);

    /// <summary>
    /// Analyzes an explicit paper set — the "Analyze top N" action on search
    /// results. Already-current analyses are skipped server-side, so the
    /// enqueued set costs at most its stale members.
    /// </summary>
    [HttpPost("selection")]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("llm")]
    public async Task<IActionResult> TriggerSelection(
        [FromBody] SelectionRequest request,
        [FromServices] ISearchTelemetry telemetry,
        CancellationToken ct)
    {
        if (request.ArxivIds is not { Count: > 0 })
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                detail: "arxivIds must be a non-empty list.");
        }

        if (request.ArxivIds.Count > 200)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                detail: "At most 200 papers per selection run.");
        }

        if (!queue.TryEnqueue(new AnalysisJob.Selection(request.ArxivIds)
        {
            RequestedByUserId = HttpContext.GetAppUser()?.Id,
        }))
        {
            return Problem(statusCode: StatusCodes.Status429TooManyRequests,
                detail: "The analysis queue is full; try again once queued runs complete.");
        }

        // Spending analysis tokens on a paper is a strong interest signal;
        // record it against the originating search when known (ranks resolve
        // from the logged results).
        if (request.SearchEventId is not null)
        {
            foreach (var arxivId in request.ArxivIds)
            {
                await telemetry.LogInteractionAsync(
                    arxivId, Domain.Entities.InteractionType.AnalyzedFromSearch,
                    request.SearchEventId, rank: null, ct);
            }
        }

        return Accepted(value: new
        {
            message = $"Selection analysis queued ({request.ArxivIds.Count} paper(s)).",
            checkStatusAt = "/api/admin/analysis/coverage",
        });
    }

    /// <summary>Per-category analyzed/total counts — the run-progress view.</summary>
    [HttpGet("coverage")]
    public async Task<IActionResult> GetCoverage(CancellationToken ct)
    {
        var coverage = await db.Categories
            .AsNoTracking()
            .OrderBy(c => c.Code)
            .Select(c => new CategoryCoverageView(
                c.Code,
                c.PaperCategories.Count,
                c.PaperCategories.Count(pc => pc.Paper.AnalysisResult != null)))
            .ToListAsync(ct);

        return Ok(coverage);
    }
}
