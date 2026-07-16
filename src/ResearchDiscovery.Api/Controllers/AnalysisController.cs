using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ResearchDiscovery.Api.Auth;
using ResearchDiscovery.Application.Abstractions;

namespace ResearchDiscovery.Api.Controllers;

/// <summary>
/// User-facing paper analysis (Phase 2). Any active user can analyze papers
/// they picked — from search results or while browsing — and the token spend
/// lands on their own budget ledger. Ops-only BULK analysis (analyze a whole
/// category) stays under api/admin/analysis, Owner-gated.
/// </summary>
[ApiController]
[Route("api/analysis")]
[Authorize(Policy = AuthPolicies.ActiveUser)]
public class AnalysisController(IAnalysisQueue queue) : ControllerBase
{
    /// <param name="SearchEventId">The logged search the selection came from,
    /// when triggered from search results — telemetry context only. Null when
    /// analyzing a paper reached some other way (e.g. the browse tab).</param>
    public sealed record SelectionRequest(IReadOnlyList<string> ArxivIds, long? SearchEventId);

    /// <summary>
    /// Queues an explicit paper set for analysis — the "Analyze" action on
    /// search results and paper cards. Already-current analyses are skipped
    /// server-side, so the enqueued set costs at most its stale members.
    /// </summary>
    [HttpPost("selection")]
    [EnableRateLimiting("llm")]
    public async Task<IActionResult> Selection(
        [FromBody] SelectionRequest request,
        [FromServices] IBudgetService budget,
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

        var userId = HttpContext.GetAppUser()!.Id;

        // Gate at entry, mirroring search compile: reject up front when the
        // ledger is exhausted rather than queueing work that will no-op.
        // Per-paper metering still happens downstream via the usage ledger.
        try
        {
            await budget.EnsureCanSpendAsync(userId, ct);
        }
        catch (BudgetExceededException ex)
        {
            return Problem(statusCode: StatusCodes.Status402PaymentRequired, detail: ex.Message);
        }

        // Fan out to one work item per paper, in submitted (rank) order, so a
        // worker pool can drain them concurrently. Idempotent: an item for an
        // already-current analysis is a cheap no-op.
        await queue.EnqueueSelectionAsync(userId, request.ArxivIds, ct);

        // Spending analysis tokens on a paper is a strong interest signal;
        // record it against the originating search when known.
        if (request.SearchEventId is not null)
        {
            foreach (var arxivId in request.ArxivIds)
            {
                await telemetry.LogInteractionAsync(
                    userId, arxivId,
                    Domain.Entities.InteractionType.AnalyzedFromSearch,
                    request.SearchEventId, rank: null, ct);
            }
        }

        return Accepted(value: new
        {
            message = $"Analysis queued ({request.ArxivIds.Count} paper(s)).",
            checkStatusAt = "/api/papers/analysis-status",
        });
    }
}
