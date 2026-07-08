using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ResearchDiscovery.Application.Abstractions;
using ResearchDiscovery.Application.Options;

namespace ResearchDiscovery.Infrastructure.Arxiv;

/// <summary>
/// Typed client for the arXiv Atom query API. Category and date-range
/// filtering both live in search_query; the submittedDate range keys the
/// per-category delta ingestion off the stored high-water mark.
/// </summary>
public class ArxivClient(
    HttpClient httpClient,
    IOptions<ArxivOptions> options,
    ILogger<ArxivClient> logger) : IArxivClient
{
    private readonly ArxivOptions _options = options.Value;

    public async Task<ArxivPage> QueryAsync(ArxivQuery query, CancellationToken ct)
    {
        var url = BuildUrl(query);
        logger.LogDebug("arXiv query: {Url}", url);

        var page = await FetchAsync(url, ct);

        // arXiv occasionally returns a transient empty feed even though
        // totalResults says the window has more entries. One bounded re-request
        // is the documented community workaround.
        if (page.Entries.Count == 0 && page.TotalResults > query.Start)
        {
            logger.LogWarning(
                "arXiv returned an empty page with totalResults={Total} at start={Start}; retrying once",
                page.TotalResults, query.Start);
            page = await FetchAsync(url, ct);
        }

        return page;
    }

    private async Task<ArxivPage> FetchAsync(string url, CancellationToken ct)
    {
        using var response = await httpClient.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        var xml = await response.Content.ReadAsStringAsync(ct);
        return ArxivAtomParser.Parse(xml);
    }

    private string BuildUrl(ArxivQuery query)
    {
        // submittedDate uses GMT minute granularity: [YYYYMMDDHHMM TO YYYYMMDDHHMM]
        var from = query.FromUtc.UtcDateTime.ToString("yyyyMMddHHmm");
        var to = query.ToUtc.UtcDateTime.ToString("yyyyMMddHHmm");
        var searchQuery = $"cat:{query.CategoryCode} AND submittedDate:[{from} TO {to}]";

        return $"{_options.BaseUrl}" +
               $"?search_query={Uri.EscapeDataString(searchQuery)}" +
               $"&start={query.Start}" +
               $"&max_results={query.MaxResults}" +
               "&sortBy=submittedDate&sortOrder=ascending";
    }
}
