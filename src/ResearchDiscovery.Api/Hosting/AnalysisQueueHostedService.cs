using ResearchDiscovery.Application.Abstractions;

namespace ResearchDiscovery.Api.Hosting;

/// <summary>Executes admin-triggered analysis jobs from the in-process queue.</summary>
public class AnalysisQueueHostedService(
    AnalysisJobQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<AnalysisQueueHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var request in queue.DequeueAllAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var analysis = scope.ServiceProvider.GetRequiredService<IAnalysisService>();

                var summary = await analysis.AnalyzeAsync(request, stoppingToken);

                logger.LogInformation(
                    "Admin-triggered analysis of {Category} finished: " +
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
                logger.LogError(ex, "Admin-triggered analysis of {Category} failed", request.CategoryCode);
            }
        }
    }
}
