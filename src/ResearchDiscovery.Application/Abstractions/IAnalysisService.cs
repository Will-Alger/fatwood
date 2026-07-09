namespace ResearchDiscovery.Application.Abstractions;

/// <summary>
/// Phase 2 analysis runs. The contract analyzes a bounded, filtered subset
/// (one category at a time) by construction, so the whole corpus can never
/// be analyzed in one call.
/// </summary>
public interface IAnalysisService
{
    Task<AnalysisSummary> AnalyzeAsync(AnalysisRequest request, CancellationToken ct);

    /// <summary>
    /// Analyzes a specific set of papers (typically the top slice of a search)
    /// rather than a category sweep. Papers with a current analysis (same
    /// schema and profile version) are skipped — no tokens are spent twice.
    /// </summary>
    Task<AnalysisSummary> AnalyzeSelectionAsync(
        IReadOnlyList<string> arxivIds, CancellationToken ct);
}

public sealed record AnalysisRequest(
    string CategoryCode,
    int MaxPapers,
    DateTimeOffset? Since);

/// <summary>
/// Outcome of one analysis run. Selected counts only papers that did not
/// already have a current analysis — re-running is idempotent and spends no
/// tokens on already-analyzed papers.
/// </summary>
public sealed record AnalysisSummary(
    string CategoryCode,
    int PapersSelected,
    int PapersAnalyzed,
    int PapersDeclined,
    int PapersFailed);
