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

    /// <summary>In Storage mode, how many papers from the head of a selection
    /// are analyzed in-process by the web host (the hot lane) instead of being
    /// queued for the worker job — the user's first cards appear in seconds
    /// while the job cold-starts for the tail. The lanes are strictly
    /// partitioned, so no paper is processed (or billed) twice. 0 disables.</summary>
    public int HotLaneCount { get; set; } = 3;

    /// <summary>How many papers a single worker analyzes at once.</summary>
    public int WorkerConcurrency { get; set; } = 4;

    /// <summary>Consecutive empty 1s receives before the worker job exits.
    /// Long enough that an "analyze a few, read, analyze more" session reuses
    /// the warm worker instead of paying a fresh container cold start; short
    /// enough to keep scale-to-zero (90s of idle 0.5 vCPU ≈ a fraction of a
    /// cent).</summary>
    public int WorkerMaxIdlePolls { get; set; } = 90;
}
