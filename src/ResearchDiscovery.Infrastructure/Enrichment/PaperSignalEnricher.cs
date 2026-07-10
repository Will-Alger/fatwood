using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ResearchDiscovery.Domain.Entities;
using ResearchDiscovery.Infrastructure.Persistence;

namespace ResearchDiscovery.Infrastructure.Enrichment;

public sealed record EnrichSummary(int CitationsFetched, int StarsFetched, int Failed);

/// <summary>
/// Fetches external quality signals — an ops-only CLI concern, never on a
/// request path (same posture as ingestion):
///
/// - Citation counts from Semantic Scholar's batch endpoint (500 ids/call,
///   free tier, ~1 req/s etiquette). 21k papers ≈ 43 calls.
/// - GitHub stars for papers advertising a github.com repo, only when a
///   GITHUB_TOKEN is present (unauthenticated limits are useless at 60/hr).
///
/// Idempotent and incremental: papers with a signal row newer than
/// RefreshAfterDays are skipped, so re-runs only pay for the delta.
/// </summary>
public class PaperSignalEnricher(
    IDbContextFactory<AppDbContext> dbFactory,
    IHttpClientFactory httpClientFactory,
    ILogger<PaperSignalEnricher> logger)
{
    public const string HttpClientName = "signal-enrichment";
    private const int SemanticScholarBatchSize = 500;
    private const int RefreshAfterDays = 14;

    public async Task<EnrichSummary> EnrichAsync(bool includeStars, CancellationToken ct)
    {
        var citations = await FetchCitationsAsync(ct);
        var stars = 0;
        var failed = 0;

        if (includeStars)
        {
            var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
            if (string.IsNullOrWhiteSpace(token))
            {
                logger.LogWarning(
                    "GITHUB_TOKEN not set — skipping stars (unauthenticated GitHub limits are 60/hr)");
            }
            else
            {
                (stars, failed) = await FetchStarsAsync(token, ct);
            }
        }

        return new EnrichSummary(citations, stars, failed);
    }

    private async Task<int> FetchCitationsAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-RefreshAfterDays);
        var pending = await db.Papers
            .AsNoTracking()
            .Where(p => p.Signal == null || p.Signal.FetchedUtc < cutoff)
            .Select(p => new { p.Id, p.ArxivId })
            .OrderBy(p => p.Id)
            .ToListAsync(ct);

        if (pending.Count == 0)
        {
            return 0;
        }

        logger.LogInformation("Fetching citations for {Count} papers from Semantic Scholar", pending.Count);
        var client = httpClientFactory.CreateClient(HttpClientName);
        var fetched = 0;

        foreach (var batch in pending.Chunk(SemanticScholarBatchSize))
        {
            ct.ThrowIfCancellationRequested();

            using var response = await client.PostAsJsonAsync(
                "https://api.semanticscholar.org/graph/v1/paper/batch?fields=citationCount,influentialCitationCount",
                new { ids = batch.Select(p => $"ARXIV:{p.ArxivId}").ToArray() }, ct);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                logger.LogWarning("Semantic Scholar rate limit hit; backing off 30s");
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                continue; // this batch is skipped this run; the next run picks it up
            }

            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

            // Response array is positional with the request; unknown papers are null.
            await using var writeDb = await dbFactory.CreateDbContextAsync(ct);
            var ids = batch.Select(p => p.Id).ToHashSet();
            var existing = await writeDb.PaperSignals
                .Where(s => ids.Contains(s.PaperId))
                .ToDictionaryAsync(s => s.PaperId, ct);

            var i = 0;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var paper = batch[i++];
                int? count = null;
                int? influential = null;
                if (item.ValueKind == JsonValueKind.Object)
                {
                    count = item.TryGetProperty("citationCount", out var c) && c.ValueKind == JsonValueKind.Number
                        ? c.GetInt32() : null;
                    influential = item.TryGetProperty("influentialCitationCount", out var ic) && ic.ValueKind == JsonValueKind.Number
                        ? ic.GetInt32() : null;
                }

                if (!existing.TryGetValue(paper.Id, out var signal))
                {
                    signal = new PaperSignal { PaperId = paper.Id };
                    writeDb.PaperSignals.Add(signal);
                }

                signal.CitationCount = count;
                signal.InfluentialCitationCount = influential;
                signal.FetchedUtc = DateTimeOffset.UtcNow;
            }

            await writeDb.SaveChangesAsync(ct);
            fetched += batch.Length;
            logger.LogInformation("Citations: {Fetched}/{Total}", fetched, pending.Count);

            await Task.Delay(TimeSpan.FromSeconds(1.2), ct);
        }

        return fetched;
    }

    private async Task<(int Fetched, int Failed)> FetchStarsAsync(string token, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var candidates = await db.Papers
            .AsNoTracking()
            .Where(p => p.CodeUrl != null && p.CodeUrl.Contains("github.com")
                && p.Signal != null && p.Signal.GitHubStars == null)
            .Select(p => new { p.Id, p.CodeUrl })
            .ToListAsync(ct);

        logger.LogInformation("Fetching GitHub stars for {Count} repos", candidates.Count);
        var client = httpClientFactory.CreateClient(HttpClientName);
        var fetched = 0;
        var failed = 0;

        foreach (var paper in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var repo = ParseGitHubRepo(paper.CodeUrl!);
            if (repo is null)
            {
                failed++;
                continue;
            }

            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"https://api.github.com/repos/{repo}");
            request.Headers.Authorization = new("Bearer", token);
            request.Headers.UserAgent.ParseAdd("ResearchDiscovery/1.0");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");

            try
            {
                using var response = await client.SendAsync(request, ct);
                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
                {
                    failed++; // deleted/private repo — nothing to record
                }
                else
                {
                    response.EnsureSuccessStatusCode();
                    using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                    var stars = doc.RootElement.GetProperty("stargazers_count").GetInt32();

                    await using var writeDb = await dbFactory.CreateDbContextAsync(ct);
                    var signal = await writeDb.PaperSignals.SingleOrDefaultAsync(
                        s => s.PaperId == paper.Id, ct);
                    if (signal is not null)
                    {
                        signal.GitHubStars = stars;
                        await writeDb.SaveChangesAsync(ct);
                        fetched++;
                    }
                }
            }
            catch (HttpRequestException ex)
            {
                failed++;
                logger.LogWarning("Stars fetch failed for {Repo}: {Message}", repo, ex.Message);
            }

            if ((fetched + failed) % 200 == 0)
            {
                logger.LogInformation("Stars: {Done}/{Total}", fetched + failed, candidates.Count);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        }

        return (fetched, failed);
    }

    /// <summary>"https://github.com/owner/repo/anything" → "owner/repo", else null.</summary>
    internal static string? ParseGitHubRepo(string url)
    {
        var marker = url.IndexOf("github.com/", StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
        {
            return null;
        }

        var segments = url[(marker + "github.com/".Length)..]
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
        {
            return null;
        }

        var repo = segments[1].TrimEnd('.', ',', ')', ';');
        if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            repo = repo[..^4];
        }

        return repo.Length > 0 ? $"{segments[0]}/{repo}" : null;
    }
}
