namespace ResearchDiscovery.Domain.Entities;

/// <summary>
/// Stored LLM analysis of a paper FOR a specific user — analyses are
/// personalized to the requesting user's profile, so the same paper can carry
/// one row per user. Cache key = (user, paper, schema version, that user's
/// profile version).
/// </summary>
public class AnalysisResult
{
    public long Id { get; set; }

    public long PaperId { get; set; }

    public Paper Paper { get; set; } = null!;

    /// <summary>
    /// Owning account. Null only for pre-account rows (claimed by the
    /// bootstrap admin) or CLI runs with no user context.
    /// </summary>
    public long? UserId { get; set; }

    public AppUser? User { get; set; }

    /// <summary>Version of the analysis JSON contract, for forward migration.</summary>
    public int SchemaVersion { get; set; }

    /// <summary>
    /// The owning user's UserProfile.Version this analysis was produced
    /// against. Analyses go stale when that profile changes.
    /// </summary>
    public int ProfileVersion { get; set; }

    /// <summary>Model identifier that produced the analysis.</summary>
    public required string Model { get; set; }

    /// <summary>Structured analysis JSON. Stored as plain text for provider portability.</summary>
    public required string ResultJson { get; set; }

    /// <summary>Denormalized composite score for sorting/filtering in browse.</summary>
    public decimal? CompositeScore { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }
}
