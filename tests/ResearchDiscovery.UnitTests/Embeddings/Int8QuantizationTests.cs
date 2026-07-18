using ResearchDiscovery.Infrastructure.Embeddings;
using ResearchDiscovery.Infrastructure.Search;
using Xunit;

namespace ResearchDiscovery.UnitTests.Embeddings;

public class Int8QuantizationTests
{
    [Fact]
    public void Quantize_RoundTrips_WithinScaleError()
    {
        var vector = MakeUnitVector(384, seed: 7);
        var (q, scale) = Int8Quantization.Quantize(vector);

        for (var i = 0; i < vector.Length; i++)
        {
            Assert.True(Math.Abs(q[i] * scale - vector[i]) <= scale / 2 + 1e-6f);
        }
    }

    [Fact]
    public void Quantize_ZeroVector_YieldsZeroScale()
    {
        var (q, scale) = Int8Quantization.Quantize(new float[384]);
        Assert.Equal(0f, scale);
        Assert.All(q, v => Assert.Equal(0, v));
    }

    [Theory]
    [InlineData(384)] // the real dimension: exercises the SIMD fast path
    [InlineData(37)] // odd length: exercises the scalar tail
    [InlineData(3)] // below any vector width: pure scalar
    public void Dot_MatchesScalarReference(int dims)
    {
        var a = MakeQuantized(dims, seed: 1);
        var b = MakeQuantized(dims, seed: 2);

        long expected = 0;
        for (var i = 0; i < dims; i++)
        {
            expected += a[i] * b[i];
        }

        Assert.Equal(expected, Int8Quantization.Dot(a, b));
    }

    [Fact]
    public void QuantizedCosine_TracksFloatCosine()
    {
        // Ranking only needs relative order to survive quantization; verify
        // absolute error stays well under the gaps ranking cares about.
        var query = MakeUnitVector(384, seed: 11);
        for (var seed = 20; seed < 30; seed++)
        {
            var doc = MakeUnitVector(384, seed);
            var exact = 0f;
            for (var i = 0; i < doc.Length; i++)
            {
                exact += query[i] * doc[i];
            }

            var (qq, qs) = Int8Quantization.Quantize(query);
            var (dq, ds) = Int8Quantization.Quantize(doc);
            var approx = Int8Quantization.Dot(qq, dq) * qs * ds;

            Assert.True(Math.Abs(exact - approx) < 0.01f,
                $"seed {seed}: exact {exact} vs quantized {approx}");
        }
    }

    [Fact]
    public void PackedVectors_SerializeRoundTrips()
    {
        var original = new InMemoryEmbeddingIndex.PackedVectors
        {
            PaperIds = [3, 8, 21],
            EpochDays = [19000, 20000, 20500],
            Scales = [0.005f, 0.006f, 0.007f],
            Vectors = [.. Enumerable.Range(-100, 12).Select(i => (sbyte)i)],
            Dims = 4,
        };

        using var stream = new MemoryStream();
        original.Serialize(stream);
        stream.Position = 0;
        var restored = InMemoryEmbeddingIndex.PackedVectors.Deserialize(stream);

        Assert.Equal(original.PaperIds, restored.PaperIds);
        Assert.Equal(original.EpochDays, restored.EpochDays);
        Assert.Equal(original.Scales, restored.Scales);
        Assert.Equal(original.Vectors, restored.Vectors);
        Assert.Equal(original.Dims, restored.Dims);
    }

    [Fact]
    public void PackedVectors_Deserialize_RejectsGarbage()
    {
        using var garbage = new MemoryStream(new byte[64]);
        Assert.Throws<FormatException>(
            () => InMemoryEmbeddingIndex.PackedVectors.Deserialize(garbage));
    }

    [Fact]
    public void PackedPostings_SerializeRoundTrips()
    {
        var original = new InMemoryLexicalIndex.PackedPostings
        {
            DocIds = [5, 9],
            DocEpochDays = [19500, 20100],
            DocLengths = [42, 17],
            AverageDocLength = 29.5,
            Terms = ["alpha", "beta", "gamma"],
            TermPostingStarts = [0, 2, 3, 4],
            PostingDocIndexes = [0, 1, 0, 1],
            PostingTfs = [2, 1, 3, 1],
        };

        using var stream = new MemoryStream();
        original.Serialize(stream);
        stream.Position = 0;
        var restored = InMemoryLexicalIndex.PackedPostings.Deserialize(stream);

        Assert.Equal(original.DocIds, restored.DocIds);
        Assert.Equal(original.DocEpochDays, restored.DocEpochDays);
        Assert.Equal(original.DocLengths, restored.DocLengths);
        Assert.Equal(original.AverageDocLength, restored.AverageDocLength);
        Assert.Equal(original.Terms, restored.Terms);
        Assert.Equal(original.TermPostingStarts, restored.TermPostingStarts);
        Assert.Equal(original.PostingDocIndexes, restored.PostingDocIndexes);
        Assert.Equal(original.PostingTfs, restored.PostingTfs);
    }

    private static float[] MakeUnitVector(int dims, int seed)
    {
        var random = new Random(seed);
        var v = new float[dims];
        var normSq = 0f;
        for (var i = 0; i < dims; i++)
        {
            v[i] = (float)(random.NextDouble() * 2 - 1);
            normSq += v[i] * v[i];
        }

        var norm = MathF.Sqrt(normSq);
        for (var i = 0; i < dims; i++)
        {
            v[i] /= norm;
        }

        return v;
    }

    private static sbyte[] MakeQuantized(int dims, int seed)
    {
        var random = new Random(seed);
        var v = new sbyte[dims];
        for (var i = 0; i < dims; i++)
        {
            v[i] = (sbyte)random.Next(-127, 128);
        }

        return v;
    }
}
