using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using ResearchDiscovery.Application.Abstractions;
using ResearchDiscovery.Application.Options;
using Xunit;

namespace ResearchDiscovery.IntegrationTests;

/// <summary>
/// LLM step assignments and profile behavior, including the load-bearing
/// invariant that editing the profile marks existing analyses stale.
/// </summary>
public class SettingsApiTests
{
    private sealed record LlmSettingsView(
        List<ModelView> Registry,
        List<AssignmentView> Assignments,
        int EstAnalysisInputTokensPerPaper,
        int EstAnalysisOutputTokensPerPaper,
        int EstCompileInputTokens,
        int EstCompileOutputTokens);

    private sealed record ModelView(
        string Id, string DisplayName, decimal InputPerMTok, decimal OutputPerMTok);

    private sealed record AssignmentView(string Step, string ModelId, bool IsDefault);

    private sealed record ProfileView(
        string ExperienceSummary, string Goals, int? WeeklyHours, int Version, DateTimeOffset? UpdatedUtc);

    [Fact]
    public async Task LlmSettings_ExposeRegistryWithPricing_AndAcceptOverrides()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        var settings = await client.GetFromJsonAsync<LlmSettingsView>("/api/admin/settings/llm");
        Assert.NotNull(settings);
        Assert.Contains(settings.Registry, m => m.Id == "claude-haiku-4-5" && m.InputPerMTok == 1.00m);
        Assert.Equal(
            LlmOptions.Steps.Order().ToList(),
            settings.Assignments.Select(a => a.Step).Order().ToList());
        Assert.All(settings.Assignments, a => Assert.True(a.IsDefault));

        // Override the analysis step, verify it sticks and loses default status.
        var put = await client.PutAsJsonAsync("/api/admin/settings/llm",
            new { step = "PaperAnalysis", modelId = "claude-sonnet-5" });
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        var updated = await client.GetFromJsonAsync<LlmSettingsView>("/api/admin/settings/llm");
        var analysis = updated!.Assignments.Single(a => a.Step == "PaperAnalysis");
        Assert.Equal("claude-sonnet-5", analysis.ModelId);
        Assert.False(analysis.IsDefault);
    }

    [Fact]
    public async Task LlmSettings_RejectUnknownModelsAndSteps()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        var badModel = await client.PutAsJsonAsync("/api/admin/settings/llm",
            new { step = "PaperAnalysis", modelId = "gpt-42" });
        Assert.Equal(HttpStatusCode.BadRequest, badModel.StatusCode);

        var badStep = await client.PutAsJsonAsync("/api/admin/settings/llm",
            new { step = "Nonsense", modelId = "claude-haiku-4-5" });
        Assert.Equal(HttpStatusCode.BadRequest, badStep.StatusCode);
    }

    [Fact]
    public async Task Profile_SaveBumpsVersion_AndMarksAnalysesStale()
    {
        using var factory = new ApiFactory();
        await factory.SeedAsync(TestData.SeedPapersAsync);
        using var client = factory.CreateClient();

        // Analyze everything in cs.LG once (profile v0).
        var first = await AnalyzeAsync(factory, "cs.LG");
        Assert.Equal(2, first.PapersAnalyzed);

        // Idempotent while the profile is unchanged.
        var second = await AnalyzeAsync(factory, "cs.LG");
        Assert.Equal(0, second.PapersSelected);

        // Saving the profile bumps the version...
        var saved = await client.PutAsJsonAsync("/api/admin/settings/profile",
            new { experienceSummary = "4 years backend", goals = "applied ML engineering", weeklyHours = 8 });
        saved.EnsureSuccessStatusCode();
        var profile = await saved.Content.ReadFromJsonAsync<ProfileView>();
        Assert.Equal(1, profile!.Version);

        // ...which makes the same papers stale: they re-analyze against the new person.
        var third = await AnalyzeAsync(factory, "cs.LG");
        Assert.Equal(2, third.PapersSelected);
        Assert.Equal(2, third.PapersAnalyzed);
    }

    [Fact]
    public async Task SettingsEndpoints_AsMember_Return403()
    {
        using var factory = new ApiFactory { TestUserExternalId = "member-1" };
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.GetAsync("/api/admin/settings/llm")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.GetAsync("/api/admin/settings/profile")).StatusCode);
    }

    private static async Task<AnalysisSummary> AnalyzeAsync(ApiFactory factory, string category)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IAnalysisService>();
        return await service.AnalyzeAsync(
            new AnalysisRequest(category, MaxPapers: 10, Since: null), CancellationToken.None);
    }
}
