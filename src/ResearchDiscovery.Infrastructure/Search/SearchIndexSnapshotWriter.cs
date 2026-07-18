using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ResearchDiscovery.Application.Abstractions;
using ResearchDiscovery.Application.Options;
using ResearchDiscovery.Infrastructure.Embeddings;
using ResearchDiscovery.Infrastructure.Persistence;

namespace ResearchDiscovery.Infrastructure.Search;

/// <summary>
/// Builds both packed indexes from the database and uploads them. Runs on the
/// machines that already have the data hot — the embed CLI after a backfill
/// and the daily ingest job after new papers land — never on the API replica.
/// </summary>
public sealed class SearchIndexSnapshotWriter(
    IDbContextFactory<AppDbContext> dbFactory,
    SearchIndexSnapshotStore store,
    IOptions<EmbeddingOptions> embeddingOptions,
    ILogger<SearchIndexSnapshotWriter> logger) : ISearchIndexSnapshots
{
    public async Task WriteAsync(CancellationToken ct)
    {
        if (!store.Enabled)
        {
            logger.LogInformation("Search-index snapshots not configured; skipping write");
            return;
        }

        var modelVersion = embeddingOptions.Value.ModelVersion;

        var vectors = await InMemoryEmbeddingIndex.BuildFromDatabaseAsync(dbFactory, modelVersion, ct);
        var vectorCount = vectors.Count;
        await UploadViaTempFileAsync(
            InMemoryEmbeddingIndex.SnapshotBlobName(modelVersion), vectors.Serialize, ct);
        vectors = null!; // release before building the (bigger) lexical index

        var postings = await InMemoryLexicalIndex.BuildFromDatabaseAsync(dbFactory, ct);
        await UploadViaTempFileAsync(InMemoryLexicalIndex.SnapshotBlobName, postings.Serialize, ct);

        logger.LogInformation(
            "Search-index snapshots written: {Vectors} vectors, {Docs} documents / {Terms} terms",
            vectorCount, postings.DocIds.Length, postings.Terms.Length);
    }

    /// <summary>Serialize to a temp file, upload from it, delete — the index
    /// is never duplicated as an in-memory buffer.</summary>
    private async Task UploadViaTempFileAsync(
        string blobName, Action<Stream> serialize, CancellationToken ct)
    {
        var path = Path.Combine(Path.GetTempPath(), $"rdisc-snapshot-{Guid.NewGuid():N}.bin");
        try
        {
            await using (var file = File.Create(path))
            {
                serialize(file);
            }

            await store.UploadFromFileAsync(blobName, path, ct);
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // best effort — temp cleanup only
            }
        }
    }
}
