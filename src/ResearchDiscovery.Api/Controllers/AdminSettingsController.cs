using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ResearchDiscovery.Api.Auth;
using ResearchDiscovery.Application.Abstractions;
using ResearchDiscovery.Infrastructure.Profile;

namespace ResearchDiscovery.Api.Controllers;

/// <summary>
/// LLM model assignments and the user profile — both spend-adjacent, both
/// admin-role gated. The registry ships pricing so the UI can show live cost
/// estimates next to every action button.
/// </summary>
[ApiController]
[Route("api/admin/settings")]
[Authorize(Policy = AuthPolicies.Owner)]
public class AdminSettingsController(ILlmSettingsService llmSettings) : ControllerBase
{
    /// <summary>Rough per-paper token sizes for client-side analysis cost estimates.</summary>
    private const int EstInputTokensPerPaper = 1400;
    private const int EstOutputTokensPerPaper = 700;
    private const int EstCompileInputTokens = 900;
    private const int EstCompileOutputTokens = 350;

    public sealed record ModelView(
        string Id, string DisplayName, decimal InputPerMTok, decimal OutputPerMTok);

    public sealed record AssignmentView(string Step, string ModelId, bool IsDefault);

    public sealed record LlmSettingsView(
        IReadOnlyList<ModelView> Registry,
        IReadOnlyList<AssignmentView> Assignments,
        int EstAnalysisInputTokensPerPaper,
        int EstAnalysisOutputTokensPerPaper,
        int EstCompileInputTokens,
        int EstCompileOutputTokens);

    [HttpGet("llm")]
    public async Task<IActionResult> GetLlmSettings(CancellationToken ct)
    {
        var assignments = await llmSettings.GetAssignmentsAsync(ct);
        return Ok(new LlmSettingsView(
            llmSettings.Registry
                .Select(m => new ModelView(m.Id, m.DisplayName, m.InputPerMTok, m.OutputPerMTok))
                .ToList(),
            assignments
                .Select(a => new AssignmentView(a.Step, a.Model.Id, a.IsDefault))
                .ToList(),
            EstInputTokensPerPaper,
            EstOutputTokensPerPaper,
            EstCompileInputTokens,
            EstCompileOutputTokens));
    }

    public sealed record SetAssignmentRequest(string Step, string ModelId);

    [HttpPut("llm")]
    public async Task<IActionResult> SetLlmAssignment(
        [FromBody] SetAssignmentRequest request, CancellationToken ct)
    {
        try
        {
            await llmSettings.SetAssignmentAsync(request.Step, request.ModelId, ct);
        }
        catch (ArgumentException ex)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, detail: ex.Message);
        }

        return NoContent();
    }

    // Profile endpoints moved to MeController: profiles are per-user state,
    // not admin settings.
}
