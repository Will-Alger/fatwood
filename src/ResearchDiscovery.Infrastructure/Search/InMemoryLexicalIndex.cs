using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ResearchDiscovery.Application.Abstractions;
using ResearchDiscovery.Infrastructure.Persistence;

namespace ResearchDiscovery.Infrastructure.Search;

/// <summary>
/// Classic BM25 (k1=1.2, b=0.75) over lowercased alphanumeric tokens of
/// title + abstract. ~21k documents build in a few seconds and score in
/// milliseconds; the postings live in memory alongside the embedding index
/// (same lazy-load + Invalidate lifecycle).
/// </summary>
public class InMemoryLexicalIndex(
    IDbContextFactory<AppDbContext> dbFactory,
    ILogger<InMemoryLexicalIndex> logger) : ILexicalIndex
{
    private const float K1 = 1.2f;
    private const float B = 0.75f;

    private sealed record Index(
        Dictionary<string, List<(long PaperId, int TermFrequency)>> Postings,
        Dictionary<long, int> DocLengths,
        double AverageDocLength);

    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private volatile Index? _index;

    public async Task<IReadOnlyList<ScoredPaper>> TopAsync(
        string query, int n, IReadOnlySet<long>? restrictTo, CancellationToken ct)
    {
        var index = await GetIndexAsync(ct);
        var terms = Tokenize(query).Distinct().ToList();
        var docCount = index.DocLengths.Count;
        if (terms.Count == 0 || docCount == 0)
        {
            return [];
        }

        var scores = new Dictionary<long, float>();
        foreach (var term in terms)
        {
            if (!index.Postings.TryGetValue(term, out var postings))
            {
                continue;
            }

            var idf = MathF.Log(1 + (docCount - postings.Count + 0.5f) / (postings.Count + 0.5f));
            foreach (var (paperId, tf) in postings)
            {
                if (restrictTo is not null && !restrictTo.Contains(paperId))
                {
                    continue;
                }

                var docLen = index.DocLengths[paperId];
                var norm = tf * (K1 + 1) /
                    (tf + K1 * (1 - B + B * (float)(docLen / index.AverageDocLength)));
                scores[paperId] = scores.GetValueOrDefault(paperId) + idf * norm;
            }
        }

        return scores
            .Select(kv => new ScoredPaper(kv.Key, kv.Value))
            .OrderByDescending(s => s.Score)
            .Take(n)
            .ToList();
    }

    public void Invalidate() => _index = null;

    private async Task<Index> GetIndexAsync(CancellationToken ct)
    {
        var cached = _index;
        if (cached is not null)
        {
            return cached;
        }

        await _loadLock.WaitAsync(ct);
        try
        {
            cached = _index;
            if (cached is not null)
            {
                return cached;
            }

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var docs = await db.Papers
                .AsNoTracking()
                .Select(p => new { p.Id, p.Title, p.Abstract })
                .ToListAsync(ct);

            var postings = new Dictionary<string, List<(long, int)>>(StringComparer.Ordinal);
            var docLengths = new Dictionary<long, int>(docs.Count);
            long totalLength = 0;

            foreach (var doc in docs)
            {
                var counts = new Dictionary<string, int>(StringComparer.Ordinal);
                var length = 0;
                foreach (var token in Tokenize($"{doc.Title} {doc.Abstract}"))
                {
                    counts[token] = counts.GetValueOrDefault(token) + 1;
                    length++;
                }

                docLengths[doc.Id] = length;
                totalLength += length;
                foreach (var (term, tf) in counts)
                {
                    if (!postings.TryGetValue(term, out var list))
                    {
                        postings[term] = list = [];
                    }

                    list.Add((doc.Id, tf));
                }
            }

            var built = new Index(
                postings,
                docLengths,
                docLengths.Count > 0 ? totalLength / (double)docLengths.Count : 1);

            logger.LogInformation(
                "Lexical index built: {Docs} documents, {Terms} terms", docs.Count, postings.Count);
            _index = built;
            return built;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    internal static IEnumerable<string> Tokenize(string text)
    {
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var isWord = i < text.Length && char.IsLetterOrDigit(text[i]);
            if (isWord && start < 0)
            {
                start = i;
            }
            else if (!isWord && start >= 0)
            {
                if (i - start >= 2)
                {
                    yield return text[start..i].ToLowerInvariant();
                }

                start = -1;
            }
        }
    }
}
