using ResearchDiscovery.Domain.Entities;
using ResearchDiscovery.Infrastructure.Persistence;

namespace ResearchDiscovery.IntegrationTests;

public static class TestData
{
    /// <summary>Seeds two categories and three papers (one cross-listed).</summary>
    public static Task SeedPapersAsync(AppDbContext db)
    {
        var csLg = new Category { Code = "cs.LG", Name = "Machine Learning" };
        var csCr = new Category { Code = "cs.CR", Name = "Cryptography and Security" };
        var now = DateTimeOffset.UtcNow;

        db.Papers.AddRange(
            NewPaper("2501.00001", "Newest ML paper", csLg, now.AddDays(-1), [csLg]),
            NewPaper("2501.00002", "Older cross-listed paper", csLg, now.AddDays(-10), [csLg, csCr]),
            NewPaper("2501.00003", "Security paper", csCr, now.AddDays(-5), [csCr]));

        return Task.CompletedTask;
    }

    private static Paper NewPaper(
        string arxivId,
        string title,
        Category primary,
        DateTimeOffset published,
        Category[] categories) =>
        new()
        {
            ArxivId = arxivId,
            LatestVersion = 1,
            Title = title,
            Abstract = $"Abstract of {title}.",
            Authors = "Ada Lovelace; Alan Turing",
            PrimaryCategory = primary,
            PublishedUtc = published,
            UpdatedUtc = published,
            AbsUrl = $"https://arxiv.org/abs/{arxivId}v1",
            PdfUrl = $"https://arxiv.org/pdf/{arxivId}v1",
            FirstIngestedUtc = published,
            LastSeenUtc = published,
            PaperCategories = categories
                .Select(c => new PaperCategory { Category = c })
                .ToList(),
        };
}
