using Microsoft.AspNetCore.Mvc;
using ResearchDiscovery.Application.Abstractions;
using ResearchDiscovery.Application.Dtos;

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
                codes, page, Math.Clamp(pageSize, 1, MaxPageSize), sortOrder.Value, analyzedOnly),
            ct);

        return Ok(result);
    }
}
