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

    /// <summary>Bookmarks a paper. Idempotent; user state, not corpus data.</summary>
    [HttpPut("{arxivId}/bookmark")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> AddBookmark(
        string arxivId, [FromServices] IBookmarkService bookmarks, CancellationToken ct) =>
        SetBookmarkAsync(arxivId, true, bookmarks, ct);

    [HttpDelete("{arxivId}/bookmark")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> RemoveBookmark(
        string arxivId, [FromServices] IBookmarkService bookmarks, CancellationToken ct) =>
        SetBookmarkAsync(arxivId, false, bookmarks, ct);

    private async Task<IActionResult> SetBookmarkAsync(
        string arxivId, bool bookmarked, IBookmarkService bookmarks, CancellationToken ct)
    {
        var found = await bookmarks.SetAsync(arxivId, bookmarked, ct);
        return found
            ? NoContent()
            : Problem(statusCode: StatusCodes.Status404NotFound,
                detail: $"No paper with arXiv id '{arxivId}'.");
    }
}
