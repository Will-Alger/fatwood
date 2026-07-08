using ResearchDiscovery.Domain.Entities;

namespace ResearchDiscovery.Application.Abstractions;

public sealed record IngestionRunSummary(
    long RunId,
    IngestionStatus Status,
    int PapersFetched,
    int PapersAdded,
    int PapersUpdated,
    string? Error);

/// <summary>
/// Orchestrates ingestion across all configured categories. Both entry points
/// acquire the cross-process ingestion lease; a second concurrent run is
/// rejected, never interleaved.
/// </summary>
public interface IIngestionService
{
    /// <summary>Initial/backfill ingestion over the configured window, ignoring high-water marks.</summary>
    Task<IngestionRunSummary> RunBackfillAsync(BackfillOverrides? overrides, CancellationToken ct);

    /// <summary>Delta ingestion from each category's high-water mark to now.</summary>
    Task<IngestionRunSummary> RunDeltaAsync(CancellationToken ct);
}

/// <summary>Optional CLI/admin overrides for a single backfill run.</summary>
public sealed record BackfillOverrides(int? WindowDays, int? MaxPapersPerCategory);
