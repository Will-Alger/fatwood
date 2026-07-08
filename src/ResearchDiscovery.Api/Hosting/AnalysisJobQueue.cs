using System.Threading.Channels;
using ResearchDiscovery.Application.Abstractions;

namespace ResearchDiscovery.Api.Hosting;

/// <summary>
/// Hands admin-triggered analysis jobs to a background worker so the admin
/// endpoint can return 202 immediately — a category run makes one LLM call
/// per paper and takes minutes. Single-worker consumption also serializes
/// runs, so two admin triggers can't analyze the same papers twice.
/// </summary>
public class AnalysisJobQueue
{
    private readonly Channel<AnalysisRequest> _channel =
        Channel.CreateBounded<AnalysisRequest>(new BoundedChannelOptions(capacity: 4)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
        });

    public bool TryEnqueue(AnalysisRequest request) => _channel.Writer.TryWrite(request);

    public IAsyncEnumerable<AnalysisRequest> DequeueAllAsync(CancellationToken ct) =>
        _channel.Reader.ReadAllAsync(ct);
}
