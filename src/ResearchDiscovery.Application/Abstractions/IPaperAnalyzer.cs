using ResearchDiscovery.Domain.Entities;

namespace ResearchDiscovery.Application.Abstractions;

/// <summary>
/// Structured analysis of a single paper, ready to persist.
/// </summary>
public sealed record PaperAnalysis(
    string ResultJson,
    decimal CompositeScore,
    string Model,
    int SchemaVersion);

/// <summary>
/// Produces the LLM analysis for one paper. Separated from the run
/// orchestration (<see cref="IAnalysisService"/>) so tests can exercise
/// selection/persistence without an Anthropic dependency.
/// </summary>
public interface IPaperAnalyzer
{
    /// <summary>
    /// Analyzes a single paper. Returns null when the model (and its
    /// fallback) declined the request — the paper is skipped, not an error.
    /// </summary>
    Task<PaperAnalysis?> AnalyzeAsync(Paper paper, CancellationToken ct);
}
