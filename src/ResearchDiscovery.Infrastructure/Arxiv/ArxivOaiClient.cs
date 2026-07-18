using System.Net;
using Microsoft.Extensions.Logging;
using ResearchDiscovery.Application.Abstractions;

namespace ResearchDiscovery.Infrastructure.Arxiv;

/// <summary>
/// Typed client for the arXiv OAI-PMH endpoint. arXiv's bulk-harvest etiquette
/// differs from the query API: the server signals flow control with HTTP 503 +
/// Retry-After, which this client honors directly instead of going through the
/// shared resilience pipeline. Pacing assumes a single consumer — the bulk
/// harvest pages strictly sequentially, so a plain delay is enough and no gate
/// is needed. Note: export.arxiv.org/oai2 301-redirects to oaipmh.arxiv.org/oai;
/// HttpClient follows it automatically.
/// </summary>
public class ArxivOaiClient(
    HttpClient httpClient,
    ILogger<ArxivOaiClient> logger) : IArxivOaiClient
{
    private const string BaseUrl = "https://export.arxiv.org/oai2";
    private const int MaxAttempts = 5;

    private static readonly TimeSpan MinRequestInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(60);

    private long _lastRequestTicks = long.MinValue;

    public Task<string> ListRecordsAsync(string set, DateTimeOffset fromUtc, CancellationToken ct) =>
        FetchAsync(BuildInitialUrl(set, fromUtc), ct);

    public Task<string> ResumeAsync(string resumptionToken, CancellationToken ct) =>
        FetchAsync(BuildResumeUrl(resumptionToken), ct);

    // OAI from= has day granularity and filters on datestamp (last touched),
    // not submission date; the harvest service re-filters on Published.
    public static string BuildInitialUrl(string set, DateTimeOffset fromUtc) =>
        $"{BaseUrl}?verb=ListRecords&metadataPrefix=arXiv" +
        $"&set={Uri.EscapeDataString(set)}" +
        $"&from={fromUtc.UtcDateTime:yyyy-MM-dd}";

    public static string BuildResumeUrl(string resumptionToken) =>
        $"{BaseUrl}?verb=ListRecords&resumptionToken={Uri.EscapeDataString(resumptionToken)}";

    private async Task<string> FetchAsync(string url, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            await PaceAsync(ct);

            using var response = await httpClient.GetAsync(url, ct);
            if (response.StatusCode == HttpStatusCode.ServiceUnavailable && attempt < MaxAttempts)
            {
                var delay = RetryAfterDelay(response);
                logger.LogWarning(
                    "arXiv OAI returned 503; waiting {DelaySeconds}s before retry {Attempt}/{MaxAttempts}",
                    delay.TotalSeconds, attempt, MaxAttempts - 1);
                await Task.Delay(delay, ct);
                continue;
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(ct);
        }
    }

    /// <summary>Retry-After in seconds, defaulted when absent or unparsable, capped at one minute.</summary>
    private static TimeSpan RetryAfterDelay(HttpResponseMessage response)
    {
        var delta = response.Headers.RetryAfter?.Delta;
        if (delta is null || delta <= TimeSpan.Zero)
        {
            return DefaultRetryAfter;
        }

        return delta > MaxRetryAfter ? MaxRetryAfter : delta.Value;
    }

    private async Task PaceAsync(CancellationToken ct)
    {
        if (_lastRequestTicks != long.MinValue)
        {
            var elapsed = TimeSpan.FromMilliseconds(Environment.TickCount64 - _lastRequestTicks);
            if (elapsed < MinRequestInterval)
            {
                await Task.Delay(MinRequestInterval - elapsed, ct);
            }
        }

        _lastRequestTicks = Environment.TickCount64;
    }
}
