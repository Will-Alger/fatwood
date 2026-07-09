using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using ResearchDiscovery.Application.Abstractions;
using ResearchDiscovery.Application.Options;

namespace ResearchDiscovery.Infrastructure.Embeddings;

/// <summary>
/// Sentence embeddings via a local ONNX MiniLM model: WordPiece tokenize,
/// transformer forward pass, attention-masked mean pooling, L2 normalize.
/// Model files (~90 MB) are downloaded once on first use — they are far too
/// large to commit and are reference artifacts, not product data.
/// </summary>
public sealed class OnnxTextEmbedder(
    IOptions<EmbeddingOptions> options,
    IHttpClientFactory httpClientFactory,
    ILogger<OnnxTextEmbedder> logger) : ITextEmbedder, IDisposable
{
    public const string HttpClientName = "embedding-model-download";

    private readonly EmbeddingOptions _options = options.Value;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private InferenceSession? _session;
    private BertTokenizer? _tokenizer;

    public int Dimensions { get; private set; } = 384;

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct) =>
        (await EmbedBatchAsync([text], ct))[0];

    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(
        IReadOnlyList<string> texts, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);

        var encoded = texts
            .Select(t => _tokenizer!.EncodeToIds(
                t, _options.MaxSequenceTokens, out _, out _).ToArray())
            .ToList();

        var batch = encoded.Count;
        var seqLen = Math.Max(1, encoded.Max(e => e.Length));

        var inputIds = new DenseTensor<long>([batch, seqLen]);
        var attentionMask = new DenseTensor<long>([batch, seqLen]);
        var tokenTypeIds = new DenseTensor<long>([batch, seqLen]);

        for (var i = 0; i < batch; i++)
        {
            for (var j = 0; j < encoded[i].Length; j++)
            {
                inputIds[i, j] = encoded[i][j];
                attentionMask[i, j] = 1;
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

        // ONNX Runtime inference is synchronous CPU work; batches are small.
        using var results = _session.Run(inputs);
        var hidden = results[0].AsTensor<float>();
        var hiddenSize = hidden.Dimensions[2];
        Dimensions = hiddenSize;

        var vectors = new List<float[]>(batch);
        for (var i = 0; i < batch; i++)
        {
            var vector = new float[hiddenSize];
            var tokenCount = 0;
            for (var j = 0; j < seqLen; j++)
            {
                if (attentionMask[i, j] == 0)
                {
                    continue;
                }

                tokenCount++;
                for (var d = 0; d < hiddenSize; d++)
                {
                    vector[d] += hidden[i, j, d];
                }
            }

            var norm = 0f;
            for (var d = 0; d < hiddenSize; d++)
            {
                vector[d] /= Math.Max(1, tokenCount);
                norm += vector[d] * vector[d];
            }

            norm = MathF.Sqrt(norm);
            if (norm > 0)
            {
                for (var d = 0; d < hiddenSize; d++)
                {
                    vector[d] /= norm;
                }
            }

            vectors.Add(vector);
        }

        return vectors;
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
                "Embedding model {Model} loaded from {Dir}", _options.ModelVersion, dir);
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

        logger.LogInformation("Downloading embedding model file {Url} -> {Path}", url, path);
        var client = httpClientFactory.CreateClient(HttpClientName);
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
