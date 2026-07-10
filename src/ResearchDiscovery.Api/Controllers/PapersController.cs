using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ResearchDiscovery.Api.Hosting;
using ResearchDiscovery.Application.Abstractions;
using ResearchDiscovery.Application.Dtos;
using ResearchDiscovery.Application.Options;
using ResearchDiscovery.Infrastructure.Persistence;
using ResearchDiscovery.Infrastructure.Profile;

namespace ResearchDiscovery.Api.Controllers;

/// <summary>
/// Browse endpoint — the only user-facing surface. Serves entirely from the
/// database; it cannot reach arXiv and cannot trigger ingestion.
/// </summary>
[ApiController]
[Route("api/papers")]
public class PapersController(IPaperQueryService queryService) : ControllerBase
{
    private const int MaxPageSize = 100;
    private const int DefaultPageSize = 25;

    [HttpGet]
    [ProducesResponseType<PagedResult<PaperDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetPapers(
        [FromQuery] string? categories,
        [FromQuery] bool bookmarkedOnly = false,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = DefaultPageSize,
        [FromQuery] string sort = "published_desc",
        [FromQuery] bool analyzedOnly = false,
        CancellationToken ct = default)
    {
        if (page < 1)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                detail: "page must be >= 1.");
        }

        PaperSortOrder? sortOrder = sort switch
        {
            "published_desc" => PaperSortOrder.PublishedDesc,
            "published_asc" => PaperSortOrder.PublishedAsc,
            "score_desc" => PaperSortOrder.ScoreDesc,
            _ => null,
        };

        if (sortOrder is null)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                detail: "sort must be one of: published_desc, published_asc, score_desc.");
        }

        // Unknown category codes simply match nothing they know; stale
        // bookmarked filters degrade gracefully instead of erroring.
        var codes = (categories ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var result = await queryService.GetPapersAsync(
            new PaperListQuery(
                codes, page, Math.Clamp(pageSize, 1, MaxPageSize), sortOrder.Value,
                analyzedOnly, bookmarkedOnly),
            ct);

        return Ok(result);
    }

    /// <summary>
    /// Bookmarks a paper. Idempotent; user state, not corpus data. Optional
    /// searchEventId/rank tie the action to the search that surfaced the
    /// paper — an implicit relevance label for the offline quality loop.
    /// </summary>
    [HttpPut("{arxivId}/bookmark")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> AddBookmark(
        string arxivId,
        [FromServices] IBookmarkService bookmarks,
        [FromServices] ISearchTelemetry telemetry,
        [FromQuery] long? searchEventId,
        [FromQuery] int? rank,
        CancellationToken ct) =>
        SetBookmarkAsync(arxivId, true, bookmarks, telemetry, searchEventId, rank, ct);

    [HttpDelete("{arxivId}/bookmark")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> RemoveBookmark(
        string arxivId,
        [FromServices] IBookmarkService bookmarks,
        [FromServices] ISearchTelemetry telemetry,
        [FromQuery] long? searchEventId,
        [FromQuery] int? rank,
        CancellationToken ct) =>
        SetBookmarkAsync(arxivId, false, bookmarks, telemetry, searchEventId, rank, ct);

    private async Task<IActionResult> SetBookmarkAsync(
        string arxivId,
        bool bookmarked,
        IBookmarkService bookmarks,
        ISearchTelemetry telemetry,
        long? searchEventId,
        int? rank,
        CancellationToken ct)
    {
        var found = await bookmarks.SetAsync(arxivId, bookmarked, ct);
        if (!found)
        {
            return Problem(statusCode: StatusCodes.Status404NotFound,
                detail: $"No paper with arXiv id '{arxivId}'.");
        }

        await telemetry.LogInteractionAsync(
            arxivId,
            bookmarked ? Domain.Entities.InteractionType.Bookmarked
                       : Domain.Entities.InteractionType.Unbookmarked,
            searchEventId, rank, ct);

        return NoContent();
    }

    /// <summary>
    /// Explicit negative feedback from search results. Purely a telemetry
    /// write — it doesn't (yet) filter anything server-side; the client hides
    /// the card locally. Negatives are the counterweight that keeps implicit
    /// labels from only ever saying "more of the same".
    /// </summary>
    [HttpPost("{arxivId}/not-interested")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> NotInterested(
        string arxivId,
        [FromServices] ISearchTelemetry telemetry,
        [FromQuery] long? searchEventId,
        [FromQuery] int? rank,
        CancellationToken ct)
    {
        await telemetry.LogInteractionAsync(
            arxivId, Domain.Entities.InteractionType.NotInterested, searchEventId, rank, ct);
        return NoContent();
    }

    public sealed record AnalysisStatusRequest(IReadOnlyList<string> ArxivIds);

    public sealed record AnalysisStatusView(bool Active, IReadOnlyList<string> Analyzed);

    /// <summary>
    /// Which of the given papers already have a current analysis (schema +
    /// profile version), plus whether an analysis job is running. Read-only,
    /// DB-only — this is what the UI's progress bar polls after "Analyze
    /// top N". A paper that never appears in Analyzed while Active is false
    /// was declined or failed.
    /// </summary>
    [HttpPost("analysis-status")]
    [ProducesResponseType<AnalysisStatusView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAnalysisStatus(
        [FromBody] AnalysisStatusRequest request,
        [FromServices] AppDbContext db,
        [FromServices] ProfileService profileService,
        [FromServices] AnalysisProgressTracker tracker,
        CancellationToken ct)
    {
        if (request.ArxivIds is not { Count: > 0 and <= 500 })
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                detail: "arxivIds must contain between 1 and 500 entries.");
        }

        var profile = await profileService.GetAsync(ct);
        var profileVersion = profile?.Version ?? 0;
        var ids = request.ArxivIds.ToList();

        var analyzed = await db.Papers
            .AsNoTracking()
            .Where(p => ids.Contains(p.ArxivId)
                && p.AnalysisResult != null
                && p.AnalysisResult.SchemaVersion >= AnalysisOptions.CurrentSchemaVersion
                && p.AnalysisResult.ProfileVersion == profileVersion)
            .Select(p => p.ArxivId)
            .ToListAsync(ct);

        return Ok(new AnalysisStatusView(tracker.Active, analyzed));
    }
}
