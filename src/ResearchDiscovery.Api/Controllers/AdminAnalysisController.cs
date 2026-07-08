using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ResearchDiscovery.Api.Filters;
using ResearchDiscovery.Api.Hosting;
using ResearchDiscovery.Application.Abstractions;
using ResearchDiscovery.Application.Options;
using ResearchDiscovery.Infrastructure.Persistence;

namespace ResearchDiscovery.Api.Controllers;

/// <summary>
/// Ops-only analysis triggers (Phase 2). Same admin posture as ingestion:
/// every action requires the X-Admin-Api-Key header, and with no key
/// configured the controller answers 404. Analysis is never reachable by
/// regular users — cost control by construction.
/// </summary>
[ApiController]
[Route("api/admin/analysis")]
[ServiceFilter(typeof(AdminApiKeyFilter))]
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

        if (!queue.TryEnqueue(job))
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
