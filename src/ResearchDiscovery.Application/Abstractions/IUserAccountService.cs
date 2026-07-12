using ResearchDiscovery.Domain.Entities;

namespace ResearchDiscovery.Application.Abstractions;

/// <summary>
/// The app-side account layer over Entra External ID identities: find-or-create
/// on first sign-in, invite redemption, and admin account operations.
/// </summary>
public interface IUserAccountService
{
    /// <summary>
    /// Resolves the AppUser for a validated token, creating the row (with the
    /// signup budget grant and bootstrap-admin promotion) on first sign-in.
    /// </summary>
    Task<AppUser> GetOrCreateAsync(
        string externalId, string email, string displayName, CancellationToken ct);

    /// <summary>Redeems an invite code, activating the account. Returns false when invalid/expired/exhausted.</summary>
    Task<bool> RedeemInviteAsync(long userId, string code, CancellationToken ct);

    Task SetThemeAsync(long userId, string? theme, CancellationToken ct);
}
