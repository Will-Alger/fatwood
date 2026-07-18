using Anthropic;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Polly;
using ResearchDiscovery.Application.Abstractions;
using ResearchDiscovery.Application.Options;
using ResearchDiscovery.Infrastructure.Accounts;
using ResearchDiscovery.Infrastructure.Analysis;
using ResearchDiscovery.Infrastructure.Email;
using ResearchDiscovery.Infrastructure.Arxiv;
using ResearchDiscovery.Infrastructure.Embeddings;
using ResearchDiscovery.Infrastructure.Enrichment;
using ResearchDiscovery.Infrastructure.Eval;
using ResearchDiscovery.Infrastructure.Ingestion;
using ResearchDiscovery.Infrastructure.Llm;
using ResearchDiscovery.Infrastructure.Persistence;
using ResearchDiscovery.Infrastructure.Profile;
using ResearchDiscovery.Infrastructure.Queries;
using ResearchDiscovery.Infrastructure.Search;
using ResearchDiscovery.Infrastructure.Telemetry;

namespace ResearchDiscovery.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Single composition point shared by the web host and the CLI ingestion
    /// mode, so both processes are configured identically.
    /// </summary>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ArxivOptions>()
            .Bind(configuration.GetSection(ArxivOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<IngestionOptions>()
            .Bind(configuration.GetSection(IngestionOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<AuthOptions>()
            .Bind(configuration.GetSection(AuthOptions.SectionName));

        services.AddOptions<AccountOptions>()
            .Bind(configuration.GetSection(AccountOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Accounts + budget ledger. The usage context is scoped: request
        // scopes get the signed-in user, background analysis scopes get the
        // requesting user from the job payload, CLI scopes stay system.
        services.AddScoped<ILlmUsageContext, LlmUsageContext>();
        services.AddScoped<ILlmUsageRecorder, LlmUsageRecorder>();
        services.AddScoped<IBudgetService, BudgetService>();
        services.AddScoped<IUserAccountService, UserAccountService>();
        services.AddScoped<IUserKeyService, UserKeyService>();
        services.AddScoped<AnthropicCallFactory>();

        // Branded OTP verification emails (Entra custom email provider hook).
        services.AddOptions<EmailOptions>()
            .Bind(configuration.GetSection(EmailOptions.SectionName));
        services.AddOptions<AuthEventsOptions>()
            .Bind(configuration.GetSection(AuthEventsOptions.SectionName));
        services.AddSingleton<OtpEmailSender>();

        // Data Protection encrypts users' BYO Anthropic keys; the key ring
        // persists in the database so restarts/replicas share it. Upgrade
        // path: ProtectKeysWithAzureKeyVault for at-rest key-ring encryption.
        services.AddDataProtection()
            .SetApplicationName("Fatwood")
            .PersistKeysToDbContext<AppDbContext>();

        // The single provider-specific registration in the entire codebase.
        // Swapping to SQL Server = swap the Npgsql package for
        // Microsoft.EntityFrameworkCore.SqlServer, change this call to
        // UseSqlServer, and regenerate the migrations. Nothing else moves.
        services.AddDbContextFactory<AppDbContext>(options =>
            options.UseNpgsql(configuration.GetConnectionString("Default")));

        var arxiv = configuration.GetSection(ArxivOptions.SectionName).Get<ArxivOptions>()
            ?? new ArxivOptions();

        services.AddSingleton<ArxivThrottlingHandler>();

        var httpBuilder = services.AddHttpClient<IArxivClient, ArxivClient>(client =>
        {
            // arXiv's terms of use ask API consumers to identify themselves.
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "ResearchDiscovery/1.0 (research-to-project discovery tool)");
        });

        // Resilience is registered BEFORE the throttling handler so it sits
        // outside it: every retry attempt passes back through the throttle and
        // stays within arXiv's rate-limit etiquette. The standard retry honors
        // Retry-After on 429/503 responses.
        httpBuilder.AddStandardResilienceHandler(options =>
        {
            options.Retry.MaxRetryAttempts = arxiv.MaxRetries;
            options.Retry.Delay = TimeSpan.FromSeconds(arxiv.RetryBaseDelaySeconds);
            options.Retry.BackoffType = DelayBackoffType.Exponential;
            options.Retry.UseJitter = true;
            options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(30);
            options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(90);
            options.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(5);
        });

        httpBuilder.AddHttpMessageHandler(sp => sp.GetRequiredService<ArxivThrottlingHandler>());

        // OAI-PMH bulk harvest. No resilience handler and no
        // ArxivThrottlingHandler on purpose: ArxivOaiClient owns 503/Retry-After
        // handling and 3s pacing itself, per arXiv's bulk-harvest flow control.
        services.AddHttpClient<IArxivOaiClient, ArxivOaiClient>(client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "ResearchDiscovery/1.0 (research-to-project discovery tool)");
            client.Timeout = TimeSpan.FromMinutes(2); // OAI pages are ~1000 records / ~3 MB
        });
        services.AddScoped<IBulkHarvestService, BulkHarvestService>();

        services.AddSingleton<PaperUpserter>();
        services.AddSingleton<IIngestionLockManager, DbIngestionLockManager>();
        services.AddScoped<IIngestionService, IngestionService>();
        services.AddScoped<IPaperQueryService, PaperQueryService>();
        services.AddScoped<IRecentSearchService, RecentSearchService>();
        services.AddScoped<IBookmarkService, BookmarkService>();

        services.AddOptions<AnalysisOptions>()
            .Bind(configuration.GetSection(AnalysisOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Phase 2 analysis layer. The Anthropic SDK resolves credentials from
        // the environment (ANTHROPIC_API_KEY); a missing key surfaces as an
        // auth error on the first analysis call, never at startup, so the
        // browse/ingestion paths run fine without one.
        services.AddSingleton(_ => new AnthropicClient());
        services.AddScoped<IPaperAnalyzer, AnthropicPaperAnalyzer>();
        services.AddScoped<IAnalysisService, AnalysisService>();

        // Analysis queue: durable Storage queue in cloud (consumed by a scaled
        // worker job), in-process queue otherwise. Registered here so the web
        // host AND the worker job resolve the same implementation. The
        // in-process worker itself is a hosted service the web host adds only
        // in in-memory mode (see Program.cs).
        var queueOptions = new AnalysisQueueOptions();
        configuration.GetSection(AnalysisQueueOptions.SectionName).Bind(queueOptions);
        services.AddOptions<AnalysisQueueOptions>()
            .Bind(configuration.GetSection(AnalysisQueueOptions.SectionName));
        if (queueOptions.UseStorageQueue)
        {
            // Hybrid: head of each selection to the in-process hot lane
            // (drained by the web host — first cards in seconds), tail to the
            // durable Storage queue for the worker job.
            services.AddSingleton<InMemoryAnalysisQueue>();
            services.AddSingleton<StorageAnalysisQueue>();
            services.AddSingleton<IAnalysisQueue, HybridAnalysisQueue>();
        }
        else
        {
            services.AddSingleton<InMemoryAnalysisQueue>();
            services.AddSingleton<IAnalysisQueue>(sp => sp.GetRequiredService<InMemoryAnalysisQueue>());
        }

        // Personalized discovery: LLM registry/settings, profile, local
        // embeddings, and the search pipeline.
        services.AddOptions<LlmOptions>()
            .Bind(configuration.GetSection(LlmOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<EmbeddingOptions>()
            .Bind(configuration.GetSection(EmbeddingOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<ILlmSettingsService, LlmSettingsService>();
        services.AddSingleton<ProfileService>();

        // The embedder is a singleton: it owns the loaded ONNX session. The
        // named client is only used to download model files on first use.
        services.AddHttpClient(OnnxTextEmbedder.HttpClientName, client =>
            client.Timeout = TimeSpan.FromMinutes(10));
        services.AddSingleton<ITextEmbedder, OnnxTextEmbedder>();
        services.AddSingleton<IEmbeddingIndex, InMemoryEmbeddingIndex>();
        services.AddSingleton<IPaperEmbeddingService, PaperEmbeddingService>();

        // Packed-index snapshots in blob storage: cold replicas download the
        // prebuilt indexes in seconds instead of scanning the database.
        // Unconfigured (local/dev) the store disables itself and indexes
        // build straight from the database.
        services.AddOptions<SearchIndexOptions>()
            .Bind(configuration.GetSection(SearchIndexOptions.SectionName));
        services.AddSingleton<SearchIndexSnapshotStore>();
        services.AddSingleton<ISearchIndexSnapshots, SearchIndexSnapshotWriter>();

        // Hybrid retrieval + reranking stages (config-flagged; both local, no
        // tokens). The cross-encoder only downloads its model when first used.
        services.AddOptions<CrossEncoderOptions>()
            .Bind(configuration.GetSection(CrossEncoderOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<ILexicalIndex, InMemoryLexicalIndex>();
        services.AddSingleton<ICrossEncoder, OnnxCrossEncoder>();

        services.AddOptions<RankingOptions>()
            .Bind(configuration.GetSection(RankingOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddScoped<ISearchService, SearchService>();
        services.AddScoped<ISearchPlanCompiler, AnthropicSearchPlanCompiler>();

        // Product telemetry: searches + interactions, logged from the API
        // surface only (the eval CLI must never write here).
        services.AddScoped<ISearchTelemetry, SearchTelemetryService>();

        // Signal enrichment (CLI-only): citations + stars.
        services.AddHttpClient(PaperSignalEnricher.HttpClientName,
            client => client.Timeout = TimeSpan.FromSeconds(60));
        services.AddSingleton<PaperSignalEnricher>();

        // Offline search-quality harness (CLI-only; never on a request path).
        services.AddScoped<IRelevanceJudge, AnthropicRelevanceJudge>();
        services.AddScoped<EvalRunner>();
        services.AddScoped<TelemetryAnalyzer>();

        return services;
    }
}
