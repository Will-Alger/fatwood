namespace ResearchDiscovery.Domain.Entities;

/// <summary>
/// One user's experience and goals — the "person" half of the paper × person
/// analysis. <see cref="Version"/> increments on every edit and is part of the
/// analysis cache key: analyses produced against an older profile version are
/// stale and re-run on demand.
/// </summary>
public class UserProfile
{
    public long Id { get; set; }

    /// <summary>
    /// Owning account. Null only for rows created before accounts existed;
    /// those are claimed by the bootstrap admin on first sign-in.
    /// </summary>
    public long? UserId { get; set; }

    public AppUser? User { get; set; }

    /// <summary>Free text: skills, domains, years of experience.</summary>
    public string ExperienceSummary { get; set; } = string.Empty;

    /// <summary>Free text: target role, domain, location, motivations.</summary>
    public string Goals { get; set; } = string.Empty;

    /// <summary>Rough weekly hours available for a side project.</summary>
    public int? WeeklyHours { get; set; }

    /// <summary>Bumped on every edit; analysis results cache against it.</summary>
    public int Version { get; set; }

    public DateTimeOffset UpdatedUtc { get; set; }
}
