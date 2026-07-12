namespace ResearchDiscovery.Application.Abstractions;

/// <summary>
/// Writes one ledger row per Anthropic response with the REAL token counts
/// from the API (never estimates), priced from the model registry. Recording
/// failures are logged and swallowed: a metering hiccup must not fail the
/// user-facing call it meters.
/// </summary>
public interface ILlmUsageRecorder
{
    Task RecordAsync(
        string step, string modelId, long inputTokens, long outputTokens, CancellationToken ct);
}
