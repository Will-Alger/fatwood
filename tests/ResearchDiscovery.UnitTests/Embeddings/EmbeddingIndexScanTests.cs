using ResearchDiscovery.Infrastructure.Embeddings;
using Xunit;

namespace ResearchDiscovery.UnitTests.Embeddings;

/// <summary>
/// Pins the scan driver's determinism contract: for the same packed data and
/// query, sequential and parallel scans return element-identical results
/// (ids AND scores), including across exact score ties, date cutoffs, and
/// candidate restrictions. Seeded data deliberately contains duplicated
/// vectors so equal-score tie-breaking is actually exercised.
/// </summary>
public class EmbeddingIndexScanTests
{
    private const int Dims = 32;
    private const int Count = 5000;
    private const int BaseEpochDay = 19000;

    [Fact]
    public async Task ParallelScan_MatchesSequential_NoFilters() =>
        await AssertParallelMatchesSequential(cutoff: null, restrict: false, n: 150);

    [Fact]
    public async Task ParallelScan_MatchesSequential_WithDateCutoff() =>
        await AssertParallelMatchesSequential(
            cutoff: BaseEpochDay + 1800, restrict: false, n: 150);

    [Fact]
    public async Task ParallelScan_MatchesSequential_WithRestrictTo() =>
        await AssertParallelMatchesSequential(cutoff: null, restrict: true, n: 150);

    [Fact]
    public async Task ParallelScan_MatchesSequential_WithCutoffAndRestrictTo() =>
        await AssertParallelMatchesSequential(
            cutoff: BaseEpochDay + 1800, restrict: true, n: 150);

    [Fact]
    public async Task ParallelScan_MatchesSequential_WhenNExceedsMatches() =>
        await AssertParallelMatchesSequential(cutoff: null, restrict: false, n: Count * 2);

    [Fact]
    public async Task ParallelScan_MatchesSequential_TopOne() =>
        await AssertParallelMatchesSequential(cutoff: null, restrict: false, n: 1);

    [Fact]
    public async Task ParallelScan_MatchesSequential_WithoutTopics() =>
        await AssertParallelMatchesSequential(
            cutoff: null, restrict: false, n: 150, topicCount: 0);

    [Fact]
    public async Task EqualScores_OrderByAscendingPaperId()
    {
        var data = MakeData();
        var (q, scale) = MakeQuery(seed: 1);

        var result = await InMemoryEmbeddingIndex.TopMultiCoreAsync(
            data, q, scale, [], n: Count, restrictTo: null, cutoff: null, parallelism: 4);

        for (var i = 1; i < result.Count; i++)
        {
            Assert.True(
                result[i - 1].Score > result[i].Score
                || (result[i - 1].Score == result[i].Score
                    && result[i - 1].PaperId < result[i].PaperId),
                $"rank {i}: ties must order by ascending paper id");
        }

        // The seeded duplicates guarantee ties exist, or this test pins nothing.
        Assert.Contains(
            Enumerable.Range(1, result.Count - 1),
            i => result[i].Score == result[i - 1].Score);
    }

    [Fact]
    public async Task CandidateIteration_MatchesSweep()
    {
        var data = MakeData();
        var (primaryQ, primaryScale) = MakeQuery(seed: 1);
        var topics = Enumerable.Range(2, 11).Select(MakeQuery).ToArray();

        // Small set (~100 ids incl. absent ones and a tie clone) so the
        // default divisor routes it through candidate iteration.
        var restrictTo = new HashSet<long> { -1, long.MaxValue };
        for (var i = 0; i < data.Count; i += 50)
        {
            restrictTo.Add(data.PaperIds[i]);
            if (i > 0)
            {
                restrictTo.Add(data.PaperIds[i - 1]); // tie partner of the clone
            }
        }

        foreach (var cutoff in new int?[] { null, BaseEpochDay + 1800 })
        {
            var iterated = await InMemoryEmbeddingIndex.TopMultiCoreAsync(
                data, primaryQ, primaryScale, topics, n: 150, restrictTo, cutoff,
                parallelism: 1, candidateScanDivisor: 8);

            // A huge divisor makes the iteration threshold unreachable,
            // forcing the sweep path over the same candidate set.
            var swept = await InMemoryEmbeddingIndex.TopMultiCoreAsync(
                data, primaryQ, primaryScale, topics, n: 150, restrictTo, cutoff,
                parallelism: 1, candidateScanDivisor: 1024);
            var sweptParallel = await InMemoryEmbeddingIndex.TopMultiCoreAsync(
                data, primaryQ, primaryScale, topics, n: 150, restrictTo, cutoff,
                parallelism: 4, candidateScanDivisor: 1024);

            Assert.NotEmpty(iterated);
            Assert.Equal(swept.Count, iterated.Count);
            for (var i = 0; i < swept.Count; i++)
            {
                Assert.Equal(swept[i].PaperId, iterated[i].PaperId);
                Assert.Equal(swept[i].Score, iterated[i].Score);
                Assert.Equal(swept[i].PaperId, sweptParallel[i].PaperId);
                Assert.Equal(swept[i].Score, sweptParallel[i].Score);
            }
        }
    }

