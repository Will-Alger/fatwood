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

    /// <summary>
    /// When set, requests authenticate as this external identity instead of
    /// the default local-dev admin — the account layer provisions it as a
    /// regular member, so tests can exercise the non-admin posture.
    /// </summary>
    public string? TestUserExternalId { get; init; }

    public string TestUserEmail { get; init; } = "member@test.local";

    /// <summary>Turns on the invite-code signup gate for this host.</summary>
    public bool RequireInviteCode { get; init; }

    /// <summary>Overrides the starter budget grant. Set to 0 to provision a
    /// member with no budget (exercises the budget-exhausted 402 path).</summary>
    public long? StarterGrantMicros { get; init; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("Database:MigrateOnStartup", "false");
        builder.UseSetting("Ingestion:Schedule:Enabled", "false");
        builder.UseSetting("Accounts:RequireInviteCode", RequireInviteCode ? "true" : "false");
        if (StarterGrantMicros is { } grant)
        {
            builder.UseSetting("Accounts:StarterGrantMicros", grant.ToString());
        }

        builder.ConfigureServices(services =>
        {
            if (TestUserExternalId is not null)
            {
                services.AddAuthentication(TestAuthHandler.SchemeName)
                    .AddScheme<TestAuthOptions, TestAuthHandler>(TestAuthHandler.SchemeName, o =>
                    {
                        o.ExternalId = TestUserExternalId;
                        o.Email = TestUserEmail;
                    });
            }

            _connection.Open();

            // Create the schema BEFORE the host starts: hosted services and
            // DataProtection's key-ring warmup touch the database as soon as
            // the host runs, and racing them against a post-start
            // EnsureCreated on this single shared connection flakes with
            // "no such table" errors.
            var schemaOptions = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(_connection)
                .Options;
            using (var schemaDb = new AppDbContext(schemaOptions))
            {
                schemaDb.Database.EnsureCreated();
            }

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

    // Schema creation happens in ConfigureServices (pre-start) — see above.

    public async Task SeedAsync(Func<AppDbContext, Task> seed)
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await seed(db);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Provisions (or fetches) the local-dev admin account and returns its id
    /// — needed by tests that call user-scoped services directly instead of
    /// going through HTTP.
    /// </summary>
    public async Task<long> EnsureDevUserAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var accounts = scope.ServiceProvider.GetRequiredService<IUserAccountService>();
        var user = await accounts.GetOrCreateAsync(
            Infrastructure.Accounts.UserAccountService.LocalDevExternalId,
            "dev@localhost", "Local Dev", CancellationToken.None);
        return user.Id;
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
