using ResearchDiscovery.Application.Abstractions;

namespace ResearchDiscovery.Application.Eval;

/// <summary>
/// One frozen evaluation query: a persona (stands in for the profile, so eval
/// is reproducible regardless of the local DB profile), the raw prose query,
/// and the compiled plan. The plan is frozen into the artifact by
/// `eval compile` so that scoring runs are fully deterministic and token-free;
/// re-compiling is an explicit, deliberate act that changes the artifact.
/// </summary>
public sealed record EvalQuery(
    string Id,
    string Persona,
    string Query,
    SearchPlan? Plan);

public sealed record EvalQuerySet(
    int Version,
    IReadOnlyList<EvalQuery> Queries);

/// <summary>
/// A graded relevance judgment for one (query, paper) pair. Source records
/// how the paper entered the judgment pool ("pool" = ranker head, "random" =
/// uniform sample of the filtered candidates) — the random slice is what makes
/// missed-gem estimation possible later.
/// </summary>
public sealed record EvalJudgment(
    string QueryId,
    string ArxivId,
    int Grade,
    string Rationale,
    string JudgeModel,
    string Source);

public sealed record EvalJudgmentSet(
    int Version,
    int RubricVersion,
    IReadOnlyList<EvalJudgment> Judgments);

/// <summary>Per-query offline scores; null metric = undefined for that query.</summary>
public sealed record EvalQueryScore(
    string QueryId,
    double? Ndcg10,
    double? Recall50,
    double? ReciprocalRank,
    int JudgedInTop10,
    int JudgedTotal,
    int RelevantTotal);

public sealed record EvalReport(
    IReadOnlyList<EvalQueryScore> Queries,
    double? MeanNdcg10,
    double? MeanRecall50,
    double? MeanReciprocalRank);
