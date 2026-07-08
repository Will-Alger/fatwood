namespace ResearchDiscovery.Domain.Entities;

/// <summary>
/// Phase 2 seam: stored LLM analysis for a paper (1:1). The table exists from
/// Phase 1 so the analysis layer slots in without a schema rewrite, but no
/// Phase 1 code ever writes to it.
/// </summary>
public class AnalysisResult
{
    public long Id { get; set; }

    public long PaperId { get; set; }

    public Paper Paper { get; set; } = null!;

    /// <summary>Version of the analysis JSON contract, for forward migration.</summary>
    public int SchemaVersion { get; set; }

    /// <summary>Model identifier that produced the analysis.</summary>
    public required string Model { get; set; }

    /// <summary>Structured analysis JSON. Stored as plain text for provider portability.</summary>
    public required string ResultJson { get; set; }

    /// <summary>Denormalized composite score for sorting/filtering in browse.</summary>
    public decimal? CompositeScore { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }
}
