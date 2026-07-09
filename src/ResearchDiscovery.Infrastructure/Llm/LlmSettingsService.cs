using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ResearchDiscovery.Application.Abstractions;
using ResearchDiscovery.Application.Options;
using ResearchDiscovery.Domain.Entities;
using ResearchDiscovery.Infrastructure.Persistence;

namespace ResearchDiscovery.Infrastructure.Llm;

public class LlmSettingsService(
    IDbContextFactory<AppDbContext> dbFactory,
    IOptions<LlmOptions> options) : ILlmSettingsService
{
    private readonly LlmOptions _options = options.Value;

    public IReadOnlyList<LlmOptions.LlmModel> Registry => _options.Models;

    public async Task<LlmOptions.LlmModel> GetModelForStepAsync(string step, CancellationToken ct)
    {
        ValidateStep(step);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var overrideRow = await db.LlmStepConfigs
            .AsNoTracking()
            .SingleOrDefaultAsync(c => c.Step == step, ct);

        var modelId = overrideRow?.ModelId ?? DefaultFor(step);

        // A DB override referencing a model since removed from the registry
        // falls back to the default rather than failing the pipeline.
        return _options.Models.FirstOrDefault(m => m.Id == modelId)
            ?? _options.Models.First(m => m.Id == DefaultFor(step));
    }

    public async Task<IReadOnlyList<LlmStepAssignment>> GetAssignmentsAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var overrides = await db.LlmStepConfigs.AsNoTracking().ToDictionaryAsync(c => c.Step, ct);

        return LlmOptions.Steps
            .Select(step =>
            {
                var hasOverride = overrides.TryGetValue(step, out var row) &&
                    _options.Models.Any(m => m.Id == row!.ModelId);
                var modelId = hasOverride ? overrides[step].ModelId : DefaultFor(step);
                var model = _options.Models.First(m => m.Id == modelId);
                return new LlmStepAssignment(step, model, !hasOverride);
            })
            .ToList();
    }

    public async Task SetAssignmentAsync(string step, string modelId, CancellationToken ct)
    {
        ValidateStep(step);
        if (_options.Models.All(m => m.Id != modelId))
        {
            throw new ArgumentException($"Unknown model id '{modelId}'. It must be registered in Llm:Models.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.LlmStepConfigs.SingleOrDefaultAsync(c => c.Step == step, ct);
        if (row is null)
        {
            db.LlmStepConfigs.Add(new LlmStepConfig
            {
                Step = step,
                ModelId = modelId,
                UpdatedUtc = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            row.ModelId = modelId;
            row.UpdatedUtc = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct);
    }

    private string DefaultFor(string step) =>
        _options.Defaults.TryGetValue(step, out var id)
            ? id
            : _options.Models[0].Id;

    private static void ValidateStep(string step)
    {
        if (!LlmOptions.Steps.Contains(step, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                $"Unknown LLM step '{step}'. Valid steps: {string.Join(", ", LlmOptions.Steps)}.");
        }
    }
}
