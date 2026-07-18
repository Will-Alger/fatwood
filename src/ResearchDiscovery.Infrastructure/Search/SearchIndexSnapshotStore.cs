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

    /// <summary>
    /// Downloads a snapshot to a delete-on-close temp file and returns a
    /// stream over it, or null when absent/unreadable. Spooling through disk
    /// keeps a large snapshot from being resident twice (download buffer +
    /// parsed arrays) during index load.
    /// </summary>
    public async Task<Stream?> TryOpenReadAsync(string blobName, CancellationToken ct)
    {
        if (_container is null)
        {
            return null;
        }

        var path = Path.Combine(Path.GetTempPath(), $"rdisc-snapshot-{Guid.NewGuid():N}.bin");
        try
        {
            var blob = _container.GetBlobClient(blobName);
            await blob.DownloadToAsync(path, ct);
            return new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.None, 1 << 16,
                FileOptions.SequentialScan | FileOptions.DeleteOnClose);
        }
        catch (Exception ex)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // best effort — temp cleanup only
            }

            _logger.LogWarning(ex,
                "Snapshot {Blob} unavailable; falling back to a database build", blobName);
            return null;
        }
    }

    /// <summary>Uploads (replaces) a snapshot from a local file. Throws on
    /// failure — writers run in offline jobs where a loud failure is more
    /// useful than a silent skip.</summary>
    public async Task UploadFromFileAsync(string blobName, string path, CancellationToken ct)
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

        await using var content = File.OpenRead(path);
        await _container.GetBlobClient(blobName).UploadAsync(content, overwrite: true, ct);
        _logger.LogInformation(
            "Uploaded snapshot {Blob} ({Size:N0} bytes)", blobName, content.Length);
    }
}
