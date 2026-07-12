using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ResearchDiscovery.Api.Auth;
using ResearchDiscovery.Application.Abstractions;
using ResearchDiscovery.Infrastructure.Profile;

namespace ResearchDiscovery.Api.Controllers;

/// <summary>
/// Smart search. Two endpoints with deliberately different postures:
/// - compile (signed-in, budget-gated): the one LLM call, natural language →
///   SearchPlan.
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
    [Authorize(Policy = AuthPolicies.ActiveUser)]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("llm")]
    public async Task<IActionResult> Compile(
        [FromBody] CompileRequest request,
        [FromServices] ISearchPlanCompiler compiler,
        [FromServices] IBudgetService budget,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                detail: "query is required.");
        }

        var categories = await queryService.GetCategoriesAsync(ct);
        var profile = await profileService.GetAsync(HttpContext.GetAppUser()!.Id, ct);

        try
        {
            await budget.EnsureCanSpendAsync(HttpContext.GetAppUser()!.Id, ct);

            var plan = await compiler.CompileAsync(
                request.Query,
                ProfileService.Describe(profile),
                categories.Select(c => c.Code).ToList(),
                ct);
            return Ok(plan);
        }
        catch (BudgetExceededException ex)
        {
            return Problem(statusCode: StatusCodes.Status402PaymentRequired, detail: ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Problem(statusCode: StatusCodes.Status502BadGateway, detail: ex.Message);
        }
        catch (Anthropic.Exceptions.AnthropicUnauthorizedException)
        {
            return Problem(statusCode: StatusCodes.Status502BadGateway,
                detail: "The Anthropic API rejected the credentials. Set ANTHROPIC_API_KEY in the API's environment.");
        }
        catch (Anthropic.Exceptions.AnthropicApiException ex)
        {
            return Problem(statusCode: StatusCodes.Status502BadGateway,
                detail: $"The Anthropic API call failed: {ex.Message}");
        }
    }

    /// <param name="QueryText">The original prose the plan was compiled from,
    /// when known — captured into telemetry so `eval adopt` can turn real
    /// searches into eval queries.</param>
    public sealed record SearchRequest(SearchPlan Plan, int? Limit, string? QueryText);

    [HttpPost]
    public async Task<IActionResult> Search(
        [FromBody] SearchRequest request,
        [FromServices] ISearchTelemetry telemetry,
        CancellationToken ct)
    {
        if (request.Plan is null || string.IsNullOrWhiteSpace(request.Plan.AnchorText))
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                detail: "plan.anchorText is required.");
        }

        var userId = HttpContext.GetAppUser()?.Id;
        var result = await searchService.SearchAsync(request.Plan, request.Limit ?? 30, userId, ct);

        // Product searches are logged (the eval CLI calls ISearchService
        // directly and never lands here). The event id goes back to the client
        // so bookmark/analyze actions can carry their search context.
        var searchEventId = await telemetry.LogSearchAsync(
            userId, request.QueryText, request.Plan, result, ct);

        return Ok(new
        {
            searchEventId,
            plan = result.Plan,
            hits = result.Hits,
            totalCandidates = result.TotalCandidates,
        });
    }
}
