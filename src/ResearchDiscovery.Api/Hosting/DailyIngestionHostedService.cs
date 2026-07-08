using Microsoft.Extensions.Options;
using ResearchDiscovery.Application.Abstractions;
using ResearchDiscovery.Application.Options;

namespace ResearchDiscovery.Api.Hosting;

/// <summary>
/// Runs a delta ingestion once a day at the configured UTC time. Overlap with
/// a concurrently running manual backfill (a separate process) is prevented by
/// the database ingestion lease, not by anything in this class.
/// </summary>
public class DailyIngestionHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<IngestionOptions> options,
    ILogger<DailyIngestionHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var schedule = options.Value.Schedule;
        if (!schedule.Enabled)
        {
            logger.LogInformation("Scheduled ingestion is disabled (Ingestion:Schedule:Enabled=false)");
            return;
        }

        var timeOfDay = TimeOnly.ParseExact(schedule.TimeUtc, "HH:mm").ToTimeSpan();
        logger.LogInformation("Scheduled daily ingestion enabled at {TimeUtc} UTC", schedule.TimeUtc);

        while (!stoppingToken.IsCancellationRequested)
        {
            var next = NextOccurrence(timeOfDay);
            logger.LogInformation("Next scheduled ingestion at {Next:u}", next);

            try
            {
                await DelayUntil(next, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var ingestion = scope.ServiceProvider.GetRequiredService<IIngestionService>();
                var summary = await ingestion.RunDeltaAsync(stoppingToken);
                logger.LogInformation(
                    "Scheduled delta run {RunId} finished with status {Status}",
                    summary.RunId, summary.Status);
            }
            catch (IngestionAlreadyRunningException)
            {
                logger.LogWarning("Skipped scheduled ingestion: another run holds the ingestion lease");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scheduled ingestion run failed; will retry at the next scheduled time");
            }
        }
    }

    private static DateTimeOffset NextOccurrence(TimeSpan timeOfDayUtc)
    {
        var now = DateTimeOffset.UtcNow;
        var next = new DateTimeOffset(now.UtcDateTime.Date.Add(timeOfDayUtc), TimeSpan.Zero);
        return next <= now ? next.AddDays(1) : next;
    }

    /// <summary>Delays in ≤1h chunks so long waits tolerate timer limits and clock adjustments.</summary>
    private static async Task DelayUntil(DateTimeOffset target, CancellationToken ct)
    {
        while (true)
        {
            var remaining = target - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            await Task.Delay(remaining > TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : remaining, ct);
        }
    }
}
