using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ResearchDiscovery.Application.Abstractions;
using ResearchDiscovery.Application.Options;
using ResearchDiscovery.Domain.Entities;
using ResearchDiscovery.Infrastructure.Persistence;

namespace ResearchDiscovery.Infrastructure.Accounts;

public class UserAccountService(
    IDbContextFactory<AppDbContext> dbFactory,
    IOptions<AccountOptions> accountOptions,
    ILogger<UserAccountService> logger) : IUserAccountService
{
    /// <summary>
    /// The synthetic identity used when Auth is disabled (local dev, tests).
    /// Always an active admin — local dev needs the full surface with no
    /// tenant configured. Production refuses to start with Auth disabled.
    /// </summary>
    public const string LocalDevExternalId = "local-dev";

    public async Task<AppUser> GetOrCreateAsync(
        string externalId, string email, string displayName, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var user = await db.AppUsers.FirstOrDefaultAsync(u => u.ExternalId == externalId, ct);
        if (user is not null)
        {
            await TouchAsync(db, user, email, displayName, ct);
            return user;
        }

        var options = accountOptions.Value;
        var isBootstrapAdmin =
            externalId == LocalDevExternalId ||
            options.BootstrapAdminEmails.Contains(email, StringComparer.OrdinalIgnoreCase);

        var now = DateTimeOffset.UtcNow;
        user = new AppUser
        {
            ExternalId = externalId,
            Email = email,
            DisplayName = displayName,
            // Bootstrap identities are the operator: full Owner tier.
            Role = isBootstrapAdmin ? UserRole.Owner : UserRole.Member,
            // The invite gate holds accounts inactive until a code is redeemed;
            // admins and open-signup mode activate immediately.
            IsActive = isBootstrapAdmin || !options.RequireInviteCode,
            CreatedUtc = now,
            LastSeenUtc = now,
        };
        db.AppUsers.Add(user);

        // The starter grant is written even when the account is still gated:
        // it becomes spendable the moment an invite activates the user.
        if (options.StarterGrantMicros > 0)
        {
            db.BudgetGrants.Add(new BudgetGrant
            {
                User = user,
                AmountMicros = options.StarterGrantMicros,
                Reason = "signup",
                CreatedUtc = now,
            });
        }

        try
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Provisioned account {Email} ({Role}, active={Active})",
                email, user.Role, user.IsActive);

            if (isBootstrapAdmin)
            {
                await ClaimLegacyDataAsync(db, user.Id, ct);
            }
        }
        catch (DbUpdateException)
        {
            // Two first-sign-in requests raced; the unique ExternalId index
            // made one of them lose. The winner's row is what we want.
            db.ChangeTracker.Clear();
            user = await db.AppUsers.FirstAsync(u => u.ExternalId == externalId, ct);
        }

        return user;
    }

    /// <summary>
    /// Rows written before accounts existed (profile, bookmarks, analyses,
    /// telemetry) have a null UserId. The first bootstrap admin inherits them
    /// — this app was single-user before it had sign-in, and that user is the
    /// operator. Runs once: after the claim no null rows remain.
    /// </summary>
    private async Task ClaimLegacyDataAsync(AppDbContext db, long userId, CancellationToken ct)
    {
        var profiles = await db.UserProfiles.Where(p => p.UserId == null)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.UserId, userId), ct);
        var bookmarks = await db.Bookmarks.Where(b => b.UserId == null)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.UserId, userId), ct);
        var analyses = await db.AnalysisResults.Where(a => a.UserId == null)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.UserId, userId), ct);
        var searches = await db.SearchEvents.Where(e => e.UserId == null)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.UserId, userId), ct);
        var interactions = await db.InteractionEvents.Where(e => e.UserId == null)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.UserId, userId), ct);

        if (profiles + bookmarks + analyses + searches + interactions > 0)
        {
            logger.LogInformation(
                "Bootstrap admin {UserId} claimed legacy data: {Profiles} profile(s), " +
                "{Bookmarks} bookmark(s), {Analyses} analysis rows, {Searches} searches, " +
                "{Interactions} interactions",
                userId, profiles, bookmarks, analyses, searches, interactions);
        }
    }

    public async Task<bool> RedeemInviteAsync(long userId, string code, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var invite = await db.InviteCodes.FirstOrDefaultAsync(c => c.Code == code, ct);
        var now = DateTimeOffset.UtcNow;
        if (invite is null ||
            invite.UsedCount >= invite.MaxUses ||
            (invite.ExpiresUtc is { } expiry && expiry < now))
        {
            return false;
        }

        var user = await db.AppUsers.FirstAsync(u => u.Id == userId, ct);
        if (!user.IsActive)
        {
            user.IsActive = true;
            invite.UsedCount++;
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Invite {Code} redeemed by user {UserId}", code, userId);
        }

        return true;
    }

    public async Task SetThemeAsync(long userId, string? theme, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.AppUsers
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.ThemePreference, theme), ct);
    }

    /// <summary>
    /// Keeps profile fields in sync with the token and bumps LastSeenUtc, but
    /// only writes when something changed or the timestamp is stale — this
    /// runs on every authenticated request.
    /// </summary>
    private static async Task TouchAsync(
        AppDbContext db, AppUser user, string email, string displayName, CancellationToken ct)
    {
        var changed = false;
        if (!string.IsNullOrEmpty(email) && user.Email != email)
        {
            user.Email = email;
            changed = true;
        }

        if (!string.IsNullOrEmpty(displayName) && user.DisplayName != displayName)
        {
            user.DisplayName = displayName;
            changed = true;
        }

        var now = DateTimeOffset.UtcNow;
        if (changed || now - user.LastSeenUtc > TimeSpan.FromMinutes(5))
        {
            user.LastSeenUtc = now;
            await db.SaveChangesAsync(ct);
        }
    }
}
