using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ResearchDiscovery.Application.Abstractions;
using ResearchDiscovery.Infrastructure.Persistence;

namespace ResearchDiscovery.Infrastructure.Search;

/// <summary>
/// Classic BM25 (k1=1.2, b=0.75) over lowercased alphanumeric tokens of
/// title + abstract, held as packed postings arrays: at 300k documents the
/// old dictionary-of-lists layout costs ~600 MB and a minutes-long cold
/// rebuild, while the packed form is ~250 MB and loads from a blob snapshot
/// in seconds (database build remains the fallback). Terms are sorted for
/// binary-search lookup; per-document publication days support date-gated
/// search inside the scoring loop.
/// </summary>
public class InMemoryLexicalIndex(
    IDbContextFactory<AppDbContext> dbFactory,
    SearchIndexSnapshotStore snapshots,
    ILogger<InMemoryLexicalIndex> logger) : ILexicalIndex
{
    private const float K1 = 1.2f;
    private const float B = 0.75f;
    private const int DbBuildBatchSize = 5000;

    public const string SnapshotBlobName = "lexical-v1.bin";

    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private volatile PackedPostings? _index;

    public async Task<IReadOnlyList<ScoredPaper>> TopAsync(
        string query, int n, IReadOnlySet<long>? restrictTo, CancellationToken ct,
        DateTimeOffset? publishedAfter = null)
    {
        var index = await GetIndexAsync(ct);
        var terms = Tokenize(query).Distinct().ToList();
        var docCount = index.DocIds.Length;
        if (terms.Count == 0 || docCount == 0)
        {
            return [];
        }

        var cutoff = Embeddings.InMemoryEmbeddingIndex.ToEpochDay(publishedAfter);
        var scores = new Dictionary<int, float>();
        foreach (var term in terms)
        {
            var termIdx = Array.BinarySearch(index.Terms, term, StringComparer.Ordinal);
            if (termIdx < 0)
            {
                continue;
            }

            var start = index.TermPostingStarts[termIdx];
            var end = index.TermPostingStarts[termIdx + 1];
            var df = end - start;
            var idf = MathF.Log(1 + (docCount - df + 0.5f) / (df + 0.5f));

            for (var p = start; p < end; p++)
            {
                var docIdx = index.PostingDocIndexes[p];
                if (cutoff is { } day && index.DocEpochDays[docIdx] < day)
                {
                    continue;
                }

                if (restrictTo is not null && !restrictTo.Contains(index.DocIds[docIdx]))
                {
                    continue;
                }

                float tf = index.PostingTfs[p];
                var docLen = index.DocLengths[docIdx];
                var norm = tf * (K1 + 1) /
                    (tf + K1 * (1 - B + B * (float)(docLen / index.AverageDocLength)));
                scores[docIdx] = scores.GetValueOrDefault(docIdx) + idf * norm;
            }
        }

        return scores
            .OrderByDescending(kv => kv.Value)
            .Take(n)
            .Select(kv => new ScoredPaper(index.DocIds[kv.Key], kv.Value))
            .ToList();
    }

    public void Invalidate() => _index = null;

    private async Task<PackedPostings> GetIndexAsync(CancellationToken ct)
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

            PackedPostings? loaded = null;
            var blob = await snapshots.TryOpenReadAsync(SnapshotBlobName, ct);
            if (blob is not null)
            {
                try
                {
                    await using (blob)
                    {
                        loaded = PackedPostings.Deserialize(blob);
                    }

                    logger.LogInformation(
                        "Lexical index loaded from snapshot: {Docs} documents, {Terms} terms",
                        loaded.DocIds.Length, loaded.Terms.Length);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Lexical snapshot unreadable; rebuilding from database");
                }
            }

            loaded ??= await BuildFromDatabaseAsync(dbFactory, ct);
            logger.LogInformation(
                "Lexical index ready: {Docs} documents, {Terms} terms",
                loaded.DocIds.Length, loaded.Terms.Length);
            _index = loaded;
            return loaded;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    /// <summary>
    /// Two-pass packed build: pass 1 streams the corpus to count document
    /// frequencies (sizing every array exactly), pass 2 streams again to fill
    /// postings. Streaming batches keep peak memory near the final packed
    /// size instead of holding 400 MB of abstracts. Also used by the
    /// snapshot writer.
    /// </summary>
    internal static async Task<PackedPostings> BuildFromDatabaseAsync(
        IDbContextFactory<AppDbContext> dbFactory, CancellationToken ct)
    {
        // Pass 1: document frequencies + per-doc lengths/dates.
        var df = new Dictionary<string, int>(StringComparer.Ordinal);
        var docIds = new List<long>();
        var docEpochDays = new List<int>();
        var docLengths = new List<int>();
        long totalLength = 0;

        await foreach (var doc in StreamDocsAsync(dbFactory, ct))
        {
            var counts = CountTerms(doc.Title, doc.Abstract, out var length);
            docIds.Add(doc.Id);
            docEpochDays.Add(Embeddings.InMemoryEmbeddingIndex.ToEpochDay(doc.PublishedUtc)!.Value);
            docLengths.Add(length);
            totalLength += length;
            foreach (var term in counts.Keys)
            {
                df[term] = df.GetValueOrDefault(term) + 1;
            }
        }

        var terms = df.Keys.ToArray();
        Array.Sort(terms, StringComparer.Ordinal);

        var termIdxByTerm = new Dictionary<string, int>(terms.Length, StringComparer.Ordinal);
        for (var i = 0; i < terms.Length; i++)
        {
            termIdxByTerm[terms[i]] = i;
        }

        var starts = new int[terms.Length + 1];
        for (var i = 0; i < terms.Length; i++)
        {
            starts[i + 1] = starts[i] + df[terms[i]];
        }

        var postingCount = starts[^1];
        var postingDocIndexes = new int[postingCount];
        var postingTfs = new byte[postingCount];
        var fill = new int[terms.Length];
        Array.Copy(starts, fill, terms.Length);

        // Pass 2: fill postings. Documents stream in the same PaperId order,
        // so docIdx assignment matches pass 1.
        var docIdx = 0;
        await foreach (var doc in StreamDocsAsync(dbFactory, ct))
        {
            var counts = CountTerms(doc.Title, doc.Abstract, out _);
            foreach (var (term, tf) in counts)
            {
                var t = termIdxByTerm[term];
                postingDocIndexes[fill[t]] = docIdx;
                postingTfs[fill[t]] = (byte)Math.Min(tf, byte.MaxValue);
                fill[t]++;
            }

            docIdx++;
        }

        return new PackedPostings
        {
            DocIds = docIds.ToArray(),
            DocEpochDays = docEpochDays.ToArray(),
            DocLengths = docLengths.ToArray(),
            AverageDocLength = docIds.Count > 0 ? totalLength / (double)docIds.Count : 1,
            Terms = terms,
            TermPostingStarts = starts,
            PostingDocIndexes = postingDocIndexes,
            PostingTfs = postingTfs,
        };
    }

    private sealed record DocRow(long Id, string Title, string Abstract, DateTimeOffset PublishedUtc);

    private static async IAsyncEnumerable<DocRow> StreamDocsAsync(
        IDbContextFactory<AppDbContext> dbFactory,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var lastId = 0L;
        while (true)
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var batch = await db.Papers
                .AsNoTracking()
                .Where(p => p.Id > lastId)
                .OrderBy(p => p.Id)
                .Take(DbBuildBatchSize)
                .Select(p => new DocRow(p.Id, p.Title, p.Abstract, p.PublishedUtc))
                .ToListAsync(ct);

            if (batch.Count == 0)
            {
                yield break;
            }

            foreach (var row in batch)
            {
                yield return row;
            }

            lastId = batch[^1].Id;
        }
    }

    private static Dictionary<string, int> CountTerms(string title, string abstractText, out int length)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        length = 0;
        foreach (var token in Tokenize($"{title} {abstractText}"))
        {
            counts[token] = counts.GetValueOrDefault(token) + 1;
            length++;
        }

        return counts;
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

    /// <summary>Packed BM25 postings; docIdx-space arrays ordered by paper id.</summary>
    internal sealed class PackedPostings
    {
        private const uint Magic = 0x494C4452; // "RDLI"
        private const int FormatVersion = 1;

        public required long[] DocIds { get; init; }

        public required int[] DocEpochDays { get; init; }

        public required int[] DocLengths { get; init; }

        public required double AverageDocLength { get; init; }

        public required string[] Terms { get; init; }

        public required int[] TermPostingStarts { get; init; }

        public required int[] PostingDocIndexes { get; init; }

        public required byte[] PostingTfs { get; init; }

        // Header and strings go through Binary(Writer|Reader); the numeric
        // arrays are raw native-endian spans streamed straight to/from the
        // stream, so a large index is never doubled up as a byte[] in memory.
        public void Serialize(Stream stream)
        {
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

            writer.Write(Magic);
            writer.Write(FormatVersion);
            writer.Write(DocIds.Length);
            writer.Write(Terms.Length);
            writer.Write(PostingDocIndexes.Length);
            writer.Write(AverageDocLength);

            stream.Write(MemoryMarshal.AsBytes(DocIds.AsSpan()));
            stream.Write(MemoryMarshal.AsBytes(DocEpochDays.AsSpan()));
            stream.Write(MemoryMarshal.AsBytes(DocLengths.AsSpan()));

            foreach (var term in Terms)
            {
                writer.Write(term);
            }

            writer.Flush();
            stream.Write(MemoryMarshal.AsBytes(TermPostingStarts.AsSpan()));
            stream.Write(MemoryMarshal.AsBytes(PostingDocIndexes.AsSpan()));
            stream.Write(PostingTfs);
        }

        public static PackedPostings Deserialize(Stream stream)
        {
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

            if (reader.ReadUInt32() != Magic || reader.ReadInt32() != FormatVersion)
            {
                throw new FormatException("Unrecognized lexical snapshot format.");
            }

            var docCount = reader.ReadInt32();
            var termCount = reader.ReadInt32();
            var postingCount = reader.ReadInt32();
            var averageDocLength = reader.ReadDouble();
            if (docCount < 0 || termCount < 0 || postingCount < 0)
            {
                throw new FormatException("Corrupt lexical snapshot.");
            }

            var docIds = new long[docCount];
            var docEpochDays = new int[docCount];
            var docLengths = new int[docCount];
            stream.ReadExactly(MemoryMarshal.AsBytes(docIds.AsSpan()));
            stream.ReadExactly(MemoryMarshal.AsBytes(docEpochDays.AsSpan()));
            stream.ReadExactly(MemoryMarshal.AsBytes(docLengths.AsSpan()));

            var terms = new string[termCount];
            for (var i = 0; i < termCount; i++)
            {
                terms[i] = reader.ReadString();
            }

            var starts = new int[termCount + 1];
            var postingDocIndexes = new int[postingCount];
            var postingTfs = new byte[postingCount];
            stream.ReadExactly(MemoryMarshal.AsBytes(starts.AsSpan()));
            stream.ReadExactly(MemoryMarshal.AsBytes(postingDocIndexes.AsSpan()));
            stream.ReadExactly(postingTfs);

            return new PackedPostings
            {
                DocIds = docIds,
                DocEpochDays = docEpochDays,
                DocLengths = docLengths,
                AverageDocLength = averageDocLength,
                Terms = terms,
                TermPostingStarts = starts,
                PostingDocIndexes = postingDocIndexes,
                PostingTfs = postingTfs,
            };
        }
    }
}
