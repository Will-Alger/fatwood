using Microsoft.Extensions.Options;
using ResearchDiscovery.Application.Abstractions;
using ResearchDiscovery.Application.Options;

namespace ResearchDiscovery.Infrastructure.Analysis;

/// <summary>
/// Storage-mode analysis queue with a hot lane. The first few papers of a
/// selection go to the in-process queue, drained by the web host itself — the
/// analysis call is one awaited HTTPS request, so the already-warm web replica
/// (kept alive by the user's own status polls) serves the papers the user is
/// actually looking at in seconds, instead of waiting out the worker job's
/// KEDA poll + container cold start. The remainder goes to the durable Storage
/// queue for the scaled worker. Lanes are strictly partitioned — no paper is
/// enqueued twice — so metering never double-bills; a web replica restart can
/// only lose the few hot-lane papers, and a re-click re-runs them cheaply
/// (analysis is idempotent).
/// </summary>
public sealed class HybridAnalysisQueue(
    InMemoryAnalysisQueue hotLane,
    StorageAnalysisQueue durable,
    IOptions<AnalysisQueueOptions> options) : IAnalysisQueue
{
    public async Task EnqueueSelectionAsync(
        long? userId, IReadOnlyList<string> arxivIds, CancellationToken ct)
    {
        var hotCount = Math.Clamp(options.Value.HotLaneCount, 0, arxivIds.Count);
        if (hotCount > 0)
        {
            await hotLane.EnqueueSelectionAsync(userId, arxivIds.Take(hotCount).ToList(), ct);
        }

        if (hotCount < arxivIds.Count)
        {
            await durable.EnqueueSelectionAsync(userId, arxivIds.Skip(hotCount).ToList(), ct);
        }
    }

    public async Task<bool> HasPendingWorkAsync(CancellationToken ct) =>
        await hotLane.HasPendingWorkAsync(ct) || await durable.HasPendingWorkAsync(ct);
}
