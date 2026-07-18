using System.Buffers.Binary;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ResearchDiscovery.Application.Abstractions;
using ResearchDiscovery.Application.Options;
using ResearchDiscovery.Infrastructure.Persistence;
using ResearchDiscovery.Infrastructure.Search;

namespace ResearchDiscovery.Infrastructure.Embeddings;

/// <summary>
/// Full-corpus vector index held in memory as int8-quantized packed arrays —
/// ~125 MB at 300k × 384 instead of ~500 MB of float32 dictionaries — scanned
/// with a SIMD integer dot product and a bounded top-K heap. Loads from a
/// blob snapshot when configured (seconds) and falls back to building from
/// the database. Per-entry publication days support date-gated search without
/// materializing giant candidate-id sets. No pgvector — the database stays
/// provider-portable.
/// </summary>
public class InMemoryEmbeddingIndex(
    IDbContextFactory<AppDbContext> dbFactory,
    IOptions<EmbeddingOptions> options,
    SearchIndexSnapshotStore snapshots,
    ILogger<InMemoryEmbeddingIndex> logger) : IEmbeddingIndex
{
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private volatile PackedVectors? _data;

    public static string SnapshotBlobName(string modelVersion) => $"embeddings-{modelVersion}.bin";

    public async Task<IReadOnlyList<ScoredPaper>> TopAsync(
        float[] query, int n, IReadOnlySet<long>? restrictTo, CancellationToken ct,
        DateTimeOffset? publishedAfter = null) =>
        await TopMultiAsync(query, [], n, restrictTo, ct, publishedAfter);

    public async Task<IReadOnlyList<ScoredPaper>> TopMultiAsync(
        float[] primary,
        IReadOnlyList<float[]> topics,
        int n,
        IReadOnlySet<long>? restrictTo,
        CancellationToken ct,
        DateTimeOffset? publishedAfter = null)
    {
        var data = await GetDataAsync(ct);
        if (data.Count == 0)
        {
            return [];
        }

        var (primaryQ, primaryScale) = Int8Quantization.Quantize(primary);
        var topicQ = new (sbyte[] Q, float Scale)[topics.Count];
        for (var t = 0; t < topics.Count; t++)
        {
            topicQ[t] = Int8Quantization.Quantize(topics[t]);
        }

        var cutoff = ToEpochDay(publishedAfter);

        // Bounded min-heap: O(N log K) beats sorting the whole corpus.
        var heap = new PriorityQueue<long, float>(n + 1);
        var dims = data.Dims;
        for (var i = 0; i < data.Count; i++)
        {
            if (cutoff is { } day && data.EpochDays[i] < day)
            {
                continue;
            }

            if (restrictTo is not null && !restrictTo.Contains(data.PaperIds[i]))
            {
                continue;
            }

            var vector = data.Vectors.AsSpan(i * dims, dims);
            var docScale = data.Scales[i];
            var score = Int8Quantization.Dot(primaryQ, vector) * primaryScale * docScale;

            if (topicQ.Length > 0)
            {
                var bestTopic = float.MinValue;
                foreach (var (q, scale) in topicQ)
                {
                    var topicScore = Int8Quantization.Dot(q, vector) * scale * docScale;
                    if (topicScore > bestTopic)
                    {
                        bestTopic = topicScore;
                    }
                }

                score = (score + bestTopic) / 2;
            }

            if (heap.Count < n)
            {
                heap.Enqueue(data.PaperIds[i], score);
            }
            else if (heap.TryPeek(out _, out var min) && score > min)
            {
                heap.DequeueEnqueue(data.PaperIds[i], score);
            }
        }

        var result = new List<ScoredPaper>(heap.Count);
        while (heap.TryDequeue(out var paperId, out var score))
        {
            result.Add(new ScoredPaper(paperId, score));
        }

        result.Reverse(); // heap drains lowest-first
        return result;
    }

    public async Task<IReadOnlyDictionary<long, float>> ScoreAsync(
        IEnumerable<long> paperIds, float[] query, CancellationToken ct)
    {
        var data = await GetDataAsync(ct);
        var (q, qScale) = Int8Quantization.Quantize(query);

        var result = new Dictionary<long, float>();
        foreach (var id in paperIds)
        {
            var i = Array.BinarySearch(data.PaperIds, id);
            if (i >= 0)
            {
                var vector = data.Vectors.AsSpan(i * data.Dims, data.Dims);
                result[id] = Int8Quantization.Dot(q, vector) * qScale * data.Scales[i];
            }
        }

        return result;
    }

    public void Invalidate() => _data = null;

    private async Task<PackedVectors> GetDataAsync(CancellationToken ct)
    {
        var cached = _data;
        if (cached is not null)
        {
            return cached;
        }

        await _loadLock.WaitAsync(ct);
        try
        {
            cached = _data;
            if (cached is not null)
            {
                return cached;
            }

            var modelVersion = options.Value.ModelVersion;
            PackedVectors? loaded = null;

            var blob = await snapshots.TryDownloadAsync(SnapshotBlobName(modelVersion), ct);
            if (blob is not null)
            {
                try
                {
                    loaded = PackedVectors.Deserialize(blob);
                    logger.LogInformation(
                        "Embedding index loaded from snapshot: {Count} vectors", loaded.Count);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Embedding snapshot unreadable; rebuilding from database");
                }
            }

            loaded ??= await BuildFromDatabaseAsync(dbFactory, modelVersion, ct);
            logger.LogInformation("Embedding index ready: {Count} vectors", loaded.Count);
            _data = loaded;
            return loaded;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    /// <summary>
    /// Builds the packed representation straight from the database. Prefers
    /// stored int8 columns; legacy float-only rows are quantized on the fly
    /// (they disappear once the embed run's quantize-missing pass lands).
    /// Also used by the snapshot writer.
    /// </summary>
    internal static async Task<PackedVectors> BuildFromDatabaseAsync(
        IDbContextFactory<AppDbContext> dbFactory, string modelVersion, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var quantized = await db.PaperEmbeddings
            .AsNoTracking()
            .Where(e => e.ModelVersion == modelVersion && e.VectorInt8 != null)
            .OrderBy(e => e.PaperId)
            .Select(e => new
            {
                e.PaperId,
                Bytes = e.VectorInt8!,
                Scale = e.Int8Scale!.Value,
                e.Paper.PublishedUtc,
            })
            .ToListAsync(ct);

        var legacy = await db.PaperEmbeddings
            .AsNoTracking()
            .Where(e => e.ModelVersion == modelVersion && e.VectorInt8 == null)
            .OrderBy(e => e.PaperId)
            .Select(e => new { e.PaperId, e.Vector, e.Paper.PublishedUtc })
            .ToListAsync(ct);

        var entries = new List<(long PaperId, byte[] Bytes, float Scale, DateTimeOffset Published)>(
            quantized.Count + legacy.Count);
        foreach (var row in quantized)
        {
            entries.Add((row.PaperId, row.Bytes, row.Scale, row.PublishedUtc));
        }

        foreach (var row in legacy)
        {
            var (q, scale) = Int8Quantization.Quantize(PaperEmbeddingService.FromBytes(row.Vector));
            entries.Add((row.PaperId, Convert(q), scale, row.PublishedUtc));
        }

        entries.Sort((a, b) => a.PaperId.CompareTo(b.PaperId));

        var count = entries.Count;
        var dims = count > 0 ? entries[0].Bytes.Length : 0;
        var paperIds = new long[count];
        var epochDays = new int[count];
        var scales = new float[count];
        var vectors = new sbyte[count * dims];

        for (var i = 0; i < count; i++)
        {
            var (paperId, bytes, scale, published) = entries[i];
            paperIds[i] = paperId;
            epochDays[i] = ToEpochDay(published)!.Value;
            scales[i] = scale;
            Int8Quantization.AsSbytes(bytes).CopyTo(vectors.AsSpan(i * dims, dims));
        }

        return new PackedVectors
        {
            PaperIds = paperIds,
            EpochDays = epochDays,
            Scales = scales,
            Vectors = vectors,
            Dims = dims,
        };
    }

    private static byte[] Convert(sbyte[] q)
    {
        var bytes = new byte[q.Length];
        Buffer.BlockCopy(q, 0, bytes, 0, q.Length);
        return bytes;
    }

    internal static int? ToEpochDay(DateTimeOffset? moment) =>
        moment is { } m ? (int)(m.UtcDateTime - DateTime.UnixEpoch).TotalDays : null;

    /// <summary>Struct-of-arrays vector store, ordered by ascending paper id.</summary>
    internal sealed class PackedVectors
    {
        private const uint Magic = 0x52444549; // "RDEI"
        private const int FormatVersion = 1;

        public required long[] PaperIds { get; init; }

        public required int[] EpochDays { get; init; }

        public required float[] Scales { get; init; }

        public required sbyte[] Vectors { get; init; }

        public required int Dims { get; init; }

        public int Count => PaperIds.Length;

        public byte[] Serialize()
        {
            var count = Count;
            var size = 16 + count * (8 + 4 + 4) + count * Dims;
            var buffer = new byte[size];
            var span = buffer.AsSpan();

            BinaryPrimitives.WriteUInt32LittleEndian(span, Magic);
            BinaryPrimitives.WriteInt32LittleEndian(span[4..], FormatVersion);
            BinaryPrimitives.WriteInt32LittleEndian(span[8..], count);
            BinaryPrimitives.WriteInt32LittleEndian(span[12..], Dims);
            var offset = 16;

            for (var i = 0; i < count; i++, offset += 8)
            {
                BinaryPrimitives.WriteInt64LittleEndian(span[offset..], PaperIds[i]);
            }

            for (var i = 0; i < count; i++, offset += 4)
            {
                BinaryPrimitives.WriteInt32LittleEndian(span[offset..], EpochDays[i]);
            }

            for (var i = 0; i < count; i++, offset += 4)
            {
                BinaryPrimitives.WriteSingleLittleEndian(span[offset..], Scales[i]);
            }

            Buffer.BlockCopy(Vectors, 0, buffer, offset, count * Dims);
            return buffer;
        }

        public static PackedVectors Deserialize(byte[] buffer)
        {
            var span = buffer.AsSpan();
            if (BinaryPrimitives.ReadUInt32LittleEndian(span) != Magic ||
                BinaryPrimitives.ReadInt32LittleEndian(span[4..]) != FormatVersion)
            {
                throw new FormatException("Unrecognized embedding snapshot format.");
            }

            var count = BinaryPrimitives.ReadInt32LittleEndian(span[8..]);
            var dims = BinaryPrimitives.ReadInt32LittleEndian(span[12..]);
            var expected = 16L + count * (8L + 4 + 4) + (long)count * dims;
            if (count < 0 || dims < 0 || buffer.Length != expected)
            {
                throw new FormatException("Corrupt embedding snapshot.");
            }

            var paperIds = new long[count];
            var epochDays = new int[count];
            var scales = new float[count];
            var vectors = new sbyte[count * dims];
            var offset = 16;

            for (var i = 0; i < count; i++, offset += 8)
            {
                paperIds[i] = BinaryPrimitives.ReadInt64LittleEndian(span[offset..]);
            }

            for (var i = 0; i < count; i++, offset += 4)
            {
                epochDays[i] = BinaryPrimitives.ReadInt32LittleEndian(span[offset..]);
            }

            for (var i = 0; i < count; i++, offset += 4)
            {
                scales[i] = BinaryPrimitives.ReadSingleLittleEndian(span[offset..]);
            }

            Buffer.BlockCopy(buffer, offset, vectors, 0, count * dims);
            return new PackedVectors
            {
                PaperIds = paperIds,
                EpochDays = epochDays,
                Scales = scales,
                Vectors = vectors,
                Dims = dims,
            };
        }
    }
}
