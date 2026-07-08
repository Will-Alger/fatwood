using ResearchDiscovery.Application.Abstractions;
using ResearchDiscovery.Domain.Entities;

namespace ResearchDiscovery.Api.Hosting;

/// <summary>Executes admin-triggered ingestion jobs from the in-process queue.</summary>
public class IngestionQueueHostedService(
    IngestionJobQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<IngestionQueueHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in queue.DequeueAllAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var ingestion = scope.ServiceProvider.GetRequiredService<IIngestionService>();

                var summary = job.Trigger == IngestionTrigger.Backfill
                    ? await ingestion.RunBackfillAsync(job.Overrides, stoppingToken)
                    : await ingestion.RunDeltaAsync(stoppingToken);

                logger.LogInformation(
                    "Admin-triggered {Trigger} run {RunId} finished with status {Status}",
                    job.Trigger, summary.RunId, summary.Status);
            }
            catch (IngestionAlreadyRunningException)
            {
                logger.LogWarning(
                    "Skipped admin-triggered {Trigger}: another run holds the ingestion lease",
                    job.Trigger);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Admin-triggered {Trigger} run failed", job.Trigger);
            }
        }
    }
}
