namespace ResearchDiscovery.Application.Abstractions;

/// <summary>
/// A compiled, fully deterministic search plan. The LLM produces this once per
/// search; executing it never touches an LLM. The plan is returned to the UI
/// as editable chips, and an edited plan is re-submitted directly — so the
/// search endpoint accepts plans, not natural language.
/// HypotheticalAbstract (HyDE) is nullable: plans logged before the field
/// existed deserialize with null and retrieval simply skips the extra anchor.
/// Intent is the compiler's read of the query's style — "precise" (the exact
/// words ARE the search: named methods, systems, acronyms), "exploratory"
/// (goal/career phrasing, vocabulary mismatch likely), or "mixed". Nullable
/// for old plans; ranking treats null as mixed.
/// </summary>
public sealed record SearchPlan(
    string Interpretation,
    string AnchorText,
    IReadOnlyList<string> Categories,
    int? DateWindowDays,
    bool? RequireNoCode,
    string? HypotheticalAbstract = null,
    string? Intent = null);

/// <summary>
/// LLM call site #1: natural-language intent → SearchPlan. The key job is
/// expansion — "moving into applied ML" becomes concrete research topics an
/// embedding model can actually match against abstracts.
/// </summary>
public interface ISearchPlanCompiler
{
    /// <param name="query">The user's natural-language search.</param>
    /// <param name="profile">Optional profile context (experience/goals) the compiler may draw on.</param>
    /// <param name="knownCategories">Category codes that exist in the corpus, so the plan never references unknown codes.</param>
    Task<SearchPlan> CompileAsync(
        string query,
        string? profile,
        IReadOnlyList<string> knownCategories,
        CancellationToken ct);
}
