using System.ComponentModel.DataAnnotations;

namespace ResearchDiscovery.Application.Options;

/// <summary>
/// Local reranker model configuration (same download-on-first-use posture as
/// the embedder). Only consulted when Ranking:UseReranker is on.
/// </summary>
public class CrossEncoderOptions
{
    public const string SectionName = "CrossEncoder";

    [Required]
    public string ModelVersion { get; set; } = "ms-marco-MiniLM-L-6-v2";

    [Required]
    [Url]
    public string ModelUrl { get; set; } =
        "https://huggingface.co/Xenova/ms-marco-MiniLM-L-6-v2/resolve/main/onnx/model.onnx";

    [Required]
    [Url]
    public string VocabUrl { get; set; } =
        "https://huggingface.co/Xenova/ms-marco-MiniLM-L-6-v2/resolve/main/vocab.txt";

    [Required]
    public string ModelDirectory { get; set; } = "models/ms-marco-MiniLM-L-6-v2";

    [Range(64, 512)]
    public int MaxSequenceTokens { get; set; } = 384;

    [Range(1, 64)]
    public int BatchSize { get; set; } = 8;
}
