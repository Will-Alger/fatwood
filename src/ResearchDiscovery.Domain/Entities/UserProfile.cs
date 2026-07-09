namespace ResearchDiscovery.Domain.Entities;

/// <summary>
/// The single user's experience and goals — the "person" half of the
/// paper × person analysis. <see cref="Version"/> increments on every edit and
/// is part of the analysis cache key: analyses produced against an older
/// profile version are stale and re-run on demand.
/// </summary>
public class UserProfile
{
    public const int SingletonId = 1;

    public int Id { get; set; }

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
