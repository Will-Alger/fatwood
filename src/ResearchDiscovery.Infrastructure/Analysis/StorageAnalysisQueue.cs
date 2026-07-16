using System.Text.Json;
using Azure.Identity;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using Microsoft.Extensions.Options;
using ResearchDiscovery.Application.Abstractions;
using ResearchDiscovery.Application.Options;

namespace ResearchDiscovery.Infrastructure.Analysis;

/// <summary>
/// Durable analysis queue on Azure Storage. The API produces one message per
/// paper; a separate worker job (see the `analyze-worker` mode) consumes them
/// with bounded concurrency and scales on queue depth. Auth is a connection
/// string (local Azurite) or managed identity (cloud). Base64 message encoding
/// is set on both ends so producer and consumer agree.
/// </summary>
public sealed class StorageAnalysisQueue : IAnalysisQueue
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly QueueClient _client;
    private int _created;

    public StorageAnalysisQueue(IOptions<AnalysisQueueOptions> options)
    {
        var o = options.Value;
        var clientOptions = new QueueClientOptions
        {
            MessageEncoding = QueueMessageEncoding.Base64,
        };

        _client = !string.IsNullOrWhiteSpace(o.ConnectionString)
            ? new QueueClient(o.ConnectionString, o.QueueName, clientOptions)
            : new QueueClient(
                new Uri($"{o.AccountUrl!.TrimEnd('/')}/{o.QueueName}"),
                new DefaultAzureCredential(),
                clientOptions);
    }

    public async Task EnqueueSelectionAsync(
        long? userId, IReadOnlyList<string> arxivIds, CancellationToken ct)
    {
        await EnsureCreatedAsync(ct);
        foreach (var arxivId in arxivIds)
        {
            var body = JsonSerializer.Serialize(new AnalysisWorkItem(userId, arxivId), Json);
            await _client.SendMessageAsync(body, cancellationToken: ct);
        }
    }

    public async Task<bool> HasPendingWorkAsync(CancellationToken ct)
    {
        try
        {
            var props = await _client.GetPropertiesAsync(ct);
            return props.Value.ApproximateMessagesCount > 0;
        }
        catch
        {
            return false; // status is a hint; never fail the poll on a queue blip
        }
    }

    // ---- consumer surface, used by the worker job ----

    public async Task<IReadOnlyList<QueueMessage>> ReceiveAsync(
        int maxMessages, TimeSpan visibilityTimeout, CancellationToken ct)
    {
        await EnsureCreatedAsync(ct);
        var response = await _client.ReceiveMessagesAsync(maxMessages, visibilityTimeout, ct);
        return response.Value;
    }

    public Task DeleteAsync(QueueMessage message, CancellationToken ct) =>
        _client.DeleteMessageAsync(message.MessageId, message.PopReceipt, ct);

    public static AnalysisWorkItem Parse(string messageText) =>
        JsonSerializer.Deserialize<AnalysisWorkItem>(messageText, Json)
        ?? throw new InvalidOperationException("Unparseable analysis queue message.");

    private async Task EnsureCreatedAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _created, 1) == 0)
        {
            await _client.CreateIfNotExistsAsync(cancellationToken: ct);
        }
    }
}
