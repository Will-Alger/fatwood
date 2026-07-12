using ResearchDiscovery.Domain.Entities;

namespace ResearchDiscovery.Application.Abstractions;

/// <summary>
/// Append-only product telemetry: executed searches and paper interactions,
/// with search context when known. Logged ONLY from the product API surface —
/// the eval CLI runs the same search pipeline but must never write here, or
/// evaluation would poison the very data it is meant to measure.
/// </summary>
public interface ISearchTelemetry
{
    /// <returns>The persisted search event id, echoed to the client so
    /// later interactions can carry their context.</returns>
    Task<long> LogSearchAsync(
        long? userId, string? queryText, SearchPlan plan, SearchResult result,
        CancellationToken ct);

    /// <summary>
    /// Logs a paper interaction. When <paramref name="searchEventId"/> is
    /// provided without a rank, the rank is resolved from the logged results.
    /// Unknown papers are ignored (telemetry never fails a user action).
    /// </summary>
    Task LogInteractionAsync(
        long? userId,
        string arxivId,
        InteractionType type,
        long? searchEventId,
        int? rank,
        CancellationToken ct);
}
