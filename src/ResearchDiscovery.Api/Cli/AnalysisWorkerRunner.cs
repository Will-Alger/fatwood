using Microsoft.Extensions.Options;
using ResearchDiscovery.Application.Abstractions;
using ResearchDiscovery.Application.Options;
using ResearchDiscovery.Infrastructure.Analysis;
using ResearchDiscovery.Infrastructure.DependencyInjection;

namespace ResearchDiscovery.Api.Cli;

/// <summary>
/// The analysis worker: `dotnet ResearchDiscovery.Api.dll analyze-worker`.
/// Drains the durable Storage queue with bounded concurrency, deleting each
/// message once its paper is analyzed, and exits after the queue stays empty
/// for a few polls — so an event-driven ACA job (KEDA-scaled on queue depth)
/// spins up on demand, drains, and terminates. A failed paper's message is
/// left undeleted and reappears after its visibility timeout for a retry;
/// analysis is idempotent, so re-processing an already-done paper is a no-op.
/// </summary>
public static class AnalysisWorkerRunner
{
    private static readonly TimeSpan VisibilityTimeout = TimeSpan.FromMinutes(5);
    private const int BatchSize = 16;

    public static async Task<int> RunAsync()
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddInfrastructure(builder.Configuration);
        using var host = builder.Build();

        var options = host.Services.GetRequiredService<IOptions<AnalysisQueueOptions>>().Value;
        if (!options.UseStorageQueue)
        {
            Console.Error.WriteLine(
                "analyze-worker requires AnalysisQueue:UseStorageQueue=true.");
            return 64;
        }

        var queue = host.Services.GetRequiredService<StorageAnalysisQueue>();
        var logger = host.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("AnalysisWorker");
        using var slots = new SemaphoreSlim(options.WorkerConcurrency, options.WorkerConcurrency);

        var idlePolls = 0;
        var processed = 0;
        try
        {
            while (idlePolls < options.WorkerMaxIdlePolls && !cts.IsCancellationRequested)
            {
                var messages = await queue.ReceiveAsync(BatchSize, VisibilityTimeout, cts.Token);
                if (messages.Count == 0)
                {
                    idlePolls++;
                    await Task.Delay(1000, cts.Token);
                    continue;
                }

                idlePolls = 0;
                var tasks = messages.Select(async message =>
                {
                    await slots.WaitAsync(cts.Token);
                    try
                    {
                        var item = StorageAnalysisQueue.Parse(message.MessageText);
                        await using var scope = host.Services.CreateAsyncScope();
                        scope.ServiceProvider.GetRequiredService<ILlmUsageContext>().UserId = item.UserId;
                        await scope.ServiceProvider.GetRequiredService<IAnalysisService>()
                            .AnalyzeSelectionAsync([item.ArxivId], item.UserId, cts.Token);
                        await queue.DeleteAsync(message, cts.Token);
                        Interlocked.Increment(ref processed);
                    }
                    catch (OperationCanceledException) when (cts.IsCancellationRequested)
                    {
                        // shutting down — leave the message for the next run
                    }
                    catch (Exception ex)
                    {
                        // Leave undeleted: it reappears after the visibility
                        // timeout for another attempt (idempotent).
                        logger.LogError(ex, "Analysis of a queued paper failed; will retry");
                    }
                    finally
                    {
                        slots.Release();
                    }
                });
                await Task.WhenAll(tasks);
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // graceful shutdown
        }

        logger.LogInformation("Analysis worker exiting: {Processed} paper(s) processed", processed);
        return 0;
    }
}
