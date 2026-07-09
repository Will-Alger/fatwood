using System.Threading.Channels;
using ResearchDiscovery.Application.Abstractions;

namespace ResearchDiscovery.Api.Hosting;

/// <summary>An admin-triggered analysis job: a category sweep or an explicit paper selection.</summary>
public abstract record AnalysisJob
{
    public sealed record Category(AnalysisRequest Request) : AnalysisJob;

    public sealed record Selection(IReadOnlyList<string> ArxivIds) : AnalysisJob;
}

/// <summary>
/// Hands admin-triggered analysis jobs to a background worker so the admin
/// endpoint can return 202 immediately — a run makes one LLM call per paper
/// and takes minutes. Single-worker consumption also serializes runs, so two
/// admin triggers can't analyze the same papers twice.
/// </summary>
public class AnalysisJobQueue
{
    private readonly Channel<AnalysisJob> _channel =
        Channel.CreateBounded<AnalysisJob>(new BoundedChannelOptions(capacity: 4)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
        });

    public bool TryEnqueue(AnalysisJob job) => _channel.Writer.TryWrite(job);

    public IAsyncEnumerable<AnalysisJob> DequeueAllAsync(CancellationToken ct) =>
        _channel.Reader.ReadAllAsync(ct);
}
