using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ResearchDiscovery.Api.Auth;
using ResearchDiscovery.Api.Hosting;
using ResearchDiscovery.Application.Abstractions;
using ResearchDiscovery.Domain.Entities;
using ResearchDiscovery.Infrastructure.Persistence;

namespace ResearchDiscovery.Api.Controllers;

/// <summary>Ops-only ingestion triggers, admin-role gated.</summary>
[ApiController]
[Route("api/admin/ingestion")]
[Authorize(Policy = AuthPolicies.Owner)]
public class AdminIngestionController(IngestionJobQueue queue, AppDbContext db) : ControllerBase
{
    public sealed record BackfillRequest(int? WindowDays, int? MaxPapersPerCategory);

    public sealed record IngestionRunView(
        long Id,
        string Trigger,
        string Status,
        DateTimeOffset StartedUtc,
        DateTimeOffset? CompletedUtc,
        int PapersFetched,
        int PapersAdded,
        int PapersUpdated,
        string? Error);

    [HttpPost("backfill")]
    public IActionResult TriggerBackfill([FromBody] BackfillRequest? request = null)
    {
        var overrides = request is { WindowDays: not null } or { MaxPapersPerCategory: not null }
            ? new BackfillOverrides(request!.WindowDays, request.MaxPapersPerCategory)
            : null;

        return Enqueue(new IngestionJobRequest(IngestionTrigger.Backfill, overrides));
    }

    [HttpPost("delta")]
    public IActionResult TriggerDelta() =>
        Enqueue(new IngestionJobRequest(IngestionTrigger.Delta, null));

    [HttpGet("runs")]
    public async Task<IActionResult> GetRuns(CancellationToken ct)
    {
        var runs = await db.IngestionRuns
            .AsNoTracking()
            .OrderByDescending(r => r.StartedUtc)
            .Take(20)
            .Select(r => new IngestionRunView(
                r.Id,
                r.Trigger.ToString(),
                r.Status.ToString(),
                r.StartedUtc,
                r.CompletedUtc,
                r.PapersFetched,
                r.PapersAdded,
                r.PapersUpdated,
                r.Error))
            .ToListAsync(ct);

        return Ok(runs);
    }

    private IActionResult Enqueue(IngestionJobRequest job)
    {
        if (!queue.TryEnqueue(job))
        {
            return Problem(statusCode: StatusCodes.Status429TooManyRequests,
                detail: "The ingestion queue is full; try again once queued runs complete.");
        }

        return Accepted(value: new
        {
            message = $"{job.Trigger} run queued.",
            checkStatusAt = "/api/admin/ingestion/runs",
        });
    }
}
