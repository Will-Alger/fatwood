using System.Security.Claims;
using ResearchDiscovery.Application.Abstractions;
using ResearchDiscovery.Domain.Entities;

namespace ResearchDiscovery.Api.Auth;

/// <summary>
/// Bridges token identity to the app's account row. Runs between
/// UseAuthentication and UseAuthorization: resolves (or first-time creates)
/// the AppUser for the validated principal, exposes it via HttpContext.Items,
/// points the scoped LLM usage context at it, and stamps role/active claims
/// so [Authorize] policies see DB-backed permissions — the token itself
/// carries identity only, never authority.
/// </summary>
public class UserContextMiddleware(RequestDelegate next)
{
    public const string ItemKey = "Fatwood:AppUser";

    public async Task InvokeAsync(
        HttpContext context, IUserAccountService accounts, ILlmUsageContext usageContext)
    {
        if (context.User.Identity is ClaimsIdentity { IsAuthenticated: true } identity)
        {
            // External ID's oid is the stable per-tenant user id. The URI-form
            // fallback covers handlers that map inbound claim names.
            var externalId =
                context.User.FindFirstValue("oid") ??
                context.User.FindFirstValue("http://schemas.microsoft.com/identity/claims/objectidentifier");

            if (!string.IsNullOrEmpty(externalId))
            {
                var email =
                    context.User.FindFirstValue("email") ??
                    context.User.FindFirstValue(ClaimTypes.Email) ??
                    context.User.FindFirstValue("preferred_username") ??
                    string.Empty;
                var name =
                    context.User.FindFirstValue("name") ??
                    context.User.FindFirstValue(ClaimTypes.Name) ??
                    email;

                var user = await accounts.GetOrCreateAsync(
                    externalId, email, name, context.RequestAborted);

                context.Items[ItemKey] = user;
                usageContext.UserId = user.Id;

                identity.AddClaim(new Claim(identity.RoleClaimType, user.Role.ToString()));
                if (user.IsActive)
                {
                    identity.AddClaim(new Claim(AuthPolicies.ActiveClaim, "true"));
                }
            }
        }

        await next(context);
    }
}

public static class UserContextExtensions
{
    /// <summary>The resolved account for this request; null on anonymous paths.</summary>
    public static AppUser? GetAppUser(this HttpContext context) =>
        context.Items.TryGetValue(UserContextMiddleware.ItemKey, out var user)
            ? user as AppUser
            : null;
}
