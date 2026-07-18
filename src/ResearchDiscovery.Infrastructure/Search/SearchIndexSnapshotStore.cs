using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ResearchDiscovery.Application.Options;

namespace ResearchDiscovery.Infrastructure.Search;

/// <summary>
/// Blob storage for packed search-index snapshots. Auth mirrors the analysis
/// queue: connection string locally (Azurite), pinned managed identity in
/// cloud. Reads and writes are best-effort — a missing or unreadable snapshot
/// just means the caller falls back to building from the database.
/// </summary>
public sealed class SearchIndexSnapshotStore
{
    private readonly BlobContainerClient? _container;
    private readonly ILogger<SearchIndexSnapshotStore> _logger;
    private int _created;

    public SearchIndexSnapshotStore(
        IOptions<SearchIndexOptions> options, ILogger<SearchIndexSnapshotStore> logger)
    {
        _logger = logger;
        var o = options.Value;
        if (!o.Enabled)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(o.ConnectionString))
        {
            _container = new BlobContainerClient(o.ConnectionString, o.ContainerName);
            return;
        }

        var clientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID");
        TokenCredential credential = string.IsNullOrWhiteSpace(clientId)
            ? new DefaultAzureCredential()
            : new ManagedIdentityCredential(clientId);
        _container = new BlobContainerClient(
            new Uri($"{o.AccountUrl!.TrimEnd('/')}/{o.ContainerName}"), credential);
    }

    public bool Enabled => _container is not null;

    /// <summary>Downloads a snapshot into memory, or null when absent/unreadable.</summary>
    public async Task<byte[]?> TryDownloadAsync(string blobName, CancellationToken ct)
    {
        if (_container is null)
        {
            return null;
        }

        try
        {
            var blob = _container.GetBlobClient(blobName);
            var response = await blob.DownloadContentAsync(ct);
            return response.Value.Content.ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Snapshot {Blob} unavailable; falling back to a database build", blobName);
            return null;
        }
    }

    /// <summary>Uploads (replaces) a snapshot. Throws on failure — writers run
    /// in offline jobs where a loud failure is more useful than a silent skip.</summary>
    public async Task UploadAsync(string blobName, byte[] content, CancellationToken ct)
    {
        if (_container is null)
        {
            _logger.LogInformation(
                "Search-index snapshots are not configured; skipping upload of {Blob}", blobName);
            return;
        }

        if (Interlocked.Exchange(ref _created, 1) == 0)
        {
            await _container.CreateIfNotExistsAsync(cancellationToken: ct);
        }

        await _container.GetBlobClient(blobName)
            .UploadAsync(new BinaryData(content), overwrite: true, ct);
        _logger.LogInformation(
            "Uploaded snapshot {Blob} ({Size:N0} bytes)", blobName, content.Length);
    }
}
