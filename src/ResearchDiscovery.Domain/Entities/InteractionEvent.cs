namespace ResearchDiscovery.Domain.Entities;

public enum InteractionType
{
    Bookmarked,
    Unbookmarked,
    AnalyzedFromSearch,

    /// <summary>Explicit negative feedback — the highest-value missing signal:
    /// positives say what to show more of, negatives say what to stop showing.</summary>
    NotInterested,
}

/// <summary>
/// A user action on a paper, with the search context that surfaced it when
/// known. "Bookmarked 2506.01234" is a fact; "bookmarked the rank-17
/// stretch-labeled wildcard" is a training signal — the nullable
/// (SearchEventId, Rank) pair is what turns the former into the latter.
/// Append-only; an unbookmark is a new row, not a deletion, so label
/// extraction can see the user changed their mind.
/// </summary>
public class InteractionEvent
{
    public long Id { get; set; }

    /// <summary>Account that acted; null for pre-account events.</summary>
    public long? UserId { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public long PaperId { get; set; }

    public Paper Paper { get; set; } = null!;

    public InteractionType Type { get; set; }

    /// <summary>The search that surfaced this paper, when the action came from search results.</summary>
    public long? SearchEventId { get; set; }

    /// <summary>1-based rank the paper held in that search's results.</summary>
    public int? Rank { get; set; }
}
