namespace ResearchDiscovery.Application.Abstractions;

/// <summary>
/// Scoped carrier of "who is spending on this LLM call". Set once per scope —
/// by auth middleware on request paths, from the job payload in background
/// analysis scopes, left null in CLI/eval scopes (system spend, not billed).
/// Call sites never pass user ids around; the recorder reads this instead.
/// </summary>
public interface ILlmUsageContext
{
    long? UserId { get; set; }

    /// <summary>True when calls in this scope use the user's own Anthropic key.</summary>
    bool UsedByoKey { get; set; }
}
