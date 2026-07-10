using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using ResearchDiscovery.Application.Abstractions;
using ResearchDiscovery.Application.Options;
using ResearchDiscovery.Infrastructure.Embeddings;

namespace ResearchDiscovery.Infrastructure.Search;

/// <summary>
/// MS MARCO cross-encoder via ONNX Runtime. Pair encoding composes the
/// tokenizer's own single-text encodings — [CLS] query [SEP] + passage tokens
/// [SEP] with segment ids 0/1 — so no pair-specific tokenizer API is needed.
/// Reuses the embedder's download HttpClient.
/// </summary>
public sealed class OnnxCrossEncoder(
    IOptions<CrossEncoderOptions> options,
    IHttpClientFactory httpClientFactory,
    ILogger<OnnxCrossEncoder> logger) : ICrossEncoder, IDisposable
{
    private readonly CrossEncoderOptions _options = options.Value;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private InferenceSession? _session;
    private BertTokenizer? _tokenizer;

    public async Task<IReadOnlyList<float>> ScoreAsync(
        string query, IReadOnlyList<string> passages, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);

        // Query gets a fixed budget; passages fill the rest of the window.
        var queryIds = _tokenizer!.EncodeToIds(query, 64, out _, out _).ToArray();
        var passageBudget = _options.MaxSequenceTokens - queryIds.Length;

        var scores = new float[passages.Count];
        for (var offset = 0; offset < passages.Count; offset += _options.BatchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = passages.Skip(offset).Take(_options.BatchSize).ToList();

            var pairs = batch
                .Select(p =>
                {
                    // Drop the passage encoding's leading [CLS]; keep its [SEP].
                    var passageIds = _tokenizer.EncodeToIds(p, passageBudget, out _, out _)
                        .Skip(1).ToArray();
                    return (QueryLength: queryIds.Length, Ids: queryIds.Concat(passageIds).ToArray());
                })
                .ToList();

            var seqLen = Math.Max(1, pairs.Max(p => p.Ids.Length));
            var inputIds = new DenseTensor<long>([batch.Count, seqLen]);
            var attentionMask = new DenseTensor<long>([batch.Count, seqLen]);
            var tokenTypeIds = new DenseTensor<long>([batch.Count, seqLen]);

            for (var i = 0; i < batch.Count; i++)
            {
                for (var j = 0; j < pairs[i].Ids.Length; j++)
                {
                    inputIds[i, j] = pairs[i].Ids[j];
                    attentionMask[i, j] = 1;
                    tokenTypeIds[i, j] = j < pairs[i].QueryLength ? 0 : 1;
                }
            }

            var inputs = new List<NamedOnnxValue>();
            foreach (var name in _session!.InputMetadata.Keys)
            {
                inputs.Add(name switch
                {
                    "input_ids" => NamedOnnxValue.CreateFromTensor(name, inputIds),
                    "attention_mask" => NamedOnnxValue.CreateFromTensor(name, attentionMask),
                    "token_type_ids" => NamedOnnxValue.CreateFromTensor(name, tokenTypeIds),
                    _ => throw new InvalidOperationException($"Unexpected model input '{name}'."),
                });
            }

            using var results = _session.Run(inputs);
            var logits = results[0].AsTensor<float>();
            for (var i = 0; i < batch.Count; i++)
            {
                scores[offset + i] = logits[i, 0];
            }
        }

        return scores;
    }

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_session is not null)
        {
            return;
        }

        await _initLock.WaitAsync(ct);
        try
        {
            if (_session is not null)
            {
                return;
            }

            var dir = Path.GetFullPath(_options.ModelDirectory);
            Directory.CreateDirectory(dir);
            var modelPath = Path.Combine(dir, "model.onnx");
            var vocabPath = Path.Combine(dir, "vocab.txt");

            await DownloadIfMissingAsync(_options.ModelUrl, modelPath, ct);
            await DownloadIfMissingAsync(_options.VocabUrl, vocabPath, ct);

            _tokenizer = BertTokenizer.Create(vocabPath);
            _session = new InferenceSession(modelPath);
            logger.LogInformation(
                "Cross-encoder {Model} loaded from {Dir}", _options.ModelVersion, dir);
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task DownloadIfMissingAsync(string url, string path, CancellationToken ct)
    {
        if (File.Exists(path) && new FileInfo(path).Length > 0)
        {
            return;
        }

        logger.LogInformation("Downloading cross-encoder file {Url} -> {Path}", url, path);
        var client = httpClientFactory.CreateClient(OnnxTextEmbedder.HttpClientName);
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var tempPath = path + ".download";
        await using (var file = File.Create(tempPath))
        {
            await response.Content.CopyToAsync(file, ct);
        }

        File.Move(tempPath, path, overwrite: true);
    }

    public void Dispose()
    {
        _session?.Dispose();
        _initLock.Dispose();
    }
}
