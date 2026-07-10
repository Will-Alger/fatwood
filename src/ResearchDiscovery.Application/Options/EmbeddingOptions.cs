using System.ComponentModel.DataAnnotations;

namespace ResearchDiscovery.Application.Options;

/// <summary>
/// Local embedding model configuration. The model runs in-process via ONNX
/// Runtime — the corpus sift costs zero API tokens. Model files are downloaded
/// once on first use (they are too large to commit).
/// </summary>
public class EmbeddingOptions
{
    public const string SectionName = "Embeddings";

    public bool Enabled { get; set; } = true;

    /// <summary>Identifier stored with each vector; changing it re-embeds the corpus.</summary>
    [Required]
    public string ModelVersion { get; set; } = "all-MiniLM-L6-v2";

    [Required]
    [Url]
    public string ModelUrl { get; set; } =
        "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/onnx/model.onnx";

    [Required]
    [Url]
    public string VocabUrl { get; set; } =
        "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/vocab.txt";

    /// <summary>Directory for downloaded model files. Relative paths resolve under the content root.</summary>
    [Required]
    public string ModelDirectory { get; set; } = "models/all-MiniLM-L6-v2";

    /// <summary>
    /// Prefix prepended to SEARCH QUERIES only (never documents). Some models
    /// (e.g. bge) are trained with an instruction prefix on the query side;
    /// MiniLM uses none, hence the empty default.
    /// </summary>
    public string QueryPrefix { get; set; } = string.Empty;

    [Range(16, 512)]
    public int MaxSequenceTokens { get; set; } = 256;

    [Range(1, 256)]
    public int BatchSize { get; set; } = 16;
}
