namespace ResearchDiscovery.Application.Abstractions;

/// <summary>
/// Cross-process mutual exclusion for ingestion. The CLI backfill and the
/// in-web scheduler are separate processes, so this must be backed by shared
/// state (a database lease), not an in-process primitive.
/// </summary>
public interface IIngestionLockManager
{
    /// <summary>Returns a lease to dispose on completion, or null if another run holds the lock.</summary>
    Task<IAsyncDisposable?> TryAcquireAsync(string holder, CancellationToken ct);
}
