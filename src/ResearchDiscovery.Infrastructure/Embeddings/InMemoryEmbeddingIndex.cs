using System.Buffers.Binary;
using System.Runtime.InteropServices;
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
    IOptions<SearchOptions> searchOptions,
    SearchIndexSnapshotStore snapshots,
    ILogger<InMemoryEmbeddingIndex> logger) : IEmbeddingIndex
{
    /// <summary>Below this the partitioning overhead beats the win.</summary>
    private const int ParallelScanThreshold = 64_000;

    private static readonly ScoreKeyComparer KeyComparer = new();

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
        var configured = searchOptions.Value.MaxScanParallelism;
        var parallelism = Math.Clamp(
            configured <= 0 ? Environment.ProcessorCount : configured,
            1, Environment.ProcessorCount);
        if (data.Count < ParallelScanThreshold)
        {
            parallelism = 1;
        }

        return await TopMultiCoreAsync(
            data, primaryQ, primaryScale, topicQ, n, restrictTo, cutoff, parallelism,
            searchOptions.Value.CandidateScanDivisor);
    }

    /// <summary>
    /// Scan driver. Results are deterministic for any parallelism: per-paper
    /// scores are independent, and the canonical ScoreKey order (score desc,
    /// then LOWER paper id wins exact float ties) makes heap contents and
    /// final ordering a pure function of the data.
    /// </summary>
    internal static async Task<IReadOnlyList<ScoredPaper>> TopMultiCoreAsync(
        PackedVectors data,
        sbyte[] primaryQ,
        float primaryScale,
        (sbyte[] Q, float Scale)[] topicQ,
        int n,
        IReadOnlySet<long>? restrictTo,
        int? cutoff,
        int parallelism,
        int candidateScanDivisor = 8)
    {
        PriorityQueue<long, ScoreKey> final;
        if (restrictTo is not null
            && restrictTo.Count < data.Count / Math.Max(2, candidateScanDivisor))
        {
            // Narrow candidate set: O(|candidates| · (log N + dims)) binary
            // searches beat sweeping the whole corpus. Same kernel math and
            // heap semantics; the canonical comparer makes the hash-set
            // iteration order irrelevant to the result.
            final = new PriorityQueue<long, ScoreKey>(n + 1, KeyComparer);
            ScanCandidates(data, primaryQ, primaryScale, topicQ, cutoff, restrictTo, n, final);
        }
        else if (parallelism <= 1)
        {
            final = new PriorityQueue<long, ScoreKey>(n + 1, KeyComparer);
            ScanRange(data, 0, data.Count, primaryQ, primaryScale, topicQ, cutoff, restrictTo, n, final);
        }
        else
        {
            // Contiguous partitions with per-partition bounded heaps; the
            // request thread takes the last partition, then a sequential
            // merge pass reduces ≤ parallelism·n survivors to the top n.
            var heaps = new PriorityQueue<long, ScoreKey>[parallelism];
            for (var p = 0; p < parallelism; p++)
            {
                heaps[p] = new PriorityQueue<long, ScoreKey>(n + 1, KeyComparer);
            }

            var chunk = data.Count / parallelism;
            var tasks = new Task[parallelism - 1];
            for (var p = 0; p < parallelism - 1; p++)
            {
                var start = p * chunk;
                var end = start + chunk;
                var heap = heaps[p];
                tasks[p] = Task.Run(() => ScanRange(
                    data, start, end, primaryQ, primaryScale, topicQ, cutoff, restrictTo, n, heap));
            }

            ScanRange(
                data, (parallelism - 1) * chunk, data.Count,
                primaryQ, primaryScale, topicQ, cutoff, restrictTo, n, heaps[^1]);
            await Task.WhenAll(tasks).ConfigureAwait(false);

            final = new PriorityQueue<long, ScoreKey>(n + 1, KeyComparer);
            foreach (var heap in heaps)
            {
                while (heap.TryDequeue(out var paperId, out var key))
                {
                    Push(final, paperId, key, n);
                }
            }
        }

        var result = new List<ScoredPaper>(final.Count);
        while (final.TryDequeue(out var paperId, out var key))
        {
            result.Add(new ScoredPaper(paperId, key.Score));
        }

        result.Reverse(); // heap drains worst-first
        return result;
    }

    /// <summary>
    /// Candidate-iteration kernel: scores exactly the restrictTo ids that
    /// exist in the index (and pass the date gate) via binary search, instead
    /// of sweeping every packed entry.
    /// </summary>
    private static void ScanCandidates(
        PackedVectors data,
        sbyte[] primaryQ,
        float primaryScale,
        (sbyte[] Q, float Scale)[] topicQ,
        int? cutoffDay,
        IReadOnlySet<long> restrictTo,
        int n,
        PriorityQueue<long, ScoreKey> heap)
    {
        var dims = data.Dims;
        foreach (var id in restrictTo)
        {
            var i = Array.BinarySearch(data.PaperIds, id);
            if (i < 0)
            {
                continue;
            }

            if (cutoffDay is { } day && data.EpochDays[i] < day)
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

            Push(heap, data.PaperIds[i], new ScoreKey(score, data.PaperIds[i]), n);
        }
    }

    /// <summary>The sequential scoring kernel over [start, end).</summary>
    private static void ScanRange(
        PackedVectors data,
        int start,
        int end,
        sbyte[] primaryQ,
        float primaryScale,
        (sbyte[] Q, float Scale)[] topicQ,
        int? cutoffDay,
        IReadOnlySet<long>? restrictTo,
        int n,
        PriorityQueue<long, ScoreKey> heap)
    {
        var dims = data.Dims;
        for (var i = start; i < end; i++)
        {
            if (cutoffDay is { } day && data.EpochDays[i] < day)
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

            Push(heap, data.PaperIds[i], new ScoreKey(score, data.PaperIds[i]), n);
        }
    }

    private static void Push(
        PriorityQueue<long, ScoreKey> heap, long paperId, ScoreKey key, int n)
    {
        if (heap.Count < n)
        {
            heap.Enqueue(paperId, key);
        }
        else if (heap.TryPeek(out _, out var min) && KeyComparer.Compare(key, min) > 0)
        {
            heap.DequeueEnqueue(paperId, key);
        }
    }

    /// <summary>
    /// Canonical result order: higher score is better; exact float ties go to
    /// the lower paper id. Today's sequential heap left tie order unspecified
    /// — pinning it is what makes the parallel scan reproducible.
    /// </summary>
    internal readonly record struct ScoreKey(float Score, long PaperId);

    internal sealed class ScoreKeyComparer : IComparer<ScoreKey>
    {
        // Min-heap order: "smallest" = lowest score, and on ties the HIGHER
        // paper id, so it is evicted first and the lower id survives.
        public int Compare(ScoreKey x, ScoreKey y)
        {
            var byScore = x.Score.CompareTo(y.Score);
            return byScore != 0 ? byScore : y.PaperId.CompareTo(x.PaperId);
        }
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

            var blob = await snapshots.TryOpenReadAsync(SnapshotBlobName(modelVersion), ct);
            if (blob is not null)
            {
                try
                {
                    await using (blob)
                    {
                        loaded = PackedVectors.Deserialize(blob);
                    }

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

        // Numeric arrays are written as raw native-endian spans (all our
        // targets are little-endian); only the header uses explicit LE. This
        // streams straight to/from disk so a large index is never doubled up
        // as a byte[] in memory.
        public void Serialize(Stream stream)
        {
            Span<byte> header = stackalloc byte[16];
            BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
            BinaryPrimitives.WriteInt32LittleEndian(header[4..], FormatVersion);
            BinaryPrimitives.WriteInt32LittleEndian(header[8..], Count);
            BinaryPrimitives.WriteInt32LittleEndian(header[12..], Dims);
            stream.Write(header);

            stream.Write(MemoryMarshal.AsBytes(PaperIds.AsSpan()));
            stream.Write(MemoryMarshal.AsBytes(EpochDays.AsSpan()));
            stream.Write(MemoryMarshal.AsBytes(Scales.AsSpan()));
            stream.Write(MemoryMarshal.AsBytes(Vectors.AsSpan()));
        }

        public static PackedVectors Deserialize(Stream stream)
        {
            Span<byte> header = stackalloc byte[16];
            stream.ReadExactly(header);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic ||
                BinaryPrimitives.ReadInt32LittleEndian(header[4..]) != FormatVersion)
            {
                throw new FormatException("Unrecognized embedding snapshot format.");
            }

            var count = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
            var dims = BinaryPrimitives.ReadInt32LittleEndian(header[12..]);
            if (count < 0 || dims < 0 || (dims > 0 && count > int.MaxValue / dims))
            {
                throw new FormatException("Corrupt embedding snapshot.");
            }

            var paperIds = new long[count];
            var epochDays = new int[count];
            var scales = new float[count];
            var vectors = new sbyte[count * dims];

            stream.ReadExactly(MemoryMarshal.AsBytes(paperIds.AsSpan()));
            stream.ReadExactly(MemoryMarshal.AsBytes(epochDays.AsSpan()));
            stream.ReadExactly(MemoryMarshal.AsBytes(scales.AsSpan()));
            stream.ReadExactly(MemoryMarshal.AsBytes(vectors.AsSpan()));

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
