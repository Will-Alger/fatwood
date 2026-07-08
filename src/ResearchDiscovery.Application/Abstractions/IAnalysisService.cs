namespace ResearchDiscovery.Application.Abstractions;

/// <summary>
/// Phase 2 seam — intentionally has NO implementation in Phase 1.
/// The contract analyzes a bounded, filtered subset (one category at a time)
/// by construction, so the whole corpus can never be analyzed in one call.
/// </summary>
public interface IAnalysisService
{
    Task AnalyzeAsync(AnalysisRequest request, CancellationToken ct);
}

public sealed record AnalysisRequest(
    string CategoryCode,
    int MaxPapers,
    DateTimeOffset? Since);
