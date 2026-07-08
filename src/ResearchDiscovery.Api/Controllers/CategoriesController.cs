using Microsoft.AspNetCore.Mvc;
using ResearchDiscovery.Application.Abstractions;
using ResearchDiscovery.Application.Dtos;

namespace ResearchDiscovery.Api.Controllers;

[ApiController]
[Route("api/categories")]
public class CategoriesController(IPaperQueryService queryService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<CategoryDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCategories(CancellationToken ct) =>
        Ok(await queryService.GetCategoriesAsync(ct));
}
