using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ResearchDiscovery.Application.Abstractions;
using ResearchDiscovery.Application.Options;
using ResearchDiscovery.Infrastructure.Persistence;

namespace ResearchDiscovery.Infrastructure.Embeddings;

/// <summary>
/// All paper vectors cached in memory (~25 MB at 16k × 384 floats); cosine
/// over the full corpus is a few milliseconds. Vectors are L2-normalized at
/// embed time, so cosine reduces to a dot product. No pgvector — this keeps
/// the database provider-portable.
/// </summary>
public class InMemoryEmbeddingIndex(
    IDbContextFactory<AppDbContext> dbFactory,
    IOptions<EmbeddingOptions> options,
    ILogger<InMemoryEmbeddingIndex> logger) : IEmbeddingIndex
{
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private volatile Dictionary<long, float[]>? _vectors;

    public async Task<IReadOnlyList<ScoredPaper>> TopAsync(
        float[] query, int n, IReadOnlySet<long>? restrictTo, CancellationToken ct)
    {
        var vectors = await GetVectorsAsync(ct);

        var scored = new List<ScoredPaper>(
            restrictTo?.Count ?? vectors.Count);

        foreach (var (paperId, vector) in vectors)
        {
            if (restrictTo is not null && !restrictTo.Contains(paperId))
            {
                continue;
            }

            scored.Add(new ScoredPaper(paperId, Dot(query, vector)));
        }

        return scored
            .OrderByDescending(s => s.Score)
            .Take(n)
            .ToList();
    }

    public async Task<IReadOnlyList<ScoredPaper>> TopMultiAsync(
        float[] primary,
        IReadOnlyList<float[]> topics,
        int n,
        IReadOnlySet<long>? restrictTo,
        CancellationToken ct)
    {
        if (topics.Count == 0)
        {
            return await TopAsync(primary, n, restrictTo, ct);
        }

        var vectors = await GetVectorsAsync(ct);
        var scored = new List<ScoredPaper>(restrictTo?.Count ?? vectors.Count);

        foreach (var (paperId, vector) in vectors)
        {
            if (restrictTo is not null && !restrictTo.Contains(paperId))
            {
                continue;
            }

            var bestTopic = float.MinValue;
            foreach (var topic in topics)
            {
                var score = Dot(topic, vector);
                if (score > bestTopic)
                {
                    bestTopic = score;
                }
            }

            scored.Add(new ScoredPaper(paperId, (Dot(primary, vector) + bestTopic) / 2));
        }

        return scored
            .OrderByDescending(s => s.Score)
            .Take(n)
            .ToList();
    }

    public async Task<IReadOnlyDictionary<long, float>> ScoreAsync(
        IEnumerable<long> paperIds, float[] query, CancellationToken ct)
    {
        var vectors = await GetVectorsAsync(ct);
        var result = new Dictionary<long, float>();
        foreach (var id in paperIds)
        {
            if (vectors.TryGetValue(id, out var vector))
            {
                result[id] = Dot(query, vector);
            }
        }

        return result;
    }

    public void Invalidate() => _vectors = null;

    private async Task<Dictionary<long, float[]>> GetVectorsAsync(CancellationToken ct)
    {
        var cached = _vectors;
        if (cached is not null)
        {
            return cached;
        }

        await _loadLock.WaitAsync(ct);
        try
        {
            cached = _vectors;
            if (cached is not null)
            {
                return cached;
            }

            var modelVersion = options.Value.ModelVersion;
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var rows = await db.PaperEmbeddings
                .AsNoTracking()
                .Where(e => e.ModelVersion == modelVersion)
                .Select(e => new { e.PaperId, e.Vector })
                .ToListAsync(ct);

            var loaded = rows.ToDictionary(
                r => r.PaperId,
                r => PaperEmbeddingService.FromBytes(r.Vector));

            logger.LogInformation("Embedding index loaded: {Count} vectors", loaded.Count);
            _vectors = loaded;
            return loaded;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private static float Dot(float[] a, float[] b)
    {
        var length = Math.Min(a.Length, b.Length);
        var sum = 0f;
        for (var i = 0; i < length; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }
}
