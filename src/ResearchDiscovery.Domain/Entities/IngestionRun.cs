namespace ResearchDiscovery.Domain.Entities;

public enum IngestionTrigger
{
    Backfill = 0,
    Delta = 1,
}

public enum IngestionStatus
{
    Running = 0,
    Completed = 1,
    Failed = 2,
}

/// <summary>Audit record for every ingestion run, manual or scheduled.</summary>
public class IngestionRun
{
    public long Id { get; set; }

    public IngestionTrigger Trigger { get; set; }

    public IngestionStatus Status { get; set; }

    public DateTimeOffset StartedUtc { get; set; }

    public DateTimeOffset? CompletedUtc { get; set; }

    public int PapersFetched { get; set; }

    public int PapersAdded { get; set; }

    public int PapersUpdated { get; set; }

    public string? Error { get; set; }
}
