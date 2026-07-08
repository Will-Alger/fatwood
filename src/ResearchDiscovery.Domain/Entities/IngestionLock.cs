namespace ResearchDiscovery.Domain.Entities;

/// <summary>
/// Single-row lease serializing ingestion across processes (web scheduler vs
/// CLI backfill). <see cref="Stamp"/> is a portable optimistic-concurrency
/// token — it avoids provider-specific mechanisms like Postgres xmin or
/// SQL Server rowversion.
/// </summary>
public class IngestionLock
{
    public const int SingletonId = 1;

    public int Id { get; set; }

    /// <summary>Identifies the current holder, e.g. "cli@HOSTNAME". Null when free.</summary>
    public string? Holder { get; set; }

    public DateTimeOffset? AcquiredUtc { get; set; }

    /// <summary>Concurrency token rotated on every acquire/release.</summary>
    public Guid Stamp { get; set; }
}
