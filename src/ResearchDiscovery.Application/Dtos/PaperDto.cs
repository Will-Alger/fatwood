using System.Text.Json;

namespace ResearchDiscovery.Application.Dtos;

public sealed record PaperDto(
    string ArxivId,
    string Title,
    string Abstract,
    IReadOnlyList<string> Authors,
    string PrimaryCategory,
    IReadOnlyList<string> Categories,
    DateTimeOffset PublishedUtc,
    DateTimeOffset UpdatedUtc,
    string AbsUrl,
    string PdfUrl,
    string? Doi,
    PaperAnalysisDto? Analysis = null,
    string? CodeUrl = null);

/// <summary>
/// Stored analysis surfaced to the browse UI. Details is the structured JSON
/// produced by the model (schema governed by SchemaVersion), passed through
/// rather than re-modelled so schema evolution never breaks this DTO.
/// </summary>
public sealed record PaperAnalysisDto(
    decimal? CompositeScore,
    string Model,
    int SchemaVersion,
    DateTimeOffset CreatedUtc,
    JsonElement Details);

public sealed record CategoryDto(string Code, string Name, int PaperCount);

public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalItems,
    int TotalPages);
