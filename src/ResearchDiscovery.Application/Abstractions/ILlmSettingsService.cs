using ResearchDiscovery.Application.Options;

namespace ResearchDiscovery.Application.Abstractions;

public sealed record LlmStepAssignment(string Step, LlmOptions.LlmModel Model, bool IsDefault);

/// <summary>
/// Resolves the effective model per pipeline step: DB override if present,
/// else the configured default. Assignments are validated against the model
/// registry — an unknown model id is rejected, never persisted.
/// </summary>
public interface ILlmSettingsService
{
    Task<LlmOptions.LlmModel> GetModelForStepAsync(string step, CancellationToken ct);

    Task<IReadOnlyList<LlmStepAssignment>> GetAssignmentsAsync(CancellationToken ct);

    /// <summary>Throws ArgumentException for unknown steps or model ids.</summary>
    Task SetAssignmentAsync(string step, string modelId, CancellationToken ct);

    IReadOnlyList<LlmOptions.LlmModel> Registry { get; }
}
