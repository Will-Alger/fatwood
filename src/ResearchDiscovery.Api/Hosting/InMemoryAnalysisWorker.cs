using ResearchDiscovery.Application.Abstractions;
using ResearchDiscovery.Infrastructure.Analysis;

namespace ResearchDiscovery.Api.Hosting;

/// <summary>
/// Drains the in-process analysis queue with bounded concurrency — several
/// papers analyze at once instead of strictly one-at-a-time, so a "top N"
/// selection finishes in roughly N/concurrency of the serial time. Each item
/// is a single paper and analysis is idempotent (already-current results are
/// skipped), so a duplicate is a cheap no-op. Only runs when the in-memory
/// queue is in use; the Storage-backed deployment consumes via the worker job.
/// </summary>
public sealed class InMemoryAnalysisWorker(
    InMemoryAnalysisQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<InMemoryAnalysisWorker> logger) : BackgroundService
{
    private const int MaxConcurrency = 4;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var slots = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);

        await foreach (var item in queue.DequeueAllAsync(stoppingToken))
        {
            await slots.WaitAsync(stoppingToken);
            _ = ProcessAsync(item, slots, stoppingToken);
        }
    }

    private async Task ProcessAsync(
        AnalysisWorkItem item, SemaphoreSlim slots, CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ILlmUsageContext>().UserId = item.UserId;
            var analysis = scope.ServiceProvider.GetRequiredService<IAnalysisService>();
            await analysis.AnalyzeSelectionAsync([item.ArxivId], item.UserId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Analysis of paper {ArxivId} failed", item.ArxivId);
        }
        finally
        {
            queue.MarkDone();
            slots.Release();
        }
    }
}
