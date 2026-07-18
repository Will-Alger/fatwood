using ResearchDiscovery.Domain.Entities;

namespace ResearchDiscovery.Application.Abstractions;

public sealed record BulkHarvestSummary(
    long RunId,
    IngestionStatus Status,
    int SetsProcessed,
    int Pages,
    int PapersFetched,
    int PapersAdded,
    int PapersSkippedExisting,
    int PapersFiltered,
    string? Error);

/// <summary>
/// One-shot OAI-PMH bulk harvest of historical paper metadata across the
/// configured categories. Acquires the same cross-process ingestion lease as
/// the daily runs, so a bulk harvest and a delta can never interleave.
/// </summary>
public interface IBulkHarvestService
{
    /// <summary>
    /// Harvests every OAI set derived from the configured categories from
    /// <paramref name="fromUtc"/> to now, add-only. <paramref name="onlySet"/>
    /// restricts the run to one set; <paramref name="resumeToken"/> continues
    /// a crashed run from its last logged resumption token (applies to the
    /// first set processed — pin it with <paramref name="onlySet"/>).
    /// </summary>
    Task<BulkHarvestSummary> RunAsync(
        DateTimeOffset fromUtc, string? onlySet, string? resumeToken, CancellationToken ct);
}
