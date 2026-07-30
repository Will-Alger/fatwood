using ResearchDiscovery.Application.Abstractions;

namespace ResearchDiscovery.Api.Hosting;

/// <summary>
/// Loads the candidate-set cache right after startup so the first filtered
/// search on a fresh revision never pays the multi-second database read
/// inline. Best-effort: on failure the first search falls back to the
/// inline load, exactly as if the warmer didn't exist.
/// </summary>
public class CandidateCacheWarmer(
    ICandidateSetCache cache,
    ILogger<CandidateCacheWarmer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Give migrations/startup a beat before hitting the database.
        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        try
        {
            var start = System.Diagnostics.Stopwatch.GetTimestamp();
            await cache.WarmAsync(stoppingToken);
            logger.LogInformation(
                "Candidate-set cache warmed in {Ms:F0}ms",
                System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Candidate-set cache warm failed — first filtered search will load inline");
        }
    }
}
