using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ResearchDiscovery.Application.Abstractions;
using ResearchDiscovery.Application.Options;
using ResearchDiscovery.Domain.Entities;
using ResearchDiscovery.Infrastructure.Persistence;

namespace ResearchDiscovery.Infrastructure.Accounts;

public class LlmUsageRecorder(
    IDbContextFactory<AppDbContext> dbFactory,
    ILlmUsageContext usageContext,
    IOptions<LlmOptions> llmOptions,
    ILogger<LlmUsageRecorder> logger) : ILlmUsageRecorder
{
    public async Task RecordAsync(
        string step, string modelId, long inputTokens, long outputTokens, CancellationToken ct)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            db.LlmUsageEvents.Add(new LlmUsageEvent
            {
                UserId = usageContext.UserId,
                Step = step,
                Model = modelId,
                InputTokens = (int)inputTokens,
                OutputTokens = (int)outputTokens,
                CostMicros = ComputeCostMicros(modelId, inputTokens, outputTokens),
                UsedByoKey = usageContext.UsedByoKey,
                CreatedUtc = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Metering must never fail the call it meters; the tokens are
            // already spent either way. Loud log so a broken ledger is noticed.
            logger.LogError(ex,
                "Failed to record LLM usage ({Step}, {Model}, {In}/{Out} tokens)",
                step, modelId, inputTokens, outputTokens);
        }
    }

    private long ComputeCostMicros(string modelId, long inputTokens, long outputTokens)
    {
        var model = llmOptions.Value.Models
            .FirstOrDefault(m => string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase));
        if (model is null)
        {
            logger.LogWarning("Model {Model} missing from the Llm registry; recording zero cost", modelId);
            return 0;
        }

        // $/MTok × tokens ÷ 1e6 = dollars; × 1e6 = micro-dollars. The 1e6s
        // cancel: micro-dollars = tokens × $/MTok.
        return (long)Math.Ceiling(
            inputTokens * model.InputPerMTok + outputTokens * model.OutputPerMTok);
    }
}
