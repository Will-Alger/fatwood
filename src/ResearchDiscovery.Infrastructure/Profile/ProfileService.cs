using Microsoft.EntityFrameworkCore;
using ResearchDiscovery.Domain.Entities;
using ResearchDiscovery.Infrastructure.Persistence;

namespace ResearchDiscovery.Infrastructure.Profile;

/// <summary>
/// Single-user profile access. Every save bumps <see cref="UserProfile.Version"/>,
/// which invalidates cached per-paper analyses (they re-run on demand).
/// </summary>
public class ProfileService(IDbContextFactory<AppDbContext> dbFactory)
{
    public async Task<UserProfile?> GetAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.UserProfiles.AsNoTracking()
            .SingleOrDefaultAsync(p => p.Id == UserProfile.SingletonId, ct);
    }

    public async Task<UserProfile> SaveAsync(
        string experienceSummary, string goals, int? weeklyHours, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var profile = await db.UserProfiles
            .SingleOrDefaultAsync(p => p.Id == UserProfile.SingletonId, ct);

        if (profile is null)
        {
            profile = new UserProfile { Id = UserProfile.SingletonId };
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