    private static async Task AssertParallelMatchesSequential(
        int? cutoff, bool restrict, int n, int topicCount = 11)
    {
        var data = MakeData();
        var (primaryQ, primaryScale) = MakeQuery(seed: 1);
        var topics = Enumerable.Range(2, topicCount).Select(MakeQuery).ToArray();
        var restrictTo = restrict ? MakeRestrictSet(data) : null;

        var sequential = await InMemoryEmbeddingIndex.TopMultiCoreAsync(
            data, primaryQ, primaryScale, topics, n, restrictTo, cutoff, parallelism: 1);
        var parallel = await InMemoryEmbeddingIndex.TopMultiCoreAsync(
            data, primaryQ, primaryScale, topics, n, restrictTo, cutoff, parallelism: 4);

        Assert.NotEmpty(sequential);
        Assert.Equal(sequential.Count, parallel.Count);
        for (var i = 0; i < sequential.Count; i++)
        {
            Assert.Equal(sequential[i].PaperId, parallel[i].PaperId);
            Assert.Equal(sequential[i].Score, parallel[i].Score);
        }
    }

    /// <summary>Every 7th paper plus ids absent from the index entirely.</summary>
    private static HashSet<long> MakeRestrictSet(InMemoryEmbeddingIndex.PackedVectors data)
    {
        var set = new HashSet<long>();
        for (var i = 0; i < data.Count; i += 7)
        {
            set.Add(data.PaperIds[i]);
        }

        set.Add(-1);
        set.Add(long.MaxValue);
        return set;
    }

    private static InMemoryEmbeddingIndex.PackedVectors MakeData(int seed = 42)
    {
        var rng = new Random(seed);
        var paperIds = new long[Count];
        var epochDays = new int[Count];
        var scales = new float[Count];
        var vectors = new sbyte[Count * Dims];

        var id = 100L;
        for (var i = 0; i < Count; i++)
        {
            id += rng.Next(1, 4);
            paperIds[i] = id;
            epochDays[i] = BaseEpochDay + rng.Next(0, 3650);
            scales[i] = 0.001f + (float)rng.NextDouble() * 0.02f;
            for (var d = 0; d < Dims; d++)
            {
                vectors[(i * Dims) + d] = (sbyte)rng.Next(-127, 128);
            }
        }

        // Exact ties: every 50th entry clones its predecessor's vector, scale,
        // and epoch day — identical score under any query, different id.
        for (var i = 50; i < Count; i += 50)
        {
            Array.Copy(vectors, (i - 1) * Dims, vectors, i * Dims, Dims);
            scales[i] = scales[i - 1];
            epochDays[i] = epochDays[i - 1];
        }

        return new InMemoryEmbeddingIndex.PackedVectors
        {
            PaperIds = paperIds,
            EpochDays = epochDays,
            Scales = scales,
            Vectors = vectors,
            Dims = Dims,
        };
    }

    private static (sbyte[] Q, float Scale) MakeQuery(int seed)
    {
        var rng = new Random(seed);
        var vector = new float[Dims];
        for (var d = 0; d < Dims; d++)
        {
            vector[d] = (float)((rng.NextDouble() * 2) - 1);
        }

        return Int8Quantization.Quantize(vector);
    }
}
