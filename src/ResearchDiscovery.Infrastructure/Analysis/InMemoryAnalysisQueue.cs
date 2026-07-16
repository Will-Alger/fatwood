using System.Threading.Channels;
using ResearchDiscovery.Application.Abstractions;

namespace ResearchDiscovery.Infrastructure.Analysis;

/// <summary>
/// In-process analysis queue (local/dev/tests, and any deployment without a
/// Storage queue configured). Fine-grained work items are drained by
/// <see cref="InMemoryAnalysisWorker"/> with bounded concurrency. A pending
/// counter (enqueued minus completed) backs the "still working" status.
/// </summary>
public sealed class InMemoryAnalysisQueue : IAnalysisQueue
{
    // Generous bound: a selection is at most 200 papers and items are tiny.
    private readonly Channel<AnalysisWorkItem> _channel =
        Channel.CreateBounded<AnalysisWorkItem>(new BoundedChannelOptions(4096)
        {
            FullMode = BoundedChannelFullMode.Wait,
        });

    private int _pending;

    public async Task EnqueueSelectionAsync(
        long? userId, IReadOnlyList<string> arxivIds, CancellationToken ct)
    {
        foreach (var arxivId in arxivIds)
        {
            Interlocked.Increment(ref _pending);
            await _channel.Writer.WriteAsync(new AnalysisWorkItem(userId, arxivId), ct);
        }
    }

    public Task<bool> HasPendingWorkAsync(CancellationToken ct) =>
        Task.FromResult(Volatile.Read(ref _pending) > 0);

    /// <summary>Consumed by the in-process worker; not part of IAnalysisQueue.</summary>
    public IAsyncEnumerable<AnalysisWorkItem> DequeueAllAsync(CancellationToken ct) =>
        _channel.Reader.ReadAllAsync(ct);

    /// <summary>The worker calls this once an item finishes (or fails).</summary>
    public void MarkDone() => Interlocked.Decrement(ref _pending);
}
