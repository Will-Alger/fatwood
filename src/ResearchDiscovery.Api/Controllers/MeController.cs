using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ResearchDiscovery.Api.Auth;
using ResearchDiscovery.Application.Abstractions;
using ResearchDiscovery.Domain.Entities;
using ResearchDiscovery.Infrastructure.Profile;

namespace ResearchDiscovery.Api.Controllers;

/// <summary>
/// The signed-in user's own account: identity, role, budget, preferences.
/// Everything here is scoped to the caller — admin operations on OTHER
/// accounts live under api/admin.
/// </summary>
[ApiController]
[Route("api/me")]
[Authorize]
public class MeController(
    IUserAccountService accounts,
    IBudgetService budgetService) : ControllerBase
{
    public sealed record BudgetView(
        long GrantedMicros, long SpentMicros, long RemainingMicros, bool Unlimited);

    public sealed record MeView(
        long Id,
        string Email,
        string DisplayName,
        string Role,
        bool IsActive,
        string? Theme,
        BudgetView Budget);

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var user = HttpContext.GetAppUser()!;
        var budget = await budgetService.GetStatusAsync(user.Id, ct);

        return Ok(new MeView(
            user.Id,
            user.Email,
            user.DisplayName,
            user.Role.ToString(),
            user.IsActive,
            user.ThemePreference,
            new BudgetView(
                budget.GrantedMicros, budget.SpentMicros, budget.RemainingMicros,
                budget.Unlimited)));
    }

    public sealed record ThemeRequest(string? Theme);

    [HttpPut("theme")]
    public async Task<IActionResult> SetTheme([FromBody] ThemeRequest request, CancellationToken ct)
    {
        if (request.Theme is not (null or "dark" or "light"))
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                detail: "theme must be \"dark\", \"light\", or null.");
        }

        await accounts.SetThemeAsync(HttpContext.GetAppUser()!.Id, request.Theme, ct);
        return NoContent();
    }

    public sealed record ProfileView(
        string ExperienceSummary, string Goals, int? WeeklyHours, int Version,
        DateTimeOffset? UpdatedUtc);

    /// <summary>The caller's own research profile — drives analysis personalization.</summary>
    [HttpGet("profile")]
    public async Task<IActionResult> GetProfile(
        [FromServices] ProfileService profileService, CancellationToken ct)
    {
        var profile = await profileService.GetAsync(HttpContext.GetAppUser()!.Id, ct);
        return Ok(profile is null
            ? new ProfileView(string.Empty, string.Empty, null, 0, null)
            : new ProfileView(
                profile.ExperienceSummary, profile.Goals, profile.WeeklyHours,
                profile.Version, profile.UpdatedUtc));
    }

    public sealed record SaveProfileRequest(
        string ExperienceSummary, string Goals, int? WeeklyHours);

    [HttpPut("profile")]
    [Authorize(Policy = AuthPolicies.ActiveUser)]
    public async Task<IActionResult> SaveProfile(
        [FromBody] SaveProfileRequest request,
        [FromServices] ProfileService profileService,
        CancellationToken ct)
    {
        if (request.WeeklyHours is < 0 or > 100)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                detail: "weeklyHours must be between 0 and 100.");
        }

        var profile = await profileService.SaveAsync(
            HttpContext.GetAppUser()!.Id,
            request.ExperienceSummary?.Trim() ?? string.Empty,
            request.Goals?.Trim() ?? string.Empty,
            request.WeeklyHours,
            ct);

        return Ok(new ProfileView(
            profile.ExperienceSummary, profile.Goals, profile.WeeklyHours,
            profile.Version, profile.UpdatedUtc));
    }

    public sealed record InviteRequest(string Code);

    /// <summary>
    /// Redeems an invite code to activate a gated account. Idempotent for
    /// already-active users.
    /// </summary>
    [HttpPost("invite")]
    public async Task<IActionResult> RedeemInvite(
        [FromBody] InviteRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Code))
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                detail: "code is required.");
        }

        var redeemed = await accounts.RedeemInviteAsync(
            HttpContext.GetAppUser()!.Id, request.Code.Trim(), ct);

        return redeemed
            ? NoContent()
            : Problem(statusCode: StatusCodes.Status400BadRequest,
                detail: "That invite code is invalid, expired, or fully used.");
    }
}
