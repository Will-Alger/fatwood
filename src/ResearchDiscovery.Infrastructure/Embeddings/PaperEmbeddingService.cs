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
                    if (existing.TryGetValue(papers[i].Id, out var row))
                    {
                        row.ModelVersion = modelVersion;
                        row.Vector = bytes;
                        row.CreatedUtc = DateTimeOffset.UtcNow;
                    }
                    else
                    {
                        db.PaperEmbeddings.Add(new PaperEmbedding
                        {
                            PaperId = papers[i].Id,
                            ModelVersion = modelVersion,
                            Vector = bytes,
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

        if (embedded > 0)
        {
            // The corpus changed (embed runs follow every ingest), so both
            // in-memory indexes reload on next use.
            index.Invalidate();
            lexicalIndex.Invalidate();
        }

        logger.LogInformation("Embedding run finished: embedded {Embedded}, failed {Failed}",
            embedded, failed);
        return new EmbedRunSummary(embedded, 0, failed);
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
