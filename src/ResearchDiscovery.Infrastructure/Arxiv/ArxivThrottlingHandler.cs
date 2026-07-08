using Microsoft.Extensions.Options;
using ResearchDiscovery.Application.Options;

namespace ResearchDiscovery.Infrastructure.Arxiv;

/// <summary>
/// Enforces arXiv's rate-limit etiquette (one request every ~3 seconds)
/// process-wide. Registered as a singleton and placed INSIDE the resilience
/// handler so that retries are paced too.
/// </summary>
public class ArxivThrottlingHandler(IOptions<ArxivOptions> options) : DelegatingHandler
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _minInterval = TimeSpan.FromSeconds(options.Value.MinRequestIntervalSeconds);
    private long _lastRequestTicks = long.MinValue;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_lastRequestTicks != long.MinValue)
            {
                var elapsed = TimeSpan.FromMilliseconds(Environment.TickCount64 - _lastRequestTicks);
                if (elapsed < _minInterval)
                {
                    await Task.Delay(_minInterval - elapsed, cancellationToken);
                }
            }

            _lastRequestTicks = Environment.TickCount64;
            return await base.SendAsync(request, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }
}
