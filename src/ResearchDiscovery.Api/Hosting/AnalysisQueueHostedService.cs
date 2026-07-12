using ResearchDiscovery.Application.Abstractions;

namespace ResearchDiscovery.Api.Hosting;

/// <summary>Executes admin-triggered analysis jobs from the in-process queue.</summary>
public class AnalysisQueueHostedService(
    AnalysisJobQueue queue,
    AnalysisProgressTracker tracker,
    IServiceScopeFactory scopeFactory,
    ILogger<AnalysisQueueHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in queue.DequeueAllAsync(stoppingToken))
        {
            tracker.Begin();
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();

                // Point the scope's usage context at the requester so every
                // Anthropic call in this run lands on their ledger.
                scope.ServiceProvider.GetRequiredService<ILlmUsageContext>()
                    .UserId = job.RequestedByUserId;

                var analysis = scope.ServiceProvider.GetRequiredService<IAnalysisService>();

                var summary = job switch
                {
                    AnalysisJob.Category c => await analysis.AnalyzeAsync(
                        c.Request, job.RequestedByUserId, stoppingToken),
                    AnalysisJob.Selection s => await analysis.AnalyzeSelectionAsync(
                        s.ArxivIds, job.RequestedByUserId, stoppingToken),
                    _ => throw new InvalidOperationException($"Unknown job type {job.GetType().Name}"),
                };

                logger.LogInformation(
                    "Admin-triggered analysis of {Label} finished: " +
                    "selected {Selected}, analyzed {Analyzed}, declined {Declined}, failed {Failed}",
                    summary.CategoryCode, summary.PapersSelected, summary.PapersAnalyzed,
                    summary.PapersDeclined, summary.PapersFailed);
            }
            catch (UnknownCategoryException ex)
            {
                // The endpoint validates before enqueueing; this covers races
                // (category deleted between validation and execution).
                logger.LogWarning("Skipped analysis run: {Message}", ex.Message);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Admin-triggered analysis job failed");
            }
            finally
            {
                tracker.End();
            }
        }
    }
}
