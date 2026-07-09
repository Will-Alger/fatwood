using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ResearchDiscovery.Application.Abstractions;
using ResearchDiscovery.Domain.Entities;
using ResearchDiscovery.Infrastructure.Persistence;

namespace ResearchDiscovery.IntegrationTests;

/// <summary>
/// Hosts the real API over an in-memory Sqlite database. Tests may seed data
/// through the context (fixtures are allowed in tests — only the running app
/// is restricted to live arXiv data). The arXiv client is stubbed to return
/// nothing so no test ever leaves the process.
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    /// <summary>Set before the host is first used to enable the admin surface.</summary>
    public string? AdminApiKey { get; init; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("Database:MigrateOnStartup", "false");
        builder.UseSetting("Ingestion:Schedule:Enabled", "false");
        builder.UseSetting("Admin:ApiKey", AdminApiKey ?? string.Empty);

        builder.ConfigureServices(services =>
        {
            _connection.Open();

            services.RemoveAll(typeof(DbContextOptions<AppDbContext>));
            services.RemoveAll(typeof(IDbContextOptionsConfiguration<AppDbContext>));
            services.RemoveAll(typeof(IDbContextFactory<AppDbContext>));
            services.RemoveAll(typeof(AppDbContext));
            services.AddDbContextFactory<AppDbContext>(options => options.UseSqlite(_connection));

            services.RemoveAll(typeof(IArxivClient));
            services.AddSingleton<IArxivClient, StubArxivClient>();

            // The LLM is stubbed the same way the arXiv client is: analysis
            // orchestration runs for real, but no test ever calls Anthropic.
            services.RemoveAll(typeof(IPaperAnalyzer));
            services.AddSingleton<IPaperAnalyzer>(new StubPaperAnalyzer(p => AnalyzePaper(p)));

            // Same for the search compiler (LLM) and the local embedder (would
            // download ~90 MB of model files): deterministic in-process stubs.
            services.RemoveAll(typeof(ISearchPlanCompiler));
            services.AddSingleton<ISearchPlanCompiler>(new StubSearchPlanCompiler(q => CompilePlan(q)));
            services.RemoveAll(typeof(ITextEmbedder));
            services.AddSingleton<ITextEmbedder>(new StubTextEmbedder());
        });
    }

    /// <summary>Deterministic plan for compile-endpoint tests.</summary>
    public Func<string, SearchPlan> CompilePlan { get; set; } = query =>
        new SearchPlan($"Stub interpretation of: {query}", query, [], null, null);

    /// <summary>
    /// Per-paper stub behavior for the analysis layer. Defaults to a fixed
    /// valid v2 document; tests override to vary scores or simulate declines.
    /// </summary>
    public Func<Paper, PaperAnalysis?> AnalyzePaper { get; set; } = DefaultAnalysis;

    public static PaperAnalysis? DefaultAnalysis(Paper paper) =>
        new(
            """
            {
              "summary": "Stub analysis.",
              "feasibility_score": 7,
              "hard_blockers": [],
              "learning_bridge": "Stub.",
              "estimated_effort": "one_to_two_weeks",
              "approach": "extend",
              "approach_rationale": "Stub.",
              "reference_code_likelihood": "medium",
              "goal_alignment_score": 5,
              "resume_signal": "Stub.",
              "extension_idea": "Stub.",
              "required_skills": ["C#"],
              "composite_score": 50
            }
            """,
            50m,
            "stub-model",
            2,
            0);

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);

        using var scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();

        return host;
    }

    public async Task SeedAsync(Func<AppDbContext, Task> seed)
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await seed(db);
        await db.SaveChangesAsync();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        _connection.Dispose();
    }

    private sealed class StubArxivClient : IArxivClient
    {
        public Task<ArxivPage> QueryAsync(ArxivQuery query, CancellationToken ct) =>
            Task.FromResult(new ArxivPage(0, []));
    }

    private sealed class StubPaperAnalyzer(Func<Paper, PaperAnalysis?> analyze) : IPaperAnalyzer
    {
        public Task<PaperAnalysis?> AnalyzeAsync(
            Paper paper, string? profileDescription, int profileVersion, CancellationToken ct) =>
            Task.FromResult(analyze(paper));
    }

    private sealed class StubSearchPlanCompiler(Func<string, SearchPlan> compile) : ISearchPlanCompiler
    {
        public Task<SearchPlan> CompileAsync(
            string query, string? profile, IReadOnlyList<string> knownCategories, CancellationToken ct) =>
            Task.FromResult(compile(query));
    }

    /// <summary>
    /// Deterministic word-hash embedder: same text → same vector, sharing
    /// words → higher cosine. Good enough to exercise rank ordering.
    /// </summary>
    public sealed class StubTextEmbedder : ITextEmbedder
    {
        public int Dimensions => 64;

        public Task<float[]> EmbedAsync(string text, CancellationToken ct) =>
            Task.FromResult(Embed(text));

        public Task<IReadOnlyList<float[]>> EmbedBatchAsync(
            IReadOnlyList<string> texts, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<float[]>>(texts.Select(Embed).ToList());

        public static float[] Embed(string text)
        {
            var vector = new float[64];
            foreach (var word in text.ToLowerInvariant()
                .Split(' ', ',', '.', ';', ':', '\n')
                .Where(w => w.Length > 2))
            {
                var bucket = Math.Abs(StableHash(word)) % vector.Length;
                vector[bucket] += 1f;
            }

            var norm = MathF.Sqrt(vector.Sum(v => v * v));
            if (norm > 0)
            {
                for (var i = 0; i < vector.Length; i++)
                {
                    vector[i] /= norm;
                }
            }

            return vector;
        }

        private static int StableHash(string s)
        {
            unchecked
            {
                var hash = 23;
                foreach (var c in s)
                {
                    hash = hash * 31 + c;
                }

                return hash;
            }
        }
    }
}
