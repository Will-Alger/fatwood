namespace ResearchDiscovery.Application.Options;

/// <summary>
/// How user-facing paper analysis is queued and consumed. Default is the
/// in-process queue (local/dev). In cloud, a durable Azure Storage queue
/// decouples analysis from the web tier so a KEDA-scaled worker job can drain
/// it independently and survive restarts.
/// </summary>
public class AnalysisQueueOptions
{
    public const string SectionName = "AnalysisQueue";

    /// <summary>When true, use the Azure Storage queue + external worker job.</summary>
    public bool UseStorageQueue { get; set; }

    public string QueueName { get; set; } = "analysis-jobs";

    /// <summary>Full connection string (local Azurite). Wins over AccountUrl.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Queue-service URL for managed-identity auth in cloud,
    /// e.g. https://acct.queue.core.windows.net.</summary>
    public string? AccountUrl { get; set; }

    /// <summary>How many papers a single worker analyzes at once.</summary>
    public int WorkerConcurrency { get; set; } = 4;

    /// <summary>Consecutive empty receives before the worker job exits (so an
    /// event-driven ACA job terminates once the queue is drained).</summary>
    public int WorkerMaxIdlePolls { get; set; } = 3;
}
