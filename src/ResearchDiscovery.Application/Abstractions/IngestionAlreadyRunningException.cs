namespace ResearchDiscovery.Application.Abstractions;

/// <summary>
/// Thrown when an ingestion run cannot start because another run (in this or
/// any other process) holds the ingestion lease.
/// </summary>
public class IngestionAlreadyRunningException()
    : Exception("An ingestion run is already in progress. Concurrent runs are not allowed.");
