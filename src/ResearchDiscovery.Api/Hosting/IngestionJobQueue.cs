using System.Threading.Channels;
using ResearchDiscovery.Application.Abstractions;
using ResearchDiscovery.Domain.Entities;

namespace ResearchDiscovery.Api.Hosting;

public sealed record IngestionJobRequest(IngestionTrigger Trigger, BackfillOverrides? Overrides);

/// <summary>
/// Hands admin-triggered ingestion jobs to a background worker so the admin
/// endpoint can return 202 immediately — a backfill can take tens of minutes,
/// far past any sensible HTTP timeout.
/// </summary>
public class IngestionJobQueue
{
    private readonly Channel<IngestionJobRequest> _channel =
        Channel.CreateBounded<IngestionJobRequest>(new BoundedChannelOptions(capacity: 4)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
        });

    public bool TryEnqueue(IngestionJobRequest request) => _channel.Writer.TryWrite(request);

    public IAsyncEnumerable<IngestionJobRequest> DequeueAllAsync(CancellationToken ct) =>
        _channel.Reader.ReadAllAsync(ct);
}
