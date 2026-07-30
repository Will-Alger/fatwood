namespace ResearchDiscovery.Application.Abstractions;

/// <summary>
/// Serves stage-0 candidate id sets (category / no-code / date filters) from
/// memory instead of materializing them from the database on every search.
/// Results are exactly the ids the SQL filter path would return, including
/// its sub-day date-cutoff semantics.
/// </summary>
public interface ICandidateSetCache
{
    /// <summary>
    /// The papers matching (ANY of <paramref name="categories"/>, or all
    /// papers when empty) ∩ (CodeUrl == null when
    /// <paramref name="requireNoCode"/>) ∩ (PublishedUtc &gt;=
    /// <paramref name="publishedAfter"/> when set).
    /// </summary>
    Task<IReadOnlySet<long>> GetAsync(
        IReadOnlyList<string> categories,
        bool requireNoCode,
        DateTimeOffset? publishedAfter,
        CancellationToken ct);

    /// <summary>Drop the snapshot; the next call reloads from the database.</summary>
    void Invalidate();

    /// <summary>
    /// Load the snapshot ahead of any search, so no user request pays the
    /// multi-second database read inline (called by the startup warmer).
    /// </summary>
    Task WarmAsync(CancellationToken ct);
}
