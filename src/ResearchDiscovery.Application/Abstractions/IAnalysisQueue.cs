namespace ResearchDiscovery.Application.Abstractions;

/// <summary>
/// One unit of analysis work: a single paper for a single user. Fine-grained
/// (one paper per item) so a worker pool can process a "top N" selection in
/// parallel and share load fairly across users.
/// </summary>
public sealed record AnalysisWorkItem(long? UserId, string ArxivId);

/// <summary>
/// The durable-ish queue for user-facing paper analysis. Selections fan out to
/// one work item per paper; a worker pool drains them concurrently. The
/// in-memory implementation keeps the queue inside the web process (local/dev);
/// the Storage-backed implementation moves it out so workers scale
/// independently and survive restarts. Category (bulk) analysis stays on the
/// separate in-process path — it's a low-frequency Owner op.
/// </summary>
public interface IAnalysisQueue
{
    /// <summary>Enqueues one work item per paper, in the given (rank) order.</summary>
    Task EnqueueSelectionAsync(
        long? userId, IReadOnlyList<string> arxivIds, CancellationToken ct);

    /// <summary>
    /// Whether any analysis work is queued or in flight — lets the status
    /// endpoint tell "still working" from "done, some declined". Approximate.
    /// </summary>
    Task<bool> HasPendingWorkAsync(CancellationToken ct);
}
