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
[Authorize(Policy = AuthPolicies.Admin)]
public class AdminSettingsController(
    ILlmSettingsService llmSettings,
    ProfileService profileService) : ControllerBase
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

    public sealed record ProfileView(
        string ExperienceSummary, string Goals, int? WeeklyHours, int Version,
        DateTimeOffset? UpdatedUtc);

    [HttpGet("profile")]
    public async Task<IActionResult> GetProfile(CancellationToken ct)
    {
        var profile = await profileService.GetAsync(ct);
        return Ok(profile is null
            ? new ProfileView(string.Empty, string.Empty, null, 0, null)
            : new ProfileView(
                profile.ExperienceSummary, profile.Goals, profile.WeeklyHours,
                profile.Version, profile.UpdatedUtc));
    }

    public sealed record SaveProfileRequest(
        string ExperienceSummary, string Goals, int? WeeklyHours);

    [HttpPut("profile")]
    public async Task<IActionResult> SaveProfile(
        [FromBody] SaveProfileRequest request, CancellationToken ct)
    {
        if (request.WeeklyHours is < 0 or > 100)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                detail: "weeklyHours must be between 0 and 100.");
        }

        var profile = await profileService.SaveAsync(
            request.ExperienceSummary?.Trim() ?? string.Empty,
            request.Goals?.Trim() ?? string.Empty,
            request.WeeklyHours,
            ct);

        return Ok(new ProfileView(
            profile.ExperienceSummary, profile.Goals, profile.WeeklyHours,
            profile.Version, profile.UpdatedUtc));
    }
}
