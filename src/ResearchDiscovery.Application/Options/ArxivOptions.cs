using System.ComponentModel.DataAnnotations;

namespace ResearchDiscovery.Application.Options;

public sealed class ArxivOptions
{
    public const string SectionName = "Arxiv";

    [Required]
    [Url]
    public string BaseUrl { get; set; } = "https://export.arxiv.org/api/query";

    /// <summary>Target arXiv category codes. Configuration-driven by spec — never hardcode in business logic.</summary>
    [MinLength(1)]
    public IReadOnlyList<string> Categories { get; set; } = [];

    [Range(1, 500)]
    public int PageSize { get; set; } = 100;

    /// <summary>arXiv etiquette: no more than one request every ~3 seconds.</summary>
    [Range(1, 60)]
    public int MinRequestIntervalSeconds { get; set; } = 3;

    [Range(0, 10)]
    public int MaxRetries { get; set; } = 3;

    [Range(1, 120)]
    public int RetryBaseDelaySeconds { get; set; } = 5;
}
