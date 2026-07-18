namespace ResearchDiscovery.Application.Abstractions;

/// <summary>
/// Writes packed snapshots of the in-memory search indexes to blob storage so
/// cold replicas load them in seconds instead of rebuilding from the database.
/// No-op when snapshot storage is not configured.
/// </summary>
public interface ISearchIndexSnapshots
{
    Task WriteAsync(CancellationToken ct);
}
