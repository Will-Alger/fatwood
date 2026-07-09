using Microsoft.AspNetCore.Mvc;
using ResearchDiscovery.Api.Filters;
using ResearchDiscovery.Application.Abstractions;
using ResearchDiscovery.Infrastructure.Profile;

namespace ResearchDiscovery.Api.Controllers;

/// <summary>
/// Smart search. Two endpoints with deliberately different postures:
/// - compile (admin key): the one LLM call, natural language → SearchPlan.
/// - search (public): executes a plan deterministically — DB + local
///   embeddings only, no tokens — so edited chips just re-submit the plan.
/// </summary>
[ApiController]
[Route("api/search")]
public class SearchController(
    ISearchService searchService,
    IPaperQueryService queryService,
    ProfileService profileService) : ControllerBase
{
    public sealed record CompileRequest(string Query);

    [HttpPost("compile")]
    [ServiceFilter(typeof(AdminApiKeyFilter))]
    public async Task<IActionResult> Compile(
        [FromBody] CompileRequest request,
        [FromServices] ISearchPlanCompiler compiler,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                detail: "query is required.");
        }

        var categories = await queryService.GetCategoriesAsync(ct);
        var profile = await profileService.GetAsync(ct);

        try
        {
            var plan = await compiler.CompileAsync(
                request.Query,
                ProfileService.Describe(profile),
                categories.Select(c => c.Code).ToList(),
                ct);
            return Ok(plan);
        }
        catch (InvalidOperationException ex)
        {
            return Problem(statusCode: StatusCodes.Status502BadGateway, detail: ex.Message);
        }
    }

    public sealed record SearchRequest(SearchPlan Plan, int? Limit);

    [HttpPost]
    public async Task<IActionResult> Search([FromBody] SearchRequest request, CancellationToken ct)
    {
        if (request.Plan is null || string.IsNullOrWhiteSpace(request.Plan.AnchorText))
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                detail: "plan.anchorText is required.");
        }

        var result = await searchService.SearchAsync(request.Plan, request.Limit ?? 30, ct);
        return Ok(result);
    }
}
