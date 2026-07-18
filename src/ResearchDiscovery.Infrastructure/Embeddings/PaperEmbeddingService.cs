using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ResearchDiscovery.Application.Abstractions;
using ResearchDiscovery.Application.Options;
using ResearchDiscovery.Domain.Entities;
using ResearchDiscovery.Infrastructure.Persistence;

namespace ResearchDiscovery.Infrastructure.Embeddings;

/// <summary>
/// Embeds every paper that lacks a current-model vector. Runs after ingestion
/// (new papers) and via the `embed` CLI (backfill). Persists in batches so an
/// interrupted run keeps its progress.
/// </summary>
public class PaperEmbeddingService(
    IDbContextFactory<AppDbContext> dbFactory,
    ITextEmbedder embedder,
    IEmbeddingIndex index,
    ILexicalIndex lexicalIndex,
    ISearchIndexSnapshots snapshots,
    IOptions<EmbeddingOptions> options,
    ILogger<PaperEmbeddingService> logger) : IPaperEmbeddingService
{
    private const int AbstractCharLimit = 4000;

    public async Task<EmbedRunSummary> EmbedMissingAsync(CancellationToken ct)
    {
        if (!options.Value.Enabled)
        {
            logger.LogInformation("Embeddings are disabled (Embeddings:Enabled=false)");
            return new EmbedRunSummary(0, 0, 0);
        }

        var modelVersion = options.Value.ModelVersion;
        var batchSize = options.Value.BatchSize;
        var embedded = 0;
        var failed = 0;

        while (!ct.IsCancellationRequested)
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var papers = await db.Papers
                .Where(p => p.Embedding == null || p.Embedding.ModelVersion != modelVersion)
                .OrderBy(p => p.Id)
                .Take(batchSize)
                .Select(p => new { p.Id, p.Title, p.Abstract })
                .ToListAsync(ct);

            if (papers.Count == 0)
            {
                break;
            }

            try
            {
                var texts = papers
                    .Select(p => $"{p.Title}. {Truncate(p.Abstract)}")
                    .ToList();
                var vectors = await embedder.EmbedBatchAsync(texts, ct);

                var ids = papers.Select(p => p.Id).ToList();
                var existing = await db.PaperEmbeddings
                    .Where(e => ids.Contains(e.PaperId))
                    .ToDictionaryAsync(e => e.PaperId, ct);

                for (var i = 0; i < papers.Count; i++)
                {
                    var bytes = ToBytes(vectors[i]);
                    var (q, scale) = Int8Quantization.Quantize(vectors[i]);
                    var int8Bytes = ToInt8Bytes(q);
                    if (existing.TryGetValue(papers[i].Id, out var row))
                    {
                        row.ModelVersion = modelVersion;
                        row.Vector = bytes;
                        row.VectorInt8 = int8Bytes;
                        row.Int8Scale = scale;
                        row.CreatedUtc = DateTimeOffset.UtcNow;
                    }
                    else
                    {
                        db.PaperEmbeddings.Add(new PaperEmbedding
                        {
                            PaperId = papers[i].Id,
                            ModelVersion = modelVersion,
                            Vector = bytes,
                            VectorInt8 = int8Bytes,
                            Int8Scale = scale,
                            CreatedUtc = DateTimeOffset.UtcNow,
                        });
                    }
                }

                await db.SaveChangesAsync(ct);
                embedded += papers.Count;

                if (embedded % 512 < batchSize)
                {
                    logger.LogInformation("Embedded {Count} papers so far", embedded);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A poisoned batch must not wedge the loop forever: log and stop
                // rather than retrying the same batch endlessly.
                failed += papers.Count;
                logger.LogError(ex, "Embedding batch starting at paper {Id} failed; stopping run",
                    papers[0].Id);
                break;
            }
        }

        var quantized = await QuantizeMissingAsync(modelVersion, ct);

        if (embedded > 0 || quantized > 0)
        {
            // The corpus changed (embed runs follow every ingest), so both
            // in-memory indexes reload on next use — and cold replicas get a
            // fresh packed snapshot instead of a database rebuild.
            index.Invalidate();
            lexicalIndex.Invalidate();
            try
            {
                await snapshots.WriteAsync(ct);
            }
            catch (Exception ex)
            {
                // Snapshots are an optimization; indexes fall back to database
                // builds, so a failed write must not fail the embed run.
                logger.LogError(ex, "Search-index snapshot write failed");
            }
        }

        logger.LogInformation("Embedding run finished: embedded {Embedded}, failed {Failed}",
            embedded, failed);
        return new EmbedRunSummary(embedded, 0, failed);
    }

    /// <summary>
    /// Backfills the int8 columns on rows embedded before quantization
    /// existed — pure math over the stored float vector, no model calls.
    /// </summary>
    private async Task<int> QuantizeMissingAsync(string modelVersion, CancellationToken ct)
    {
        var quantized = 0;
        while (!ct.IsCancellationRequested)
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var rows = await db.PaperEmbeddings
                .Where(e => e.ModelVersion == modelVersion && e.VectorInt8 == null)
                .OrderBy(e => e.PaperId)
                .Take(1024)
                .ToListAsync(ct);

            if (rows.Count == 0)
            {
                break;
            }

            foreach (var row in rows)
            {
                var (q, scale) = Int8Quantization.Quantize(FromBytes(row.Vector));
                row.VectorInt8 = ToInt8Bytes(q);
                row.Int8Scale = scale;
            }

            await db.SaveChangesAsync(ct);
            quantized += rows.Count;
        }

        if (quantized > 0)
        {
            logger.LogInformation("Quantized {Count} legacy embedding rows", quantized);
        }

        return quantized;
    }

    internal static byte[] ToInt8Bytes(sbyte[] quantized)
    {
        var bytes = new byte[quantized.Length];
        Buffer.BlockCopy(quantized, 0, bytes, 0, quantized.Length);
        return bytes;
    }

    internal static byte[] ToBytes(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    internal static float[] FromBytes(byte[] bytes)
    {
        var vector = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, vector, 0, bytes.Length);
        return vector;
    }

    private static string Truncate(string text) =>
        text.Length <= AbstractCharLimit ? text : text[..AbstractCharLimit];
}
