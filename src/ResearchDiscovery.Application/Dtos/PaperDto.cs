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
    string? Doi);

public sealed record CategoryDto(string Code, string Name, int PaperCount);

public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalItems,
    int TotalPages);
