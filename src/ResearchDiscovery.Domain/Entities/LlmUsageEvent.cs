namespace ResearchDiscovery.Domain.Entities;

/// <summary>
/// One Anthropic API call: who spent, on what, and the real token counts from
/// the response (not estimates). The debit side of the budget ledger.
/// </summary>
public class LlmUsageEvent
{
    public long Id { get; set; }

    /// <summary>Null for system/CLI usage (eval judge, ops) — never billed to a user.</summary>
    public long? UserId { get; set; }

    public AppUser? User { get; set; }

    /// <summary>Pipeline step (LlmOptions step names: QueryCompiler, PaperAnalysis, ...).</summary>
    public string Step { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    public int InputTokens { get; set; }

    public int OutputTokens { get; set; }

    /// <summary>Cost in integer micro-dollars, computed from the model registry pricing.</summary>
    public long CostMicros { get; set; }

    /// <summary>True when the call was made with the user's own Anthropic key (not platform spend).</summary>
    public bool UsedByoKey { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }
}
