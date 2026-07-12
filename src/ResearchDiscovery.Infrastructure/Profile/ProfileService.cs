using Microsoft.EntityFrameworkCore;
using ResearchDiscovery.Domain.Entities;
using ResearchDiscovery.Infrastructure.Persistence;

namespace ResearchDiscovery.Infrastructure.Profile;

/// <summary>
/// Per-user profile access. Every save bumps <see cref="UserProfile.Version"/>,
/// which invalidates that user's cached per-paper analyses (they re-run on
/// demand). A null userId (anonymous, CLI/system scopes) has no profile.
/// </summary>
public class ProfileService(IDbContextFactory<AppDbContext> dbFactory)
{
    public async Task<UserProfile?> GetAsync(long? userId, CancellationToken ct)
    {
        if (userId is null)
        {
            return null;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.UserProfiles.AsNoTracking()
            .SingleOrDefaultAsync(p => p.UserId == userId, ct);
    }

    public async Task<UserProfile> SaveAsync(
        long userId, string experienceSummary, string goals, int? weeklyHours,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var profile = await db.UserProfiles
            .SingleOrDefaultAsync(p => p.UserId == userId, ct);

        if (profile is null)
        {
            profile = new UserProfile { UserId = userId };
            db.UserProfiles.Add(profile);
        }

        profile.ExperienceSummary = experienceSummary;
        profile.Goals = goals;
        profile.WeeklyHours = weeklyHours;
        profile.Version++;
        profile.UpdatedUtc = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        return profile;
    }

    /// <summary>Compact prompt-ready description, or null when no profile exists.</summary>
    public static string? Describe(UserProfile? profile)
    {
        if (profile is null ||
            (string.IsNullOrWhiteSpace(profile.ExperienceSummary) && string.IsNullOrWhiteSpace(profile.Goals)))
        {
            return null;
        }

        var hours = profile.WeeklyHours is { } h ? $"\nWeekly hours available: {h}" : string.Empty;
        return $"Experience: {profile.ExperienceSummary}\nGoals: {profile.Goals}{hours}";
    }
}
