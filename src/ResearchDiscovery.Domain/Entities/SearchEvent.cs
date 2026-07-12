namespace ResearchDiscovery.Domain.Entities;

/// <summary>
/// One executed search, logged with the exact plan and the exact result list
/// it produced. This is the raw material for the offline quality loop: real
/// queries become eval queries (`eval adopt`), and interactions joined back
/// to (search, rank) become relevance labels and bias evidence (`eval bias`).
/// Only the product search endpoint logs; the eval CLI never does — otherwise
/// evaluation runs would poison their own telemetry.
/// </summary>
public class SearchEvent
{
    public long Id { get; set; }

    /// <summary>Account that searched; null for anonymous/pre-account events.</summary>
    public long? UserId { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    /// <summary>
    /// The user's original prose, present only when the search came from a
    /// freshly compiled query (chip edits and re-runs carry it forward).
    /// Null for plan-only executions with no known prose ancestry.
    /// </summary>
    public string? QueryText { get; set; }

    /// <summary>The full SearchPlan as JSON — enough to re-execute the search exactly.</summary>
    public string PlanJson { get; set; } = string.Empty;

    public int TotalCandidates { get; set; }

    public int ResultLimit { get; set; }

    public List<SearchEventResult> Results { get; set; } = [];
}

/// <summary>One result slot of a logged search: which paper ranked where, with what evidence.</summary>
public class SearchEventResult
{
    public long SearchEventId { get; set; }

    public SearchEvent SearchEvent { get; set; } = null!;

    /// <summary>1-based display rank.</summary>
    public int Rank { get; set; }

    public long PaperId { get; set; }

    public Paper Paper { get; set; } = null!;

    /// <summary>The relevance (cosine) score the ranker assigned.</summary>
    public float Score { get; set; }

    public bool IsWildcard { get; set; }

    /// <summary>Experience proximity annotation at display time: "close", "stretch", or null.</summary>
    public string? Proximity { get; set; }

    /// <summary>
    /// Interleaving team ("A" = control ranker, "B" = candidate) when the
    /// search ran an interleaved experiment; null otherwise. Interactions on
    /// tagged slots are votes — `eval bias` tallies the match.
    /// </summary>
    public string? Variant { get; set; }
}
