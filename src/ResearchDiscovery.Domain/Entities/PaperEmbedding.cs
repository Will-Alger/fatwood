namespace ResearchDiscovery.Domain.Entities;

/// <summary>
/// Local-model embedding of a paper's title + abstract. Stored as raw
/// little-endian float32 bytes (provider-portable — no pgvector) and ranked
/// in-process; ~16k × 384 floats is ~25 MB, trivially cacheable in memory.
/// </summary>
public class PaperEmbedding
{
    public long PaperId { get; set; }

    public Paper Paper { get; set; } = null!;

    /// <summary>Embedding model identifier; rows from older models are re-embedded.</summary>
    public required string ModelVersion { get; set; }

    /// <summary>Little-endian float32 vector bytes.</summary>
    public required byte[] Vector { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }
}
