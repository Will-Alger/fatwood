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
    // Sized to the work item: an analysis call is ~8s, so 90s covers a call
    // plus SDK retries with margin. A shorter timeout matters because a
    // transiently-failed head-of-selection paper blocks the client's ordered
    // reveal until its redelivery — 5 minutes here collided with the client's
    // 6-minute give-up.
    private static readonly TimeSpan VisibilityTimeout = TimeSpan.FromSeconds(90);

    /// <summary>Delivery attempts before a message is dropped as poison —
    /// without this, an always-failing message redelivers forever, re-waking
    /// the scale-to-zero job every visibility timeout.</summary>
    private const int PoisonDequeueCount = 5;

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

        // Receive exactly as many as we can process at once so a message never
        // sits invisible in a local backlog while another execution could take
        // it (Storage caps a single receive at 32).
        var batchSize = Math.Clamp(options.WorkerConcurrency, 1, 32);

        var idlePolls = 0;
        var processed = 0;
        try
        {
            while (idlePolls < options.WorkerMaxIdlePolls && !cts.IsCancellationRequested)
            {
                var messages = await queue.ReceiveAsync(batchSize, VisibilityTimeout, cts.Token);
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
                        if (message.DequeueCount >= PoisonDequeueCount)
                        {
                            logger.LogError(
                                "Dropping analysis message after {Attempts} failed attempts",
                                message.DequeueCount);
                            await queue.DeleteAsync(message, cts.Token);
                            return;
                        }

                        var item = StorageAnalysisQueue.Parse(message.MessageText);
                        await using var scope = host.Services.CreateAsyncScope();

                        // Budget was gated at enqueue, but a large backlog can
                        // outlive the remaining budget — re-check per item so a
                        // drained ledger stops the run instead of overshooting.
                        if (item.UserId is { } budgetUserId)
                        {
                            try
                            {
                                await scope.ServiceProvider.GetRequiredService<IBudgetService>()
                                    .EnsureCanSpendAsync(budgetUserId, cts.Token);
                            }
                            catch (BudgetExceededException)
                            {
                                await queue.DeleteAsync(message, cts.Token);
                                return;
                            }
                        }

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
